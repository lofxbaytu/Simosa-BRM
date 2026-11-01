// 簡化 ECDIS 頁面元件共用:持有 EcdisStore 並在其變更時重繪的基底、面板樣式、S-52 風格配色(依日/黃昏/夜主題)。

import { LitElement, css } from 'lit';
import { property } from 'lit/decorators.js';
import type { EcdisStore } from '../../lib/ecdis/ecdis-store.js';
import type { Theme } from '../../lib/theme.js';

export class EcdisStoreElement extends LitElement {
  @property({ attribute: false }) store!: EcdisStore;
  private unsub: (() => void) | null = null;

  override connectedCallback(): void {
    super.connectedCallback();
    this.bind();
  }

  override disconnectedCallback(): void {
    super.disconnectedCallback();
    this.unsub?.();
    this.unsub = null;
  }

  override willUpdate(changed: Map<string, unknown>): void {
    if (changed.has('store')) this.bind();
  }

  private bind(): void {
    this.unsub?.();
    this.unsub = this.store ? this.store.subscribe(() => this.requestUpdate()) : null;
  }
}

export const panelStyles = css`
  :host {
    display: block;
    background: var(--ob-bg-surface);
    border: 1px solid var(--ob-border);
    border-radius: 6px;
    padding: 6px 8px;
    font-size: 12px;
    color: var(--ob-text);
  }
  h3 {
    margin: 0 0 4px;
    font-size: 12px;
    font-weight: 700;
    letter-spacing: 0.04em;
    color: var(--ob-text-secondary);
    display: flex;
    align-items: center;
    gap: 6px;
  }
  h3 .spacer {
    flex: 1 1 auto;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
    margin: 3px 0;
  }
  .row .label {
    color: var(--ob-text-secondary);
    min-width: 52px;
  }
  .kv {
    display: grid;
    grid-template-columns: auto 1fr;
    column-gap: 8px;
    row-gap: 2px;
    font-variant-numeric: tabular-nums;
  }
  .kv .k {
    color: var(--ob-text-secondary);
  }
  .kv .v {
    text-align: right;
    font-weight: 600;
  }
  .kv .v.warn {
    color: var(--ob-warning);
  }
  .kv .v.alarm {
    color: var(--ob-alarm);
  }
  .kv .v.muted {
    color: var(--ob-text-muted);
    font-weight: 400;
  }
  button {
    font: inherit;
    color: var(--ob-text);
    background: var(--ob-bg-surface-raised);
    border: 1px solid var(--ob-border-strong);
    border-radius: 4px;
    padding: 2px 8px;
    min-height: 24px;
    cursor: pointer;
  }
  button.active {
    background: var(--ob-accent);
    color: var(--ob-text-on-accent);
    border-color: var(--ob-accent-strong);
  }
  button.alarm {
    background: var(--ob-alarm);
    color: var(--ob-alarm-text);
    border-color: var(--ob-alarm);
    font-weight: 700;
  }
  button:disabled {
    opacity: 0.5;
    cursor: default;
  }
  select,
  input[type='number'] {
    font: inherit;
    color: var(--ob-text);
    background: var(--ob-bg-input);
    border: 1px solid var(--ob-border-strong);
    border-radius: 4px;
    padding: 2px 4px;
    min-height: 24px;
  }
  input[type='number'] {
    width: 64px;
  }
  label.chk {
    display: inline-flex;
    align-items: center;
    gap: 3px;
    white-space: nowrap;
  }
  table {
    width: 100%;
    border-collapse: collapse;
    font-variant-numeric: tabular-nums;
  }
  th,
  td {
    text-align: right;
    padding: 2px 4px;
    border-bottom: 1px solid var(--ob-border);
    white-space: nowrap;
  }
  th {
    color: var(--ob-text-secondary);
    font-weight: 600;
  }
  th:first-child,
  td:first-child {
    text-align: left;
  }
  tbody tr {
    cursor: pointer;
  }
  tbody tr.selected {
    background: var(--ob-accent-soft);
  }
  tbody tr.danger td {
    color: var(--ob-alarm);
    font-weight: 700;
  }
  .note {
    color: var(--ob-text-muted);
    font-size: 11px;
    margin-top: 4px;
  }
  .bar {
    position: relative;
    height: 8px;
    background: var(--ob-bg-input);
    border: 1px solid var(--ob-border);
    border-radius: 4px;
    overflow: hidden;
  }
  .bar .mark {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 3px;
    background: var(--ob-accent);
  }
  .bar .mark.over {
    background: var(--ob-alarm);
  }
  .bar .centre {
    position: absolute;
    top: 0;
    bottom: 0;
    left: 50%;
    width: 1px;
    background: var(--ob-border-strong);
  }
  .alarm-item {
    display: grid;
    grid-template-columns: auto 1fr auto;
    gap: 6px;
    align-items: center;
    padding: 3px 4px;
    border-radius: 4px;
    margin: 2px 0;
    border: 1px solid var(--ob-border);
  }
  .alarm-item.alarm {
    border-color: var(--ob-alarm);
  }
  .alarm-item.warning {
    border-color: var(--ob-warning);
  }
  .alarm-item.caution {
    border-color: var(--ob-caution);
  }
  .alarm-item.unack.alarm {
    background: var(--ob-alarm);
    color: var(--ob-alarm-text);
  }
  .alarm-item.unack.alarm.blink {
    animation: blink 1s steps(1) infinite;
  }
  .alarm-item .tag {
    font-size: 10px;
    border: 1px solid currentColor;
    border-radius: 8px;
    padding: 0 5px;
    white-space: nowrap;
  }
  .alarm-item .text {
    font-size: 11px;
  }
  .alarm-item .meta {
    font-size: 10px;
    opacity: 0.8;
  }
  @keyframes blink {
    50% {
      opacity: 0.35;
    }
  }
`;

/** S-52 風格配色(簡化;依主題;非型式認可色票)。 */
export interface EcdisPalette {
  deep: string;
  medium: string;
  shallow: string;
  veryShallow: string;
  land: string;
  landLine: string;
  breakwater: string;
  text: string;
  textMuted: string;
  /** 安全等深線 */
  contour: string;
  route: string;
  routeActive: string;
  routeCorridor: string;
  zone: string;
  zoneNoGo: string;
  target: string;
  targetDanger: string;
  own: string;
  track: string;
  rings: string;
}

export const ECDIS_PALETTE: Readonly<Record<Theme, EcdisPalette>> = {
  day: {
    deep: '#ffffff',
    medium: '#c9e0f4',
    shallow: '#9fc4e8',
    veryShallow: '#6fa3d6',
    land: '#c8b46e',
    landLine: '#8a7a40',
    breakwater: '#8f7d45',
    text: '#101820',
    textMuted: '#4e5c6a',
    contour: '#1a4a8a',
    route: '#c42828',
    routeActive: '#e01010',
    routeCorridor: 'rgba(196, 40, 40, 0.10)',
    zone: '#a000a0',
    zoneNoGo: '#c40000',
    target: '#0b7a3a',
    targetDanger: '#d62828',
    own: '#101820',
    track: '#2a6fb8',
    rings: '#7d8a98',
  },
  dusk: {
    deep: '#1b2630',
    medium: '#1f3a55',
    shallow: '#285079',
    veryShallow: '#34659a',
    land: '#5a4a24',
    landLine: '#8a7640',
    breakwater: '#746238',
    text: '#dfe5ea',
    textMuted: '#a3aeb8',
    contour: '#5aa2ff',
    route: '#e04848',
    routeActive: '#ff6060',
    routeCorridor: 'rgba(224, 72, 72, 0.15)',
    zone: '#c060c0',
    zoneNoGo: '#ff4d4d',
    target: '#4cc26a',
    targetDanger: '#ff4d4d',
    own: '#f2f5f7',
    track: '#5aa2ff',
    rings: '#6b7680',
  },
  night: {
    deep: '#05080c',
    medium: '#0b1a2e',
    shallow: '#132a4a',
    veryShallow: '#1d3d6b',
    land: '#3a3018',
    landLine: '#6a5a30',
    breakwater: '#55482a',
    text: '#a9b1b8',
    textMuted: '#7b848c',
    contour: '#4f86c6',
    route: '#c04040',
    routeActive: '#e05050',
    routeCorridor: 'rgba(192, 64, 64, 0.15)',
    zone: '#a050a0',
    zoneNoGo: '#e03c3c',
    target: '#3a9a56',
    targetDanger: '#e03c3c',
    own: '#c4ccd2',
    track: '#4f86c6',
    rings: '#3a444d',
  },
};
