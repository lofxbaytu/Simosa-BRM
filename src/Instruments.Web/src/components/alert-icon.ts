// 警報圖示(IEC 62288 Ed.2 / MSC.302(87) BAM 圖示的概念,自繪 SVG,非型式認可圖形):
//  - alarm 紅色三角形(緊急警報加粗雙框)、warning 黃橙圓形、caution 黃色方形,內有「!」;
//  - 未確認(activeUnacknowledged)閃爍,caution 不閃;
//  - 靜音中:右下加「喇叭 + 斜線」;已解除未確認:右下加打勾;已確認:不閃、實心。
// 顏色取主題變數:--ob-alarm(紅,只用於警報)、--ob-warning(黃橙)、--ob-caution(黃)。

import { css, svg } from 'lit';
import type { AlertVisual } from '../lib/alerts.js';

export const alertIconStyles = css`
  .alert-icon {
    display: inline-block;
    width: 22px;
    height: 22px;
    flex: none;
    vertical-align: middle;
  }
  .alert-icon .shape {
    stroke-width: 1.5;
  }
  .alert-icon.tone-alarm .shape,
  .alert-icon.tone-emergency .shape {
    fill: var(--ob-alarm);
    stroke: var(--ob-alarm);
  }
  .alert-icon.tone-warning .shape {
    fill: var(--ob-warning);
    stroke: var(--ob-warning);
  }
  .alert-icon.tone-caution .shape {
    fill: var(--ob-caution);
    stroke: var(--ob-caution);
  }
  .alert-icon .mark {
    fill: #101010;
    font-weight: 800;
    font-size: 13px;
  }
  .alert-icon .ring {
    fill: none;
    stroke: var(--ob-alarm);
    stroke-width: 1.5;
  }
  .alert-icon .badge-bg {
    fill: var(--ob-bg-surface);
    stroke: var(--ob-text);
    stroke-width: 1;
  }
  .alert-icon .badge {
    fill: none;
    stroke: var(--ob-text);
    stroke-width: 1.6;
    stroke-linecap: round;
    stroke-linejoin: round;
  }
  .alert-icon .badge-fill {
    fill: var(--ob-text);
  }
  .flash {
    animation: brm-alert-flash 1s steps(1, end) infinite;
  }
  @keyframes brm-alert-flash {
    0% {
      opacity: 1;
    }
    50% {
      opacity: 0.18;
    }
  }
`;

/** 繪製一個警報圖示;title 供滑鼠停留與無障礙。 */
export function alertIcon(v: AlertVisual, title = '') {
  const shape =
    v.shape === 'triangle'
      ? svg`<polygon class="shape" points="12,2.5 22.5,20.5 1.5,20.5" />${v.tone === 'emergency' ? svg`<polygon class="ring" points="12,-1 25.5,22.8 -1.5,22.8" />` : ''}`
      : v.shape === 'circle'
        ? svg`<circle class="shape" cx="12" cy="12" r="10" />`
        : svg`<rect class="shape" x="2.5" y="2.5" width="19" height="19" rx="1" />`;
  const markY = v.shape === 'triangle' ? 18.5 : 16.6;
  const badge = v.silenced
    ? svg`<circle class="badge-bg" cx="18.5" cy="18.5" r="5.5" /><path class="badge-fill" d="M15.5 17 h1.6 l2.4 -2 v7 l-2.4 -2 h-1.6 z" /><path class="badge" d="M14.6 22.6 L22.4 14.4" />`
    : v.rectified
      ? svg`<circle class="badge-bg" cx="18.5" cy="18.5" r="5.5" /><path class="badge" d="M15.6 18.6 l2 2 l3.6 -4" />`
      : '';
  return svg`<svg class="alert-icon tone-${v.tone} ${v.flashing ? 'flash' : ''}" viewBox="-2 -2 28 28" role="img" aria-label=${title}>
    <title>${title}</title>
    ${shape}
    <text class="mark" x="12" y=${markY} text-anchor="middle">!</text>
    ${badge}
  </svg>`;
}
