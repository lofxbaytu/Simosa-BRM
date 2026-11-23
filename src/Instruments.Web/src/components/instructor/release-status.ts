// <brm-release-status>:教官站頁首的發行版本與降版提醒(完整與簡化模式;駕駛台頁不放 — 學員不需要)。
// 資料來源見 lib/release-status.ts(同源的 /_status/release.json;取不到時整個元件不顯示任何東西)。
//  - 沒有降版:小字「發行版 <版本>」(不是按鈕;來源與取得時間在 title)。發行版是安裝程式的版本,套用軟體更新包不會改它:
//    套用過比發行版新的軟體更新包時(實際執行的是更新包的程式)改寫成「軟體更新 <版次>」—— 頁首寫的是**實際執行的那一版**,
//    發行版在 title。不把兩個版本並排:「發行版 X · 軟體更新 Y」比「發行版 X」寬約 125 px,簡化模式簡報頁 1366×768 的頁首會多折一行
//    (36 → 65.6 px),同儕評估提示的最後一條被擠出簡報框 22 px(端對端實測);船上靠更新包升級是常態,不能讓常態的版面比較差。
//  - 有降版(simosa-pkg status 的 releaseDowngrade):警示色的徽章鈕「發行版 <版本> · 降版提醒」,一直留在頁首、不擋操作;
//    按一下在頁首下方展開說明(重點、建議的處理、status 的完整訊息、資料來源),再按一下、按「收合」、按 Esc 或點說明以外的地方收起
//    (展開的說明會蓋住頁首下方的控制項:簡化模式執行階段的 BRM 標記鈕與航跡圖的鈕在它下面,點別處就收起,不必先找「收合」)。
// 排版:host 是 display: contents — 徽章直接是頁首(flex、可換行)的排版項目。展開的說明是**浮貼**在頁首下方的一塊(position: absolute,
//   相對於頁首:使用它的頁首必須是 position: relative),與頁首同寬、不佔版面高度:1093×614 的教官站各列都已經在最小高度,
//   說明若佔一列(約 170–210 px)會把整個版面推出視窗、控制項捲不到(第三十輪量測;docs/design/web-layout.md 第 2.2 節)。
//   預設收合;展開時會蓋住頁首下方的一部分畫面,收合後還原。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { downgradeSummary, formatStatusTime, releaseStatusProvider, type ReleaseStatus } from '../../lib/release-status.js';

/** 「這是哪一台的發行版」:頁面由誰提供就是誰的(網址列的主機);本機位址另外註明 */
function sourceText(): string {
  const host = typeof location !== 'undefined' ? location.host : '';
  if (!host) return '這是提供本頁面的電腦的發行版';
  const local = /^(127\.0\.0\.1|localhost|\[::1\])(:\d+)?$/.test(host);
  return `這是提供本頁面的電腦(${host}${local ? ',即這台電腦' : ''})的發行版`;
}

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
      button.downgrade {
        color: var(--ob-warning);
        border-color: var(--ob-warning);
        background: transparent;
        font-weight: 600;
        white-space: nowrap;
        min-height: 24px;
        padding: 2px 8px;
      }
      button.downgrade[aria-expanded='true'] {
        background: var(--ob-bg-surface-raised);
      }
      /* 展開的說明:浮貼在頁首下方、與頁首同寬(定位容器是頁首);不佔版面高度,太長時自己捲動 */
      .detail {
        position: absolute;
        top: calc(100% + 4px);
        left: 0;
        right: 0;
        z-index: 50;
        display: grid;
        gap: 4px;
        padding: 8px 12px;
        border: 1px solid var(--ob-warning);
        border-radius: 6px;
        background: var(--ob-bg-surface);
        box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
        color: var(--ob-text);
        font-size: 13px;
        line-height: 1.5;
        max-height: min(60vh, 320px);
        overflow-y: auto;
        scrollbar-width: thin;
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
      .detail .foot {
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

  /** 發行版狀態;null = 不顯示。連上頁面時自動由 releaseStatusProvider 取得(取不到維持 null),也可由外部直接指定 */
  @property({ attribute: false }) status: ReleaseStatus | null = null;
  @state() private open = false;

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
  }

  private setOpen(open: boolean): void {
    this.open = open;
    // 展開時才監聽 Esc 與說明以外的點按(收合後移除,不影響教官站其他快速鍵與點按)
    if (open) {
      window.addEventListener('keydown', this.onKeyDown);
      window.addEventListener('pointerdown', this.onOutsidePointer, true);
    } else {
      window.removeEventListener('keydown', this.onKeyDown);
      window.removeEventListener('pointerdown', this.onOutsidePointer, true);
    }
  }

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
    const st = this.status;
    if (!st) return nothing;
    const when = formatStatusTime(st.generatedUtc);
    const source = `${sourceText()}${when ? `;啟動時取得(${when})` : ''}`;
    if (!st.downgrade) {
      const update = st.softwareUpdateVersion;
      // 發行版 = 安裝程式的版本;之後套用的軟體更新包(比它新)才是實際執行的程式:頁首寫實際執行的那一版
      if (update) {
        const about = `實際執行的是軟體更新包 ${update} 的程式(套用在發行版 ${st.releaseVersion} 之上;發行版是安裝程式的版本,套用更新包不會改它)。${source}`;
        return html`<span class="version update" data-release=${st.releaseVersion} title=${about}>軟體更新 ${update}</span>`;
      }
      return html`<span class="version" title="發行版是安裝程式的版本(套用軟體更新包後,這裡改寫更新包的版次)。${source}">發行版 ${st.releaseVersion}</span>`;
    }
    const sum = downgradeSummary(st.downgrade);
    return html`
      <button class="downgrade" aria-expanded=${this.open ? 'true' : 'false'} aria-controls="release-detail" title="${sum.running};${sum.newer}。按一下看說明與處理方式" @click=${this.toggle}>發行版 ${st.releaseVersion} · 降版提醒</button>
      ${this.open
        ? html`<div class="detail" id="release-detail" role="region" aria-label="降版提醒">
            <div class="lead"><strong>降版提醒</strong>:${sum.running};${sum.newer}。${sum.stale ? `${sum.stale}。` : ''}</div>
            <div class="advice">處理:${sum.advice}。</div>
            ${st.downgrade.message ? html`<div class="full"><span class="k">完整訊息(simosa-pkg status)</span>${st.downgrade.message}</div>` : nothing}
            <div class="foot">
              <span class="source">${source};套用更新包或重新安裝後,重新啟動才會更新</span>
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
