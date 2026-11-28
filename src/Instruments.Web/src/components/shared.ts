// 元件共用:樣式片段與指令事件。

import { css } from 'lit';
import type { SimCommand } from '../types/command.js';

/** 儀器方塊(tile)與按鈕的共用樣式。 */
export const tileStyles = css`
  :host {
    display: block;
    box-sizing: border-box;
    min-width: 0;
    min-height: 0;
    color: var(--ob-text);
  }
  *,
  *::before,
  *::after {
    box-sizing: border-box;
  }
  .tile {
    height: 100%;
    display: flex;
    flex-direction: column;
    background: var(--ob-bg-surface);
    border: 1px solid var(--ob-border);
    border-radius: 6px;
    padding: 6px 8px;
    box-shadow: var(--ob-shadow);
    overflow: hidden;
  }
  .tile-title {
    font-size: 11px;
    letter-spacing: 0.04em;
    color: var(--ob-text-secondary);
    text-transform: uppercase;
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    gap: 6px;
    white-space: nowrap;
  }
  .tile-title .zh {
    text-transform: none;
    letter-spacing: 0;
    color: var(--ob-text-muted);
  }
  .value {
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    line-height: 1;
    white-space: nowrap;
  }
  .value.big {
    font-size: clamp(28px, 3.2vw, 56px);
  }
  .value.mid {
    font-size: clamp(18px, 1.6vw, 28px);
  }
  .value.small {
    font-size: clamp(14px, 1.1vw, 20px);
  }
  .unit {
    font-size: 11px;
    color: var(--ob-text-secondary);
    margin-left: 3px;
    font-weight: 400;
  }
  .row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    min-width: 0;
  }
  .label {
    font-size: 11px;
    color: var(--ob-text-secondary);
    white-space: nowrap;
  }
  .muted {
    color: var(--ob-text-muted);
  }
  .alarm {
    background: var(--ob-alarm);
    color: var(--ob-alarm-text);
    border-radius: 4px;
    padding: 0 4px;
  }
  /* .alarm 是徽章(行內小方塊)的樣式。表格列也用同一個類別名表示「這一列是警報」(講評摘要的逾時 / 失敗列、航跡圖目標船小表的
     CPA 警報列),列內由各元件自己把數值標成警報色 —— 整列套上紅底的話,那些數值是紅底紅字,看不到(2026-10 起就是這樣,
     第三十一輪主控程序驗收重播檢視的畫面時發現)。表格列不套徽章的底色與字色。 */
  tr.alarm {
    background: none;
    color: inherit;
    border-radius: 0;
    padding: 0;
  }
  .warning {
    color: var(--ob-warning);
  }
  .caution {
    color: var(--ob-caution);
  }
  svg {
    display: block;
    width: 100%;
    height: 100%;
  }
  svg text {
    font-family: inherit;
    fill: var(--ob-scale);
    font-variant-numeric: tabular-nums;
  }
  button,
  .btn {
    font: inherit;
    font-size: 12px;
    color: var(--ob-text);
    background: var(--ob-bg-surface-raised);
    border: 1px solid var(--ob-border-strong);
    border-radius: 4px;
    padding: 4px 8px;
    cursor: pointer;
    user-select: none;
    touch-action: none;
    line-height: 1.2;
    min-height: 26px;
  }
  button:hover {
    border-color: var(--ob-accent);
  }
  button:focus-visible {
    outline: none;
    box-shadow: var(--ob-focus);
  }
  button:active,
  button.active {
    background: var(--ob-accent);
    color: var(--ob-text-on-accent);
    border-color: var(--ob-accent-strong);
  }
  button:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }
  input,
  select {
    font: inherit;
    color: var(--ob-text);
    background: var(--ob-bg-input);
    border: 1px solid var(--ob-border-strong);
    border-radius: 4px;
    padding: 3px 6px;
    min-height: 26px;
  }
  input:focus-visible,
  select:focus-visible {
    outline: none;
    box-shadow: var(--ob-focus);
  }
  .panel-title {
    font-size: 12px;
    font-weight: 600;
    color: var(--ob-text-secondary);
    margin-bottom: 4px;
    display: flex;
    justify-content: space-between;
    align-items: baseline;
  }
`;

/**
 * conning 條各方塊(brm-heading … brm-route)共用的樣式,接在 tileStyles 之後(第三十一輪;docs/design/web-layout.md 第 2.5 節)。
 *
 * brm-conning-bar 在視窗寬於 1900 px(單列)時對 .bar 設 --brm-conning-single: 1,1900 px 以下(兩列)設 0。各方塊把
 * 單列專用的規則包在 `@container style(--brm-conning-single: 1)`、兩列專用的規則包在 `@container style(--brm-conning-single: 0)`
 * (樣式查詢;Chromium / Edge / WebView2 111 以上),要再依方塊的寬度或高度分情況時與尺寸條件合寫(`style(…) and (max-width: …)`)。
 * 兩組規則互不影響;不在 conning 條內、或瀏覽器不支援樣式查詢時兩組都不套用(退回原本的排法,不會出錯)。
 *
 * 單列的原則:內容放不下時在方塊內**換行**,不讓方塊邊界(.tile 的 overflow: hidden)把數值或單位切掉 —— 單列 10 至 13 格時每格只有
 * 約 117 至 210 px,字型較寬(CI 的 DejaVu Sans、Windows 的字型)時原本剛好放得下的一列就被裁切。這裡的換行只在「放不下」時才發生。
 *
 * 兩列的原則:方塊只有約 107 至 135 px 高(內容區 93 px 起),換行會把水平裁切換成下緣被切掉,所以**水平與垂直一起算**:
 *  - 行高寫成固定的 px(標題列 13 px;各方塊的文字列也是),方塊的內容高度不隨字型的行高而變(Windows 的字型量不到);
 *  - 大字(航向、主機轉速、UKC)的行高縮成字級的 0.82 倍(數字沒有降部,字框上下的留白不佔高度),字級另以方塊高度設上限(cqb);
 *  - 圖形(航向帶、轉速條、推力條、刻度)吃剩下的高度、可以縮短,文字不被切掉;
 *  - 排法只看方塊的寬度與高度(@container 的尺寸條件),不隨數值的位數在一行與兩行之間跳動;
 *  - 只有標題列在帶狀態標示(「無效」、流場來源、航點名稱)而放不下一行時換行,各方塊的高度預算已經算進這一行。
 */
export const conningTileStyles = css`
  @container style(--brm-conning-single: 1) {
    /* 標題列:英文縮寫與中文標題放不下一行時,中文標題換到下一行(靠右);中文標題本身比方塊寬時在詞間 / 字間換行,
       沒有空白的長字串(航線方塊標題裡的航點名稱,例如 22 個字元的 KAOHSIUNG_2ND_ENTRANCE)比方塊寬時在字元之間換行 */
    .tile-title {
      flex-wrap: wrap;
      row-gap: 0;
    }
    .tile-title .zh {
      margin-left: auto;
      min-width: 0;
      white-space: normal;
      overflow-wrap: anywhere;
      text-align: right;
    }
    /* 「標籤 + 數值」的一列:放不下時數值換到標籤的下一行(靠右) */
    .row {
      flex-wrap: wrap;
      row-gap: 0;
    }
    .row > :last-child {
      margin-left: auto;
    }
    /* 可以在內部換行的數值裡「盡量不拆開」的一段(數值與它的單位):行內區塊 —— 一行放得下整段時整段一起換行;
       連單獨一行都放不下(比所在的欄還寬)時才在段內的空白處換行,仍不被裁切 */
    .nb {
      display: inline-block;
    }
  }
  /* 兩列(視窗 ≤ 1900 px):標題列的行高固定 13 px;英文縮寫與中文標題放不下一行時,中文標題(與其中的狀態標示)換到下一行(靠右)。
     沒有空白的長字串(航線方塊標題裡的航點名稱)比方塊寬時在字元之間換行 */
  @container style(--brm-conning-single: 0) {
    .tile-title {
      flex-wrap: wrap;
      row-gap: 0;
      line-height: 13px;
    }
    .tile-title .zh {
      margin-left: auto;
      min-width: 0;
      white-space: normal;
      overflow-wrap: anywhere;
      text-align: right;
    }
    .nb {
      display: inline-block;
    }
  }
`;

export const COMMAND_EVENT = 'sim-command';

export type CommandEvent = CustomEvent<SimCommand>;

/** 由操船台元件送出指令事件(bubbles + composed 穿出 shadow DOM 到 <brm-app>)。 */
export function dispatchCommand(target: EventTarget, cmd: SimCommand): void {
  target.dispatchEvent(new CustomEvent<SimCommand>(COMMAND_EVENT, { detail: cmd, bubbles: true, composed: true }));
}

declare global {
  interface HTMLElementEventMap {
    'sim-command': CommandEvent;
  }
}
