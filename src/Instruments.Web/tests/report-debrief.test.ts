// 講評報告(規劃書第 8.4、9.2 節):由核心紀錄夾具 record-E07-core.jsonl 產生的練習資訊、核心 summary、事件時間軸、
// 行為標記結果與雙評一致率、講評五段、參數集版次與來源文件編號;F01 操縱量測(summary.manoeuvres[])與試俥目標比較表;
// 列印樣板含頁首標示「中塑海運內部訓練」。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { render } from 'lit';
import { describe, expect, it } from 'vitest';
import { parseReplay } from '../src/lib/record-replay.js';
import {
  buildDebriefReport,
  DEBRIEF_SECTIONS,
  defaultReportNo,
  emptyReportMeta,
  exerciseNoFromScenario,
  manoeuvreLabel,
  parseManoeuvres,
  recordKey,
  REPORT_HEADER_LABEL,
  reportOutline,
  splitValueKey,
  stcwMapping,
} from '../src/report/debrief-report.js';
import { createMarkerForm, ratingOf } from '../src/report/marker-form.js';
import { renderFormDoc, renderReportDoc } from '../src/report/report-views.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const E07 = readFileSync(path.join(here, 'fixtures/record-E07-core.jsonl'), 'utf8');
const E07_SHA = '502CC597A0B143A41B99A0C5155F29B028029A9BCF63F819C7BE188998B34728';
const NOW = new Date('2026-10-17T12:00:00Z');

function html(t: ReturnType<typeof renderReportDoc>): HTMLElement {
  const div = document.createElement('div');
  render(t, div);
  return div;
}

/** F01 用的操縱量測(形狀同 SimCore ManoeuvreRecord;值為測試用) */
const MANOEUVRES = [
  {
    id: 'turning35.port',
    kind: 'turning',
    side: 'port',
    state: 'measured',
    complete: true,
    start_s: 60,
    end_s: 520,
    shipId: 'FSB1',
    loading: 'full',
    dynamics: 'mmg/1',
    rudder_deg: -35,
    approach: { speed_kn: 12.1, sog_kn: 12.1, heading_deg: 0, rpm: 120, telegraph: 'FAH', steady: true, steadyFor_s: 30, fromInitial: true, autopilot: false },
    environment: { windSpeed_kn: 0, windDir_deg: 0, currentDrift_kn: 0, currentSet_deg: 0, waterDepth_m: 200, depthRatio: 25 },
    values: { advance_m: 412.3, transfer_m: 190.4, tacticalDiameter_m: 455.1 },
    marks: [],
    comparisons: [
      { key: 'advance_m', measured: 412.3, target: 400, deviation: 12.3, deviation_pct: 3.1, tolerance: 40, withinTolerance: true, comparable: true, dataQuality: 'trial', source: 'identification.turningCircle35.port.advance_m' },
      { key: 'tacticalDiameter_m', measured: 455.1, target: 380, deviation: 75.1, deviation_pct: 19.8, tolerance: 38, withinTolerance: false, comparable: true, dataQuality: 'trial', source: 'identification.turningCircle35.port.tacticalDiameter_m' },
      { key: 'steadySpeed_kn', measured: 4.5, target: 4.75, targetMin: 4.6, targetMax: 4.9, deviation: -0.1, tolerance: 0.3, withinTolerance: true, comparable: false, dataQuality: 'poster', source: 'x' },
    ],
    dataQuality: 'trial',
    notes: ['No.2 海報值為圖面讀值'],
  },
  { id: 'crashStop', kind: 'crashStop', state: 'aborted', complete: false, start_s: 600, approach: {}, environment: {}, values: { stopTime_s: 200 }, marks: [], comparisons: [], dataQuality: 'none', notes: [] },
];

function withManoeuvres(text: string): string {
  return text
    .split('\n')
    .map((l) => {
      if (!l.includes('"kind":"summary"')) return l;
      const o = JSON.parse(l) as { summary: Record<string, unknown> };
      o.summary['manoeuvres'] = MANOEUVRES;
      return JSON.stringify(o);
    })
    .join('\n');
}

describe('由核心紀錄 E07 產生講評報告', () => {
  const data = parseReplay(E07);
  const meta = emptyReportMeta();
  meta.courseVersion = '1.0';
  meta.instructor = '陳船長';
  meta.participants = [
    { traineeId: 'T001', name: '王大明', rank: '船長', role: 'master' },
    { traineeId: 'T002', name: '李小華', rank: '二副', role: 'oow' },
  ];
  meta.debrief.reaction = '團隊覺得引水人登船後資訊量大';

  it('練習資訊、紀錄識別與報告編號', () => {
    const r = buildDebriefReport({ data, meta, now: NOW });
    expect(r.headerLabel).toBe('中塑海運內部訓練');
    expect(r.exercise).toMatchObject({
      scenarioId: 'E07_steering_failure',
      scenarioName: '麥寮進港航道舵機故障',
      exerciseNo: 'E07',
      shipId: 'FSB1',
      shipName: 'FS BITUMEN NO.1',
      loading: 'full',
      loadingLabel: '滿載',
      duration_s: 300,
      seed: 20260707,
      dynamics: 'mmg/1',
      source: 'simcore-jsonl',
      scenarioStartUtc: '2026-07-07T06:30:00Z',
      stateHash: '5F8F3DD099E748F8EC2BAD4A75A08A6C4848CA8288EBA5078AD60266B19FC44A',
      sha256: E07_SHA,
    });
    expect(recordKey(data)).toBe(`sha256:${E07_SHA}`);
    expect(r.reportNo).toBe(defaultReportNo(data));
    expect(r.reportNo).toBe('RPT-20261017-E07-502CC5');
    expect(exerciseNoFromScenario('F01_FSB2_ballast')).toBe('F01');
    expect(buildDebriefReport({ data, meta: { ...meta, reportNo: 'R-1' } }).reportNo).toBe('R-1');
  });

  it('核心 summary:評分、容許帶、必要事件、UKC、操縱;無本地推導', () => {
    const r = buildDebriefReport({ data, meta });
    expect(r.summary?.assessment.score).toBe(85);
    expect(r.summary?.assessment.passMark).toBe(70);
    expect(r.summary?.assessment.passed).toBe(true);
    expect(r.summary?.assessment.bands.map((b) => b.id)).toEqual(['xtdMax', 'xtdTime', 'ukcTime', 'speed', 'minCpa']);
    expect(r.summary?.ukc.min_m).toBeCloseTo(8.196, 3);
    expect(r.local).toBeNull();
    expect(r.manoeuvres).toEqual([]);
  });

  it('事件時間軸依時間排序,含 xtdExceeded 與 bandWarn', () => {
    const r = buildDebriefReport({ data, meta });
    expect(r.timeline.map((e) => e.kind)).toEqual(['timelineTriggered', 'voiceScript', 'message', 'targetActivated', 'xtdExceeded', 'bandWarn', 'bandWarn']);
    expect(r.timeline[4]).toMatchObject({ t: 271, severity: 'warning', source: '評估' });
    expect(r.timelineOmitted).toBe(0);
    // 另外載入的 BRM 快速標記併入時間軸
    const withMarker = buildDebriefReport({ data, meta, extraMarkers: [{ id: 1, category: 'communication', t: 120, wall: '', note: '舵工未複誦', polarity: 'delta' }] });
    const row = withMarker.timeline.find((e) => e.kind === 'brmMarker')!;
    expect(row.label).toBe('Δ 溝通:舵工未複誦');
    expect(withMarker.markers).toHaveLength(1);
  });

  it('參數集版次與來源程序文件編號', () => {
    const r = buildDebriefReport({ data, meta });
    expect(r.parameters.id).toBe('simosa-company-procedures');
    expect(r.parameters.version).toBe('2026.10.1');
    expect(r.parameters.sha256).toBe('058285D5DE1EB5D1EEBA0A424D76A5A6F726C437BA457B471FC42365B8B5F538');
    expect(r.parameters.warnings).toEqual([]);
    const docs = Object.fromEntries(r.parameters.documents.map((d) => [d.source, d]));
    expect(Object.keys(docs).sort()).toEqual(['BRM-TRN-001', 'DWO-007', 'PORT-MLW', 'SMS-EMG-004', 'SMS-NAV-012']);
    expect(docs['DWO-007']!.revisions).toEqual(['3']);
    expect(docs['SMS-NAV-012']!.clauses).toEqual(expect.arrayContaining(['3.2', '3.3', '4.1']));
    const xtd = r.parameters.rows.find((p) => p.key === 'navigation.xtd.channel_m')!;
    expect(xtd).toMatchObject({ value: 50, unit: 'm', source: 'SMS-NAV-012', revision: '2', clause: '4.1' });
    // header.parameterSet.resolved 的引用路徑
    expect(xtd.paths.length).toBeGreaterThan(0);
    expect(reportOutline(r)).toContain('參數集 simosa-company-procedures 2026.10.1');
  });

  it('行為標記結果:主要評估員的表與第二評估員一致率', () => {
    const key = recordKey(data);
    const a = createMarkerForm({ record: { key }, trainee: { id: 'T001', name: '王大明', role: 'master' }, assessor: { name: '陳船長' }, now: NOW });
    const b = createMarkerForm({ record: { key }, trainee: { id: 'T001', name: '王大明' }, assessor: { name: '林船長', role: 'second' }, now: NOW });
    a.ratings.forEach((r, i) => (r.score = 3 + (i % 3 === 0 ? 1 : 0)));
    b.ratings.forEach((r, i) => (r.score = i < 2 ? 1 : 3));
    const cl = ratingOf(a, 'communication.closedLoop')!;
    cl.score = 2;
    cl.observations.push({ t_s: 181, text: '右舵 10 未複誦' });
    cl.deficiency = { type: 'human', humanFactor: 'stressLanguage' };
    const other = createMarkerForm({ record: { key: 'sha256:OTHER' }, trainee: { id: 'T009', name: '別班' }, assessor: { name: 'X' }, now: NOW });
    const r = buildDebriefReport({ data, meta, forms: [a, b, other] });
    expect(r.behavioural).toHaveLength(1);
    const res = r.behavioural[0]!;
    expect(res.form.formId).toBe(a.formId);
    const row = res.rows.find((x) => x.elementId === 'communication.closedLoop')!;
    expect(row).toMatchObject({ score: 2, deficiency: '人員/緊張/語言', stcw: ['BRM2', 'LT5'] });
    expect(row.observations).toEqual([{ t_s: 181, text: '右舵 10 未複誦' }]);
    expect(res.second?.agreement.compared).toBe(15);
    expect(res.second?.agreement.withinTolerance).toBe(13);
    expect(res.second?.agreement.meetsTarget).toBe(true);
    expect(r.anchorsStatus).toBe('草案,待訓練負責人核准');
  });

  it('STCW 準則 → 要素對應自動列出', () => {
    const map = stcwMapping();
    expect(map).toHaveLength(13);
    const brm2 = map.find((m) => m.id === 'BRM2')!;
    expect(brm2.group).toBe('STCW A-II/1 BRM 評量準則');
    expect(brm2.elements).toEqual(['溝通:閉環溝通/複誦', '溝通:清楚的簡報', '溝通:分享意圖與計畫']);
  });

  it('列印樣板:頁首標示、各章節、講評五段、參與者、來源文件', () => {
    const r = buildDebriefReport({ data, meta, now: NOW });
    const el = html(renderReportDoc(r));
    const text = el.textContent ?? '';
    expect(el.querySelector('.doc-org')!.textContent).toBe(REPORT_HEADER_LABEL);
    expect(el.querySelector('.doc-sub')!.textContent).toContain('第 1.0 版');
    for (const s of ['一、練習資訊', '二、自動量測與評分', '三、事件時間軸', '四、行為標記評估', '五、講評紀錄', '六、參數集版次與來源文件']) expect(text).toContain(s);
    for (const s of DEBRIEF_SECTIONS) expect(text).toContain(s.title);
    expect(DEBRIEF_SECTIONS.map((s) => s.title)).toEqual(['(1)反應階段', '(2)團隊自我回顧', '(3)倡議-探詢分析', '(4)推廣到船上標準作業程序與公司程序', '(5)總結與個人行動點']);
    expect(text).toContain('團隊覺得引水人登船後資訊量大');
    expect(text).toContain('E07 E07_steering_failure 麥寮進港航道舵機故障');
    expect(text).toContain('王大明');
    expect(text).toContain('當值船副');
    expect(text).toContain('SMS-EMG-004');
    expect(text).toContain('2026.10.1');
    expect(text).toContain('自動評分依 STCW A-I/12');
    expect(el.querySelector('.score-line .score')!.textContent).toBe('85');
    expect(el.querySelectorAll('table.timeline tbody tr')).toHaveLength(7);
    expect(el.querySelector('[data-sec="manoeuvres"]')).toBeNull();
    expect(text).toContain('未載入行為標記評估表');
  });
});

describe('F01 操縱量測與試俥目標比較', () => {
  it('summary.manoeuvres[] 解析為比較表;無此段時為空', () => {
    const data = parseReplay(withManoeuvres(E07));
    const rows = parseManoeuvres(data.summary?.raw);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({ id: 'turning35.port', label: '35° 迴旋(左)', state: 'measured', complete: true, approachSpeed_kn: 12.1, rudder_deg: -35, dataQuality: 'trial' });
    expect(rows[0]!.comparisons[0]).toMatchObject({ key: 'advance_m', label: '縱距(前進距離)', unit: 'm', measured: 412.3, target: 400, withinTolerance: true, comparable: true, deviationPct: 3.1 });
    expect(rows[0]!.comparisons[2]).toMatchObject({ targetMin: 4.6, targetMax: 4.9, unit: 'kn', comparable: false });
    expect(rows[1]).toMatchObject({ label: '全速倒俥緊急停船', state: 'aborted' });
    expect(manoeuvreLabel('zigzag10.portFirst', 'zigzag')).toBe('10/10 Z 形(先左)');
    expect(splitValueKey('overshoot1_deg')).toEqual({ label: '第一超越角', unit: '°' });
    expect(parseManoeuvres(undefined)).toEqual([]);
  });

  it('報告顯示比較表並把後續章節順延', () => {
    const data = parseReplay(withManoeuvres(E07));
    const r = buildDebriefReport({ data, meta: emptyReportMeta() });
    const el = html(renderReportDoc(r));
    const sec = el.querySelector('[data-sec="manoeuvres"]')!;
    expect(sec.textContent).toContain('三、操縱量測與試俥目標比較');
    const rows = sec.querySelectorAll('table.manoeuvre-cmp tbody tr');
    expect(rows).toHaveLength(3);
    expect(rows[0]!.textContent).toContain('公差內');
    expect(rows[1]!.textContent).toContain('超出公差');
    expect(rows[1]!.className).toContain('sev-warning');
    expect(rows[2]!.textContent).toContain('條件不可比');
    expect(rows[2]!.textContent).toContain('4.6–4.9');
    expect(rows[2]!.textContent).toContain('海報讀值');
    expect(sec.textContent).toContain('無試俥目標可比較');
    expect(el.textContent).toContain('四、事件時間軸');
    expect(el.textContent).toContain('七、參數集版次與來源文件');
  });
});

describe('沒有核心 summary 的紀錄', () => {
  it('退回本地推導摘要', () => {
    const lines = E07.split('\n').filter((l) => !l.includes('"kind":"summary"'));
    const data = parseReplay(lines.join('\n'));
    const r = buildDebriefReport({ data });
    expect(r.summary).toBeNull();
    expect(r.local?.samples).toBe(data.states.length);
    expect(r.exercise.duration_s).toBeCloseTo(data.tEnd - data.tStart, 6);
    expect(html(renderReportDoc(r)).textContent).toContain('本地摘要');
  });
});

describe('評估表列印版', () => {
  it('每要素列出 1/3/5 分錨定描述、分數、觀察與 STCW', () => {
    const f = createMarkerForm({ record: { key: 'k' }, trainee: { id: 'T001', name: '王大明' }, assessor: { name: '陳船長' }, now: NOW });
    const r = ratingOf(f, 'communication.closedLoop')!;
    r.score = 2;
    r.observations.push({ t_s: 181, text: '右舵 10 未複誦' });
    const el = html(renderFormDoc(f, { scenario: 'E07', reportNo: 'RPT-1', courseLine: '課程' }));
    expect(el.querySelectorAll('.form-el')).toHaveLength(15);
    expect(el.querySelectorAll('table.anchors-tbl td')).toHaveLength(45);
    expect(el.textContent).toContain('草案,待訓練負責人核准');
    expect(el.textContent).toContain('00:03:01 · 右舵 10 未複誦');
    expect(el.textContent).toContain('待改進');
  });
});
