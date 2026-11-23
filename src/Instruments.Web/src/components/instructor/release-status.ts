// <brm-release-status>:教官站頁首的發行版本與降版提醒(完整與簡化模式;駕駛台頁不放 — 學員不需要)。
// 資料來源見 lib/release-status.ts:本機(提供本頁面的電腦)取同源的 /_status/release.json;頁面與核心不在同一台電腦時
// (教官筆電以 start-instructor-laptop 開的教官站:頁面是教官筆電自己的 simosa-web 提供的,核心在船上筆電)另外跨來源取**船上筆電**的。
// 兩邊都取不到時整個元件不顯示任何東西。
//
// 只有本機的狀態(同一台電腦、或船上的還沒取到 / 取不到):
//  - 沒有降版:小字「發行版 <版本>」(不是按鈕;來源與取得時間在 title)。發行版是安裝程式的版本,套用軟體更新包不會改它:
//    套用過比發行版新的軟體更新包時(實際執行的是更新包的程式)改寫成「軟體更新 <版次>」—— 頁首寫的是**實際執行的那一版**,
//    發行版在 title。不把兩個版本並排:「發行版 X · 軟體更新 Y」比「發行版 X」寬約 125 px,簡化模式簡報頁 1366×768 的頁首會多折一行
//    (36 → 65.6 px),同儕評估提示的最後一條被擠出簡報框 22 px(端對端實測);船上靠更新包升級是常態,不能讓常態的版面比較差。
//  - 有降版(simosa-pkg status 的 releaseDowngrade):警示色的徽章鈕「發行版 <版本> · 降版提醒」,一直留在頁首、不擋操作;
//    按一下在頁首下方展開說明(重點、建議的處理、status 的完整訊息、資料來源),再按一下、按「收合」、按 Esc 或點說明以外的地方收起
//    (展開的說明會蓋住頁首下方的控制項:簡化模式執行階段的 BRM 標記鈕與航跡圖的鈕在它下面,點別處就收起,不必先找「收合」)。
//  - 應該有船上的(shipWebUrl 有值)而沒取到:畫面同上,不顯示錯誤;title 多一句「船上筆電的版本:未取得(…)」,展開的說明只加短句
//    「船上筆電的版本:未取得」(長的說明會讓 1093×614 的說明多折兩行、蓋住控制項)。船上連得到但沒有發行版狀態(船上是開發模式,
//    或船上啟動時 simosa-pkg status 沒有取到)時寫「船上筆電沒有發行版狀態」,不寫未取得。
//
// 取到船上筆電的狀態(第三十一輪;頁首的主要資訊改成**船上筆電**實際執行的版本,本機的在 title 與展開的說明):
//  - 兩台實際執行的版本相同、都沒有降版:小字「船上 <版本>」(不是按鈕)。只寫版本,不寫「發行版 / 軟體更新」(在 title):
//    「船上 2026.11.31」比只有本機時的「軟體更新 2026.11.31」窄,頁首不會因此多折一行(版面矩陣見 docs/design/web-layout.md 第 2.2 節)。
//  - 有任何一項要提醒:警示色的徽章鈕「船上 <版本> · <標記>」,標記是 兩台降版 / 船上降版 / 本機降版 / 版本不同(取最前面成立的一項);
//    展開的說明依序寫船上筆電、本機、版本不同(哪一台較舊與建議的處理)。本機沒有發行版狀態(例:教官筆電是開發模式)時只寫船上的。
//    各台 status 的完整訊息預設收合(<details>「完整訊息(… 的 simosa-pkg status)」;重點與處理已經寫在上面):兩台的完整訊息都攤開時
//    說明在 1093×614 高 320 px,蓋住執行控制的「凍結 / 恢復 / 重設 / 結束練習」(審查實測)。
//  - 什麼時候取:連上核心時(shipLinked 由 false 變 true;每次重新連上都重取 — 船上可能套用更新後重新啟動過)。取不到時依
//    SHIP_RETRY_DELAYS_MS 重試(連上後約 4、19、49、109、169 秒;船上的啟動腳本在核心就緒之後還要啟動操控台、等雷達、取發行版狀態,
//    最慢約 36 秒後才啟動網頁伺服),仍取不到就當成沒有,等下一次重新連上。船上回「沒有發行版狀態」時不重試。斷線期間保留上一次取到的內容。
//
// 排版:host 是 display: contents — 徽章直接是頁首(flex、可換行)的排版項目。展開的說明是**浮貼**在頁首下方的一塊(position: absolute,
//   相對於頁首:使用它的頁首必須是 position: relative),與頁首同寬、不佔版面高度:1093×614 的教官站各列都已經在最小高度,
//   說明若佔一列(約 170–210 px)會把整個版面推出視窗、控制項捲不到(第三十輪量測;docs/design/web-layout.md 第 2.2 節)。
//   預設收合;展開時會蓋住頁首下方的一部分畫面,收合後還原。說明最高 min(60vh, 320px),超過時在說明裡捲動;最後一列(資料來源與「收合」鈕)
//   貼在說明的底邊(position: sticky),不必捲到底才找得到「收合」。
//   使用它的頁面可以給 detailLimit(回傳視窗座標 y 的函式):說明的底邊不超過那個位置(最高再縮到那裡,內容在說明裡捲動)。完整教官站給的是
//   執行控制第一個鈕的上緣 —— 說明展開時「凍結 / 恢復 / 重設 / 結束練習」一定點得到(1093×614 的頁首下方到執行控制只有 264 px)。

import { LitElement, html, css, nothing, type PropertyValues, type TemplateResult } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import {
  SHIP_NO_STATUS,
  compareFleet,
  downgradeSummary,
  formatStatusTime,
  mismatchSummary,
  releaseStatusProvider,
  runningDescription,
  runningLabel,
  runningVersion,
  type ReleaseStatus,
  type ShipStatusResult,
} from '../../lib/release-status.js';

/**
 * 取不到船上的狀態時的重試間隔(ms;第 1、2、… 次重試之前各等多久):連上核心後約 4、19、49、109、169 秒各試一次(連同第一次最多 6 次),之後不再試。
 * 船上啟動時核心比網頁伺服早就緒,教官站已經開著時第一次會取不到。啟動腳本(tools/launch/start-ship-laptop.*)在核心就緒之後依序啟動操控台服務
 * (1 秒)、等雷達(最多 15 秒)、取發行版狀態(simosa-pkg status,最多 20 秒)才啟動網頁伺服:最慢約 36 秒加上各程序的啟動時間 —— 只試到 19 秒的話,
 * 慢的筆電上就一直看不到船上的版本(審查發現)。不無限重試:船上是沒有這個網址 / 沒有 CORS 標頭的舊版時,每次失敗瀏覽器都在主控台記 1–2 筆錯誤。
 */
export const SHIP_RETRY_DELAYS_MS: readonly number[] = [4000, 15000, 30000, 60000, 60000];

/** 展開的說明與 detailLimit 之間留的空隙(px) */
export const DETAIL_LIMIT_GAP_PX = 4;
/** 有 detailLimit 時說明的最小高度(px):頁首折成很多行、位置太低時不再縮(那時寧可蓋住下面,也要讀得到說明與「收合」) */
export const DETAIL_MIN_HEIGHT_PX = 120;
/** 說明的最大高度(px;樣式表另有 60vh 的上限) */
export const DETAIL_MAX_HEIGHT_PX = 320;

/** 「本機是哪一台」:頁面由誰提供就是誰(網址列的主機);本機位址另外註明 */
function localHostText(): string {
  const host = typeof location !== 'undefined' ? location.host : '';
  if (!host) return '提供本頁面的電腦';
  const local = /^(127\.0\.0\.1|localhost|\[::1\])(:\d+)?$/.test(host);
  return `提供本頁面的電腦(${host}${local ? ',即這台電腦' : ''})`;
}

const timeNote = (st: ReleaseStatus | null, text = '啟動時取得'): string => {
  const when = formatStatusTime(st?.generatedUtc ?? null);
  return when ? `;${text}(${when})` : '';
};

@customElement('brm-release-status')
export class ReleaseStatusBadge extends LitElement {
  static override styles = [
    tileStyles,
    css`
      :host {
        display: contents;
      }
      .version {
        color: var(--ob-text-muted);
        white-space: nowrap;
      }
      /* 與頁首的分段鈕(.seg button)同高 24 px:頁首第二行原本約 20–26 px 高,徽章不讓頁首明顯變高 */
      button.badge {
        color: var(--ob-warning);
        border-color: var(--ob-warning);
        background: transparent;
        font-weight: 600;
        white-space: nowrap;
        min-height: 24px;
        padding: 2px 8px;
      }
      button.badge[aria-expanded='true'] {
        background: var(--ob-bg-surface-raised);
      }
      /* 展開的說明:浮貼在頁首下方、與頁首同寬(定位容器是頁首);不佔版面高度,太長時自己捲動 */
      .detail {
        position: absolute;
        top: calc(100% + 4px);
        left: 0;
        right: 0;
        z-index: 50;
        /* 直向的 flex(不是 grid):grid 的項目只能在自己的格子裡貼齊,最後一列(.foot)就貼不到說明的底邊 */
        display: flex;
        flex-direction: column;
        gap: 4px;
        /* 下方的 8 px 內距放在最後一列(.foot)上:見 .foot 的說明 */
        padding: 8px 12px 0;
        border: 1px solid var(--ob-warning);
        border-radius: 6px;
        background: var(--ob-bg-surface);
        box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
        color: var(--ob-text);
        font-size: 13px;
        line-height: 1.5;
        /* 320 = DETAIL_MAX_HEIGHT_PX;有 detailLimit 時 fitDetail() 另以行內樣式縮小 */
        max-height: min(60vh, 320px);
        overflow-y: auto;
        scrollbar-width: thin;
      }
      .detail > * {
        flex: none;
      }
      .detail .lead strong {
        color: var(--ob-warning);
      }
      .detail .full {
        color: var(--ob-text-secondary);
      }
      .detail .k {
        display: block;
        font-size: 12px;
        color: var(--ob-text-muted);
      }
      /* 船上 + 本機:各台 status 的完整訊息預設收合(重點與處理已在上面);標題列 24 px 高(與頁首的鈕相同),整列可點 */
      .detail details.full > summary {
        display: list-item;
        cursor: pointer;
        font-size: 12px;
        line-height: 24px;
        color: var(--ob-text-muted);
      }
      .detail details.full > summary:hover {
        color: var(--ob-text);
      }
      .detail .machine + .machine,
      .detail .machine + .mismatch {
        border-top: 1px solid var(--ob-border);
        padding-top: 4px;
      }
      /* 最後一列(資料來源、「收合」鈕)貼在說明的底邊:內容超過最大高度而要捲動時,不必捲到底才找得到「收合」。
         左右的負邊界把這一列延伸到說明的左右內距上(底色蓋住捲過去的文字);說明下方的 8 px 內距由這一列自己的 padding-bottom 提供
         (說明本身的下方內距是 0)—— 不可以用「說明留下方內距、這一列再以負的 margin-bottom 伸進去」的寫法:Chromium 把貼底的位置算在說明的
         內容區底邊,這一列會被往上推 8 px,不捲動時也蓋住上一段最後一行的下半(第三十一輪主控程序量測:上一段底邊 249、這一列上緣 245)。
         上緣的柔邊只有 2 px(小於與上一段之間的 4 px 間隔),不蓋到文字 */
      .detail .foot {
        position: sticky;
        bottom: 0;
        margin: 0 -12px;
        padding: 0 12px 8px;
        background: var(--ob-bg-surface);
        box-shadow: 0 -2px 2px var(--ob-bg-surface);
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: 6px 12px;
        font-size: 12px;
        color: var(--ob-text-muted);
      }
    `,
  ];

  /** 本機(提供本頁面的電腦)的發行版狀態;null = 沒有。連上頁面時自動由 releaseStatusProvider.load 取得(取不到維持 null),也可由外部直接指定 */
  @property({ attribute: false }) status: ReleaseStatus | null = null;
  /** 船上筆電 simosa-web 的位址(lib/release-status.ts 的 shipWebBaseUrl);null = 頁面與核心在同一台電腦,不另外取船上的 */
  @property({ attribute: false }) shipWebUrl: string | null = null;
  /** 已連上船上的核心(教官站的即時連線為「已連線」):由 false 變 true 時取一次船上的狀態 */
  @property({ type: Boolean }) shipLinked = false;
  /** 船上筆電的發行版狀態;null = 沒有(不必取、還沒取到或取不到)。由 releaseStatusProvider.loadShip 取得,也可由外部直接指定 */
  @property({ attribute: false }) shipStatus: ReleaseStatus | null = null;
  /**
   * 展開的說明底邊不可超過的位置(視窗座標 y;回傳 null = 沒有限制)。由使用它的頁面提供:完整教官站回傳執行控制第一個鈕的上緣,
   * 說明就不會蓋住「凍結 / 恢復 / 重設 / 結束練習」(內容放不下時在說明裡捲動,「收合」貼在底邊)。沒給時只有樣式表的 min(60vh, 320px)
   */
  @property({ attribute: false }) detailLimit: (() => number | null) | null = null;
  @state() private open = false;
  /** 船上筆電連得到、但沒有發行版狀態(SHIP_NO_STATUS:船上是開發模式,或船上啟動時沒有取到):不重試,說明寫「沒有發行版狀態」而不是「未取得」 */
  @state() private shipNone = false;

  /** 取船上狀態的世代:位址改變、重新連上或元件移除時遞增,較早的請求回來時丟棄 */
  private shipGen = 0;
  private shipTimer: ReturnType<typeof setTimeout> | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    void releaseStatusProvider
      .load()
      .then((s) => {
        if (s && this.isConnected) this.status = s;
      })
      .catch(() => undefined); // 取不到就不顯示(provider 換成會丟例外的實作時也一樣)
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.setOpen(false);
    this.cancelShipLoad();
  }

  protected override willUpdate(changed: PropertyValues<this>): void {
    if (!changed.has('shipWebUrl') && !changed.has('shipLinked')) return;
    if (!this.shipWebUrl) {
      // 同一台電腦:沒有「船上的」可言
      this.cancelShipLoad();
      this.shipStatus = null;
      this.shipNone = false;
    } else if (changed.has('shipWebUrl') && changed.get('shipWebUrl') !== undefined && changed.get('shipWebUrl') !== this.shipWebUrl) {
      // 換了一台船:上一台的內容不留
      this.cancelShipLoad();
      this.shipStatus = null;
      this.shipNone = false;
      if (this.shipLinked) this.loadShip(0);
    } else if (this.shipLinked) {
      // 連上(或重新連上)核心:取一次
      this.loadShip(0);
    } else {
      // 斷線:停止重試,保留上一次取到的內容
      this.cancelShipLoad();
    }
  }

  private cancelShipLoad(): void {
    this.shipGen++;
    if (this.shipTimer !== null) clearTimeout(this.shipTimer);
    this.shipTimer = null;
  }

  private loadShip(attempt: number): void {
    this.cancelShipLoad();
    const gen = this.shipGen;
    const url = this.shipWebUrl;
    if (!url) return;
    let pending: Promise<ShipStatusResult>;
    try {
      pending = releaseStatusProvider.loadShip(url);
    } catch {
      pending = Promise.resolve(null);
    }
    void pending
      .catch(() => null)
      .then((s) => {
        // 較早的請求(之後斷線又連上、換了一台船、元件已移除)晚回來:丟棄,不可以蓋掉較新的結果或另外排重試
        if (gen !== this.shipGen || !this.isConnected) return;
        // 取不到或沒有發行版狀態:不留上一次的內容(船上可能已經換成別的版本)
        this.shipNone = s === SHIP_NO_STATUS;
        this.shipStatus = s === SHIP_NO_STATUS ? null : s;
        // 只有取不到才隔一段時間再試;船上明確回「沒有發行版狀態」時不試(狀態檔在船上下次啟動前不會出現,重新連上核心時會再取)
        const delay = SHIP_RETRY_DELAYS_MS[attempt];
        if (s === null && delay !== undefined) this.shipTimer = setTimeout(() => this.loadShip(attempt + 1), delay);
      });
  }

  private setOpen(open: boolean): void {
    this.open = open;
    // 展開時才監聽 Esc 與說明以外的點按(收合後移除,不影響教官站其他快速鍵與點按);視窗大小改變時重新算說明的最大高度
    if (open) {
      window.addEventListener('keydown', this.onKeyDown);
      window.addEventListener('pointerdown', this.onOutsidePointer, true);
      window.addEventListener('resize', this.fitDetail);
    } else {
      window.removeEventListener('keydown', this.onKeyDown);
      window.removeEventListener('pointerdown', this.onOutsidePointer, true);
      window.removeEventListener('resize', this.fitDetail);
    }
  }

  protected override updated(): void {
    if (this.open) this.fitDetail();
  }

  /**
   * 依 detailLimit 縮小說明的最大高度:底邊留在 limit 之上 DETAIL_LIMIT_GAP_PX,但不小於 DETAIL_MIN_HEIGHT_PX、不大於樣式表的上限
   * (60vh 與 DETAIL_MAX_HEIGHT_PX)。沒有 detailLimit(或它回傳 null / 丟例外)時拿掉行內樣式,回到樣式表的最大高度。
   */
  private fitDetail = (): void => {
    const detail = this.renderRoot.querySelector<HTMLElement>('.detail');
    if (!detail) return;
    let limit: number | null = null;
    try {
      limit = this.detailLimit ? this.detailLimit() : null;
    } catch {
      limit = null;
    }
    if (limit === null || !Number.isFinite(limit)) {
      detail.style.maxHeight = '';
      return;
    }
    const room = Math.floor(limit - detail.getBoundingClientRect().top - DETAIL_LIMIT_GAP_PX);
    const cap = Math.min(DETAIL_MAX_HEIGHT_PX, Math.floor(window.innerHeight * 0.6), Math.max(DETAIL_MIN_HEIGHT_PX, room));
    detail.style.maxHeight = `${cap}px`;
  };

  private toggle = (): void => this.setOpen(!this.open);

  private onKeyDown = (ev: KeyboardEvent): void => {
    if (ev.key === 'Escape') this.setOpen(false);
  };

  /**
   * 點在徽章與說明以外的地方:收合(那一下點按照常作用在它點到的控制項上 —— 不攔截、不 preventDefault)。
   * 在擷取階段聽:被點的元件自己停止事件傳遞時也收得到。徽章與說明在本元件的 shadow root 裡,composedPath 含本元件就是點在裡面。
   */
  private onOutsidePointer = (ev: Event): void => {
    if (!ev.composedPath().includes(this)) this.setOpen(false);
  };

  override render() {
    const local = this.status;
    const ship = this.shipWebUrl ? this.shipStatus : null;
    if (ship) return this.renderFleet(ship, local);
    if (!local) return nothing;
    return this.renderLocal(local);
  }

  // ------------------------------------------------------------ 只有本機的狀態
  private renderLocal(st: ReleaseStatus) {
    // 應該有船上的(頁面與核心不在同一台電腦)而沒取到:畫面不變,只在 title 與展開的說明註明。
    // 船上連得到但沒有發行版狀態(各欄 null)是另一句:那時船上不是舊版、網頁伺服也在執行。展開的說明只用短句(長句在 1093×614 多折兩行)
    const shipNote = !this.shipWebUrl
      ? ''
      : this.shipNone
        ? `。船上筆電沒有發行版狀態(已向 ${this.shipWebUrl} 取到回應:船上是開發模式,或船上啟動時沒有取到 simosa-pkg status)`
        : `。船上筆電的版本:未取得(連上船上的核心後向 ${this.shipWebUrl} 取;船上是較舊的版本或網頁伺服沒有啟動時取不到)`;
    const shipNoteShort = !this.shipWebUrl ? '' : this.shipNone ? '。船上筆電沒有發行版狀態' : '。船上筆電的版本:未取得';
    const source = `這是${localHostText()}的發行版${timeNote(st)}`;
    if (!st.downgrade) {
      const update = st.softwareUpdateVersion;
      // 發行版 = 安裝程式的版本;之後套用的軟體更新包(比它新)才是實際執行的程式:頁首寫實際執行的那一版
      if (update) {
        const about = `實際執行的是軟體更新包 ${update} 的程式(套用在發行版 ${st.releaseVersion} 之上;發行版是安裝程式的版本,套用更新包不會改它)。${source}${shipNote}`;
        return html`<span class="version update" data-release=${st.releaseVersion} title=${about}>軟體更新 ${update}</span>`;
      }
      return html`<span class="version" title="發行版是安裝程式的版本(套用軟體更新包後,這裡改寫更新包的版次)。${source}${shipNote}">發行版 ${st.releaseVersion}</span>`;
    }
    const sum = downgradeSummary(st.downgrade);
    return html`
      <button class="badge downgrade" aria-expanded=${this.open ? 'true' : 'false'} aria-controls="release-detail" title="${sum.running};${sum.newer}。按一下看說明與處理方式${shipNote}" @click=${this.toggle}>發行版 ${st.releaseVersion} · 降版提醒</button>
      ${this.open
        ? html`<div class="detail" id="release-detail" role="region" aria-label="降版提醒">
            <div class="lead"><strong>降版提醒</strong>:${sum.running};${sum.newer}。${sum.stale ? `${sum.stale}。` : ''}</div>
            <div class="advice">處理:${sum.advice}。</div>
            ${st.downgrade.message ? html`<div class="full"><span class="k">完整訊息(simosa-pkg status)</span>${st.downgrade.message}</div>` : nothing}
            <div class="foot">
              <span class="source">${source};套用更新包或重新安裝後,重新啟動才會更新${shipNoteShort}</span>
              <button class="collapse" @click=${this.toggle}>收合</button>
            </div>
          </div>`
        : nothing}
    `;
  }

  // ------------------------------------------------------------ 船上筆電 + 本機
  // class 名稱用 onboard(不用 ship):教官站頁首的船名是 header .ship,端對端的選擇器會穿過 shadow root
  /** 展開的說明裡一台電腦的段落:有降版時是提醒(重點、處理;status 的完整訊息預設收合),沒有時一行「實際執行的版本」 */
  private renderMachine(kind: 'onboard' | 'local', who: string, st: ReleaseStatus | null): TemplateResult {
    if (!st) return html`<div class="machine ${kind} none"><div class="line">${who}:沒有發行版狀態(開發模式,或啟動時沒有取到)。</div></div>`;
    if (!st.downgrade) return html`<div class="machine ${kind}"><div class="line">${who}:實際執行的是${runningDescription(st)}。</div></div>`;
    const sum = downgradeSummary(st.downgrade, who);
    const message = st.downgrade.message;
    // 「status 顯示的軟體版次不是實際執行的程式」:有完整訊息時放進收合的那一段(完整訊息已經有同一句就不重複),重點列少折一行;
    // 沒有完整訊息(沒有可收合的段落)時留在重點列
    const staleInLead = sum.stale && !message ? `${sum.stale}。` : '';
    const staleInFull = sum.stale && message && !message.includes(sum.stale) ? html`<div class="stale">${sum.stale}。</div>` : nothing;
    return html`<div class="machine ${kind} down">
      <div class="lead"><strong>${who} · 降版提醒</strong>:${sum.running};${sum.newer}。${staleInLead}</div>
      <div class="advice">處理:${sum.advice}。</div>
      ${message
        ? html`<details class="full">
            <summary class="k">完整訊息(${who}的 simosa-pkg status)</summary>
            ${staleInFull}
            <div class="message">${message}</div>
          </details>`
        : nothing}
    </div>`;
  }

  private renderFleet(ship: ReleaseStatus, local: ReleaseStatus | null) {
    const cmp = compareFleet(ship, local);
    const version = runningVersion(ship);
    const localWho = '這台電腦(本機)';
    const localLine = local
      ? cmp.mismatch
        ? `${localWho}執行的是${runningLabel(local)}`
        : `${localWho}相同(${runningLabel(local)})`
      : `${localWho}沒有發行版狀態`;
    const sources = `船上筆電:${this.shipWebUrl}${timeNote(ship, '船上啟動時取得')};本機:${localHostText()}${timeNote(local)}`;
    if (!cmp.tag) {
      // 兩台相同、沒有降版:小字,不是按鈕
      const about = `船上筆電實際執行的是${runningDescription(ship)};${localLine}。${sources}`;
      return html`<span class="version onboard" data-running=${runningLabel(ship)} title=${about}>船上 ${version}</span>`;
    }
    const mismatch = local ? mismatchSummary(ship, local) : null;
    const points: string[] = [];
    if (ship.downgrade) points.push(downgradeSummary(ship.downgrade, '船上筆電').running);
    if (local?.downgrade) points.push(downgradeSummary(local.downgrade, localWho).running);
    if (mismatch) points.push(`版本不同:${mismatch.what}`);
    return html`
      <button
        class="badge fleet ${cmp.shipDowngrade || cmp.localDowngrade ? 'downgrade' : 'mismatch'}"
        data-tag=${cmp.tag}
        aria-expanded=${this.open ? 'true' : 'false'}
        aria-controls="release-detail"
        title="${points.join(';')}。按一下看說明與處理方式"
        @click=${this.toggle}
      >船上 ${version} · ${cmp.tag}</button>
      ${this.open
        ? html`<div class="detail" id="release-detail" role="region" aria-label="船上筆電與本機的版本提醒">
            ${this.renderMachine('onboard', '船上筆電', ship)} ${this.renderMachine('local', localWho, local)}
            ${mismatch
              ? html`<div class="mismatch">
                  <div class="lead"><strong>版本不同</strong>:${mismatch.what}。</div>
                  <div class="advice">處理:${mismatch.advice}。</div>
                </div>`
              : nothing}
            <div class="foot">
              <span class="source">${sources};套用更新包或重新安裝後,該台重新啟動才會更新</span>
              <button class="collapse" @click=${this.toggle}>收合</button>
            </div>
          </div>`
        : nothing}
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-release-status': ReleaseStatusBadge;
  }
}
