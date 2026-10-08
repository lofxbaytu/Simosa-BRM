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
