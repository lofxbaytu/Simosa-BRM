// 練習紀錄的分段解析(講評報告頁;規劃書第 9.2 節、第 14 章「記憶體不持續成長」):
// 2 小時的教官站紀錄約 25 MB(版本 4 以前約 55 MB)、核心 JSON Lines 紀錄約 0.5–1 GB。整份讀成一個字串再 JSON.parse 時,
// 頁面同時持有全文(非 ASCII 內容時每字元 2 位元組)、整棵原始物件樹與解析後的狀態,7200 筆 E02 的尖峰約 +360 MB;
// 超過 V8 字串長度上限(約 512 MB)的核心紀錄則根本讀不進來。
//
// 這裡改為一段一段讀(Blob.slice → arrayBuffer → TextDecoder 串流解碼,預設每段 1 MB)並逐筆解析:
//  - 核心 JSON Lines:逐行交給 JsonLinesReplayBuilder;
//  - 教官站紀錄 JSON(單一 JSON 文件;下載檔的格式不變):以 TopLevelObjectScanner 逐字掃描頂層物件,頂層陣列逐筆 JSON.parse,
//    states 的每一筆解析後立即交給 SessionReplayBuilder(原始物件隨即可回收),其餘頂層欄位(事件、指令、情境…)收集後在結尾一次處理。
// 結果與 record-replay.parseReplay(全文) 完全相同(同一組建構器;格式判斷、行號、錯誤訊息、JSON 的重複鍵與空白規則皆同),
// 由 tests/record-stream.test.ts 以夾具、各版本的教官站紀錄與隨機破壞 / 隨機切段的差異測試驗證。
//
// 格式判斷與 detectRecordFormat 相同,但不需要全文:
//  - 去掉開頭空白後第一個字元不是「{」→ 無法辨識;
//  - 第一個換行之前(沒有換行則到結尾)若是一個完整的 JSON 物件且 kind 為字串 → JSON Lines;否則當作單一 JSON 文件。

import type { OwnShipState } from '../types/state.js';
import { JsonLinesReplayBuilder, SessionReplayBuilder, invalidJsonReplay, notSessionRecordReplay, unrecognizedReplay, type ReplayDataOf, type ReplayStateBase, type StateProjection } from './record-replay.js';
import { SESSION_RECORD_FORMAT, type SessionRecord } from './session-record.js';

const C_TAB = 9;
const C_LF = 10;
const C_CR = 13;
const C_SPACE = 32;
const C_QUOTE = 34;
const C_COMMA = 44;
const C_COLON = 58;
const C_LBRACKET = 91;
const C_BACKSLASH = 92;
const C_RBRACKET = 93;
const C_LBRACE = 123;
const C_RBRACE = 125;

/** JSON 文法允許的空白(空格、Tab、LF、CR)。 */
function isJsonWs(c: number): boolean {
  return c === C_SPACE || c === C_LF || c === C_CR || c === C_TAB;
}

/** String.prototype.trim() 會去掉的字元(ECMAScript WhiteSpace + LineTerminator)。 */
export function isJsWhitespace(c: number): boolean {
  if (c <= C_SPACE) return c === C_SPACE || (c >= C_TAB && c <= C_CR);
  if (c < 0xa0) return false;
  return c === 0xa0 || c === 0x1680 || (c >= 0x2000 && c <= 0x200a) || c === 0x2028 || c === 0x2029 || c === 0x202f || c === 0x205f || c === 0x3000 || c === 0xfeff;
}

/** 頂層物件掃描的接收端:成員值已 JSON.parse;頂層陣列逐筆給。 */
export interface TopLevelSink {
  /** 非陣列的頂層成員 */
  member(key: string, value: unknown): void;
  /** 頂層成員是陣列:開始 */
  arrayStart(key: string): void;
  arrayItem(key: string, index: number, value: unknown): void;
  arrayEnd(key: string): void;
}

// 掃描模式
const M_OBJ_FIRST = 0; // 「{」之後:鍵或「}」
const M_KEY_START = 1; // 「,」之後:鍵
const M_COLON = 2; // 鍵之後:「:」
const M_VAL_START = 3; // 「:」之後:值
const M_AFTER_VAL = 4; // 值之後:「,」或「}」
const M_ARR_FIRST = 5; // 頂層陣列「[」之後:元素或「]」
const M_ARR_NEXT = 6; // 陣列「,」之後:元素
const M_ARR_AFTER = 7; // 陣列元素之後:「,」或「]」
const M_TOK_STR = 8; // 字串語彙內
const M_TOK_NEST = 9; // 巢狀物件 / 陣列語彙內
const M_TOK_SCALAR = 10; // 數字 / true / false / null 語彙內
const M_DONE = 11; // 頂層物件已閉合

// 語彙完成後的去向
const CTX_KEY = 0;
const CTX_MEMBER = 1;
const CTX_ELEM = 2;

/**
 * 逐段掃描一個頂層 JSON 物件:只在頂層(成員)與頂層陣列(元素)切分,切出的每一段交給 JSON.parse。
 * 判定與 JSON.parse(全文) 一樣嚴格:切分點之間只接受 JSON 空白與應有的標點,每一段的內容由 JSON.parse 驗證
 * (合法的值其範圍唯一,因此「逐段都合法」等同「全文合法」)。掃描狀態跨段保留,不重掃。
 */
export class TopLevelObjectScanner {
  /** 文法錯誤(之後的輸入一律忽略) */
  failed = false;
  /** 頂層物件已閉合 */
  done = false;
  /** 閉合之後到目前為止只有 JSON 空白 */
  trailingJsonWs = true;
  /** 閉合之後到目前為止只有 trim() 會去掉的字元 */
  trailingJsWs = true;

  private mode = M_OBJ_FIRST;
  private started = false;
  private key = '';
  private index = 0;
  private ctx = CTX_KEY;
  private depth = 0;
  private inStr = false;
  private esc = false;
  /** 跨段的語彙:前幾段的片段 */
  private carry: string[] = [];

  constructor(private readonly sink: TopLevelSink) {}

  /** 餵入 s[from, to);第一個字元必須是「{」。 */
  feed(s: string, from: number, to: number): void {
    if (this.failed) return;
    let i = from;
    if (!this.started) {
      if (i >= to) return;
      this.started = true;
      if (s.charCodeAt(i) !== C_LBRACE) {
        this.failed = true;
        return;
      }
      i++;
    }
    /** 目前語彙在本段的起點(語彙由前一段延續時為 from) */
    let tokStart = from;
    while (i < to) {
      switch (this.mode) {
        case M_TOK_STR: {
          let esc = this.esc;
          let end = -1;
          while (i < to) {
            const c = s.charCodeAt(i++);
            if (esc) esc = false;
            else if (c === C_BACKSLASH) esc = true;
            else if (c === C_QUOTE) {
              end = i;
              break;
            }
          }
          this.esc = esc;
          if (end >= 0 && !this.endToken(s, tokStart, end)) return;
          break;
        }
        case M_TOK_NEST: {
          let depth = this.depth;
          let inStr = this.inStr;
          let esc = this.esc;
          let end = -1;
          while (i < to) {
            const c = s.charCodeAt(i++);
            if (inStr) {
              if (esc) esc = false;
              else if (c === C_BACKSLASH) esc = true;
              else if (c === C_QUOTE) inStr = false;
            } else if (c === C_QUOTE) inStr = true;
            else if (c === C_LBRACE || c === C_LBRACKET) depth++;
            else if ((c === C_RBRACE || c === C_RBRACKET) && --depth === 0) {
              end = i;
              break;
            }
          }
          this.depth = depth;
          this.inStr = inStr;
          this.esc = esc;
          if (end >= 0 && !this.endToken(s, tokStart, end)) return;
          break;
        }
        case M_TOK_SCALAR: {
          // 到分隔字元為止(分隔字元不屬於語彙,留給下一個模式處理)
          let end = -1;
          while (i < to) {
            const c = s.charCodeAt(i);
            if (c === C_COMMA || c === C_RBRACE || c === C_RBRACKET || isJsonWs(c)) {
              end = i;
              break;
            }
            i++;
          }
          if (end >= 0 && !this.endToken(s, tokStart, end)) return;
          break;
        }
        case M_DONE: {
          while (i < to) {
            const c = s.charCodeAt(i++);
            if (isJsonWs(c)) continue;
            this.trailingJsonWs = false;
            if (!isJsWhitespace(c)) {
              // 物件之後還有內容:不是單一 JSON 文件,第一行也不是完整的一行
              this.trailingJsWs = false;
              this.failed = true;
              return;
            }
          }
          break;
        }
        default: {
          const c = s.charCodeAt(i);
          if (isJsonWs(c)) {
            i++;
            break;
          }
          switch (this.mode) {
            case M_OBJ_FIRST:
            case M_KEY_START:
              if (c === C_QUOTE) {
                this.ctx = CTX_KEY;
                this.mode = M_TOK_STR;
                this.esc = false;
                tokStart = i++;
              } else if (c === C_RBRACE && this.mode === M_OBJ_FIRST) {
                this.mode = M_DONE;
                this.done = true;
                i++;
              } else {
                this.failed = true;
                return;
              }
              break;
            case M_COLON:
              if (c !== C_COLON) {
                this.failed = true;
                return;
              }
              this.mode = M_VAL_START;
              i++;
              break;
            case M_VAL_START:
              if (c === C_LBRACKET) {
                this.index = 0;
                this.sink.arrayStart(this.key);
                this.mode = M_ARR_FIRST;
                i++;
              } else {
                this.ctx = CTX_MEMBER;
                tokStart = i;
                i = this.startValue(c, i);
              }
              break;
            case M_AFTER_VAL:
              if (c === C_COMMA) this.mode = M_KEY_START;
              else if (c === C_RBRACE) {
                this.mode = M_DONE;
                this.done = true;
              } else {
                this.failed = true;
                return;
              }
              i++;
              break;
            case M_ARR_FIRST:
            case M_ARR_NEXT:
              if (c === C_RBRACKET && this.mode === M_ARR_FIRST) {
                this.sink.arrayEnd(this.key);
                this.mode = M_AFTER_VAL;
                i++;
              } else {
                this.ctx = CTX_ELEM;
                tokStart = i;
                i = this.startValue(c, i);
              }
              break;
            case M_ARR_AFTER:
              if (c === C_COMMA) this.mode = M_ARR_NEXT;
              else if (c === C_RBRACKET) {
                this.sink.arrayEnd(this.key);
                this.mode = M_AFTER_VAL;
              } else {
                this.failed = true;
                return;
              }
              i++;
              break;
          }
        }
      }
    }
    // 本段結束時語彙尚未完成:留下片段,下一段接續
    if (this.mode === M_TOK_STR || this.mode === M_TOK_NEST || this.mode === M_TOK_SCALAR) this.carry.push(s.slice(tokStart, to));
  }

  /** 值的第一個字元決定語彙種類;回傳下一個要讀的位置。 */
  private startValue(c: number, i: number): number {
    this.esc = false;
    if (c === C_QUOTE) {
      this.mode = M_TOK_STR;
      return i + 1;
    }
    if (c === C_LBRACE || c === C_LBRACKET) {
      this.mode = M_TOK_NEST;
      this.depth = 1;
      this.inStr = false;
      return i + 1;
    }
    // 其餘一律當作純量,由 JSON.parse 判定是否合法(不合法的字元、空的值都會失敗)
    this.mode = M_TOK_SCALAR;
    return i;
  }

  /** 語彙完成(s[tokStart, end) 加上先前的片段):JSON.parse 後依去向處理;失敗回傳 false。 */
  private endToken(s: string, tokStart: number, end: number): boolean {
    let text = s.slice(tokStart, end);
    if (this.carry.length > 0) {
      this.carry.push(text);
      text = this.carry.join('');
      this.carry = [];
    }
    let v: unknown;
    try {
      v = JSON.parse(text);
    } catch {
      this.failed = true;
      return false;
    }
    if (this.ctx === CTX_KEY) {
      this.key = v as string;
      this.mode = M_COLON;
    } else if (this.ctx === CTX_MEMBER) {
      this.sink.member(this.key, v);
      this.mode = M_AFTER_VAL;
    } else {
      this.sink.arrayItem(this.key, this.index++, v);
      this.mode = M_ARR_AFTER;
    }
    return true;
  }
}

export interface ReplayStreamResult<S> {
  data: ReplayDataOf<S>;
  /** 全文是單一 JSON 物件(JSON.parse(全文) 會成功且結果是物件) */
  jsonObject: boolean;
  /** jsonObject 為 true 時,頂層 format 欄的值(報告頁據此分辨評估表、名冊等其他檔案) */
  jsonFormat: unknown;
}

// 解析階段
const P_LEAD = 0; // 開頭空白
const P_FIRST = 1; // 內容開始後、第一個換行之前(尚未決定格式)
const P_JSON = 2; // 單一 JSON 文件
const P_LINES = 3; // JSON Lines
const P_UNKNOWN = 4; // 無法辨識

/**
 * 紀錄的分段解析器:push() 依序餵入文字片段(切在哪裡都可以),finish() 取得結果。
 * project:狀態投影(省略 = 保留完整狀態)。
 */
export class ReplayStreamParser<S extends ReplayStateBase = OwnShipState> implements TopLevelSink {
  private phase = P_LEAD;
  /** 開頭空白中的換行數 = 內容所在行的行號(由 0 起) */
  private leadNewlines = 0;
  /** 開頭有 JSON 不接受的空白(例如 U+00A0):JSON.parse(全文) 會失敗 */
  private leadNonJsonWs = false;
  private readonly scanner = new TopLevelObjectScanner(this);

  // ---- 頂層物件的接收端(單一 JSON 文件,或 JSON Lines 的第一行)
  /** states 以外的頂層欄位(無原型:鍵 __proto__ 也只是一般欄位) */
  private readonly top = Object.create(null) as Record<string, unknown>;
  private curArray: unknown[] | null = null;
  /** 最後一個 states 鍵的值:陣列 / 不是陣列 / 沒有此鍵 */
  private statesKind: 'array' | 'other' | 'missing' = 'missing';
  /** states 出現時 format 已確認是教官站紀錄:逐筆解析(否則先收著,結尾確認 format 後再解析) */
  private statesLive = false;
  private pendingStates: unknown[] | null = null;
  private readonly session: SessionReplayBuilder<S>;

  // ---- JSON Lines
  private readonly jsonl: JsonLinesReplayBuilder<S>;
  private lineNo = 0;
  private linePieces: string[] = [];
  /** 第一行的物件之後、換行之前只有 JSON 空白 */
  private firstLineJsonWs = true;
  /** 第一行之後的內容全是 JSON 空白 */
  private restJsonWs = true;

  constructor(project?: StateProjection<S>) {
    this.session = new SessionReplayBuilder<S>(project);
    this.jsonl = new JsonLinesReplayBuilder<S>(project);
  }

  push(chunk: string): void {
    let i = 0;
    const n = chunk.length;
    if (this.phase === P_LEAD) {
      while (i < n) {
        const c = chunk.charCodeAt(i);
        if (!isJsWhitespace(c)) break;
        if (c === C_LF) this.leadNewlines++;
        if (!isJsonWs(c)) this.leadNonJsonWs = true;
        i++;
      }
      if (i >= n) return;
      this.phase = chunk.charCodeAt(i) === C_LBRACE ? P_FIRST : P_UNKNOWN;
    }
    if (this.phase === P_FIRST) {
      const nl = chunk.indexOf('\n', i);
      if (nl < 0) {
        this.scanner.feed(chunk, i, n);
        return;
      }
      this.scanner.feed(chunk, i, nl);
      // 第一個換行:第一行是完整的 JSON 物件且 kind 為字串 → JSON Lines;否則是單一 JSON 文件
      if (this.firstLineIsObject() && typeof this.top['kind'] === 'string') {
        this.phase = P_LINES;
        this.firstLineJsonWs = this.scanner.trailingJsonWs;
        this.jsonl.addObject(this.top, this.leadNewlines);
        this.lineNo = this.leadNewlines + 1;
        i = nl + 1;
      } else {
        this.phase = P_JSON;
        i = nl;
      }
    }
    if (this.phase === P_JSON) this.scanner.feed(chunk, i, n);
    else if (this.phase === P_LINES) this.feedLines(chunk, i);
  }

  finish(): ReplayStreamResult<S> {
    switch (this.phase) {
      case P_FIRST: {
        // 全文沒有換行:必須是一個完整的 JSON 值(尾端只能有 JSON 空白)
        if (this.scanner.failed || !this.scanner.done || !this.scanner.trailingJsonWs) return { data: unrecognizedReplay(), jsonObject: false, jsonFormat: undefined };
        const jsonObject = !this.leadNonJsonWs;
        if (typeof this.top['kind'] === 'string') {
          this.jsonl.addObject(this.top, this.leadNewlines);
          return this.result(this.jsonl.finish(), jsonObject);
        }
        return this.result(jsonObject ? this.finishSession() : invalidJsonReplay(), jsonObject);
      }
      case P_JSON: {
        const ok = !this.leadNonJsonWs && !this.scanner.failed && this.scanner.done && this.scanner.trailingJsonWs;
        return this.result(ok ? this.finishSession() : invalidJsonReplay(), ok);
      }
      case P_LINES: {
        // 最後一行(結尾沒有換行時的殘餘;有換行時是空字串)
        const last = this.linePieces.join('');
        this.linePieces = [];
        this.lineDone(last);
        return this.result(this.jsonl.finish(), !this.leadNonJsonWs && this.firstLineJsonWs && this.restJsonWs);
      }
      default:
        return { data: unrecognizedReplay(), jsonObject: false, jsonFormat: undefined };
    }
  }

  private result(data: ReplayDataOf<S>, jsonObject: boolean): ReplayStreamResult<S> {
    return { data, jsonObject, jsonFormat: jsonObject ? this.top['format'] : undefined };
  }

  /** 第一個換行之前是否為完整的 JSON 物件(物件之後只有 trim() 會去掉的字元)。 */
  private firstLineIsObject(): boolean {
    return !this.scanner.failed && this.scanner.done && this.scanner.trailingJsWs;
  }

  private finishSession(): ReplayDataOf<S> {
    if (this.top['format'] !== SESSION_RECORD_FORMAT || this.statesKind !== 'array') return notSessionRecordReplay();
    if (this.pendingStates) {
      this.session.resetStates();
      this.pendingStates.forEach((raw, i) => this.session.addState(raw, i));
      this.pendingStates = null;
    }
    return this.session.finish(this.top as unknown as Omit<SessionRecord, 'states'>);
  }

  private feedLines(s: string, from: number): void {
    let start = from;
    for (;;) {
      const nl = s.indexOf('\n', start);
      if (nl < 0) break;
      let line = s.slice(start, nl);
      if (this.linePieces.length > 0) {
        this.linePieces.push(line);
        line = this.linePieces.join('');
        this.linePieces = [];
      }
      this.lineDone(line);
      start = nl + 1;
    }
    if (start < s.length) this.linePieces.push(s.slice(start));
  }

  private lineDone(line: string): void {
    if (this.restJsonWs && !/^[ \t\r]*$/.test(line)) this.restJsonWs = false;
    this.jsonl.addLine(line, this.lineNo++);
  }

  // ---- TopLevelSink
  member(key: string, value: unknown): void {
    if (key === 'states') {
      this.statesKind = 'other';
      this.pendingStates = null;
      this.statesLive = false;
      return;
    }
    this.top[key] = value;
  }

  arrayStart(key: string): void {
    if (key === 'states') {
      this.statesKind = 'array';
      this.statesLive = this.top['format'] === SESSION_RECORD_FORMAT;
      this.pendingStates = this.statesLive ? null : [];
      if (this.statesLive) this.session.resetStates();
      return;
    }
    this.curArray = [];
    this.top[key] = this.curArray;
  }

  arrayItem(key: string, index: number, value: unknown): void {
    if (key === 'states') {
      if (this.statesLive) this.session.addState(value, index);
      else this.pendingStates!.push(value);
      return;
    }
    this.curArray!.push(value);
  }

  arrayEnd(_key: string): void {
    this.curArray = null;
  }
}

/** 整份文字一次解析(已經有文字在手上時;結果與分段餵入相同)。 */
export function parseReplayText<S extends ReplayStateBase = OwnShipState>(text: string, project?: StateProjection<S>): ReplayStreamResult<S> {
  const p = new ReplayStreamParser<S>(project);
  p.push(text);
  return p.finish();
}

/** 分段讀取的預設大小(位元組) */
export const RECORD_CHUNK_BYTES = 1 << 20;

/**
 * 把 Blob / File 一段一段解碼成文字(UTF-8;開頭的 BOM 去掉、不合法的位元組換成 U+FFFD,與 Blob.text() 相同),
 * 各段依序交給 onChunk。每段之間等待讀取(讓出主執行緒),頁面不會整段卡住。
 */
export async function forEachBlobTextChunk(blob: Blob, onChunk: (text: string, bytesRead: number) => void, chunkBytes = RECORD_CHUNK_BYTES): Promise<void> {
  const decoder = new TextDecoder('utf-8');
  const size = blob.size;
  for (let offset = 0; offset < size; offset += chunkBytes) {
    const end = Math.min(size, offset + chunkBytes);
    const text = decoder.decode(await blob.slice(offset, end).arrayBuffer(), { stream: true });
    onChunk(text, end);
  }
  const tail = decoder.decode();
  if (tail) onChunk(tail, size);
}

export interface ParseReplayBlobOptions<S extends ReplayStateBase> {
  project?: StateProjection<S>;
  chunkBytes?: number;
  /** 每讀完一段呼叫一次(已讀位元組、總位元組) */
  onProgress?: (bytesRead: number, totalBytes: number) => void;
}

/** 分段讀取並解析一份紀錄(Blob / File):不把整份紀錄讀成一個字串。 */
export async function parseReplayBlob<S extends ReplayStateBase = OwnShipState>(blob: Blob, options: ParseReplayBlobOptions<S> = {}): Promise<ReplayStreamResult<S>> {
  const parser = new ReplayStreamParser<S>(options.project);
  await forEachBlobTextChunk(
    blob,
    (text, read) => {
      parser.push(text);
      options.onProgress?.(read, blob.size);
    },
    options.chunkBytes,
  );
  return parser.finish();
}
