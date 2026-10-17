// 自動講評摘要顯示(規劃書第 8.3 節指標、第 8.4 節講評;設計文件第 6 節 W6):
//  - 有核心 summary(coreSummary)時優先顯示:評分/通過、硬性失敗、容許帶、必要事件、故障應變、會遇、航跡、UKC、操縱、
//    參數集版次與各規則引用的程序文件(來源/版次/條款);「自動評分僅輔助」(advisory)提示;
//  - 無核心 summary(Python 參考伺服器、離線)時退回本地推導(DebriefAccumulator 的 DebriefSummary);
//  - 兩者皆有時可展開「本地推導」並列對照(交叉核對:時長、航程、最大 ROT、車令次數…);
//  - 必要事件倒數(countdowns,timeline-panel 的 requiredCountdowns):核心 summary 的 pending 列顯示剩餘/逾時秒數,
//    無核心 summary 時另列「必要事件(即時)」表。

import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { tileStyles } from '../shared.js';
import { instructorStyles, shortTime } from './instructor-shared.js';
import type { DebriefSummary } from '../../lib/debrief.js';
import { BAND_STATE_LABEL, HARD_FAIL_RULE_LABEL, REQUIRED_STATE_LABEL, type AssessmentSummary, type SummaryBand, type SummaryRequired } from '../../lib/assessment.js';
import { ACK_KIND_LABEL, ROLE_LABEL } from '../../lib/sim-event.js';
import type { RequiredCountdown } from './timeline-panel.js';

function fix(v: number | undefined, digits = 1, unit = ''): string {
  if (v === undefined) return '--';
  return `${v.toFixed(digits)}${unit ? ` ${unit}` : ''}`;
}

function secs(v: number | undefined): string {
  return v === undefined ? '--' : shortTime(v);
}

function sourceText(x: { source?: string; revision?: string; clause?: string; paramKey?: string }): string {
  if (!x.source && !x.paramKey) return '';
  const parts: string[] = [];
  if (x.source) parts.push(`${x.source}${x.revision ? ` rev.${x.revision}` : ''}${x.clause ? ` §${x.clause}` : ''}`);
  if (x.paramKey) parts.push(x.paramKey);
  return parts.join(' · ');
}

/** 倒數狀態的一句話(與 timeline-panel 的顯示一致)。 */
export function countdownText(c: RequiredCountdown): string {
  switch (c.state) {
    case 'counting':
      return c.remaining_s === null ? '計時中' : `剩 ${Math.ceil(c.remaining_s)} s`;
    case 'overdue':
      return `逾時 ${Math.floor(c.remaining_s ?? 0)} s`;
    case 'met':
      return `達成${c.by ? `(${c.by})` : ''}`;
    case 'late':
      return '逾時後達成';
    case 'missed':
      return '逾時';
    default:
      return '';
  }
}

@customElement('brm-debrief-summary')
export class DebriefSummaryView extends LitElement {
  static override styles = [
    tileStyles,
    instructorStyles,
    css`
      :host {
        display: block;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 13px;
      }
      :host([large]) table {
        font-size: 18px;
      }
      :host([large]) .score .num {
        font-size: 48px;
      }
      td,
      th {
        padding: 3px 6px;
        border-bottom: 1px solid var(--ob-border);
        text-align: left;
        vertical-align: top;
      }
      th {
        color: var(--ob-text-secondary);
        font-weight: 500;
        white-space: nowrap;
      }
      td.num {
        text-align: right;
        font-variant-numeric: tabular-nums;
        font-weight: 600;
        white-space: nowrap;
      }
      td.at {
        color: var(--ob-text-muted);
        font-variant-numeric: tabular-nums;
        white-space: nowrap;
      }
      td.src {
        color: var(--ob-text-muted);
        font-size: 11px;
      }
      tr.over td.num {
        color: var(--ob-warning);
      }
      tr.alarm td.num {
        color: var(--ob-alarm);
      }
      tr.ok td.num {
        color: var(--ob-ok);
      }
      .score {
        display: flex;
        gap: 16px;
        align-items: baseline;
        flex-wrap: wrap;
        padding: 4px 0 6px;
      }
      .score .num {
        font-size: 32px;
        font-weight: 700;
        font-variant-numeric: tabular-nums;
      }
      .score .pass {
        font-weight: 700;
        padding: 2px 10px;
        border-radius: 12px;
        border: 1px solid var(--ob-ok);
        color: var(--ob-ok);
      }
      .score .pass.failed {
        border-color: var(--ob-alarm);
        color: var(--ob-alarm);
      }
      .score .meta {
        color: var(--ob-text-secondary);
        font-size: 12px;
      }
      .section {
        font-size: 12px;
        font-weight: 600;
        color: var(--ob-text-secondary);
        margin: 8px 0 2px;
        display: flex;
        justify-content: space-between;
        align-items: baseline;
      }
      .section button {
        min-height: 20px;
        padding: 0 6px;
        font-size: 11px;
      }
      .advisory {
        font-size: 11px;
        color: var(--ob-text-muted);
        border-top: 1px dashed var(--ob-border);
        margin-top: 6px;
        padding-top: 4px;
      }
      .params {
        font-size: 11px;
        color: var(--ob-text-secondary);
      }
      .params code {
        font-size: 10px;
      }
    `,
  ];

  /** 本地推導(DebriefAccumulator) */
  @property({ attribute: false }) summary: DebriefSummary | null = null;
  /** 核心 summary(優先顯示) */
  @property({ attribute: false }) coreSummary: AssessmentSummary | null = null;
  @property({ type: Boolean, reflect: true }) large = false;
  /** 必要事件倒數(即時;重播無) */
  @property({ attribute: false }) countdowns: readonly RequiredCountdown[] = [];

  @state() private showLocal = false;

  override render() {
    const core = this.coreSummary;
    if (core) {
      return html`
        ${this.renderCore(core)}
        ${this.summary && this.summary.samples > 0
          ? html`<div class="section"><span>本地推導(教官站由狀態累計,供交叉核對)</span><button class="toggle-local" @click=${() => (this.showLocal = !this.showLocal)}>${this.showLocal ? '收起' : '並列顯示'}</button></div>
              ${this.showLocal ? this.renderLocal(this.summary, core) : nothing}`
          : nothing}
      `;
    }
    const s = this.summary;
    const active = this.countdowns.filter((c) => c.state !== 'inactive');
    const countdownTable =
      active.length > 0
        ? html`<div class="section"><span>必要事件(即時倒數)</span></div>
            <table aria-label="必要事件倒數"><tbody>${active.map((c) => this.row(c.rule.id, countdownText(c), c.startedAt_s !== null ? `自 ${shortTime(c.startedAt_s)}${c.within_s !== null ? ` / 限 ${Math.round(c.within_s)} s` : ''}` : '', c.state === 'overdue' || c.state === 'missed' ? 'alarm' : c.state === 'met' ? 'ok' : c.state === 'late' ? 'over' : ''))}</tbody></table>`
        : nothing;
    if (!s || s.samples === 0) return html`${countdownTable}<div class="hint">尚無資料</div>`;
    return html`${countdownTable}${this.renderLocal(s, null)}
      <div class="hint">本地推導:閾值為第一版暫用常數(速限 ${s.thresholds.portSpeedLimit_kn} kn、UKC ${s.thresholds.ukcAlarm_m} m、ROT ${s.thresholds.rotLimit_degPerMin}°/min、滿舵 ${s.thresholds.hardOver_deg}°);核心有評估規則與參數集時會改顯示核心 summary(規劃書第 9.1 節)</div>`;
  }

  private row(label: string, value: string, time = '', cls = '', src = '') {
    return html`<tr class=${cls}>
      <th>${label}</th>
      <td class="num">${value}</td>
      <td class="at">${time}</td>
      ${src ? html`<td class="src">${src}</td>` : html`<td class="src"></td>`}
    </tr>`;
  }

  private renderCore(c: AssessmentSummary) {
    const a = c.assessment;
    const score = a.score;
    const passed = a.passed;
    const ps = c.parameterSet;
    const g = c.general;
    const m = c.manoeuvring;
    const u = c.ukc;
    const t = c.track;
    const ca = c.collisionAvoidance;
    return html`
      <div class="score" aria-label="評分">
        <span class="num ${passed === false ? 'warning' : ''}">${score !== undefined ? Math.round(score) : '--'}</span>
        <span class="meta">分${a.passMark !== undefined ? ` / 及格 ${a.passMark}` : ''}</span>
        ${passed !== undefined ? html`<span class="pass ${passed ? '' : 'failed'}">${passed ? '通過' : a.hardFailed ? '硬性失敗' : '未達及格'}</span>` : nothing}
        <span class="meta">${c.final ? '定稿' : '即時(未定稿)'}${c.scenarioId ? ` · ${c.scenarioId}` : ''}${ps?.version ? ` · 參數集 ${ps.id ?? ''} ${ps.version}` : ' · 無參數集'}${ps?.sha256 ? ` (${ps.sha256.slice(0, 8)})` : ''}</span>
      </div>
      ${a.hardFails.length > 0
        ? html`<div class="section"><span>硬性失敗</span></div>
            <table><tbody>${a.hardFails.map((h) => this.row(`${h.id}`, `${HARD_FAIL_RULE_LABEL[h.rule ?? ''] ?? h.rule ?? ''}${h.value !== undefined ? ` ${fix(h.value, 2)}${h.limit !== undefined ? ` / ${fix(h.limit, 2)}` : ''}` : ''}`, secs(h.t_s), 'alarm', sourceText(h)))}</tbody></table>`
        : nothing}
      ${a.required.length > 0
        ? html`<div class="section"><span>必要事件</span></div>
            <table aria-label="必要事件"><tbody>${a.required.map((r) => this.requiredRow(r))}</tbody></table>`
        : nothing}
      ${a.bands.length > 0
        ? html`<div class="section"><span>容許帶</span></div>
            <table aria-label="容許帶"><tbody>${a.bands.map((b) => this.bandRow(b))}</tbody></table>`
        : nothing}
      ${c.faultResponses.length > 0
        ? html`<div class="section"><span>故障應變</span></div>
            <table aria-label="故障應變"><tbody>
              ${c.faultResponses.map((f) =>
                this.row(
                  `${f.fault}${f.itemId ? `(${f.itemId})` : ''}`,
                  f.firstAction ? `首個動作 ${f.firstAction.kind} +${fix(f.firstAction.dt_s, 0, 's')}` : '無相關動作',
                  secs(f.injectedAt_s),
                  f.withinFirstAction === false ? 'over' : f.withinFirstAction ? 'ok' : '',
                  [
                    ...f.acknowledgements.map((k) => `${ACK_KIND_LABEL[k.kind] ?? k.kind}${k.role ? `(${ROLE_LABEL[k.role] ?? k.role})` : ''} +${fix(k.dt_s, 0, 's')}`),
                    f.cleared_s !== undefined ? `清除 ${shortTime(f.cleared_s)}` : '',
                  ]
                    .filter(Boolean)
                    .join(' · '),
                ),
              )}
            </tbody></table>`
        : nothing}
      <div class="section"><span>總覽</span></div>
      <table aria-label="核心講評摘要">
        <tbody>
          ${this.row('練習時間', secs(g.duration_s), g.freezes ? `凍結 ${g.freezes} 次` : g.frozen_s ? `凍結 ${shortTime(g.frozen_s)}` : '')}
          ${this.row('航程', fix(g.distance_nm, 2, 'nm'))}
          ${this.row('平均速度(對地)', fix(g.avgSog_kn, 1, 'kn'), `最大 ${fix(g.maxSog_kn, 1, 'kn')}`)}
          ${ca.targets.length > 0 || ca.minCpa_nm !== undefined ? this.row('最小 CPA', fix(ca.minCpa_nm, 2, 'nm'), `會遇 ${ca.encounters ?? ca.targets.length} · 碰撞 ${ca.collisions ?? 0}`, (ca.collisions ?? 0) > 0 ? 'alarm' : '') : nothing}
          ${ca.targets.map((e) =>
            this.row(
              `會遇 ${e.name ?? e.targetId}`,
              `CPA ${fix(e.minCpa_nm, 2, 'nm')}`,
              e.timeToAction_s !== undefined ? `首次行動 +${fix(e.timeToAction_s, 0, 's')}(${e.firstAction ?? ''})` : '無行動',
              e.substantial === false ? 'over' : '',
              `${e.situation ?? ''}${e.courseChange_deg !== undefined ? ` 改向 ${fix(e.courseChange_deg, 0, '°')}` : ''}${e.speedChange_kn !== undefined && e.speedChange_kn !== 0 ? ` 改速 ${fix(e.speedChange_kn, 1, 'kn')}` : ''}`,
            ),
          )}
          ${t
            ? html`${this.row('最大 XTD', fix(t.maxXtd_m, 0, 'm'), secs(t.maxXtdAt_s), (t.timeOutsideCorridor_s ?? 0) > 0 ? 'over' : '', `平均 ${fix(t.meanAbsXtd_m, 0, 'm')}${t.offRoute ? ' · 偏離航線' : ''}`)}
              ${this.row('走廊外時間', secs(t.timeOutsideCorridor_s), `${t.corridorExcursions ?? 0} 次`, (t.timeOutsideCorridor_s ?? 0) > 0 ? 'over' : '', t.minDistanceToNoGo_m !== undefined ? `距禁區最近 ${fix(t.minDistanceToNoGo_m, 0, 'm')}` : '')}
              ${t.waypoints.map((w) => this.row(`通過 ${w.name}`, `XTD ${fix(w.xtd_m, 0, 'm')}`, secs(w.passedAt_s), '', `距航點 ${fix(w.distance_m, 0, 'm')}`))}`
            : nothing}
          ${this.row('最小 UKC', fix(u.min_m, 2, 'm'), secs(u.minAt_s), u.min_m !== undefined && u.limit_m !== undefined && u.min_m < u.limit_m ? 'alarm' : '', `${u.limit_m !== undefined ? `門檻 ${fix(u.limit_m, 2, 'm')}` : ''}${u.limitSource ? ` · ${u.limitSource}` : ''}`)}
          ${this.row('UKC 低於門檻的時間', secs(u.timeBelowLimit_s), `${u.excursions ?? 0} 次 · 擱淺 ${u.groundings ?? 0}`, (u.timeBelowLimit_s ?? 0) > 0 || (u.groundings ?? 0) > 0 ? 'alarm' : '')}
          ${this.row('滿舵次數', `${m.hardOverCount ?? '--'}`, secs(m.hardOverTime_s) + (m.hardOverTime_s !== undefined ? ' 累計' : ''), (m.hardOverCount ?? 0) > 0 ? 'over' : '')}
          ${this.row('舵令次數', `${m.rudderOrders ?? '--'}`, m.rudderDistribution_s ? Object.entries(m.rudderDistribution_s).map(([k, v]) => `${k}° ${shortTime(v)}`).join(' · ') : '')}
          ${this.row('最大 ROT', fix(m.maxRot_degPerMin, 1, '°/min'), secs(m.maxRotAt_s), (m.timeRotOverLimit_s ?? 0) > 0 ? 'over' : '', `超限 ${secs(m.timeRotOverLimit_s)}`)}
          ${this.row('車鐘變更次數', `${m.telegraphChanges ?? '--'}`, m.telegraphOrders.map((o) => `${o.order} ${shortTime(o.duration_s ?? 0)}`).join(' · '), '', `轉速令 ${m.rpmOrders ?? 0} · 側推 ${m.thrusterOrders ?? 0}`)}
          ${this.row('超過速限的時間', secs(m.timeOverSpeedLimit_s), m.maxOverSpeed_kn !== undefined && m.maxOverSpeed_kn > 0 ? `最多超 ${fix(m.maxOverSpeed_kn, 1, 'kn')}` : '', (m.timeOverSpeedLimit_s ?? 0) > 0 ? 'over' : '', m.speedLimitSource ?? '')}
          ${m.autopilotTime_s !== undefined ? this.row('自動舵時間', secs(m.autopilotTime_s), `切換 ${m.autopilotSwitches ?? 0} 次`) : nothing}
          ${c.phases.length > 1 ? this.row('階段', c.phases.map((p) => `${p.phase} ${shortTime(p.from_s)}–${shortTime(p.to_s)}`).join(' → ')) : nothing}
          ${c.events.total !== undefined ? this.row('事件', `${c.events.total}`, Object.entries(c.events.byKind).map(([k, v]) => `${k} ${v}`).join(' · ')) : nothing}
        </tbody>
      </table>
      ${a.parameters.length > 0
        ? html`<div class="section"><span>引用的程序參數</span></div>
            <div class="params" aria-label="參數來源">
              ${a.parameters.map((p) => html`<div><code>${p.key}</code> = ${String(p.value ?? '')}${p.unit ? ` ${p.unit}` : ''}${p.source ? ` — ${p.source}${p.revision ? ` rev.${p.revision}` : ''}${p.clause ? ` §${p.clause}` : ''}` : ''}</div>`)}
            </div>`
        : nothing}
      <div class="advisory">${c.advisory ? '自動評分僅輔助,最終由評估員判定(規劃書第 8.3 節、附錄 A-I/12)' : ''}${ps?.path ? ` · 參數集 ${ps.path}` : ''}</div>
    `;
  }

  private bandRow(b: SummaryBand) {
    const cls = b.state === 'fail' ? 'alarm' : b.state === 'warn' ? 'over' : b.state === 'ok' ? 'ok' : '';
    return this.row(`${b.id}${b.metric ? `(${b.metric})` : ''}`, `${fix(b.value, 1)} · ${BAND_STATE_LABEL[b.state]}`, `警告 ${fix(b.warn, 1)} / 失敗 ${fix(b.fail, 1)}${b.penalty ? ` · 扣 ${b.penalty}` : ''}`, cls, sourceText(b));
  }

  private requiredRow(r: SummaryRequired) {
    // 核心 summary 仍為 pending 時,以即時倒數顯示剩餘/逾時秒數
    const cd = r.state === 'pending' ? this.countdowns.find((c) => c.rule.id === r.id && (c.state === 'counting' || c.state === 'overdue')) : undefined;
    const cls = r.state === 'met' ? 'ok' : r.state === 'missed' || cd?.state === 'overdue' ? 'alarm' : r.state === 'late' ? 'over' : '';
    const within = r.within_s ?? (cd && cd.within_s !== null ? cd.within_s : undefined);
    return this.row(r.id, cd ? countdownText(cd) : REQUIRED_STATE_LABEL[r.state], `${r.dt_s !== undefined ? `+${fix(r.dt_s, 0, 's')}` : ''}${within !== undefined ? ` / 限 ${fix(within, 0, 's')}` : ''}${r.by ? ` · ${r.by}` : ''}${r.penalty ? ` · 扣 ${r.penalty}` : ''}`, cls, sourceText(r));
  }

  private renderLocal(s: DebriefSummary, core: AssessmentSummary | null) {
    const th = s.thresholds;
    const at = (t: number | null) => (t === null ? '' : shortTime(t));
    const cmp = (v: number | undefined, digits = 1, unit = '') => (core ? `核心 ${fix(v, digits, unit)}` : '');
    return html`
      <table aria-label="自動講評摘要">
        <tbody>
          ${this.row('練習時間', shortTime(s.duration_s), s.frozen_s > 0 ? `凍結 ${shortTime(s.frozen_s)}` : '', '', core ? `核心 ${secs(core.general.duration_s)}` : '')}
          ${this.row('航程', `${s.distance_nm.toFixed(2)} nm`, '', '', cmp(core?.general.distance_nm, 2, 'nm'))}
          ${this.row('平均速度(對地)', `${s.avgSpeed_kn.toFixed(1)} kn`, `最大 ${s.maxSog_kn.toFixed(1)} kn`, '', cmp(core?.general.avgSog_kn, 1, 'kn'))}
          ${this.row(`超過港區速限 ${th.portSpeedLimit_kn} kn 的時間`, shortTime(s.timeOverSpeedLimit_s), '', s.timeOverSpeedLimit_s > 0 ? 'over' : '', core ? `核心 ${secs(core.manoeuvring.timeOverSpeedLimit_s)}` : '')}
          ${this.row('最小 UKC', s.minUkc_m === null ? '--' : `${s.minUkc_m.toFixed(2)} m`, at(s.minUkcAt_s), s.minUkc_m !== null && s.minUkc_m < th.ukcAlarm_m ? 'alarm' : '', cmp(core?.ukc.min_m, 2, 'm'))}
          ${this.row(`UKC 低於 ${th.ukcAlarm_m.toFixed(1)} m 的時間`, shortTime(s.timeUkcBelowAlarm_s), '', s.timeUkcBelowAlarm_s > 0 ? 'alarm' : '')}
          ${this.row('最大舵角', `${s.maxRudder_deg.toFixed(1)}°`, at(s.maxRudderAt_s), s.hardOverCount > 0 ? 'over' : '')}
          ${this.row('滿舵次數', `${s.hardOverCount}`, `≥ ${th.hardOver_deg}°`, '', core ? `核心 ${core.manoeuvring.hardOverCount ?? '--'}` : '')}
          ${this.row('舵令變更次數', `${s.rudderOrders}`, '', '', core ? `核心 ${core.manoeuvring.rudderOrders ?? '--'}` : '')}
          ${this.row('最大 ROT', `${s.maxRot_degPerMin.toFixed(1)}°/min`, at(s.maxRotAt_s), s.maxRot_degPerMin > th.rotLimit_degPerMin ? 'over' : '', cmp(core?.manoeuvring.maxRot_degPerMin, 1, '°/min'))}
          ${this.row(`ROT 超過 ${th.rotLimit_degPerMin}°/min 的時間`, shortTime(s.timeRotOverLimit_s), '', s.timeRotOverLimit_s > 0 ? 'over' : '')}
          ${this.row('車鐘變更次數', `${s.telegraphChanges}`, '', '', core ? `核心 ${core.manoeuvring.telegraphChanges ?? '--'}` : '')}
          ${s.agroundEvents > 0 || s.collisionEvents > 0 ? this.row('擱淺 / 碰撞', `${s.agroundEvents} / ${s.collisionEvents}`, '', 'alarm') : nothing}
        </tbody>
      </table>
    `;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'brm-debrief-summary': DebriefSummaryView;
  }
}
