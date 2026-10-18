// 拖船面板(規劃書第 6.2 節 T 項、第 8.1 節 E13;docs/design/physics-tugs-anchors-mooring.md 第 2 節):
// 每艘拖船:模式 推頂/拖拉/待命/解纜、力方向轉盤(作用於自船的力方向,相對船艏順時針;0 向前、90 向右舷)、出力滑桿、
// 狀態與實際出力/效能、斷纜提示;送 tugControl 指令(只放有改的欄位,省略 = 核心保持)。
// 推/拉時一併送目前的方向與出力令,讓「TG1 推右舷 50 %」一個動作完成;方向/出力單獨改時只送該欄位。
// compact 屬性(簡化教官模式,引水人/船長下拖船令的情境):每艘一列,模式四鈕、方向四鈕(向前/向右舷/向後/向左舷)、出力 25/50/75/100 %;
// 預設鈕的選取標示以 state 的命令值(directionOrder_deg / power_pct)為準。
// 草稿:轉盤拖曳/方向鍵/輸入中的值為「編輯中」草稿(推/拉時一併送出);已送出的值在核心回報前暫留顯示(避免閃回舊值),
// state 的命令值與送出值一致、或 PENDING_MAX_UPDATES 次狀態更新後仍未一致(例如被拒)即丟棄,之後一律以 state 為準;
// 拖船 id 集合改變或 epoch 改變(教官站在重設/載入情境/切換來源時遞增)時清空全部草稿。
// 轉盤:pointerup 才送出;pointercancel / lostpointercapture(系統取消、失焦)只還原按下前的草稿,不送指令。
// 核心只有 C# SimCore 支援(Python 參考伺服器回 ok=false);state.tugs 省略時本面板不顯示(由教官站決定)。

import { LitElement, html, css, nothing, svg } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { dispatchCommand, tileStyles } from '../shared.js';
import { instructorStyles, numberFromInput } from './instructor-shared.js';
import { TUG_MODE_LABEL, TUG_STATUS_LABEL, tugControlCommand, tugDirectionText, tugLineBreakCommand, tugName, type ScenarioTugDef } from '../../lib/external-forces.js';
import { TUG_OPERATING_MODES, type TugMode, type TugState } from '../../types/external.js';
import { normalizeHeading } from '../../lib/angles.js';

/** 簡化模式的方向快速鍵(相對船艏) */
export const TUG_DIRECTION_PRESETS: ReadonlyArray<{ deg: number; label: string }> = [
  { deg: 0, label: '向前' },
  { deg: 90, label: '向右舷' },
  { deg: 180, label: '向後' },
  { deg: 270, label: '向左舷' },
];

/** 簡化模式的出力快速鍵(%) */
export const TUG_POWER_PRESETS: readonly number[] = [25, 50, 75, 100];

interface TugDraft {
  direction: number;
  power: number;
}

/** 單一欄位的草稿:pending = 已送出、等待核心回報;age = 送出後經過的狀態更新次數 */
interface FieldDraft {
  value: number;
  pending: boolean;
  age: number;
}

interface TugDrafts {
  direction?: FieldDraft;
  power?: FieldDraft;
}

/** 送出值等待核心回報的最多狀態更新次數(25 Hz 約 2 s);超過即丟棄草稿、改顯示 state */
export const PENDING_MAX_UPDATES = 50;

function angleDiff(a: number, b: number): number {
  const d = Math.abs(normalizeHeading(a) - normalizeHeading(b));
  return Math.min(d, 360 - d);
}

@customElement('brm-tug-panel')
export class TugPanel extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      .tug {
        display: grid;
        grid-template-columns: 96px minmax(0, 1fr);
        gap: 4px 8px;
        padding: 6px 0;
        border-top: 1px solid var(--ob-border);
      }
      .tug:first-of-type {
        border-top: none;
      }
      .name {
        grid-column: 1 / -1;
        display: flex;
        gap: 6px;
        align-items: baseline;
        font-weight: 600;
        font-size: 13px;
      }
      .status {
        font-size: 11px;
        font-weight: 600;
        padding: 0 5px;
        border-radius: 3px;
        border: 1px solid var(--ob-border-strong);
        color: var(--ob-text-secondary);
      }
      .status.working {
        border-color: var(--ob-ok);
        color: var(--ob-ok);
      }
      .status.connecting,
      .status.repositioning {
        border-color: var(--ob-caution);
        color: var(--ob-caution);
      }
      .status.parted {
        background: var(--ob-alarm);
        border-color: var(--ob-alarm);
        color: var(--ob-alarm-text);
      }
      .read {
        font-size: 12px;
        font-variant-numeric: tabular-nums;
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 1px 6px;
      }
      .read .l {
        color: var(--ob-text-muted);
      }
      .dial {
        width: 96px;
        height: 96px;
        touch-action: none;
        cursor: pointer;
        border-radius: 50%;
      }
      .dial:focus-visible {
        outline: 2px solid var(--ob-accent);
      }
      .dial .ring {
        fill: var(--ob-bg-surface-raised);
        stroke: var(--ob-border-strong);
      }
      .dial .hull {
        fill: var(--ob-ship-fill);
        stroke: var(--ob-ship-stroke);
      }
      .dial .tick {
        stroke: var(--ob-text-muted);
      }
      .dial .actual {
        stroke: var(--ob-ok);
        stroke-width: 4;
        stroke-linecap: round;
      }
      .dial .order {
        stroke: var(--ob-text-secondary);
        stroke-width: 2;
        stroke-dasharray: 3 2;
      }
      .dial .draft {
        fill: var(--ob-accent);
      }
      .controls {
        display: flex;
        flex-direction: column;
        gap: 4px;
        min-width: 0;
      }
      .modes {
        display: grid;
        grid-template-columns: repeat(4, 1fr);
        gap: 3px;
      }
      .modes button {
        min-height: 26px;
        font-size: 12px;
        padding: 1px 2px;
      }
      .row {
        display: flex;
        align-items: center;
        gap: 6px;
        font-size: 12px;
      }
      .row input[type='range'] {
        flex: 1;
        min-width: 0;
      }
      .row input[type='number'] {
        width: 60px;
      }
      .row .val {
        min-width: 44px;
        text-align: right;
        font-variant-numeric: tabular-nums;
      }
      .warn {
        grid-column: 1 / -1;
        font-size: 12px;
        color: var(--ob-alarm);
        font-weight: 600;
      }
      /* ---- 精簡版(簡化教官模式) ---- */
      :host([compact]) .tug {
        grid-template-columns: minmax(0, 1fr);
      }
      :host([compact]) .name {
        font-size: 15px;
      }
      :host([compact]) .modes button,
      :host([compact]) .presets button {
        min-height: 40px;
        font-size: 14px;
      }
      .presets {
        display: grid;
        grid-template-columns: repeat(4, 1fr);
        gap: 3px;
      }
      :host([compact]) .read {
        grid-template-columns: repeat(4, auto);
        font-size: 14px;
      }
    `,
  ];

  @property({ attribute: false }) tugs: readonly TugState[] = [];
  /** 情境 tugs[](可用模式、繫柱拖力;省略時推與拉皆可) */
  @property({ attribute: false }) defs: readonly ScenarioTugDef[] = [];
  /** 精簡版(簡化教官模式) */
  @property({ type: Boolean, reflect: true }) compact = false;
  /** 練習世代(教官站在重設、載入情境、切換來源時遞增);改變時清空草稿 */
  @property({ type: Number }) epoch = 0;

  /** 每艘拖船的方向/出力草稿(編輯中或已送出待回報) */
  @state() private drafts: Readonly<Record<string, TugDrafts>> = {};

  /** state 的方向令(無則實際方向) */
  private static orderDirection(t: TugState): number {
    return t.directionOrder_deg ?? t.direction_deg;
  }

  private static orderPower(t: TugState): number {
    return t.power_pct ?? 0;
  }

  private draft(t: TugState): TugDraft {
    const d = this.drafts[t.id];
    return { direction: d?.direction?.value ?? TugPanel.orderDirection(t), power: d?.power?.value ?? TugPanel.orderPower(t) };
  }

  /** 編輯中的草稿(尚未送出) */
  private setDraft(id: string, patch: Partial<TugDraft>): void {
    const cur = this.drafts[id] ?? {};
    const next: TugDrafts = { ...cur };
    if (patch.direction !== undefined) next.direction = { value: patch.direction, pending: false, age: 0 };
    if (patch.power !== undefined) next.power = { value: patch.power, pending: false, age: 0 };
    this.drafts = { ...this.drafts, [id]: next };
  }

  /** 已送出的值:暫留顯示到核心回報 */
  private markSent(id: string, field: keyof TugDrafts, value: number): void {
    this.drafts = { ...this.drafts, [id]: { ...this.drafts[id], [field]: { value, pending: true, age: 0 } } };
  }

  /** 還原某欄位草稿(undefined = 刪除,改顯示 state) */
  private restoreDraft(id: string, field: keyof TugDrafts, prev: FieldDraft | undefined): void {
    const next: TugDrafts = { ...this.drafts[id] };
    if (prev) next[field] = prev;
    else delete next[field];
    this.drafts = { ...this.drafts, [id]: next };
  }

  override willUpdate(changed: Map<PropertyKey, unknown>): void {
    if (changed.has('epoch') && changed.get('epoch') !== undefined) {
      this.drafts = {};
      this.dialing = null;
    }
    if (!changed.has('tugs')) return;
    const prev = changed.get('tugs') as readonly TugState[] | undefined;
    const ids = (list: readonly TugState[]) => list.map((t) => t.id).join('\u0000');
    if (prev !== undefined && ids(prev) !== ids(this.tugs)) {
      this.drafts = {};
      this.dialing = null;
      return;
    }
    // 送出值:state 命令值一致即丟棄;久未回報亦丟棄
    let changedAny = false;
    const next: Record<string, TugDrafts> = {};
    for (const [id, d] of Object.entries(this.drafts)) {
      const t = this.tugs.find((x) => x.id === id);
      const out: TugDrafts = {};
      for (const field of ['direction', 'power'] as const) {
        const f = d[field];
        if (!f) continue;
        if (!f.pending || !t) {
          out[field] = f;
          continue;
        }
        const matched = field === 'direction' ? angleDiff(TugPanel.orderDirection(t), f.value) < 0.5 : Math.abs(TugPanel.orderPower(t) - f.value) < 0.5;
        changedAny = true;
        if (!matched && f.age + 1 < PENDING_MAX_UPDATES) out[field] = { ...f, age: f.age + 1 };
      }
      if (out.direction || out.power) next[id] = out;
    }
    if (changedAny) this.drafts = next;
  }

  private modeAllowed(id: string, mode: TugMode): boolean {
    if (mode !== 'push' && mode !== 'pull') return true;
    const modes = this.defs.find((d) => d.id === id)?.modes;
    return !modes || modes.length === 0 || modes.includes(mode);
  }

  /** 模式鈕:推/拉時附上目前方向與出力草稿;待命/解纜只送模式。 */
  private sendMode(t: TugState, mode: TugMode): void {
    const d = this.draft(t);
    dispatchCommand(this, mode === 'push' || mode === 'pull' ? tugControlCommand(t.id, { mode, direction_deg: d.direction, power_pct: d.power }) : tugControlCommand(t.id, { mode }));
  }

  private sendDirection(t: TugState, deg: number): void {
    const dir = Math.round(normalizeHeading(deg)) % 360;
    this.markSent(t.id, 'direction', dir);
    dispatchCommand(this, tugControlCommand(t.id, { direction_deg: dir }));
  }

  private sendPower(t: TugState, pct: number): void {
    const p = Math.round(Math.min(100, Math.max(0, pct)));
    this.markSent(t.id, 'power', p);
    dispatchCommand(this, tugControlCommand(t.id, { power_pct: p }));
  }

  // ---- 轉盤:指標點選/拖曳設定草稿,放開時送出;方向鍵 ±5°、Enter 送出
  private dialAngle(ev: PointerEvent): number | null {
    const el = ev.currentTarget as SVGElement;
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) return null;
    const dx = ev.clientX - (r.left + r.width / 2);
    const dy = ev.clientY - (r.top + r.height / 2);
    if (Math.hypot(dx, dy) < 4) return null;
    return Math.round(normalizeHeading((Math.atan2(dx, -dy) * 180) / Math.PI) / 5) * 5 % 360;
  }

  private dialing: string | null = null;
  /** 按下轉盤前的方向草稿(取消時還原) */
  private dialPrev: FieldDraft | undefined;

  private onDialDown(ev: PointerEvent, t: TugState): void {
    const a = this.dialAngle(ev);
    if (a === null) return;
    (ev.currentTarget as Element).setPointerCapture?.(ev.pointerId);
    this.dialing = t.id;
    this.dialPrev = this.drafts[t.id]?.direction;
    this.setDraft(t.id, { direction: a });
  }

  private onDialMove(ev: PointerEvent, t: TugState): void {
    if (this.dialing !== t.id) return;
    const a = this.dialAngle(ev);
    if (a !== null) this.setDraft(t.id, { direction: a });
  }

  private onDialUp(t: TugState): void {
    if (this.dialing !== t.id) return;
    this.dialing = null;
    this.dialPrev = undefined;
    this.sendDirection(t, this.draft(t).direction);
  }

  /** 拖曳被取消(pointercancel / lostpointercapture):還原按下前的草稿,不送指令 */
  private onDialCancel(t: TugState): void {
    if (this.dialing !== t.id) return;
    this.dialing = null;
    this.restoreDraft(t.id, 'direction', this.dialPrev);
    this.dialPrev = undefined;
  }

  private onDialKey(ev: KeyboardEvent, t: TugState): void {
    const d = this.draft(t);
    if (ev.key === 'ArrowRight' || ev.key === 'ArrowUp') this.setDraft(t.id, { direction: normalizeHeading(d.direction + 5) });
    else if (ev.key === 'ArrowLeft' || ev.key === 'ArrowDown') this.setDraft(t.id, { direction: normalizeHeading(d.direction - 5) });
    else if (ev.key === 'Enter') this.sendDirection(t, d.direction);
    else return;
    ev.preventDefault();
  }

  private renderDial(t: TugState) {
    const d = this.draft(t);
    const ray = (deg: number, r0: number, r1: number) => {
      const a = (deg * Math.PI) / 180;
      return { x1: Math.sin(a) * r0, y1: -Math.cos(a) * r0, x2: Math.sin(a) * r1, y2: -Math.cos(a) * r1 };
    };
    const act = ray(t.direction_deg, 6, 40);
    const ord = ray(t.directionOrder_deg ?? t.direction_deg, 6, 44);
    const dot = ray(d.direction, 0, 42);
    return html`<svg
      class="dial"
      viewBox="-50 -50 100 100"
      role="slider"
      tabindex="0"
      aria-label="${tugName(t)} 力方向(相對船艏)"
      aria-valuemin="0"
      aria-valuemax="359"
      aria-valuenow=${Math.round(d.direction)}
      aria-valuetext="${Math.round(d.direction)}° ${tugDirectionText(d.direction)}"
      @pointerdown=${(e: PointerEvent) => this.onDialDown(e, t)}
      @pointermove=${(e: PointerEvent) => this.onDialMove(e, t)}
      @pointerup=${() => this.onDialUp(t)}
      @pointercancel=${() => this.onDialCancel(t)}
      @lostpointercapture=${() => this.onDialCancel(t)}
      @keydown=${(e: KeyboardEvent) => this.onDialKey(e, t)}
    >
      ${svg`<circle class="ring" r="47"></circle>
        ${[0, 45, 90, 135, 180, 225, 270, 315].map((deg) => {
          const k = ray(deg, 41, 47);
          return svg`<line class="tick" x1=${k.x1} y1=${k.y1} x2=${k.x2} y2=${k.y2}></line>`;
        })}
        <path class="hull" d="M0,-24 L7,-12 L7,22 L-7,22 L-7,-12 Z"></path>
        <line class="order" x1=${ord.x1} y1=${ord.y1} x2=${ord.x2} y2=${ord.y2}></line>
        ${t.force_t > 0.05 ? svg`<line class="actual" x1=${act.x1} y1=${act.y1} x2=${act.x2} y2=${act.y2}></line>` : nothing}
        <circle class="draft" cx=${dot.x2} cy=${dot.y2} r="4.5"></circle>`}
    </svg>`;
  }

  private renderRead(t: TugState) {
    const bp = t.bollardPull_t ?? this.defs.find((d) => d.id === t.id)?.bollardPull_t;
    return html`<div class="read">
      <span class="l">出力</span><span class="force">${t.force_t.toFixed(1)}${bp !== undefined ? ` / ${bp.toFixed(0)}` : ''} t</span>
      <span class="l">方向</span><span>${Math.round(t.direction_deg).toString().padStart(3, '0')}°${t.directionOrder_deg !== undefined && Math.abs(t.directionOrder_deg - t.direction_deg) > 0.5 ? ` → ${Math.round(t.directionOrder_deg).toString().padStart(3, '0')}°` : ''}</span>
      <span class="l">出力令</span><span>${t.power_pct !== undefined ? `${Math.round(t.power_pct)} %` : '—'}</span>
      <span class="l">效能</span><span class="eff ${t.efficiency !== undefined && t.efficiency < 0.6 ? 'bad' : ''}">${t.efficiency !== undefined ? `${Math.round(t.efficiency * 100)} %` : '—'}</span>
    </div>`;
  }

  private renderModes(t: TugState) {
    return html`<div class="modes" role="group" aria-label="${tugName(t)} 模式">
      ${TUG_OPERATING_MODES.map((m) => {
        // 拖船已離開(unavailable)時核心一律以 tugUnavailable 拒絕,模式鈕全部停用
        const allowed = this.modeAllowed(t.id, m) && t.status !== 'unavailable' && t.mode !== 'unavailable';
        const blocked = m === 'pull' && (t.lineParted === true || t.status === 'parted');
        return html`<button class="mode-${m} ${t.mode === m ? 'active' : ''}" ?disabled=${!allowed} title=${!allowed ? '情境未提供此模式(tugModeUnavailable)' : blocked ? '拖纜已斷:拉會被核心拒絕(tugLineParted)' : ''} @click=${() => this.sendMode(t, m)}>${TUG_MODE_LABEL[m]}</button>`;
      })}
    </div>`;
  }

  private renderWarn(t: TugState) {
    if (!(t.status === 'parted' || t.lineParted)) return nothing;
    return html`<div class="warn parted-warning">拖纜斷裂${t.mode === 'push' && t.status !== 'parted' ? ':推頂中仍可續推,不能改拉' : ':拉/待命者已失力'};故障清除後可重新帶纜</div>`;
  }

  private renderFull(t: TugState) {
    const d = this.draft(t);
    return html`<div class="tug" data-tug=${t.id}>
      <div class="name"><span>${tugName(t)}</span><span class="status ${t.status}">${TUG_STATUS_LABEL[t.status]}</span><span class="hint">${TUG_MODE_LABEL[t.mode]}</span></div>
      ${this.renderDial(t)}
      <div class="controls">
        ${this.renderRead(t)}
        ${this.renderModes(t)}
        <div class="row">
          <label>方向°</label>
          <input class="dir" type="number" min="0" max="359" step="5" .value=${String(Math.round(d.direction))} @input=${(e: Event) => {
            const v = numberFromInput(e.target);
            if (v !== undefined) this.setDraft(t.id, { direction: normalizeHeading(v) });
          }} @change=${() => this.sendDirection(t, this.draft(t).direction)} />
          <span class="hint">${tugDirectionText(d.direction)}</span>
        </div>
        <div class="row">
          <label>出力</label>
          <input class="power" type="range" min="0" max="100" step="5" .value=${String(Math.round(d.power))} @input=${(e: Event) => this.setDraft(t.id, { power: numberFromInput(e.target) ?? 0 })} @change=${(e: Event) => this.sendPower(t, numberFromInput(e.target) ?? 0)} />
          <span class="val">${Math.round(d.power)} %</span>
        </div>
      </div>
      ${this.renderWarn(t)}
    </div>`;
  }

  private renderCompact(t: TugState) {
    // 預設鈕標示 state 的命令值(不是草稿):重設或其他教官端改令後立即反映核心實際的令
    const dirOrder = Math.round(TugPanel.orderDirection(t)) % 360;
    const powerOrder = Math.round(TugPanel.orderPower(t));
    return html`<div class="tug" data-tug=${t.id}>
      <div class="name"><span>${tugName(t)}</span><span class="status ${t.status}">${TUG_STATUS_LABEL[t.status]}</span><span class="force">${t.force_t.toFixed(0)} t ${Math.round(t.direction_deg).toString().padStart(3, '0')}°</span></div>
      ${this.renderModes(t)}
      <div class="presets dir-presets" role="group" aria-label="${tugName(t)} 力方向">
        ${TUG_DIRECTION_PRESETS.map((p) => html`<button class="dir-${p.deg} ${dirOrder === p.deg ? 'active' : ''}" @click=${() => this.sendDirection(t, p.deg)}>${p.label}</button>`)}
      </div>
      <div class="presets power-presets" role="group" aria-label="${tugName(t)} 出力">
        ${TUG_POWER_PRESETS.map((p) => html`<button class="pw-${p} ${powerOrder === p ? 'active' : ''}" @click=${() => this.sendPower(t, p)}>${p} %</button>`)}
      </div>
      ${this.renderWarn(t)}
    </div>`;
  }

  override render() {
    return html`<div class="panel">
      <div class="panel-title"><span>拖船 TUGS</span><span class="zh" title="方向 = 作用於自船的力,相對船艏順時針(000 向前、090 向右舷、270 向左舷)">力方向相對船艏<span class="tag unsupported">僅 C# 核心</span></span></div>
      ${this.tugs.length === 0 ? html`<div class="hint">情境沒有拖船</div>` : this.tugs.map((t) => (this.compact ? this.renderCompact(t) : this.renderFull(t)))}
      ${!this.compact && this.tugs.length > 0
        ? html`<div class="btn-row">${this.tugs.map((t) => html`<button class="danger line-break" title="注入拖纜斷裂故障(injectFault tugLineBreak;E13)" @click=${() => dispatchCommand(this, tugLineBreakCommand(t.id))}>${t.id} 斷纜故障</button>`)}</div>`
        : nothing}
    </div>`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-tug-panel': TugPanel;
  }
}
