// 練習紀錄的分段解析(講評報告頁,第三十一輪起教官站的重播檢視也用:lib/replay-states.ts;規劃書第 9.2 節、第 14 章「記憶體不持續成長」):
// 2 小時的教官站紀錄約 25 MB(版本 4 以前約 55 MB)、核心 JSON Lines 紀錄約 0.5–1 GB。整份讀成一個字串再 JSON.parse 時,
// 頁面同時持有全文(非 ASCII 內容時每字元 2 位元組)、整棵原始物件樹與解析後的狀態:實測 7200 筆 E02 的教官站紀錄尖峰約
// +250–300 MB,30 分鐘的核心紀錄(337 MB)約 +2.4 GB,2 小時的核心紀錄(1.35 GB)分頁直接當掉(數字見 README「紀錄讀取」)。
//
// 這裡改為一段一段讀(Blob.stream() → TextDecoder 串流解碼;每段最多 1 MB)並逐筆解析:
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
import { JsonLinesReplayBuilder, SessionReplayBuilder, invalidJsonReplay, notSessionRecordReplay, unrecognizedReplay, type ReplayDataOf, type ReplayStateBase, type ReplayStateStoreFactory, type StateProjection } from './record-replay.js';
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
 * project:狀態投影(省略 = 保留完整狀態)。stores:自備的狀態存放(record-replay.ts ReplayStateStore;給了就不用 project)——
 * 兩種格式的建構器各取一個,只有產生結果的那一個會被 finish()(重播檢視:lib/replay-states.ts)。
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

  constructor(project?: StateProjection<S>, stores?: ReplayStateStoreFactory<S>) {
    this.session = new SessionReplayBuilder<S>(stores ? stores() : project);
    this.jsonl = new JsonLinesReplayBuilder<S>(stores ? stores() : project);
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
export function parseReplayText<S extends ReplayStateBase = OwnShipState>(text: string, project?: StateProjection<S>, stores?: ReplayStateStoreFactory<S>): ReplayStreamResult<S> {
  const p = new ReplayStreamParser<S>(project, stores);
  p.push(text);
  return p.finish();
}

/** 一段文字的位元組上限(預設 1 MB);瀏覽器一次給的資料比它大時再切開 */
export const RECORD_CHUNK_BYTES = 1 << 20;

/** 連續解碼 / 解析這麼久(毫秒)就讓出主執行緒一次(見 forEachBlobTextChunk) */
export const RECORD_YIELD_MS = 50;

function nowMs(): number {
  return typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now();
}

/**
 * 讓出主執行緒一次:排一個巨集任務(MessageChannel;沒有時 setTimeout 0),等它執行後才繼續 —— 瀏覽器在這中間處理輸入、
 * 跑計時器並把畫面(讀取進度)畫出來。不用 setTimeout:連續呼叫五層之後瀏覽器把延遲墊到 4 ms。
 */
export function yieldToEventLoop(): Promise<void> {
  return new Promise((resolve) => {
    if (typeof MessageChannel === 'function') {
      const channel = new MessageChannel();
      channel.port1.onmessage = () => {
        channel.port1.close();
        resolve();
      };
      channel.port2.postMessage(0);
    } else setTimeout(resolve, 0);
  });
}

// ---- 讀取端的失敗(瀏覽器讀不到檔案)與解析端的例外要分得開:前者可以換一份新取得的檔案重讀(lib/record-source.ts),後者是內容或程式的事,重讀也一樣。
// 不另包一層錯誤物件(呼叫端與測試看到的仍是瀏覽器原本的錯誤),只把錯誤物件記在這個集合裡。
const readFailures = new WeakSet<object>();

/** 把 err 記成「讀取端的失敗」並回傳(不是物件的錯誤值包成 Error 才記得住)。 */
export function markRecordReadFailure(err: unknown): unknown {
  const e = typeof err === 'object' && err !== null ? err : new Error(String(err));
  readFailures.add(e);
  return e;
}

/**
 * err 是不是讀取端的失敗:Blob.stream() 的 read() 被拒絕、讀到的位元組比檔案大小少、(沒有 stream 時)slice 讀取被拒絕、
 * 或重新取得檔案失敗(lib/record-source.ts)。onChunk / onProgress / 解析器擲出的例外不是。
 */
export function isRecordReadFailure(err: unknown): boolean {
  return typeof err === 'object' && err !== null && readFailures.has(err);
}

/**
 * 把 Blob / File 一段一段解碼成文字(UTF-8;開頭的 BOM 去掉、不合法的位元組換成 U+FFFD,與 Blob.text() 相同),
 * 各段依序交給 onChunk(文字、已讀位元組)。
 *
 * 讓出主執行緒:`await reader.read()` 本身**不保證**讓出 —— 檔案已在快取、磁碟讀得比解析快時,資料一直是備妥的,等待只經過微任務,
 * 整個讀取成為一個長任務(實測 Chromium:337 MB 的核心紀錄全程 2.4 s 中頁面停住 2.2 s,計時器與畫面都不動,進度只畫得出「100%」)。
 * 因此每累計 yieldEveryMs 毫秒的工作就明確讓出一次(yieldToEventLoop);0 = 每段之後都讓出,Infinity = 不讓出。
 *
 * 以 Blob.stream() 讀:整個檔案只開啟一次。瀏覽器(實測 Chromium 153)在使用者選取檔案的當下記下檔案的修改時間,之後**每次開啟**都核對:
 *  - 開啟時修改時間與選取當下不同 → 第一次 read() 就以 `TypeError: network error` 失敗,一個位元組都不交出(FileReader 那一系 ——
 *    Blob.text() / arrayBuffer() / slice().arrayBuffer() —— 同樣的情形是 NotReadableError;檔案已經不在時 stream 仍是 TypeError: network error,
 *    FileReader 那一系是 NotFoundError)。同一個 File 物件之後再讀幾次都一樣失敗(修改時間的紀錄不會更新),要重新向瀏覽器取得檔案才讀得到。
 *  - 開啟成功之後檔案再被附加(核心仍在寫入的紀錄)不受影響:讀到 blob.size(第一次取用 size 當下的大小)為止。
 *  所以不能改成每段各自 Blob.slice().arrayBuffer():每一段都是一次新的開啟,核心仍在寫入的紀錄第二段起就讀不到。沒有 stream() 的環境才退回 slice。
 *  核心仍在寫入的紀錄因此**偶爾**在第一次 read() 失敗(選取到開啟之間約 1 毫秒,剛好有一次寫入落在裡面;失敗率 ≈ 這段時間 ÷ 寫入的間隔,
 *  數字見 README「紀錄讀取」)—— 由 lib/record-source.ts 換一份新取得的檔案重讀。
 *
 * 讀到的位元組比 blob.size 少 = 讀取失敗:檔案在讀取途中被截短(別的程序把它覆寫成較短的內容)時,Chromium 的 stream **不報錯**,
 * 交出一部分就結束(實測:337 MB 的紀錄讀到 20% 時截成 4 KB,stream 交出 73 MB 後 done、沒有 error;原本一次 file.text() 會以
 * NotReadableError 失敗)。不核對的話半份紀錄會被當成讀完的整份(30 分鐘的練習顯示成 6 分半;第三十一輪審查 MEM-2)。
 * 這時擲出 name 為 NotReadableError 的錯誤(recordReadErrorText 有中文說明)。
 * 只核對 stream 這條路:退回 slice 的那條路,瀏覽器在檔案被改動時本來就會以 NotReadableError 失敗(見上)。
 *
 * 讀取端的失敗(read() 被拒絕、位元組不足、slice 讀取被拒絕)都記成 isRecordReadFailure;onChunk 擲出的例外原樣往外丟、不記。
 */
export async function forEachBlobTextChunk(blob: Blob, onChunk: (text: string, bytesRead: number) => void, chunkBytes = RECORD_CHUNK_BYTES, yieldEveryMs = RECORD_YIELD_MS): Promise<void> {
  const decoder = new TextDecoder('utf-8');
  const size = blob.size;
  let sliceStart = nowMs();
  /** 一段處理完之後:這一輪已經連續工作超過 yieldEveryMs 就讓出一次 */
  const breathe = async (): Promise<void> => {
    if (nowMs() - sliceStart < yieldEveryMs) return;
    await yieldToEventLoop();
    sliceStart = nowMs();
  };
  if (typeof blob.stream === 'function') {
    let reader: ReadableStreamDefaultReader<Uint8Array>;
    try {
      reader = blob.stream().getReader();
    } catch (err) {
      throw markRecordReadFailure(err);
    }
    let read = 0;
    try {
      for (;;) {
        let step: ReadableStreamReadResult<Uint8Array>;
        try {
          step = await reader.read();
        } catch (err) {
          throw markRecordReadFailure(err);
        }
        if (step.done) break;
        const value = step.value;
        for (let offset = 0; offset < value.byteLength; offset += chunkBytes) {
          const part = offset === 0 && value.byteLength <= chunkBytes ? value : value.subarray(offset, offset + chunkBytes);
          read += part.byteLength;
          onChunk(decoder.decode(part, { stream: true }), read);
          await breathe();
        }
      }
    } catch (err) {
      // onChunk(解析)擲出例外時停止讀取;讀取本身的錯誤照樣往外丟
      await reader.cancel().catch(() => undefined);
      throw err;
    }
    if (read < size) throw markRecordReadFailure(shortReadError(read, size));
  } else {
    for (let offset = 0; offset < size; offset += chunkBytes) {
      const end = Math.min(size, offset + chunkBytes);
      let buffer: ArrayBuffer;
      try {
        buffer = await blob.slice(offset, end).arrayBuffer();
      } catch (err) {
        throw markRecordReadFailure(err);
      }
      onChunk(decoder.decode(buffer, { stream: true }), end);
      await breathe();
    }
  }
  const tail = decoder.decode();
  if (tail) onChunk(tail, size);
}

/** 讀到的位元組比檔案大小少(讀取途中檔案被截短):與瀏覽器讀不到檔案時相同的錯誤名稱,呼叫端以 recordReadErrorText 顯示說明。 */
function shortReadError(read: number, size: number): Error {
  const err = new Error(`檔案只讀到 ${read} / ${size} 位元組(讀取途中檔案被截短或改動)`);
  err.name = 'NotReadableError';
  return err;
}

export interface ParseReplayBlobOptions<S extends ReplayStateBase> {
  project?: StateProjection<S>;
  /** 自備的狀態存放(給了就不用 project;見 ReplayStreamParser) */
  stores?: ReplayStateStoreFactory<S>;
  /** 一段文字的位元組上限(預設 RECORD_CHUNK_BYTES) */
  chunkBytes?: number;
  /** 連續工作多久(毫秒)讓出主執行緒一次(預設 RECORD_YIELD_MS;0 = 每段之後都讓出,Infinity = 不讓出) */
  yieldEveryMs?: number;
  /** 每讀完一段呼叫一次(已讀位元組、總位元組);擲出例外即停止讀取(parseReplayBlob 以同一個例外拒絕) */
  onProgress?: (bytesRead: number, totalBytes: number) => void;
}

/** 分段讀取並解析一份紀錄(Blob / File):不把整份紀錄讀成一個字串;讀取中定時讓出主執行緒(頁面不會整段停住,進度畫得出來)。 */
export async function parseReplayBlob<S extends ReplayStateBase = OwnShipState>(blob: Blob, options: ParseReplayBlobOptions<S> = {}): Promise<ReplayStreamResult<S>> {
  const parser = new ReplayStreamParser<S>(options.project, options.stores);
  await forEachBlobTextChunk(
    blob,
    (text, read) => {
      parser.push(text);
      options.onProgress?.(read, blob.size);
    },
    options.chunkBytes,
    options.yieldEveryMs,
  );
  return parser.finish();
}

/** 讀取中顯示進度的檔案大小下限(位元組;小檔案一閃而過,不顯示)。講評報告頁與重播檢視共用。 */
export const RECORD_PROGRESS_MIN_BYTES = 8 * 1024 * 1024;

/** 檔案在選取後被改動(或讀不到)的說明:教官要做的事是重新選取一次,或等練習結束。 */
export const RECORD_CHANGED_TEXT = '檔案在選取後被改動或無法讀取(核心仍在寫入這份紀錄時請重新選取一次,或待練習結束後再開啟)';
/** 檔案已經不在原來的位置的說明。 */
export const RECORD_GONE_TEXT = '找不到檔案(已被刪除、改名或移走,或所在的磁碟已移除):請確認檔案還在之後重新選取一次';
/** 讀取失敗但認不出原因時的說明(後面附原始錯誤)。 */
export const RECORD_UNREADABLE_TEXT = '無法讀取檔案(請重新選取一次;核心仍在寫入這份紀錄時也可以待練習結束後再開啟)';

function errorName(err: unknown): string {
  const name = typeof err === 'object' && err !== null ? (err as { name?: unknown }).name : undefined;
  return typeof name === 'string' ? name : '';
}

/**
 * 讀檔失敗的說明(講評報告頁與重播檢視共用;顯示在「無法載入「檔名」:」之後)。瀏覽器的原始錯誤是英文而且看不出原因
 * (Chromium 的 stream 讀不到檔案時一律是「TypeError: network error」),教官要知道的是怎麼做:
 *  - 檔案在選取後被改動:NotReadableError(FileReader 那一系、讀到的位元組比檔案大小少),或讀取端的 `TypeError: network error`
 *    (Blob.stream();檔案被刪除、改名、所在的磁碟被移除時 stream 也是這個錯誤,分不出來,所以說明寫「被改動或無法讀取」)→ 請重新選取一次,或待練習結束後再開啟;
 *  - 檔案不在了:NotFoundError(重新取得檔案時、FileReader 那一系);
 *  - 重讀到上限仍讀不到(RecordReadRetryError,lib/record-source.ts):寫明重讀了幾次;
 *  - 其他讀取端的失敗(認不出來):中文說明之後附原始錯誤,方便回報問題;
 *  - 不是讀取端的失敗(解析器或程式的例外):照原樣顯示。
 */
export function recordReadErrorText(err: unknown): string {
  const name = errorName(err);
  if (name === 'RecordReadRetryError') {
    const e = err as { attempts?: unknown; cause?: unknown };
    const cause = recordReadErrorText(e.cause);
    const raw = cause === RECORD_CHANGED_TEXT ? '' : `;最後一次:${cause}`;
    return `檔案持續被改動,重新讀取 ${Number(e.attempts) - 1} 次後仍讀不到(請待練習結束後再開啟,或稍後重新選取一次)${raw}`;
  }
  if (name === 'NotReadableError') return RECORD_CHANGED_TEXT;
  if (name === 'NotFoundError') return RECORD_GONE_TEXT;
  if (!isRecordReadFailure(err)) return String(err);
  if (name === 'TypeError' && /network error/i.test(String((err as { message?: unknown }).message ?? ''))) return RECORD_CHANGED_TEXT;
  return `${RECORD_UNREADABLE_TEXT};原始錯誤:${String(err)}`;
}

/** 位元組 → MB 的顯示文字(10 MB 以下一位小數)。 */
export function megabytesText(bytes: number): string {
  return (bytes / (1024 * 1024)).toFixed(bytes < 10 * 1024 * 1024 ? 1 : 0);
}
