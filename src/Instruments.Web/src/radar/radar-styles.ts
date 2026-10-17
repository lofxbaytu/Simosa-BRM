// 雷達頁元件共用樣式(沿用 theme.css 的 --ob-* 色票;紅色只用於危險目標/警報)。

import { css } from 'lit';

export const panelStyles = css`
  :host {
    display: block;
    box-sizing: border-box;
    min-width: 0;
    min-height: 0;
    color: var(--ob-text);
    font-size: 12px;
  }
  *,
  *::before,
  *::after {
    box-sizing: border-box;
  }
  .panel {
    background: var(--ob-bg-surface);
    border: 1px solid var(--ob-border);
    border-radius: 6px;
    padding: 6px 8px;
    box-shadow: var(--ob-shadow);
    min-width: 0;
  }
  .panel + .panel {
    margin-top: 6px;
  }
  h3 {
    margin: 0 0 4px;
    font-size: 11px;
    font-weight: 600;
    letter-spacing: 0.04em;
    color: var(--ob-text-secondary);
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    gap: 6px;
  }
  h3 .zh {
    font-weight: 400;
    color: var(--ob-text-muted);
  }
  .row {
    display: flex;
    align-items: center;
    gap: 4px;
    flex-wrap: wrap;
    min-height: 26px;
  }
  .row + .row {
    margin-top: 3px;
  }
  .grid2 {
    display: grid;
    grid-template-columns: auto 1fr auto;
    gap: 3px 6px;
    align-items: center;
  }
  label,
  .label {
    color: var(--ob-text-secondary);
    white-space: nowrap;
  }
  .value {
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .muted {
    color: var(--ob-text-muted);
  }
  .danger {
    color: var(--ob-alarm);
    font-weight: 700;
  }
  .warning {
    color: var(--ob-warning);
  }
  .ok {
    color: var(--ob-ok);
  }
  button {
    font: inherit;
    font-size: 12px;
    color: var(--ob-text);
    background: var(--ob-bg-surface-raised);
    border: 1px solid var(--ob-border-strong);
    border-radius: 4px;
    padding: 3px 7px;
    cursor: pointer;
    min-height: 26px;
    line-height: 1.2;
    white-space: nowrap;
  }
  button:hover {
    border-color: var(--ob-accent);
  }
  button:focus-visible,
  input:focus-visible,
  select:focus-visible {
    outline: none;
    box-shadow: var(--ob-focus);
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
  }
  button:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }
  input,
  select {
    font: inherit;
    font-size: 12px;
    color: var(--ob-text);
    background: var(--ob-bg-input);
    border: 1px solid var(--ob-border-strong);
    border-radius: 4px;
    padding: 2px 4px;
    min-height: 26px;
  }
  input[type='number'] {
    width: 64px;
    font-variant-numeric: tabular-nums;
  }
  input[type='range'] {
    flex: 1 1 auto;
    min-width: 60px;
    padding: 0;
    accent-color: var(--ob-accent);
  }
  table {
    width: 100%;
    border-collapse: collapse;
    font-variant-numeric: tabular-nums;
  }
  th,
  td {
    padding: 2px 4px;
    text-align: right;
    white-space: nowrap;
    border-bottom: 1px solid var(--ob-border);
  }
  th {
    font-weight: 600;
    color: var(--ob-text-secondary);
    font-size: 11px;
    cursor: pointer;
  }
  th.sorted {
    color: var(--ob-accent);
  }
  td:first-child,
  th:first-child {
    text-align: left;
  }
  tr.sel td {
    background: var(--ob-accent-soft);
  }
  tr.danger td {
    color: var(--ob-alarm);
    font-weight: 700;
  }
  tr.lost td {
    color: var(--ob-text-muted);
  }
  tr {
    cursor: pointer;
  }
  .seg {
    display: inline-flex;
  }
  .seg button {
    border-radius: 0;
  }
  .seg button:first-child {
    border-radius: 4px 0 0 4px;
  }
  .seg button:last-child {
    border-radius: 0 4px 4px 0;
  }
  .seg button + button {
    border-left-width: 0;
  }
`;
