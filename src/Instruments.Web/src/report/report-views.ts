// 報告頁的列印文件樣板(A4;@media print 只印 .sheet):講評報告、行為標記評估表、個人訓練紀錄、課程班次紀錄。
// 純樣板函式(lit html),資料模型在 debrief-report.ts / marker-form.ts / training-record.ts。

import { html, nothing, type TemplateResult } from 'lit';
import { BAND_STATE_LABEL, HARD_FAIL_RULE_LABEL, REQUIRED_STATE_LABEL, type AssessmentSummary } from '../lib/assessment.js';
import { formatSimTime } from '../lib/format.js';
import { ACK_KIND_LABEL } from '../lib/sim-event.js';
import { sessionRoleLabel, type RestHoursConfirmation } from '../lib/session-info.js';
import {
  CALIBRATION_TARGET,
  dualTrackRows,
  EXPIRY_FLAG_LABEL,
  instructorRows,
  REFRESHER_LEAD_DAYS,
  SCHEDULE_KIND_LABEL,
  validateAdminRecords,
  type AdminRecords,
  type ExpiryFlag,
  type ScheduleQuarter,
} from './admin-records.js';
import { ANCHORS, allElements, scaleLabel, type BehaviouralAnchors } from './anchors.js';
import { DATA_QUALITY_LABEL, participantRoleLabel, stcwMapping, stcwText, type BehaviouralResult, type DebriefReport, type ManoeuvreRow } from './debrief-report.js';
import { summarizeForm, validateMarkerForm, type AgreementResult, type MarkerForm } from './marker-form.js';
import { INTERNAL_TRAINING_STATEMENT, sessionHours, trainingFormLabel, validateCourseSession, VERDICT_LABEL, type CourseSession, type IndividualTrainingRecord, type SessionExercise, type TraineeResult } from './training-record.js';

export function fix(v: number | undefined | null, digits = 1, unit = ''): string {
  if (v === undefined || v === null || !Number.isFinite(v)) return '—';
  return `${v.toFixed(digits)}${unit ? ` ${unit}` : ''}`;
}

export function tStr(v: number | undefined | null): string {
  return v === undefined || v === null || !Number.isFinite(v) ? '—' : formatSimTime(v);
}

function dtStr(v: number | undefined): string {
  return v === undefined ? '' : ` (+${v.toFixed(0)} s)`;
}

export function localDateTime(iso: string | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

function srcText(x: { source?: string; revision?: string; clause?: string; paramKey?: string }): string {
  const parts: string[] = [];
  if (x.source) parts.push(`${x.source}${x.revision ? ` 第 ${x.revision} 版` : ''}${x.clause ? ` ${x.clause}` : ''}`);
  if (x.paramKey) parts.push(x.paramKey);
  return parts.join(' · ');
}

function shortHash(h: string | undefined): string {
  return h ? `${h.slice(0, 12)}…` : '—';
}

function kv(rows: Array<[string, unknown]>): TemplateResult {
  return html`<dl class="kv">${rows.map(([k, v]) => html`<dt>${k}</dt><dd>${v === undefined || v === null || v === '' ? '—' : v}</dd>`)}</dl>`;
}

function sheetHeader(label: string, title: string, sub: string, right: string): TemplateResult {
  return html`<div class="doc-head">
    <div class="doc-org">${label}</div>
    <div class="doc-title">${title}</div>
    <div class="doc-sub">${sub}</div>
    <div class="doc-right">${right}</div>
  </div>`;
}

function signatures(labels: string[]): TemplateResult {
  return html`<div class="signatures">${labels.map((l) => html`<div class="sig"><span class="sig-line"></span><span class="sig-label">${l}</span></div>`)}</div>`;
}

// ------------------------------------------------------------------ 講評報告

export function renderReportDoc(r: DebriefReport, anchors: BehaviouralAnchors = ANCHORS): TemplateResult {
  const ex = r.exercise;
  const m = r.meta;
  return html`<article class="sheet report-doc" data-doc="report">
    ${sheetHeader(r.headerLabel, '講評報告 Debrief Report', `${m.courseName}${m.courseVersion ? ` 第 ${m.courseVersion} 版` : ''}`, `報告編號 ${r.reportNo}`)}
    <p class="advisory">自動評分依 STCW A-I/12 在驗證前僅作輔助,最終由評估員以「安全有效完成任務」判定(規劃書第 8.3 節)。</p>

    <section class="sec" data-sec="exercise">
      <h2>一、練習資訊</h2>
      ${kv([
        ['練習', `${ex.exerciseNo} ${ex.scenarioId}${ex.scenarioName ? ` ${ex.scenarioName}` : ''}`],
        ['船舶', `${ex.shipName}(${ex.shipId})`],
        ['裝載', ex.loadingLabel],
        ['練習日期', localDateTime(ex.startedUtc ?? ex.createdUtc)],
        ['情境時刻', ex.scenarioStartUtc ? `${ex.scenarioStartUtc}(UTC)` : '—'],
        ['時長', `${tStr(ex.duration_s)}(${fix(ex.duration_s / 60, 1)} min)`],
        ['課程班次', m.sessionId],
        ['教官', m.instructor],
        ['情境種子', ex.seed],
        ['動力學模型', ex.dynamics],
        ['紀錄來源', ex.source === 'simcore-jsonl' ? '核心 JSON Lines 紀錄' : '教官站紀錄'],
        ['狀態雜湊', shortHash(ex.stateHash)],
        ['紀錄檔 SHA-256', shortHash(ex.sha256)],
        ['倒帶次數', ex.rewinds],
      ])}
      <h3>參與者與角色</h3>
      ${m.participants.length === 0
        ? html`<p class="muted">未填參與者</p>`
        : html`<table class="tbl participants">
            <thead><tr><th>姓名</th><th>職級</th><th>角色</th><th>學員 ID</th></tr></thead>
            <tbody>${m.participants.map((p) => html`<tr><td>${p.name}</td><td>${p.rank}</td><td>${participantRoleLabel(p.role)}</td><td>${p.traineeId || '—'}</td></tr>`)}</tbody>
          </table>`}
      ${renderSessionInfo(r)}
    </section>

    <section class="sec" data-sec="summary">
      <h2>二、自動量測與評分</h2>
      ${r.summary ? renderSummary(r.summary) : renderLocal(r)}
    </section>

    ${r.manoeuvres.length > 0 ? renderManoeuvres(r.manoeuvres) : nothing}

    <section class="sec" data-sec="timeline">
      <h2>${r.manoeuvres.length > 0 ? '四' : '三'}、事件時間軸</h2>
      ${r.timeline.length === 0
        ? html`<p class="muted">紀錄中沒有事件</p>`
        : html`<table class="tbl timeline">
            <thead><tr><th class="t">時刻</th><th>來源</th><th>事件</th></tr></thead>
            <tbody>${r.timeline.map((e) => html`<tr class="sev-${e.severity}"><td class="t">${tStr(e.t)}</td><td>${e.source}</td><td>${e.label}</td></tr>`)}</tbody>
          </table>
          ${r.timelineOmitted > 0 ? html`<p class="muted">另有 ${r.timelineOmitted} 筆事件未列出(完整事件見重播檢視)</p>` : nothing}`}
    </section>

    <section class="sec" data-sec="behavioural">
      <h2>${r.manoeuvres.length > 0 ? '五' : '四'}、行為標記評估</h2>
      <p class="muted">錨定描述版本 ${r.anchorsVersion}(${r.anchorsStatus});1 至 5 分,2 分以下附時間戳與觀察描述(規劃書第 8.2 節)。</p>
      ${r.behavioural.length === 0 ? html`<p class="muted">未載入行為標記評估表</p>` : r.behavioural.map((b) => renderBehaviouralResult(b, anchors))}
      ${renderStcwMapping(anchors)}
    </section>

    <section class="sec" data-sec="debrief">
      <h2>${r.manoeuvres.length > 0 ? '六' : '五'}、講評紀錄(第 8.4 節講評結構)</h2>
      ${r.debrief.map((d) => html`<div class="debrief-part"><h3>${d.title}</h3>${d.text.trim() ? html`<p class="pre">${d.text}</p>` : html`<p class="muted hint">${d.hint}</p><div class="write-lines"></div>`}</div>`)}
      <div class="debrief-part"><h3>教官總評</h3>${m.remarks.trim() ? html`<p class="pre">${m.remarks}</p>` : html`<div class="write-lines"></div>`}</div>
    </section>

    <section class="sec" data-sec="parameters">
      <h2>${r.manoeuvres.length > 0 ? '七' : '六'}、參數集版次與來源文件</h2>
      ${kv([
        ['參數集', r.parameters.id],
        ['版次', r.parameters.version],
        ['路徑', r.parameters.path],
        ['SHA-256', shortHash(r.parameters.sha256)],
      ])}
      ${r.parameters.warnings.map((w) => html`<p class="warn">${w}</p>`)}
      ${r.parameters.documents.length === 0
        ? html`<p class="muted">紀錄未含參數集來源文件(舊紀錄或情境未引用公司程序參數)</p>`
        : html`<h3>來源程序文件</h3>
            <table class="tbl docs">
              <thead><tr><th>文件編號</th><th>版次</th><th>條款</th><th>引用參數數</th></tr></thead>
              <tbody>${r.parameters.documents.map((d) => html`<tr><td>${d.source}</td><td>${d.revisions.join('、') || '—'}</td><td>${d.clauses.join('、') || '—'}</td><td class="num">${d.keys.length}</td></tr>`)}</tbody>
            </table>`}
      ${r.parameters.rows.length > 0
        ? html`<h3>引用參數</h3>
            <table class="tbl params">
              <thead><tr><th>參數鍵</th><th>值</th><th>來源</th></tr></thead>
              <tbody>${r.parameters.rows.map((p) => html`<tr><td class="mono">${p.key}</td><td>${p.value === undefined ? '—' : `${String(p.value)}${p.unit ? ` ${p.unit}` : ''}`}</td><td>${srcText(p)}</td></tr>`)}</tbody>
            </table>`
        : nothing}
    </section>

    ${signatures(['教官', '評估員', '日期'])}
    <footer class="doc-foot">產生時間 ${localDateTime(r.generatedUtc)} · 紀錄鍵 ${r.exercise.recordKey.slice(0, 24)} · Simosa BRM 操船模擬器(訓練模擬器,非航行設備)</footer>
  </article>`;
}

export function restHoursText(rest: RestHoursConfirmation | undefined): string {
  if (!rest) return '未確認';
  return `${rest.confirmedBy || '—'} · ${localDateTime(rest.confirmedAtUtc)} · ${rest.compliant ? '符合' : '不符合'}${rest.note ? ` · ${rest.note}` : ''}`;
}

/** 紀錄標頭的參與者登錄與休息時間確認(教官站 sessionInfo;規劃書第 5.5 節)。 */
function renderSessionInfo(r: DebriefReport): TemplateResult {
  const si = r.sessionInfo;
  const rest = si?.restHoursConfirmation;
  return html`<h3>休息時間確認(STCW A-VIII/1、MLC 2006 規則 2.3)</h3>
    ${rest
      ? html`<dl class="kv rest-hours">
          <dt>確認人</dt><dd>${rest.confirmedBy || '—'}</dd>
          <dt>確認時刻</dt><dd>${localDateTime(rest.confirmedAtUtc)}(${rest.confirmedAtUtc})</dd>
          <dt>符合規定</dt><dd class=${rest.compliant ? '' : 'warn'}>${rest.compliant ? '是:已對照船上工時/休息時間紀錄核對,符合 STCW A-VIII/1 與公司規定' : '否(由訓練負責人決策照常執行)'}</dd>
          <dt>備註</dt><dd>${rest.note || '—'}</dd>
        </dl>`
      : html`<p class="muted rest-hours">紀錄沒有休息時間確認(教官站未於練習前送出 sessionInfo);請於紙本補註核對情形。</p>`}
    ${si && si.participants.length > 0
      ? html`<h3>教官站登錄之參與者(紀錄標頭)</h3>
          <table class="tbl session-participants">
            <thead><tr><th>姓名</th><th>職務</th><th>職級</th><th>證書號</th></tr></thead>
            <tbody>${si.participants.map((p) => html`<tr><td>${p.name}</td><td>${sessionRoleLabel(p.role)}</td><td>${p.rank ?? ''}</td><td class="mono">${p.certificateNo ?? '—'}</td></tr>`)}</tbody>
          </table>
          ${si.course ? html`<p class="muted small">課程:${[si.course.id, si.course.title, si.course.instructor ? `教官 ${si.course.instructor}` : ''].filter(Boolean).join(' · ')}</p>` : nothing}`
      : nothing}`;
}

function renderSummary(s: AssessmentSummary): TemplateResult {
  const a = s.assessment;
  const verdict = a.hardFailed ? html`<span class="badge fail">硬性失敗</span>` : a.passed === true ? html`<span class="badge ok">通過</span>` : a.passed === false ? html`<span class="badge fail">未達及格線</span>` : nothing;
  const tr = s.track;
  const man = s.manoeuvring;
  return html`
    <div class="score-line"><span class="score">${a.score ?? '—'}</span><span>/ 及格線 ${a.passMark ?? '—'}</span>${verdict}<span class="muted">${s.final ? '定稿' : '未定稿'}${s.parameterSet?.version ? ` · 參數集 ${s.parameterSet.version}` : ''}</span></div>
    <h3>硬性失敗</h3>
    ${a.hardFails.length === 0
      ? html`<p class="muted">無</p>`
      : html`<table class="tbl"><thead><tr><th>規則</th><th>時刻</th><th>值 / 門檻</th><th>參數</th></tr></thead>
          <tbody>${a.hardFails.map((h) => html`<tr class="sev-alarm"><td>${HARD_FAIL_RULE_LABEL[h.rule ?? ''] ?? h.rule ?? ''} ${h.id}</td><td>${tStr(h.t_s)}</td><td>${fix(h.value, 2)} / ${fix(h.limit, 2)}</td><td>${h.paramKey ?? ''}</td></tr>`)}</tbody></table>`}
    <h3>必要事件</h3>
    ${a.required.length === 0
      ? html`<p class="muted">情境無必要事件規則</p>`
      : html`<table class="tbl"><thead><tr><th>規則</th><th>結果</th><th>時間差 / 時限</th><th>扣分</th><th>程序來源</th></tr></thead>
          <tbody>${a.required.map((q) => html`<tr class=${q.state === 'missed' ? 'sev-alarm' : q.state === 'late' ? 'sev-warning' : ''}><td>${q.id}${q.by ? `(${ACK_KIND_LABEL[q.by] ?? q.by})` : ''}</td><td class="nw">${REQUIRED_STATE_LABEL[q.state]}</td><td class="nw">${fix(q.dt_s, 0, 's')} / ${fix(q.within_s, 0, 's')}</td><td class="num">${q.penalty ?? 0}</td><td>${srcText(q)}</td></tr>`)}</tbody></table>`}
    <h3>容許帶</h3>
    ${a.bands.length === 0
      ? html`<p class="muted">情境無容許帶</p>`
      : html`<table class="tbl"><thead><tr><th>指標</th><th>值</th><th>警告 / 失敗</th><th>狀態</th><th>扣分</th><th>程序來源</th></tr></thead>
          <tbody>${a.bands.map((b) => html`<tr class=${b.state === 'fail' ? 'sev-alarm' : b.state === 'warn' ? 'sev-warning' : ''}><td>${b.id}<span class="mono small"> ${b.metric ?? ''}</span></td><td class="nw">${fix(b.value, 2)}</td><td class="nw">${fix(b.warn, 2)} / ${fix(b.fail, 2)}</td><td class="nw">${BAND_STATE_LABEL[b.state]}</td><td class="num">${b.penalty ?? 0}</td><td>${srcText(b)}</td></tr>`)}</tbody></table>`}
    <h3>故障應變與警報確認</h3>
    ${s.faultResponses.length === 0
      ? html`<p class="muted">練習中無故障注入</p>`
      : html`<table class="tbl"><thead><tr><th>故障</th><th>注入</th><th>首個動作</th><th>確認</th><th>警報確認</th></tr></thead>
          <tbody>${s.faultResponses.map(
            (f) => html`<tr><td>${f.fault}${f.itemId ? ` (${f.itemId})` : ''}</td><td>${tStr(f.injectedAt_s)}</td><td>${f.firstAction ? `${f.firstAction.kind}${dtStr(f.firstAction.dt_s)}` : '—'}</td><td>${f.acknowledgements.length === 0 ? '—' : f.acknowledgements.map((k) => `${ACK_KIND_LABEL[k.kind] ?? k.kind}${dtStr(k.dt_s)}`).join('、')}</td><td>${f.alertAckDt_s !== undefined ? `+${f.alertAckDt_s.toFixed(0)} s` : '—'}</td></tr>`,
          )}</tbody></table>`}
    ${s.alerts
      ? kv([
          ['警報成立 / 確認 / 未確認', `${s.alerts.raised ?? 0} / ${s.alerts.acknowledged ?? 0} / ${s.alerts.unacknowledged ?? 0}`],
          ['平均 / 最長確認時間', `${fix(s.alerts.meanAckTime_s, 0, 's')} / ${fix(s.alerts.maxAckTime_s, 0, 's')}`],
          ['升級 / 靜音', `${s.alerts.escalations ?? 0} / ${s.alerts.silences ?? 0}`],
          ['BNWAS 第 1/2/3 階段 · 重設', `${s.alerts.bnwas.stage1 ?? 0}/${s.alerts.bnwas.stage2 ?? 0}/${s.alerts.bnwas.stage3 ?? 0} · ${s.alerts.bnwas.resets ?? 0}`],
        ])
      : nothing}
    <h3>會遇(避碰)</h3>
    ${s.collisionAvoidance.targets.length === 0
      ? html`<p class="muted">無會遇紀錄(會遇 ${s.collisionAvoidance.encounters ?? 0}、碰撞 ${s.collisionAvoidance.collisions ?? 0})</p>`
      : html`<table class="tbl"><thead><tr><th>目標</th><th>情境</th><th>偵測</th><th>首次行動</th><th>最小 CPA</th><th>BCR</th><th>改向 / 改速</th></tr></thead>
          <tbody>${s.collisionAvoidance.targets.map(
            (t) => html`<tr><td>${t.name ?? t.targetId}</td><td>${t.situation ?? ''}</td><td>${tStr(t.detectedAt_s)}</td><td>${t.firstAction ?? ''} ${tStr(t.firstActionAt_s)}</td><td>${fix(t.minCpa_nm, 2, 'nm')} @${tStr(t.minCpaAt_s)}</td><td>${fix(t.bcrMin_nm, 2, 'nm')}</td><td>${fix(t.courseChange_deg, 0, '°')} / ${fix(t.speedChange_kn, 1, 'kn')}${t.substantial ? '(大幅)' : ''}</td></tr>`,
          )}</tbody></table>`}
    <div class="grid2">
      <div>
        <h3>航跡</h3>
        ${tr
          ? kv([
              ['平均 |XTD|', fix(tr.meanAbsXtd_m, 1, 'm')],
              ['最大 XTD', `${fix(tr.maxXtd_m, 1, 'm')} @${tStr(tr.maxXtdAt_s)}`],
              ['走廊外時間 / 次數', `${fix(tr.timeOutsideCorridor_s, 0, 's')} / ${tr.corridorExcursions ?? 0}`],
              ['距禁區最近', fix(tr.minDistanceToNoGo_m, 0, 'm')],
              ['平均航向偏差', fix(tr.meanHeadingDeviation_deg, 1, '°')],
              ['航線完成', tr.complete === undefined ? '—' : tr.complete ? '是' : '否'],
            ])
          : html`<p class="muted">情境無航線</p>`}
      </div>
      <div>
        <h3>UKC</h3>
        ${kv([
          ['最小 UKC', `${fix(s.ukc.min_m, 2, 'm')} @${tStr(s.ukc.minAt_s)}`],
          ['最小時 squat', fix(s.ukc.minSquat_m, 2, 'm')],
          ['低於門檻時間 / 次數', `${fix(s.ukc.timeBelowLimit_s, 0, 's')} / ${s.ukc.excursions ?? 0}`],
          ['門檻', `${fix(s.ukc.limit_m, 2, 'm')}${s.ukc.limitSource ? `(${s.ukc.limitSource})` : ''}`],
          ['擱淺', s.ukc.groundings ?? 0],
        ])}
      </div>
    </div>
    <h3>操縱</h3>
    ${kv([
      ['練習時間 / 航程', `${tStr(s.general.duration_s)} / ${fix(s.general.distance_nm, 2, 'nm')}`],
      ['平均 / 最大 SOG', `${fix(s.general.avgSog_kn, 1, 'kn')} / ${fix(s.general.maxSog_kn, 1, 'kn')}`],
      ['舵令次數 / 滿舵次數', `${man.rudderOrders ?? 0} / ${man.hardOverCount ?? 0}`],
      ['最大 ROT', `${fix(man.maxRot_degPerMin, 1, '°/min')} @${tStr(man.maxRotAt_s)}`],
      ['ROT 超限時間', fix(man.timeRotOverLimit_s, 0, 's')],
      ['車令變更 / 側推使用', `${man.telegraphChanges ?? 0} / ${man.thrusterOrders ?? 0}`],
      ['超過速限時間', `${fix(man.timeOverSpeedLimit_s, 0, 's')}${man.speedLimitSource ? `(${man.speedLimitSource})` : ''}`],
      ['舵角分布', man.rudderDistribution_s ? Object.entries(man.rudderDistribution_s).map(([k, v]) => `${k}° ${v.toFixed(0)} s`).join('、') : '—'],
      ['階段', s.phases.map((p) => `${p.phase} ${tStr(p.from_s)}–${tStr(p.to_s)}`).join('、') || '—'],
    ])}
  `;
}

function renderLocal(r: DebriefReport): TemplateResult {
  const l = r.local;
  if (!l) return html`<p class="muted">紀錄沒有核心 summary,也沒有可推導的狀態。</p>`;
  return html`<p class="muted">紀錄沒有核心 summary(Python 參考伺服器或舊紀錄),以下為由狀態抽樣推導的本地摘要,門檻為常數。</p>
    ${kv([
      ['練習時間 / 航程', `${tStr(l.duration_s)} / ${fix(l.distance_nm, 2, 'nm')}`],
      ['平均 / 最大 SOG', `${fix(l.avgSpeed_kn, 1, 'kn')} / ${fix(l.maxSog_kn, 1, 'kn')}`],
      ['最小 UKC', `${fix(l.minUkc_m, 2, 'm')} @${tStr(l.minUkcAt_s)}`],
      ['最大舵角 / 滿舵次數 / 舵令次數', `${fix(l.maxRudder_deg, 0, '°')} / ${l.hardOverCount} / ${l.rudderOrders}`],
      ['最大 ROT', `${fix(l.maxRot_degPerMin, 1, '°/min')} @${tStr(l.maxRotAt_s)}`],
      ['車鐘變更', l.telegraphChanges],
      ['超過港區速限時間', fix(l.timeOverSpeedLimit_s, 0, 's')],
      ['擱淺 / 碰撞', `${l.agroundEvents} / ${l.collisionEvents}`],
    ])}`;
}

function renderManoeuvres(list: ManoeuvreRow[]): TemplateResult {
  return html`<section class="sec" data-sec="manoeuvres">
    <h2>三、操縱量測與試俥目標比較</h2>
    <p class="muted">目標值取自 data/ships/&lt;船&gt;/trial_targets.json(No.1 為試俥實測、No.2 為海報讀值、壓載為估計值;第 3.5 節),公差依第 6.4 節;條件不可比者僅供參考。</p>
    ${list.map(
      (m) => html`<div class="manoeuvre">
        <h3>${m.label} <span class="muted small">${m.id} · ${m.state === 'measured' ? (m.complete ? '完整量測' : '部分量測') : m.state === 'aborted' ? '中止' : '進行中'} · ${tStr(m.start_s)}–${tStr(m.end_s)}${m.approachSpeed_kn !== undefined ? ` · 進入速度 ${m.approachSpeed_kn.toFixed(1)} kn` : ''}${m.rudder_deg !== undefined ? ` · 舵 ${m.rudder_deg.toFixed(0)}°` : ''}${m.order ? ` · 車令 ${m.order}` : ''}</span></h3>
        ${m.comparisons.length > 0
          ? html`<table class="tbl manoeuvre-cmp">
              <thead><tr><th>項目</th><th>量測</th><th>目標</th><th>偏差</th><th>公差</th><th>判定</th><th>資料品質</th></tr></thead>
              <tbody>${m.comparisons.map(
                (c) => html`<tr class=${c.comparable && !c.withinTolerance ? 'sev-warning' : ''}><td>${c.label}</td><td class="num">${fix(c.measured, 1)} ${c.unit}</td><td class="num">${c.targetMin !== undefined && c.targetMax !== undefined ? `${fix(c.targetMin, 1)}–${fix(c.targetMax, 1)}` : fix(c.target, 1)} ${c.unit}</td><td class="num">${fix(c.deviation, 1)}${c.deviationPct !== undefined ? `(${c.deviationPct.toFixed(1)}%)` : ''}</td><td class="num">±${fix(c.tolerance, 1)}</td><td>${!c.comparable ? '條件不可比' : c.withinTolerance ? '公差內' : '超出公差'}</td><td>${DATA_QUALITY_LABEL[c.dataQuality] ?? c.dataQuality}</td></tr>`,
              )}</tbody>
            </table>`
          : html`<p class="muted">量測值:${m.values.map((v) => `${v.label} ${v.value.toFixed(1)} ${v.unit}`).join('、') || '—'}(無試俥目標可比較)</p>`}
        ${m.notes.length > 0 ? html`<ul class="notes">${m.notes.map((n) => html`<li>${n}</li>`)}</ul>` : nothing}
      </div>`,
    )}
  </section>`;
}

function scoreCell(score: number | null): TemplateResult {
  if (score === null) return html`<span class="muted">未評</span>`;
  return html`<span class="score-cell s${score}">${score}</span>`;
}

function renderBehaviouralResult(b: BehaviouralResult, anchors: BehaviouralAnchors): TemplateResult {
  const f = b.form;
  const sm = b.summary;
  const issues = validateMarkerForm(f, { anchors });
  const errors = issues.filter((i) => i.level === 'error').length;
  return html`<div class="behavioural">
    <h3>受評者:${f.trainee.name || '—'}${f.trainee.rank ? `(${f.trainee.rank})` : ''}${f.trainee.role ? ` · ${participantRoleLabel(f.trainee.role)}` : ''} — 評估員 ${f.assessor.name || '—'}(${f.assessor.role === 'primary' ? '主要' : '第二'})</h3>
    ${errors > 0 ? html`<p class="warn">評估表尚有 ${errors} 項未符合規定(例如 2 分以下缺時間戳或觀察描述)</p>` : nothing}
    <table class="tbl markers">
      <thead><tr><th>類別</th><th>要素</th><th class="num">分數</th><th>觀察(時刻 · 描述)</th><th>缺失分類</th><th>STCW</th></tr></thead>
      <tbody>${b.rows.map(
        (row) => html`<tr class=${row.score !== null && row.score <= anchors.lowScoreThreshold ? 'low' : ''}>
          <td>${row.categoryLabel}</td><td>${row.label}</td><td class="num">${scoreCell(row.score)}</td>
          <td>${row.observations.length === 0 ? '' : row.observations.map((o) => html`<div>${o.t_s === null ? '—' : tStr(o.t_s)} · ${o.text}</div>`)}${row.comment ? html`<div class="muted">${row.comment}</div>` : nothing}</td>
          <td>${row.deficiency}</td><td class="mono small">${row.stcw.join(' ')}</td>
        </tr>`,
      )}</tbody>
    </table>
    <p class="cat-means">平均 ${fix(sm.mean, 2)} · 最低 ${sm.min ?? '—'} · 已評 ${sm.rated}/${sm.total} · 2 分以下 ${sm.lowCount} 項 · ${sm.categories.map((c) => `${c.label} ${fix(c.mean, 1)}`).join('、')}${sm.deficiencies.hardware + sm.deficiencies.procedure + sm.deficiencies.human > 0 ? ` · 缺失 硬體 ${sm.deficiencies.hardware}/程序 ${sm.deficiencies.procedure}/人員 ${sm.deficiencies.human}` : ''}</p>
    ${f.overallComment ? html`<p class="pre">${f.overallComment}</p>` : nothing}
    ${b.second ? renderAgreement(b.second.form, b.second.agreement) : nothing}
  </div>`;
}

export function renderAgreement(second: MarkerForm, a: AgreementResult): TemplateResult {
  const label = (id: string) => allElements().find((e) => e.id === id)?.label ?? id;
  return html`<div class="agreement">
    <h4>雙評一致率(第二評估員 ${second.assessor.name || '—'})</h4>
    <p>±${a.tolerance} 分內 ${a.withinTolerance}/${a.compared} 項 = <strong>${a.rate === null ? '—' : `${(a.rate * 100).toFixed(0)}%`}</strong>(已比較 ${a.compared}/${a.total} 項;目標 ≥ ${(a.target * 100).toFixed(0)}%:<span class="agreement-verdict">${a.complete ? (a.meetsTarget ? '達成' : '未達成') : `未完成(缺 ${a.total - a.compared} 項)`}</span>);完全一致 ${a.exact} 項</p>
    ${a.complete ? nothing : html`<p class="warn">兩份評估表都評完全部 ${a.total} 項後才判定一致率是否達成(第 8.2 節校準)</p>`}
    ${a.differences.length > 0 ? html`<p class="warn">需協調:${a.differences.map((d) => `${label(d.elementId)}(${d.a} vs ${d.b})`).join('、')}</p>` : nothing}
    ${a.missing.length > 0 ? html`<p class="muted">未比較(任一方未評分):${a.missing.map(label).join('、')}</p>` : nothing}
    ${a.warnings.map((w) => html`<p class="warn">${w}</p>`)}
  </div>`;
}

function renderStcwMapping(anchors: BehaviouralAnchors): TemplateResult {
  const rows = stcwMapping(anchors);
  return html`<h3>要素與 STCW 準則對應</h3>
    <table class="tbl stcw">
      <thead><tr><th>準則</th><th>內容</th><th>對應要素</th></tr></thead>
      <tbody>${rows.map((r) => html`<tr><td class="mono">${r.id}</td><td>${r.text}<div class="muted small">${r.group}</div></td><td>${r.elements.join('、')}</td></tr>`)}</tbody>
    </table>`;
}

// ------------------------------------------------------------------ 行為標記評估表(列印版)

export function renderFormDoc(form: MarkerForm, ctx: { scenario: string; reportNo: string; courseLine: string }, anchors: BehaviouralAnchors = ANCHORS): TemplateResult {
  const sm = summarizeForm(form, anchors);
  return html`<article class="sheet form-doc" data-doc="form">
    ${sheetHeader('中塑海運內部訓練', '行為標記評估表 Behavioural Marker Assessment', ctx.courseLine, `評估表 ${form.formId}`)}
    <p class="advisory">錨定描述版本 ${anchors.version}(${anchors.statusLabel})· 1 不可接受、2 待改進、3 可接受、4 良好、5 優秀;2 分以下必須附時間戳與觀察描述(規劃書第 8.2 節)。</p>
    ${kv([
      ['練習', ctx.scenario],
      ['報告編號', ctx.reportNo],
      ['受評者', `${form.trainee.name || '—'}${form.trainee.rank ? `(${form.trainee.rank})` : ''}${form.trainee.role ? ` · ${participantRoleLabel(form.trainee.role)}` : ''}`],
      ['評估員', `${form.assessor.name || '—'}(${form.assessor.role === 'primary' ? '主要評估員' : '第二評估員'})`],
      ['評分時間', localDateTime(form.assessedUtc)],
      ['平均 / 最低', `${fix(sm.mean, 2)} / ${sm.min ?? '—'}(已評 ${sm.rated}/${sm.total})`],
    ])}
    ${anchors.categories.map(
      (c) => html`<section class="sec form-cat">
        <h2>${c.label} ${c.en}</h2>
        ${c.elements.map((e) => {
          const r = form.ratings.find((x) => x.elementId === e.id);
          const def = r?.deficiency;
          return html`<div class="form-el">
            <div class="form-el-head"><span class="el-label">${e.label}</span><span class="el-score">${scoreCell(r?.score ?? null)} ${r?.score ? scaleLabel(r.score, anchors) : ''}</span></div>
            <table class="anchors-tbl"><tr><td><b>1</b> ${e.anchors['1']}</td><td><b>3</b> ${e.anchors['3']}</td><td><b>5</b> ${e.anchors['5']}</td></tr></table>
            <div class="el-obs">${(r?.observations ?? []).map((o) => html`<div>${o.t_s === null ? '—' : tStr(o.t_s)} · ${o.text}</div>`)}${r?.comment ? html`<div class="muted">${r.comment}</div>` : nothing}</div>
            <div class="el-meta">缺失:${def?.type ? `${anchors.deficiencyTypes.find((d) => d.id === def.type)?.label ?? ''}${def.type === 'human' && def.humanFactor ? `/${anchors.humanFactors.find((h) => h.id === def.humanFactor)?.label ?? ''}` : ''}` : '—'} · STCW ${e.stcw.map((s) => html`<span title=${stcwText(s)}>${s}</span> `)}</div>
          </div>`;
        })}
      </section>`,
    )}
    ${form.overallComment ? html`<section class="sec"><h2>綜合評語</h2><p class="pre">${form.overallComment}</p></section>` : nothing}
    ${signatures(['評估員', '日期'])}
  </article>`;
}

// ------------------------------------------------------------------ 個人訓練紀錄

function autoScoreText(r: TraineeResult): string {
  const verdict = r.hardFailed ? '硬性失敗' : r.passed === true ? '通過' : r.passed === false ? '未達及格線' : '';
  if (r.score === undefined && !verdict) return '—';
  return `${r.score ?? '—'}${verdict ? ` ${verdict}` : ''}`;
}

function markerText(r: TraineeResult): string {
  if (r.formIncomplete) return '評估表未完成';
  return `${r.markerMean ?? '—'} / ${r.markerMin ?? '—'}`;
}

export function renderIndividualDoc(rec: IndividualTrainingRecord): TemplateResult {
  return html`<article class="sheet individual-doc" data-doc="individual">
    ${sheetHeader('中塑海運內部訓練', '個人訓練紀錄 Individual Training Record', `${rec.courseName}${rec.courseVersion ? ` 第 ${rec.courseVersion} 版` : ''}`, `紀錄編號 ${rec.recordNo}`)}
    <p class="statement">${rec.statement}</p>
    ${kv([
      ['學員', `${rec.trainee.name}(ID ${rec.trainee.id})`],
      ['職級', rec.trainee.rank],
      ['課程', `${rec.courseName}${rec.courseVersion ? ` 第 ${rec.courseVersion} 版` : ''}`],
      ['訓練形式', trainingFormLabel(rec.trainingForm)],
      ['船舶', rec.shipId],
      ['日期', `${rec.startDate || '—'} 至 ${rec.endDate || '—'}`],
      ['時數', `出席 ${rec.attendedHours} h / 本期 ${rec.sessionHours} h`],
      ['出席練習', rec.attendedExercises.join('、') || '—'],
      ['完成練習', rec.completedExercises.join('、') || '—'],
      ['授課教官', rec.instructors.join('、')],
      ['評估員', rec.assessors.join('、')],
      ...(rec.trainee.certificateExpiry ? ([['法定 BRM 證書到期日', rec.trainee.certificateExpiry]] as Array<[string, unknown]>) : []),
    ])}
    <h2>評估結果摘要</h2>
    ${rec.results.length === 0
      ? html`<p class="muted">本期尚無練習</p>`
      : html`<table class="tbl results">
          <thead><tr><th>練習</th><th>日期</th><th class="num">時數</th><th>出席 / 完成</th><th>角色</th><th>自動評分(輔助)</th><th class="num">行為標記平均 / 最低</th><th>評估員判定</th><th>備註</th><th>休息時間確認</th><th>報告編號</th></tr></thead>
          <tbody>${rec.results.map(
            (r) => html`<tr><td>${r.exerciseNo}<div class="muted small break-any">${r.scenarioId}</div></td><td class="nw">${r.date}</td><td class="num">${r.hours}</td><td class="nw">${r.attended ? '是' : '否'} / ${r.completed ? '是' : '否'}</td><td class="nw role">${r.role ? participantRoleLabel(r.role) : '—'}</td><td class="nw">${autoScoreText(r)}</td><td class="num">${markerText(r)}</td><td class="nw verdict">${r.verdict ? VERDICT_LABEL[r.verdict] : '—'}</td><td class="remark">${r.remark ?? ''}</td><td class="small rest-hours ${r.restHoursConfirmation && !r.restHoursConfirmation.compliant ? 'warn' : ''}">${r.attended ? restHoursText(r.restHoursConfirmation) : '—'}</td><td class="mono small">${r.reportNo}</td></tr>`,
          )}</tbody>
        </table>`}
    <p class="muted">「評估員判定」為評估員對該學員的通過/未通過判定與備註(R9;補訓與複查流程待 D20);「自動評分」為團隊層級且僅作輔助(規劃書第 8.3、8.4 節)。「評估表未完成」表示主要評估員的評估表未評滿十五項或 2 分以下缺時間戳與觀察描述,不計入行為標記分數。「角色」與「休息時間確認」(確認人 · 時刻 · 是否符合 STCW A-VIII/1 與公司規定)由講評紀錄帶入(規劃書第 5.5 節);訓練時數供轉錄至船上工時/休息時間紀錄。</p>
    ${rec.evaluation ? html`<h2>課後評估</h2><p class="pre">${rec.evaluation}</p>` : nothing}
    ${signatures(['學員', '授課教官', '訓練負責人'])}
    <footer class="doc-foot">${INTERNAL_TRAINING_STATEMENT}</footer>
  </article>`;
}

// ------------------------------------------------------------------ 課程班次紀錄

/** 出席矩陣每張表最多幾個練習欄(A4 直式內容寬約 180 mm;學員一列,不隨人數加寬)。 */
export const ATTENDANCE_COLUMNS = 10;

function chunk<T>(list: readonly T[], n: number): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < list.length; i += n) out.push(list.slice(i, i + n));
  return out;
}

/** 出席矩陣:每列一位學員、每欄一個練習;練習多於 ATTENDANCE_COLUMNS 時分成多張表。 */
function attendanceTables(session: CourseSession): TemplateResult {
  const groups = chunk<SessionExercise>(session.exercises, ATTENDANCE_COLUMNS);
  return html`${groups.map(
    (g, gi) => html`<table class="tbl attendance">
      ${groups.length > 1 ? html`<caption class="muted small">出席矩陣 ${gi + 1}/${groups.length}</caption>` : nothing}
      <thead><tr><th>學員</th>${g.map((e) => html`<th class="att" title=${e.scenarioId}>${e.exerciseNo || e.scenarioId}<div class="muted small">${e.date.slice(5)}</div></th>`)}</tr></thead>
      <tbody>${session.trainees.map(
        (t) => html`<tr><td>${t.name || t.id}<span class="muted small"> ${t.id}</span></td>${g.map((e) => html`<td class="att">${e.completed.includes(t.id) ? '完成' : e.attended.includes(t.id) ? '出席' : '—'}</td>`)}</tr>`,
      )}</tbody>
    </table>`,
  )}`;
}

export function renderSessionDoc(session: CourseSession): TemplateResult {
  const issues = validateCourseSession(session);
  return html`<article class="sheet session-doc" data-doc="session">
    ${sheetHeader('中塑海運內部訓練', '課程班次紀錄 Course Session Record', `${session.courseName}${session.courseVersion ? ` 第 ${session.courseVersion} 版` : ''}`, `班次 ${session.sessionId || '—'}`)}
    ${issues.length > 0 ? html`<ul class="warn screen-only">${issues.map((i) => html`<li>${i}</li>`)}</ul>` : nothing}
    ${kv([
      ['課程', `${session.courseName}${session.courseVersion ? ` 第 ${session.courseVersion} 版` : ''}`],
      ['訓練形式', trainingFormLabel(session.trainingForm)],
      ['船舶', session.shipId],
      ['開課日期', `${session.startDate || '—'} 至 ${session.endDate || '—'}`],
      ['總時數', `${sessionHours(session)} h(${session.exercises.length} 個練習)`],
      ['授課教官', session.instructors.join('、')],
      ['評估員', session.assessors.join('、')],
      ['設備配置', session.equipment],
    ])}
    <h2>參訓學員</h2>
    <table class="tbl"><thead><tr><th>ID</th><th>姓名</th><th>職級</th><th class="num">出席練習數</th><th class="num">出席時數</th></tr></thead>
      <tbody>${session.trainees.map((t) => {
        const att = session.exercises.filter((e) => e.attended.includes(t.id));
        return html`<tr><td class="mono">${t.id}</td><td>${t.name}</td><td>${t.rank}</td><td class="num">${att.length}</td><td class="num">${Math.round(att.reduce((s, e) => s + e.hours, 0) * 100) / 100}</td></tr>`;
      })}</tbody>
    </table>
    <h2>練習與出席</h2>
    ${session.exercises.length === 0
      ? html`<p class="muted">尚無練習</p>`
      : html`<table class="tbl exercises">
            <thead><tr><th>練習</th><th>日期</th><th class="num">時數</th><th>教官 / 評估員</th><th>設備配置</th><th class="num">出席 / 完成</th></tr></thead>
            <tbody>${session.exercises.map(
              (e) => html`<tr><td>${e.exerciseNo}<div class="muted small break-any">${e.scenarioId}</div><div class="muted small mono">${e.reportNo}</div></td><td class="nw">${e.date}</td><td class="num">${e.hours}</td><td>${e.instructor}${e.assessors.length ? ` / ${e.assessors.join('、')}` : ''}</td><td class="small">${e.equipment}</td><td class="num">${e.attended.length} / ${e.completed.length}</td></tr>`,
            )}</tbody>
          </table>
          ${session.trainees.length === 0 ? nothing : attendanceTables(session)}`}
    <h2>課後評估</h2>
    ${session.evaluation ? html`<p class="pre">${session.evaluation}</p>` : html`<div class="write-lines"></div>`}
    ${signatures(['授課教官', '訓練負責人', '日期'])}
    <footer class="doc-foot">${INTERNAL_TRAINING_STATEMENT}</footer>
  </article>`;
}

// ------------------------------------------------------------------ 教官資格紀錄與證書效期(?tab=admin)

function flagCell(flag: ExpiryFlag, days: number | null): TemplateResult {
  const cls = flag === 'expired' ? 'flag expired' : flag === 'soon' ? 'flag soon' : flag === 'upcoming' ? 'flag upcoming' : 'flag';
  return html`<span class=${cls}>${EXPIRY_FLAG_LABEL[flag]}${days !== null && flag !== 'none' ? `(${days >= 0 ? `${days} 天` : `逾 ${-days} 天`})` : ''}</span>`;
}

function rateText(rate: number | null | undefined): string {
  return rate === null || rate === undefined ? '—' : `${Math.round(rate * 100)}%`;
}

/**
 * 教官資格紀錄、學員證書效期雙軌追蹤與複訓排程(列印版;規劃書 R9/R10、第 8.0 節複訓、第 8.3 節評估者校準、第 14 章評估列)。
 * today 為計算到期狀態的日期(YYYY-MM-DD)。
 */
export function renderAdminDoc(admin: AdminRecords, today: string, schedule: readonly ScheduleQuarter[]): TemplateResult {
  const issues = validateAdminRecords(admin);
  const instructors = instructorRows(admin, today);
  const tracks = dualTrackRows(admin, today);
  const counts = { expired: tracks.filter((r) => r.flag === 'expired').length, soon: tracks.filter((r) => r.flag === 'soon').length, upcoming: tracks.filter((r) => r.flag === 'upcoming').length };
  return html`<article class="sheet admin-doc" data-doc="admin">
    ${sheetHeader('中塑海運內部訓練', '教官資格紀錄與證書效期追蹤 Instructor Qualifications & Certificate Tracking', `複訓週期 ${admin.refresherIntervalYears} 年 · 狀態計算日 ${today}`, `學員 ${admin.tracks.length} · 教官 ${admin.instructors.length}`)}
    ${issues.length > 0 ? html`<ul class="warn screen-only">${issues.map((i) => html`<li>${i}</li>`)}</ul>` : nothing}
    <section class="sec" data-sec="instructors">
      <h2>一、教官資格紀錄(STCW A-I/6;R10、D18)</h2>
      ${instructors.length === 0
        ? html`<p class="muted">尚無教官</p>`
        : html`<table class="tbl instructors">
            <thead><tr><th>姓名</th><th>A-I/6 訓練師資格<div class="muted small">課程 / 日期</div></th><th>評估者資格(A-I/6 第 7–8 段)<div class="muted small">課程 / 日期</div></th><th>該型模擬器實務經驗</th><th>最近校準<div class="muted small">日期 · 基準錄影 · 一致率</div></th><th>資格到期日</th></tr></thead>
            <tbody>${instructors.map(
              (r) => html`<tr class=${r.flag === 'expired' ? 'sev-alarm' : r.flag === 'soon' ? 'sev-warning' : ''}>
                <td>${r.instructor.name || r.instructor.id}<div class="muted small mono">${r.instructor.id}</div></td>
                <td>${r.instructor.trainerCourse || '—'}<div class="nw small">${r.instructor.trainerDate || '—'}</div></td>
                <td>${r.instructor.assessorCourse || '—'}<div class="nw small">${r.instructor.assessorDate || '—'}</div></td>
                <td class="small">${r.instructor.simulatorExperience || '—'}</td>
                <td class="small">${r.latestCalibration ? html`${r.latestCalibration.date} · ${r.latestCalibration.referenceVideo || '—'} · <b class=${r.calibrated ? '' : 'warn'}>${rateText(r.latestCalibration.agreementRate)}</b>${r.calibrated ? '(可獨立評分)' : `(未達 ${Math.round(CALIBRATION_TARGET * 100)}%,需補做校準)`}${r.recalibrationDue ? html`<div class="warn">逾一年,需再校準</div>` : nothing}` : html`<span class="warn">尚未校準</span>`}</td>
                <td class="nw">${r.instructor.expiryDate || '—'}<div class="small">${flagCell(r.flag, r.expiryDays)}</div></td>
              </tr>`,
            )}</tbody>
          </table>
          ${instructors.some((r) => r.instructor.calibrations.length > 0)
            ? html`<h3>評估者校準紀錄(第 8.3 節:3 段基準錄影;≥${Math.round(CALIBRATION_TARGET * 100)}% 要素在 ±1 分內)</h3>
                <table class="tbl calibrations">
                  <thead><tr><th>教官</th><th>日期</th><th>基準錄影</th><th class="num">一致率</th><th>結果</th><th>備註</th></tr></thead>
                  <tbody>${instructors.flatMap((r) =>
                    [...r.instructor.calibrations]
                      .sort((a, b) => a.date.localeCompare(b.date))
                      .map((c) => html`<tr><td>${r.instructor.name || r.instructor.id}</td><td class="nw">${c.date || '—'}</td><td>${c.referenceVideo || '—'}</td><td class="num">${rateText(c.agreementRate)}</td><td class="nw">${c.agreementRate === null ? '—' : c.agreementRate >= CALIBRATION_TARGET ? '達成' : '未達成'}</td><td class="small">${c.note ?? ''}</td></tr>`),
                  )}</tbody>
                </table>`
            : nothing}`}
    </section>
    <section class="sec" data-sec="tracks">
      <h2>二、學員證書效期雙軌追蹤(R9:法定證書與公司 BRM 複訓)</h2>
      <p class="muted">公司複訓到期日 = 上次完成日 + ${admin.refresherIntervalYears} 年(規劃書第 8.0 節、D19);「對齊」= 與法定證書換證同步。已逾期 ${counts.expired}、90 天內 ${counts.soon}、180 天內 ${counts.upcoming}。</p>
      ${tracks.length === 0
        ? html`<p class="muted">尚無學員(可由名冊同步)</p>`
        : html`<table class="tbl tracks">
            <thead><tr><th>學員</th><th>職級</th><th>法定證書<div class="muted small">類別 · 號碼</div></th><th>法定到期</th><th>上次完成</th><th>公司複訓到期</th><th>最近到期</th><th>備註</th></tr></thead>
            <tbody>${tracks.map(
              (r) => html`<tr class="${r.flag === 'expired' ? 'sev-alarm' : r.flag === 'soon' ? 'sev-warning' : ''} ${r.flag}">
                <td>${r.track.name || r.track.traineeId}<div class="muted small mono">${r.track.traineeId}</div></td>
                <td>${r.track.rank}</td>
                <td class="small">${r.track.statutoryType || '—'}<div class="mono">${r.track.statutoryNo || '—'}</div></td>
                <td class="nw">${r.track.statutoryExpiry || '—'}<div class="small">${flagCell(r.statutoryFlag, r.statutoryDays)}</div></td>
                <td class="nw">${r.track.lastCompletedDate || '—'}</td>
                <td class="nw">${r.track.refresherDue || '—'}${r.track.alignedWithStatutory ? html`<div class="muted small">已對齊換證</div>` : nothing}<div class="small">${flagCell(r.companyFlag, r.companyDays)}</div></td>
                <td class="nw next-due">${r.nextDue || '—'}<div class="muted small">${r.nextDueKind === 'statutory' ? '法定' : r.nextDueKind === 'company' ? '公司複訓' : r.nextDueKind === 'both' ? '同步' : ''}</div></td>
                <td class="small">${r.track.note}</td>
              </tr>`,
            )}</tbody>
          </table>`}
    </section>
    <section class="sec" data-sec="schedule">
      <h2>三、複訓排程建議(季度)</h2>
      <p class="muted">建議完成日 = 到期日前 ${REFRESHER_LEAD_DAYS} 天(到期前一年內在船時排入;已逾期者排入本季);實際排程依航次與訪船時間由訓練負責人決定。</p>
      ${schedule.length === 0
        ? html`<p class="muted">尚無到期日可排程</p>`
        : schedule.map(
            (q) => html`<h3>${q.quarter}</h3>
              <table class="tbl schedule">
                <thead><tr><th>建議完成</th><th>到期日</th><th>項目</th><th>學員</th><th>職級</th></tr></thead>
                <tbody>${q.items.map((it) => html`<tr class=${it.overdue ? 'sev-alarm' : ''}><td class="nw">${it.plannedDate}</td><td class="nw">${it.dueDate}${it.overdue ? '(已逾期)' : ''}</td><td>${SCHEDULE_KIND_LABEL[it.kind]}</td><td>${it.name || it.traineeId}<span class="muted small"> ${it.traineeId}</span></td><td>${it.rank}</td></tr>`)}</tbody>
              </table>`,
          )}
    </section>
    ${signatures(['訓練負責人', '日期'])}
    <footer class="doc-foot">${INTERNAL_TRAINING_STATEMENT}</footer>
  </article>`;
}
