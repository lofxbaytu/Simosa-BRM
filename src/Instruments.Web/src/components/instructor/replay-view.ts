// 重播檢視(講評站雛型,規劃書第 9.2 節「重播」與「航跡圖」):以檔案選擇器載入 SimCore 的 JSON Lines 紀錄
// 或教官站的 JSON 紀錄,時間軸拖曳檢視任一時刻的狀態(鳥瞰航跡圖 + conning 儀器摘要),列出輸入事件、核心事件與
// BRM 標記(若紀錄含),整段的講評摘要(核心 summary 優先,無則本地推導);不需核心連線。
// 設計文件第 6 節 W8:時間軸上畫事件標記(顏色依來源;hardFail/collision/grounding 紅)、航跡圖疊 header.scenario 的
// 航線走廊與區域、顯示 header.parameterSet 版次。時間軸另畫倒帶標記(ReplayData.rewinds[];懸停顯示倒帶 tick 與丟棄筆數),
// 航跡圖畫該時刻 state 的目標船(cpaAlarm 狀態由 t ≤ 目前時刻的事件推得)。
// 事件與教官站即時事件流是同樣的列(同一個碰墊的反覆接觸已合併,lib/event-merge.ts):合併列在時間軸上畫成第一次到最近一次的橫條。
//
// 讀檔(第三十一輪;lib/replay-states.ts):紀錄檔分段讀取(只開一次 Blob.stream();8 MB 以上在工具列顯示百分比,讀取中頁面仍可操作),
// 不把整份紀錄讀成一個字串。狀態只留顯示用的精簡投影、最多 REPLAY_MAX_STATES 筆:沒有超過的紀錄每筆都在(與原本相同;超不超過看練習最終的
// 歷史,倒帶前曾經多長不算);超過的(25 Hz 的核心紀錄約 5 分鐘以上)每 N 筆取 1 筆,資訊列之後寫明間隔,⏮ ⏭ 逐筆的間隔跟著變大,時間軸的
// 方向鍵也改走逐筆(滑桿一格 0.04 秒比留下的狀態密)。講評摘要、事件、輸入、標記、倒帶點、航跡與航跡圖上的事件位置都以**全部**狀態計算,
// 不受降採樣影響。又選了別的檔案時,先前還沒讀完的那一次直接中止(後開的紀錄不會被先開的蓋掉)。讀取失敗(含讀取途中檔案被截短)時原本的紀錄不動。
//
// 核心仍在寫入的紀錄(lib/record-source.ts;實測見 README「紀錄讀取」):瀏覽器在選取檔案的當下記下修改時間,開啟時不同就讀不到,而且同一個 File
// 再讀幾次都一樣。所以「紀錄檔」在瀏覽器支援時改開 showOpenFilePicker(拿到檔案 handle),紀錄檔也可以直接拖進來;這兩種來源讀取失敗時
// 重新取得檔案、從頭重讀(新的解析器與狀態存放),重讀的進度寫明是重新讀取。不支援的環境是一般的檔案輸入:不重讀,顯示中文說明(請重新選取一次)。
// 這兩種來源的讀取說明要等讀到第一段(檔案已經開啟)才更新:工具列的字每改一次,接下來主執行緒要忙 5–25 ms(那個畫格的工作),落在「取得檔案」與「開啟」之間時
// 檔案被改動的機會跟著變大(審查時實測要重讀的比例是講評報告頁的 4–6 倍,重讀也常常再失敗;見 loadSource)。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import { stateIndexAt, type ReplayRewind } from '../../lib/record-replay.js';
import { blobRecordSource, dataTransferHasFiles, readRecordSource, RecordFilePicker, recordSourcesFromDataTransfer, type RecordReadRetryOptions, type RecordSource } from '../../lib/record-source.js';
import { megabytesText, recordReadErrorText, RECORD_PROGRESS_MIN_BYTES } from '../../lib/record-stream.js';
import { loadReplayBlob, loadReplayText, REPLAY_MAX_STATES, replayDisplayState, type ReplayStateStats, type ReplayViewData } from '../../lib/replay-states.js';
import { TrackHistory, type TrackPoint } from '../../lib/track-history.js';
import { getShipConfig } from '../../lib/ship-config.js';
import type { DebriefSummary } from '../../lib/debrief.js';
import { brmCategory } from '../../lib/brm-markers.js';
import { defaultState } from '../../lib/state-parser.js';
import { eventLabel, eventSeverity } from '../../lib/sim-event.js';
import { rawEventCount } from '../../lib/event-merge.js';
import { eventMarksAt, overlayFromHeaderScenario, overlayHasContent, type PlotOverlay } from '../../lib/plot-overlay.js';
import { cpaAlarmTargets } from '../../lib/targets.js';
import type { SimEvent } from '../../types/event.js';
import type { OwnShipState } from '../../types/state.js';
import '../conning-bar.js';
import '../track-plot.js';
import './debrief-summary.js';
import { rowTime } from './event-stream.js';

const PLAY_RATES = [0.25, 1, 4, 16];

/** 讀取被後來的載入取代(loadSource 的 onProgress 擲出,中止分段讀取) */
class SupersededLoad extends Error {}

/** 檔案輸入的 accept 與檔案對話框的副檔名(同一份) */
const RECORD_EXTENSIONS = ['.jsonl', '.json', '.txt'] as const;

@customElement('brm-replay-view')
export class ReplayView extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: grid;
        grid-template-rows: auto auto minmax(0, 1fr);
        gap: 6px;
        height: 100%;
        min-height: 0;
      }
      .toolbar {
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
        font-size: 12px;
      }
      .toolbar .file {
        display: inline-flex;
        align-items: center;
        gap: 6px;
      }
      .toolbar .info {
        color: var(--ob-text-secondary);
      }
      .toolbar .error {
        color: var(--ob-warning);
      }
      /* 降採樣的說明:資訊列之後另起一項(寬度不夠時自己折到下一行),字色與資訊列相同、加粗 */
      .toolbar .decimated {
        color: var(--ob-text-secondary);
        font-weight: 600;
      }
      .timeline {
        display: grid;
        grid-template-columns: auto 1fr auto;
        gap: 8px;
        align-items: center;
      }
      .timeline .track {
        display: grid;
        grid-template-rows: 14px auto;
        gap: 2px;
      }
      .marks {
        position: relative;
        height: 14px;
        border-bottom: 1px solid var(--ob-border);
      }
      .marks .mark {
        position: absolute;
        top: 1px;
        width: 2px;
        height: 12px;
        transform: translateX(-1px);
        background: var(--ob-text-muted);
        cursor: pointer;
      }
      .marks .mark.timeline {
        background: var(--ob-accent);
      }
      .marks .mark.assessment {
        background: var(--ob-ok);
      }
      .marks .mark.traffic {
        background: var(--ob-caution);
      }
      .marks .mark.instructor {
        background: var(--ob-text-muted);
      }
      .marks .mark.warning {
        background: var(--ob-warning);
      }
      .marks .mark.alarm {
        background: var(--ob-alarm);
        width: 3px;
      }
      /* 合併列(同一個碰墊的反覆接觸):第一次到最近一次的橫條,畫在標記列下緣,不蓋住其他事件的直線標記 */
      .marks .mark.merged {
        top: 9px;
        height: 4px;
        transform: none;
        opacity: 0.55;
        border-radius: 2px;
      }
      .marks .mark.phase {
        top: 0;
        height: 14px;
        width: 1px;
        background: var(--ob-border-strong);
      }
      .marks .mark.marker {
        top: 8px;
        height: 5px;
        background: var(--ob-text);
      }
      .marks .mark.rewind {
        top: 0;
        height: 14px;
        width: 0;
        background: none;
        border-left: 3px double var(--ob-warning);
        transform: translateX(-2px);
      }
      .timeline input[type='range'] {
        width: 100%;
        min-height: 20px;
      }
      .timeline .t {
        font-variant-numeric: tabular-nums;
        font-weight: 600;
        min-width: 70px;
        text-align: center;
      }
      .main {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 360px;
        grid-template-rows: minmax(150px, 22vh) minmax(0, 1fr);
        gap: 6px;
        min-height: 0;
      }
      .main brm-conning-bar {
        grid-column: 1 / -1;
      }
      /* 寬度 ≤ 1900 px:conning 條折成兩列(conning-bar.ts 同一門檻),與教官站相同給 220 px / 30vh;
         原本 22vh(1366×768 約 169 px)時兩列方塊的下半(LON、航向刻度)被方塊邊界切掉 */
      @media (max-width: 1900px) {
        .main {
          grid-template-rows: minmax(220px, 30vh) minmax(0, 1fr);
        }
      }
      /* 右欄四個面板依比例分高度,各有下限:事件串流至少 140 px(標題、六個篩選鈕、清單至少 64 px;原本講評摘要先取 40vh,
         1366×768 的事件串流只剩 38 px,篩選鈕被下一個面板蓋住)、輸入事件 / BRM 標記各約 2 列、講評摘要至少 110 px 並在自己的面板內捲動。
         欄寬固定(minmax(0, 1fr):面板內容不撐寬欄);欄高連下限都放不下時整欄捲動(第二十九輪 L3) */
      .side {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: minmax(140px, 1fr) minmax(64px, 0.6fr) minmax(56px, 0.5fr) minmax(110px, 1fr);
        gap: 6px;
        min-height: 0;
        overflow-y: auto;
        scrollbar-width: thin;
      }
      .side brm-event-stream {
        min-height: 0;
      }
      .param-set {
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
      .side .panel {
        min-height: 0;
      }
      /* 講評摘要:面板高度由格列決定,摘要本身是捲動區(長的指標名稱在表格內換行,不撐寬面板) */
      .side .summary > * {
        flex: 0 0 auto;
      }
      .side .summary > brm-debrief-summary {
        flex: 1 1 auto;
        min-height: 0;
        overflow-y: auto;
        scrollbar-width: thin;
      }
      .item.current {
        background: var(--ob-accent-soft);
      }
      .empty {
        display: grid;
        place-items: center;
        height: 100%;
        color: var(--ob-text-secondary);
        border: 1px dashed var(--ob-border-strong);
        border-radius: 6px;
        font-size: 14px;
        text-align: center;
        padding: 12px;
      }
      .errors {
        color: var(--ob-warning);
        font-size: 11px;
        max-height: 60px;
        overflow: auto;
      }
    `,
  ];

  @property({ type: Boolean }) compact = false;
  /** 顯示用狀態的筆數上限(超過才降採樣;預設 REPLAY_MAX_STATES。之後載入的紀錄才套用) */
  @property({ attribute: false }) maxStates = REPLAY_MAX_STATES;

  @state() private data: ReplayViewData | null = null;
  @state() private fileName = '';
  @state() private t = 0;
  @state() private playing = false;
  @state() private rate = 1;
  @state() private summary: DebriefSummary | null = null;
  /** 讀取中的說明(空字串 = 沒有在讀取);8 MB 以上的檔案帶百分比 */
  @state() private loadingText = '';
  /** 最近一次讀取失敗的說明(讀取中不顯示;成功載入時清掉,再失敗時換成新的) */
  @state() private loadError = '';
  @state() private overlay: PlotOverlay | null = null;

  /**
   * 載入序號:每開始一次載入(loadText / loadBlob)加一。分段讀取期間頁面仍可操作,使用者可能在大檔還沒讀完時又選了別的檔案 ——
   * 較早開始的那一次在下一段就中止(不佔記憶體、也不會在讀完時蓋掉後開的紀錄)。
   */
  private loadSeq = 0;
  /** 目前顯示的那一筆狀態(精簡投影補成元件吃的 OwnShipState;同一筆不重做,元件也不因此重繪) */
  private shown: { data: ReplayViewData; index: number; state: OwnShipState } | null = null;
  private points: TrackPoint[] = [];
  private history = new TrackHistory(1, 100000);
  private timer: ReturnType<typeof setInterval> | null = null;
  private lastTickWall = 0;

  override connectedCallback(): void {
    super.connectedCallback();
    this.addEventListener('dragover', this.onDragOver);
    this.addEventListener('drop', this.onDrop);
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.removeEventListener('dragover', this.onDragOver);
    this.removeEventListener('drop', this.onDrop);
    this.stopPlay();
  }

  /** 由文字載入紀錄(測試與已在手上的文字;檔案走 loadBlob 分段讀取,結果相同)。 */
  loadText(text: string, name = ''): ReplayViewData {
    const data = loadReplayText(text, { maxStates: this.maxStates });
    this.loadSeq++;
    this.apply(data, name);
    return data;
  }

  /**
   * 分段讀取並載入一份紀錄(Blob / File;一般的檔案輸入選的檔案、測試):只開一次 Blob.stream(),讀取中頁面仍可操作,原本載入的紀錄留到新的讀完才換掉。
   * 回傳載入的資料;讀取失敗(說明顯示在工具列,原本的紀錄不動)或被後來的載入取代時回傳 null。這種來源讀取失敗時不重讀(見 loadSource)。
   */
  loadBlob(blob: Blob, name = ''): Promise<ReplayViewData | null> {
    return this.loadSource(blobRecordSource(blob, name));
  }

  /**
   * 載入一份紀錄(lib/record-source.ts 的來源)。可以重新取得檔案的來源(檔案對話框的 handle、拖放)讀取失敗時 —— 核心仍在寫入的紀錄偶爾在
   * 開啟的瞬間被改動 —— 重新取得檔案、從頭重讀:每次都是全新的 loadReplayBlob(新的解析器與狀態存放;上一次讀到一半的東西沒有人留著)。
   * 被後來的載入取代時不再重讀。retry:重讀的次數、等待與時鐘(由呼叫端注入;預設見 record-source.ts)。
   *
   * 工具列的讀取說明:
   *  - 一般的檔案(不能重新取得):馬上寫「讀取中…」,8 MB 以上的再換成百分比(與原本相同)。
   *  - 可以重新取得的來源:**讀到第一段之後才寫**(「讀取中…」或百分比),在那之前、以及一連串開啟就失敗的重讀之間,畫面都不更新。
   *    瀏覽器在 getFile() 的當下記下修改時間、開啟時核對,這兩件事之間主執行緒在忙多久,檔案被改動的機會就有多大;而工具列的字每改一次,
   *    Lit 的更新只要 0.1–0.3 ms,接下來那個畫格主執行緒卻要忙 5–25 ms(整個重播檢視跟著更新;沒有再細分是哪一部分),getFile() 的回覆
   *    排在它後面。實測(真實 Chromium,進行中的紀錄拖進 ?mode=replay,每 20 ms 附加一行):
   *    先寫「讀取中…」再取檔案時 300 次裡 50–62 次要重讀(講評報告頁 9–11 次),每次重讀前又改一次字,重讀也有四成再失敗;
   *    改成現在的寫法後 300 次裡 16 次要重讀(講評報告頁 9 次),都是重讀 1 次就讀到(其餘數字見 README「紀錄讀取」)。
   *  - 重讀的那幾次:進度寫「重新讀取 …(檔案在讀取時被改動,第 N 次重新讀取)」、從 0% 重新算(讀到一半才失敗時,原本的百分比留到重讀的第一段
   *    讀到為止,最多約 0.5 秒;不會無聲地跳回 0%)。開啟就失敗的那幾次什麼都還沒有顯示,不必說明。
   */
  async loadSource(source: RecordSource, retry: RecordReadRetryOptions = {}): Promise<ReplayViewData | null> {
    const seq = ++this.loadSeq;
    const name = source.name;
    if (!source.reopenable) this.loadingText = '讀取中…';
    try {
      const { value: data, blob } = await readRecordSource(
        source,
        (blob, attempt) => {
          const again = attempt > 1 ? `(檔案在讀取時被改動,第 ${attempt - 1} 次重新讀取)` : '';
          let shownPct = -1;
          // 這裡不可以先改字再讀(見上):loadReplayBlob 一呼叫就送出開啟,說明到 onProgress(已經讀到一段)才更新
          return loadReplayBlob(blob, {
            maxStates: this.maxStates,
            onProgress: (read, total) => {
              if (seq !== this.loadSeq) throw new SupersededLoad();
              if (total < RECORD_PROGRESS_MIN_BYTES) {
                // 小檔案不顯示百分比:只在第一段寫一次
                if (shownPct < 0) {
                  shownPct = 0;
                  this.loadingText = attempt > 1 ? `重新讀取 ${name}…${again}` : '讀取中…';
                }
                return;
              }
              const pct = Math.floor((read / total) * 100);
              if (pct === shownPct) return;
              shownPct = pct;
              this.loadingText = `${attempt > 1 ? '重新讀取' : '讀取'} ${name}… ${pct}%(共 ${megabytesText(total)} MB)${again}`;
            },
          });
        },
        { ...retry, isCurrent: () => seq === this.loadSeq },
      );
      if (seq !== this.loadSeq) return null;
      this.apply(data, name);
      this.reflectFileInput(blob);
      return data;
    } catch (err) {
      if (seq !== this.loadSeq || err instanceof SupersededLoad) return null;
      this.loadingText = '';
      this.loadError = `無法讀取「${name}」:${recordReadErrorText(err)}`;
      return null;
    }
  }

  /**
   * 檔案輸入旁邊由瀏覽器畫的檔名跟著目前的紀錄:檔案對話框(showOpenFilePicker)或拖放載入的檔案不是檔案輸入自己選的,
   * 不放進去的話紀錄已經載入了,它還寫著「未選擇任何檔案」。只是顯示用(不觸發 change);做不到的環境維持原樣。
   */
  private reflectFileInput(blob: Blob): void {
    const input = this.renderRoot.querySelector<HTMLInputElement>('input.file-input');
    if (!input || typeof File !== 'function' || !(blob instanceof File) || typeof DataTransfer !== 'function') return;
    if (input.files?.[0] === blob) return;
    try {
      const dt = new DataTransfer();
      dt.items.add(blob);
      input.files = dt.files;
    } catch {
      /* 維持原樣 */
    }
  }

  private apply(data: ReplayViewData, name: string): void {
    this.data = data;
    this.fileName = name;
    this.loadingText = '';
    this.loadError = '';
    this.points = data.track;
    this.summary = data.debrief;
    const base = overlayFromHeaderScenario(data.header.scenario);
    this.overlay = overlayHasContent(base) || data.events.length > 0 ? base : null;
    this.stopPlay();
    this.seek(data.tStart);
  }

  /** 跳至指定時刻(教官站事件流點擊、測試)。 */
  seekTo(t: number): void {
    this.stopPlay();
    this.seek(t);
  }

  /** 目前時刻 */
  get time(): number {
    return this.t;
  }

  /** 已載入紀錄 */
  get loaded(): boolean {
    return this.data !== null;
  }

  private async onFile(ev: Event): Promise<void> {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    await this.loadBlob(file, file.name);
  }

  /** 檔案對話框(showOpenFilePicker;不支援或用不了時退回檔案輸入自己的對話框) */
  private readonly picker = new RecordFilePicker();

  /**
   * 按「紀錄檔」:瀏覽器支援時改開 showOpenFilePicker —— 拿到的是檔案 handle,核心仍在寫入的紀錄讀取失敗時可以重新取得檔案再讀
   * (一般檔案輸入給的 File 做不到;見 lib/record-source.ts)。不支援時是檔案輸入自己的對話框:開之前清掉上一次的選擇,
   * 同一個檔案再選一次也會觸發 change(讀取失敗時要「重新選取一次」,或練習還在進行、想重讀同一份紀錄的最新內容)。
   */
  private onFileClick(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    input.value = '';
    if (!this.picker.available) return;
    ev.preventDefault();
    void this.pickRecord(input);
  }

  private async pickRecord(input: HTMLInputElement): Promise<void> {
    const sources = await this.picker.pick({ description: '練習紀錄', extensions: RECORD_EXTENSIONS });
    if (sources === null) {
      // 這個環境的 showOpenFilePicker 用不了(picker.available 之後是 false):改開檔案輸入自己的對話框
      input.click();
      return;
    }
    const source = sources[0];
    if (source) await this.loadSource(source);
  }

  /** 拖放紀錄檔:有檔案才接受(其他拖曳 —— 文字、時間軸 —— 不攔)。 */
  private readonly onDragOver = (ev: DragEvent): void => {
    if (!dataTransferHasFiles(ev.dataTransfer)) return;
    ev.preventDefault();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = 'copy';
  };

  private readonly onDrop = (ev: DragEvent): void => {
    if (!ev.dataTransfer || !dataTransferHasFiles(ev.dataTransfer)) return;
    ev.preventDefault();
    // 來源要在事件處理函式裡同步取出(事件結束後 DataTransfer 就不能取用)
    const pending = recordSourcesFromDataTransfer(ev.dataTransfer);
    void pending.then((sources) => (sources[0] ? this.loadSource(sources[0]) : null));
  };

  private displayState(d: ReplayViewData, index: number): OwnShipState {
    const c = this.shown;
    if (c && c.data === d && c.index === index) return c.state;
    const state = replayDisplayState(d.states[index]!);
    this.shown = { data: d, index, state };
    return state;
  }

  private seek(t: number): void {
    const d = this.data;
    if (!d) return;
    this.t = Math.min(d.tEnd, Math.max(d.tStart, t));
    this.history = new TrackHistory(1, 100000);
    for (const p of this.points) {
      if (p.t > this.t) break;
      this.history.push(p.x, p.y, p.t);
    }
    if (this.overlay) this.overlay = { ...this.overlay, events: eventMarksAt(d.events, d.eventPositions, this.t) };
  }

  private onEventSeek(ev: CustomEvent<SimEvent>): void {
    // 合併列跳到它時間欄寫的時刻(第一次接觸;與時間軸上的橫條相同),其餘事件就是事件的時刻
    this.seekTo(rowTime(ev.detail));
  }

  private renderMarks(d: ReplayViewData) {
    const span = d.tEnd - d.tStart;
    if (span <= 0) return nothing;
    const pct = (t: number) => `${((Math.min(d.tEnd, Math.max(d.tStart, t)) - d.tStart) / span) * 100}%`;
    const phases = d.summary?.phases ?? [];
    return html`<div class="marks" aria-label="事件標記">
      ${phases.slice(1).map((p) => html`<span class="mark phase" style="left:${pct(p.from_s)}" title=${`階段 ${p.phase} ${shortTime(p.from_s)}`}></span>`)}
      ${d.events.map((e) => {
        const m = e.merged;
        if (!m) return html`<span class="mark ${e.source} ${eventSeverity(e)}" style="left:${pct(e.t)}" title=${`${shortTime(e.t)} ${eventLabel(e)}`} @click=${() => this.seekTo(e.t)}></span>`;
        const width = `max(2px, calc(${pct(m.lastT)} - ${pct(m.firstT)}))`;
        return html`<span class="mark merged ${e.source} ${eventSeverity(e)}" style="left:${pct(m.firstT)};width:${width}" title=${eventLabel(e)} @click=${() => this.seekTo(m.firstT)}></span>`;
      })}
      ${d.markers.map((m) => html`<span class="mark marker" style="left:${pct(m.t)}" title=${`${shortTime(m.t)} ${brmCategory(m.category).label} ${m.note}`} @click=${() => this.seekTo(m.t)}></span>`)}
      ${d.rewinds.map((r) => html`<span class="mark rewind" style="left:${pct(r.t)}" title=${rewindTitle(r, d.header.dt)} @click=${() => this.seekTo(r.t)}></span>`)}
    </div>`;
  }

  private onSlide(ev: Event): void {
    this.stopPlay();
    this.seek(Number((ev.target as HTMLInputElement).value));
  }

  private togglePlay(): void {
    if (this.playing) this.stopPlay();
    else this.startPlay();
  }

  private startPlay(): void {
    const d = this.data;
    if (!d || d.states.length === 0) return;
    if (this.t >= d.tEnd) this.seek(d.tStart);
    this.playing = true;
    this.lastTickWall = performance.now();
    this.timer = setInterval(() => {
      const now = performance.now();
      const dt = ((now - this.lastTickWall) / 1000) * this.rate;
      this.lastTickWall = now;
      const next = this.t + dt;
      if (next >= d.tEnd) {
        this.seek(d.tEnd);
        this.stopPlay();
      } else this.seek(next);
    }, 100);
  }

  private stopPlay(): void {
    this.playing = false;
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private step(sign: 1 | -1): void {
    const d = this.data;
    if (!d || d.states.length === 0) return;
    this.stopPlay();
    const i = stateIndexAt(d.states, this.t);
    let j = Math.min(d.states.length - 1, Math.max(0, i + sign));
    // 往回:前一筆的時刻與目前這一筆相同時(凍結中核心每 200 ms 記一筆 t 不變的狀態),再往前找到時刻更早的那一筆。
    // 只退一筆的話時刻不變,顯示的又是同一段的最後一筆,永遠退不過凍結的那一段(第三十一輪審查 DATA-3;往後沒有這個問題:
    // i 已是該時刻的最後一筆,下一筆的時刻一定比較晚)。相鄰兩筆時刻不同的紀錄不受影響。
    if (sign < 0) while (j > 0 && d.states[j]!.t >= d.states[i]!.t) j--;
    this.seek(d.states[j]!.t);
  }

  /**
   * 時間軸的方向鍵:降採樣時改走逐筆(與 ⏮ ⏭ 相同)。滑桿一格是 0.04 秒,留下的狀態相隔 0.08–數秒,原生的方向鍵多數按下去畫面不會變
   * (第三十一輪審查 MEM-4:2 小時的紀錄要按 32 次才換一筆)。沒有降採樣的紀錄不攔截,方向鍵照舊一次 0.04 秒;Home / End / PageUp / PageDown 一律照舊。
   */
  private onSliderKey(ev: KeyboardEvent): void {
    const d = this.data;
    if (!d || d.stateStats.stride <= 1 || ev.altKey || ev.ctrlKey || ev.metaKey) return;
    const sign = ev.key === 'ArrowRight' || ev.key === 'ArrowUp' ? 1 : ev.key === 'ArrowLeft' || ev.key === 'ArrowDown' ? -1 : 0;
    if (sign === 0) return;
    ev.preventDefault();
    this.step(sign);
  }

  override render() {
    const d = this.data;
    const idx = d ? stateIndexAt(d.states, this.t) : -1;
    const current = d && idx >= 0 ? this.displayState(d, idx) : defaultState('FSB1');
    const cfg = getShipConfig(current.shipId);
    const stepNote = d ? stepIntervalText(d.stateStats) : '';
    return html`
      <div class="toolbar">
        <label class="file">
          <span>紀錄檔</span>
          <input class="file-input" type="file" accept=${`${RECORD_EXTENSIONS.join(',')},application/json`} @click=${this.onFileClick} @change=${this.onFile} />
        </label>
        ${this.loadingText ? html`<span class="info loading">${this.loadingText}</span>` : this.loadError ? html`<span class="error" role="alert">${this.loadError}</span>` : nothing}
        ${d
          ? html`<span class="info">
              ${this.fileName} · ${d.source === 'simcore-jsonl' ? 'SimCore JSON Lines' : '教官站紀錄'} · ${d.header.scenarioId ?? '(無情境)'}${d.header.scenarioName ? ` ${d.header.scenarioName}` : ''} ·
              ${d.header.shipId ?? '?'}${d.header.loading ? `/${d.header.loading}` : ''} · ${d.stateStats.total} 筆狀態 · ${d.inputs.length} 筆輸入 · ${eventCountText(d.events)} · ${d.markers.length} 筆標記
              ${d.header.dynamics ? ` · ${d.header.dynamics}` : ''}${d.footer?.stateHash ? ` · 雜湊 ${d.footer.stateHash.slice(0, 8)}…` : ''}
              ${d.header.parameterSet ? html`<span class="param-set"> · 參數集 ${d.header.parameterSet.id ?? ''} ${d.header.parameterSet.version ?? ''}${d.header.parameterSet.resolved.length > 0 ? `(已解析 ${d.header.parameterSet.resolved.length} 個引用)` : ''}</span>` : nothing}
              ${d.summary?.assessment.score !== undefined ? html`<span class="param-set"> · 評分 ${Math.round(d.summary.assessment.score)}${d.summary.assessment.passed !== undefined ? (d.summary.assessment.passed ? '(通過)' : '(未通過)') : ''}</span>` : nothing}
            </span>${d.stateStats.stride > 1 ? html`<span class="decimated">${decimationText(d.stateStats)}</span>` : nothing}`
          : html`<span class="info">選擇 SimCore 的 .jsonl 紀錄(src/SimCore/README.md 第 4 節)或教官站下載的 .json 紀錄(也可以把檔案拖進這個畫面)</span>`}
      </div>
      ${d && d.states.length > 0
        ? html`<div class="timeline">
            <span class="btn-row">
              <button @click=${() => this.step(-1)} title=${`上一筆${stepNote}`}>⏮</button>
              <button class="play" @click=${this.togglePlay}>${this.playing ? '暫停' : '播放'}</button>
              <button @click=${() => this.step(1)} title=${`下一筆${stepNote}`}>⏭</button>
              <select .value=${String(this.rate)} @change=${(e: Event) => (this.rate = Number((e.target as HTMLSelectElement).value))}>
                ${PLAY_RATES.map((r) => html`<option value=${r} ?selected=${r === this.rate}>×${r}</option>`)}
              </select>
            </span>
            <div class="track">
              ${this.renderMarks(d)}
              <input class="slider" type="range" min=${d.tStart} max=${d.tEnd} step="0.04" .value=${String(this.t)} @input=${this.onSlide} @keydown=${this.onSliderKey} aria-label="時間軸" />
            </div>
            <span class="t">${shortTime(this.t)} / ${shortTime(d.tEnd)}</span>
          </div>`
        : html`<div class="hint">${d ? '紀錄沒有狀態資料' : ''}</div>`}
      ${d && d.states.length > 0
        ? html`<div class="main">
            <brm-conning-bar .state=${current} .config=${cfg} .issues=${[]} .noData=${false}></brm-conning-bar>
            <brm-track-plot .state=${current} .config=${cfg} .history=${this.history} .noData=${false} .overlay=${this.overlay} .targets=${current.targets} .alarmTargets=${cpaAlarmTargets(d.events, this.t)} ?target-table=${!this.compact}></brm-track-plot>
            <div class="side">
              <brm-event-stream .events=${d.events} @event-seek=${this.onEventSeek}></brm-event-stream>
              <div class="panel">
                <div class="panel-title"><span>輸入事件 INPUTS</span><span class="zh">${d.inputs.length} 筆 · 點選跳至</span></div>
                <div class="list" aria-label="輸入事件">
                  ${d.inputs.length === 0 ? html`<div class="hint">紀錄中沒有輸入事件</div>` : nothing}
                  ${d.inputs.map(
                    (i) => html`<div class="item ${isCurrent(i.t, this.t, d) ? 'current' : ''}" @click=${() => (this.stopPlay(), this.seek(i.t))}>
                      <span class="time">${shortTime(i.t)}</span>
                      <span class="text">${i.command.type}${i.command.value !== undefined ? ` ${typeof i.command.value === 'string' ? i.command.value : JSON.stringify(i.command.value)}` : ''}${i.command.args ? ` ${JSON.stringify(i.command.args)}` : ''}</span>
                      ${i.ack ? html`<span class=${i.ack.ok ? 'ok' : 'bad'} title=${i.ack.detail ?? ''}>${i.ack.ok ? 'OK' : '拒絕'}</span>` : nothing}
                    </div>`,
                  )}
                </div>
              </div>
              <div class="panel">
                <div class="panel-title"><span>BRM 標記 MARKERS</span><span class="zh">${d.markers.length} 筆</span></div>
                <div class="list" aria-label="BRM 標記">
                  ${d.markers.length === 0 ? html`<div class="hint">${d.source === 'simcore-jsonl' ? 'SimCore 紀錄不含 BRM 標記(標記在教官站紀錄內)' : '紀錄中沒有標記'}</div>` : nothing}
                  ${d.markers.map(
                    (m) => html`<div class="item" @click=${() => (this.stopPlay(), this.seek(m.t))}>
                      <span class="time">${shortTime(m.t)}</span>
                      <span class="text"><b>${brmCategory(m.category).label}</b>${m.polarity ? (m.polarity === 'plus' ? ' ＋' : ' Δ') : ''} ${m.note}</span>
                    </div>`,
                  )}
                  ${d.operations.length > 0 ? html`<div class="hint">另有 ${d.operations.length} 筆學員操作(教官站推導)</div>` : nothing}
                </div>
              </div>
              <div class="panel summary">
                <div class="panel-title"><span>講評摘要 DEBRIEF</span><span class="zh">${d.summary ? `核心 summary${d.summary.final ? '(定稿)' : ''}` : '本地推導(紀錄無核心 summary)'}</span></div>
                <brm-debrief-summary .summary=${this.summary} .coreSummary=${d.summary}></brm-debrief-summary>
                ${d.errors.length > 0 ? html`<div class="errors">略過 ${d.errors.length} 行:${d.errors.slice(0, 5).join(';')}${d.errors.length > 5 ? '…' : ''}</div>` : nothing}
                ${Object.keys(d.unknownKinds).length > 0 ? html`<div class="hint">未知的行種類:${Object.entries(d.unknownKinds).map(([k, n]) => `${k} ×${n}`).join('、')}</div>` : nothing}
              </div>
            </div>
          </div>`
        : html`<div class="empty">載入紀錄檔後,以時間軸拖曳檢視任一時刻的航跡與儀器</div>`}
    `;
  }
}

/** 倒帶標記的懸停文字:倒帶 tick(restore 要求的 tick;舊紀錄無此欄時由 t / dt 推得)、來源時刻與丟棄的舊分支筆數。 */
export function rewindTitle(r: ReplayRewind, dt: number): string {
  const tick = Number.isFinite(r.tick) ? r.tick : Math.round(r.t / (dt > 0 ? dt : 0.02));
  return `倒帶至 ${shortTime(r.t)}(tick ${tick})← 自 ${shortTime(r.from)};丟棄 ${r.droppedStates} 筆狀態、${r.droppedEvents} 筆事件`;
}

/** 「N 筆事件」;有合併列(同一個碰墊的反覆接觸)時另寫合併前的事件數。 */
export function eventCountText(events: readonly SimEvent[]): string {
  const raw = rawEventCount(events);
  return `${events.length} 筆事件${raw > events.length ? `(合併前 ${raw})` : ''}`;
}

/** 秒數的顯示文字:10 秒以上取整數,以下最多兩位小數(去掉尾端的 0)。 */
function secondsText(s: number): string {
  return s >= 10 ? String(Math.round(s)) : String(Number(s.toFixed(2)));
}

/**
 * 降採樣的說明(資訊列之後另起一項;沒有降採樣時空字串,資訊列與原本完全相同):不得有無聲的降採樣 —— 寫明每幾筆取 1 筆、
 * 大約幾秒一筆、逐筆的間隔跟著變大,以及哪些不受影響。間隔(interval_s)是時刻有前進的相鄰兩筆的中位數;留下的狀態時刻全部相同
 * (整份紀錄都在凍結中)時是 0,那時不寫秒數。
 */
export function decimationText(st: ReplayStateStats): string {
  if (st.stride <= 1) return '';
  const n = (v: number) => v.toLocaleString('en-US');
  const every = st.interval_s > 0 ? `約每 ${secondsText(st.interval_s)} 秒一筆,` : '';
  // 最終筆數不到上限卻有降採樣:只會發生在時刻不依序的損壞紀錄(倒帶時丟的不是尾端,replay-states.ts ReplayHistoryStore.dropAfter)—— 不寫「超過」
  const why = st.total > st.maxStates ? `狀態筆數超過 ${n(st.maxStates)}` : `紀錄裡狀態的時刻不是依序遞增(倒帶前超過 ${n(st.maxStates)} 筆)`;
  return `${why}:畫面每 ${st.stride} 筆取 1 筆(${every}⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 ${n(st.total)} 筆計算`;
}

/** 逐筆鈕的補充(降採樣時「(約 1.28 秒)」,間隔不明時「(每 32 筆)」;沒有降採樣時空字串)。 */
export function stepIntervalText(st: ReplayStateStats): string {
  if (st.stride <= 1) return '';
  return st.interval_s > 0 ? `(約 ${secondsText(st.interval_s)} 秒)` : `(每 ${st.stride} 筆)`;
}

/** 該輸入事件是否為目前時刻之前最後一筆(清單高亮) */
function isCurrent(t: number, now: number, d: ReplayViewData): boolean {
  let last: number | null = null;
  for (const i of d.inputs) if (i.t <= now) last = i.t;
  return last !== null && last === t;
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-replay-view': ReplayView;
  }
}
