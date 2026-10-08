// 教官指令與核心回應:列出本站送出的指令與核心的 ack(Python 參考伺服器回 ok/detail;C# 核心目前不回應)。

import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime, wallTime } from './instructor-shared.js';
import type { CommandLogEntry } from '../../lib/session-record.js';

function describe(entry: CommandLogEntry): string {
  const c = entry.command;
  const v = c.value === undefined ? '' : typeof c.value === 'string' ? c.value : JSON.stringify(c.value);
  const a = c.args ? JSON.stringify(c.args) : '';
  return [c.type, v, a].filter(Boolean).join(' ');
}

@customElement('brm-command-log')
export class CommandLogView extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
        min-height: 0;
      }
      .panel {
        height: 100%;
      }
      .text {
        font-family: ui-monospace, Consolas, monospace;
        font-size: 11px;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .ack {
        font-size: 10px;
        white-space: nowrap;
      }
    `,
  ];

  @property({ attribute: false }) entries: readonly CommandLogEntry[] = [];
  @property({ type: Number }) max = 200;

  override render() {
    const items = this.entries.slice(-this.max).reverse();
    return html`
      <div class="panel">
        <div class="panel-title"><span>教官指令 COMMANDS</span><span class="zh">${this.entries.length} 筆</span></div>
        <div class="list" aria-label="教官指令紀錄">
          ${items.length === 0 ? html`<div class="hint">尚未送出指令</div>` : ''}
          ${items.map(
            (e) => html`<div class="item" title=${JSON.stringify(e.command)}>
              <span class="time">${shortTime(e.t)}</span>
              <span class="text">${describe(e)}</span>
              <span class="ack ${e.ack ? (e.ack.ok ? 'ok' : 'bad') : 'muted'}" title=${e.ack?.detail ?? ''}>
                ${e.ack ? (e.ack.ok ? '核心 OK' : `核心拒絕${e.ack.detail ? `:${e.ack.detail}` : ''}`) : `已送出 ${wallTime(e.wall)}`}
              </span>
            </div>`,
          )}
        </div>
      </div>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-command-log': CommandLogView;
  }
}
