// 教官站元件共用樣式:面板、表單列、清單、核心支援標示。

import { css } from 'lit';

export const instructorStyles = css`
  .panel {
    display: flex;
    flex-direction: column;
    gap: 6px;
    background: var(--ob-bg-surface);
    border: 1px solid var(--ob-border);
    border-radius: 6px;
    padding: 8px 10px;
    min-height: 0;
    box-shadow: var(--ob-shadow);
  }
  .panel-title {
    font-size: 12px;
    font-weight: 600;
    color: var(--ob-text-secondary);
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    gap: 6px;
  }
  .panel-title .zh {
    color: var(--ob-text-muted);
    font-weight: 400;
  }
  .form {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 4px 8px;
    align-items: center;
    font-size: 12px;
  }
  .form label {
    color: var(--ob-text-secondary);
    white-space: nowrap;
  }
  .form input,
  .form select {
    width: 100%;
    min-width: 0;
  }
  .form .full {
    grid-column: 1 / -1;
  }
  .btn-row {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }
  .btn-row button {
    flex: 1 1 auto;
  }
  button.primary {
    background: var(--ob-accent);
    color: var(--ob-text-on-accent);
    border-color: var(--ob-accent-strong);
    font-weight: 600;
  }
  button.danger {
    border-color: var(--ob-warning);
    color: var(--ob-warning);
  }
  button.frozen {
    background: var(--ob-caution);
    color: #1a1600;
    border-color: var(--ob-caution);
  }
  .tag {
    display: inline-block;
    font-size: 10px;
    line-height: 1.2;
    padding: 1px 5px;
    border-radius: 3px;
    border: 1px solid var(--ob-border-strong);
    color: var(--ob-text-secondary);
    margin-left: 4px;
    white-space: nowrap;
  }
  .tag.unsupported {
    border-color: var(--ob-warning);
    color: var(--ob-warning);
  }
  .tag.ok {
    border-color: var(--ob-ok);
    color: var(--ob-ok);
  }
  .list {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    font-size: 12px;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .list .item {
    display: flex;
    gap: 6px;
    align-items: baseline;
    padding: 2px 4px;
    border-radius: 3px;
    border: 1px solid transparent;
  }
  .list .item:hover {
    background: var(--ob-bg-surface-raised);
  }
  .list .item.selected {
    border-color: var(--ob-accent);
    background: var(--ob-accent-soft);
  }
  .list .time {
    font-variant-numeric: tabular-nums;
    color: var(--ob-text-muted);
    white-space: nowrap;
    min-width: 62px;
  }
  .list .text {
    flex: 1;
    min-width: 0;
  }
  .hint {
    font-size: 11px;
    color: var(--ob-text-muted);
  }
  .ok {
    color: var(--ob-ok);
  }
  .bad {
    color: var(--ob-warning);
  }
`;

/** 秒 → "MM:SS" 或 "H:MM:SS"(清單用的短格式)。 */
export function shortTime(t: number): string {
  const total = Math.max(0, Math.floor(t));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const ms = `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
  return h > 0 ? `${h}:${ms}` : ms;
}

/** ISO 時間 → 本地 "HH:MM:SS"。 */
export function wallTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '--:--:--';
  return [d.getHours(), d.getMinutes(), d.getSeconds()].map((n) => n.toString().padStart(2, '0')).join(':');
}

/** 讀取數值輸入欄位;空白或非數字回傳 undefined。 */
export function numberFromInput(el: EventTarget | null): number | undefined {
  const v = (el as HTMLInputElement | null)?.value ?? '';
  if (v.trim() === '') return undefined;
  const n = Number(v);
  return Number.isFinite(n) ? n : undefined;
}
