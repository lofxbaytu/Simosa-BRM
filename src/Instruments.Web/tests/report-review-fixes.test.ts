// 講評報告頁審查發現(2026-10-17)的回歸測試:
//  (1) 教官站紀錄的 recordKey 改用練習開始時刻 startedUtc(不隨匯出時間改變);
//  (2) 雙評一致率要求兩表都評完十五項才判定;
//  (3) 不完整/不合規的評估表不寫入班次行為標記分數;
//  (4) 沒有紀錄鍵的評估表不套到任何報告/班次(可綁定到目前紀錄);
//  (5) 個人訓練紀錄有評估員判定(通過/未通過)與備註(R9),CSV 同;
//  (6) 新增觀察的時刻預設空白;
//  (7) 損壞的教官站紀錄不丟未處理例外,多選檔案繼續處理;
//  (8) @page 頁首字串跳脫 CSS 換行與控制字元;
//  (9) CSV 防公式注入;
//  (10) 教官站紀錄的報告編號尾碼取紀錄鍵雜湊;
//  (11) 「完成」要求 summary 定稿或評估表完整;
//  (12) 出席矩陣每列一位學員、每表最多 ATTENDANCE_COLUMNS 個練習;
//  (13) 報告編號日期與練習日期同為本地日期。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { render } from 'lit';
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { BrmMarkerStore, csvSafeText } from '../src/lib/brm-markers.js';
import { parseReplay } from '../src/lib/record-replay.js';
import { buildSessionRecord, type SessionRecordInput } from '../src/lib/session-record.js';
import { defaultState } from '../src/lib/state-parser.js';
import type { ScenarioSummary } from '../src/lib/scenario.js';
import { buildDebriefReport, defaultReportNo, emptyReportMeta, recordKey } from '../src/report/debrief-report.js';
import { computeAgreement, createMarkerForm, ratingOf } from '../src/report/marker-form.js';
import type { ReportApp } from '../src/report/report-app.js';
import { cssString } from '../src/report/report-app.js';
import { ATTENDANCE_COLUMNS, renderAgreement, renderIndividualDoc, renderSessionDoc } from '../src/report/report-views.js';
import { buildIndividualRecord, emptyCourseSession, isoDate, normalizeCourseSession, SESSION_CSV_COLUMNS, sessionHours, sessionToCsv, upsertExerciseFromReport, type CourseSession } from '../src/report/training-record.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const fx = (n: string) => readFileSync(path.join(here, 'fixtures', n), 'utf8');
const E07 = fx('record-E07-core.jsonl');
const E05 = fx('record-E05-core.jsonl');

const SCENARIO = { id: 'E07_steering_failure', name: 'E07', loading: 'full', seed: 1 } as unknown as ScenarioSummary;

function instructorInput(extra: Partial<SessionRecordInput> = {}): SessionRecordInput {
  return {
    mode: 'full',
    source: 'demo:FSB1',
    scenario: SCENARIO,
    shipId: 'FSB1',
    sampleInterval_s: 1,
    states: [{ ...defaultState('FSB1'), t: 0, tick: 0 }, { ...defaultState('FSB1'), t: 600, tick: 30000 }],
    operations: [],
    markers: [],
    commands: [],
    summary: null,
    events: [],
    coreSummary: null,
    ...extra,
  };
}

function instructorText(startedUtc: string | undefined, createdUtc: string): string {
  return JSON.stringify(buildSessionRecord(instructorInput(startedUtc ? { startedUtc } : {}), new Date(createdUtc)));
}

function oneTraineeSession(): CourseSession {
  const s = emptyCourseSession();
  s.sessionId = 'S1';
  s.trainees.push({ id: 'T001', name: '王大明', rank: '船長' });
  return s;
}

function metaWithT001() {
  const meta = emptyReportMeta();
  meta.participants.push({ traineeId: 'T001', name: '王大明', rank: '船長', role: 'master' });
  return meta;
}

function fullForm(key: string, score = 3) {
  const f = createMarkerForm({ record: { key }, trainee: { id: 'T001', name: '王大明' }, assessor: { name: '陳船長', role: 'primary' } });
  f.ratings.forEach((r) => (r.score = score));
  return f;
}

type App = ReportApp & { updateComplete: Promise<unknown> };

async function mount(query = '/report.html'): Promise<App> {
  history.replaceState(null, '', query);
  const el = document.createElement('brm-report-app') as App;
  document.body.appendChild(el);
  for (let i = 0; i < 50 && !el.ready; i++) await new Promise((r) => setTimeout(r, 5));
  await el.updateComplete;
  return el;
}

function click(el: Element | null): void {
  if (!el) throw new Error('找不到元素');
  (el as HTMLElement).click();
}

function change(el: Element | null, value: string): void {
  if (!el) throw new Error('找不到元素');
  (el as HTMLInputElement).value = value;
  el.dispatchEvent(new Event('change', { bubbles: true }));
}

function csvRows(csv: string): string[] {
  return csv.slice(1).trimEnd().split('\r\n');
}

describe('(1) 教官站紀錄的紀錄鍵用練習開始時刻,不隨匯出時間改變', () => {
  it('同一練習兩次匯出(createdUtc 不同、startedUtc 相同)→ 同一 recordKey 與報告編號;班次只一筆', () => {
    const d1 = parseReplay(instructorText('2026-10-17T01:58:12.345Z', '2026-10-17T02:00:00Z'));
    const d2 = parseReplay(instructorText('2026-10-17T01:58:12.345Z', '2026-10-17T02:05:00Z'));
    expect(d1.header.startedUtc).toBe('2026-10-17T01:58:12.345Z');
    expect(recordKey(d1)).toBe('E07_steering_failure@2026-10-17T01:58:12.345Z');
    expect(recordKey(d2)).toBe(recordKey(d1));
    expect(defaultReportNo(d2)).toBe(defaultReportNo(d1));
    const s = oneTraineeSession();
    upsertExerciseFromReport(s, buildDebriefReport({ data: d1, meta: metaWithT001() }), []);
    upsertExerciseFromReport(s, buildDebriefReport({ data: d2, meta: metaWithT001() }), []);
    expect(s.exercises).toHaveLength(1);
    expect(sessionHours(s)).toBe(s.exercises[0]!.hours);
  });

  it('舊紀錄(無 startedUtc)仍以 createdUtc 為鍵,既有評估表與報告欄位對得上', () => {
    const d = parseReplay(instructorText(undefined, '2026-10-17T02:00:00Z'));
    expect(d.header.startedUtc).toBeUndefined();
    expect(recordKey(d)).toBe('E07_steering_failure@2026-10-17T02:00:00.000Z');
    // 核心紀錄不變
    expect(recordKey(parseReplay(E07))).toMatch(/^sha256:502CC5/);
  });

  it('報告頁:同一練習第二次交接時,講評、參與者與評估表都還在', async () => {
    const app = await mount();
    expect(app.loadRecordText(instructorText('2026-10-17T01:58:12.345Z', '2026-10-17T02:00:00Z'), 'h1')).toBe(true);
    app.meta.debrief.reaction = '學員反應紀錄';
    app.meta.participants.push({ traineeId: '', name: '王', rank: '', role: 'master' });
    app.newForm('primary');
    await app.updateComplete;
    expect(app.loadRecordText(instructorText('2026-10-17T01:58:12.345Z', '2026-10-17T02:10:00Z'), 'h2')).toBe(true);
    expect(app.meta.debrief.reaction).toBe('學員反應紀錄');
    expect(app.meta.participants).toHaveLength(1);
    expect(app.formsForRecord()).toHaveLength(1);
    document.body.innerHTML = '';
  });
});

describe('教官站產生紀錄時寫入練習開始時刻', () => {
  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
  });

  type Inner = { onState(state: unknown, issues: string[]): void; stopSource(): void; restartExercise(): void; sessionRecordJson(): string };

  it('同一練習多次匯出 startedUtc 不變(createdUtc 會變);重設後開始新練習換新的 startedUtc', async () => {
    history.replaceState(null, '', '/?demo=1');
    const app = document.createElement('brm-instructor-app') as HTMLElement & { updateComplete: Promise<unknown> };
    document.body.appendChild(app);
    await app.updateComplete;
    const inner = app as unknown as Inner;
    inner.stopSource();
    inner.restartExercise();
    inner.onState({ ...defaultState('FSB1'), t: 1, tick: 50 }, []);
    const a = JSON.parse(inner.sessionRecordJson()) as { startedUtc?: string; createdUtc: string };
    await new Promise((r) => setTimeout(r, 15));
    inner.onState({ ...defaultState('FSB1'), t: 3, tick: 150 }, []);
    const b = JSON.parse(inner.sessionRecordJson()) as { startedUtc?: string; createdUtc: string };
    expect(typeof a.startedUtc).toBe('string');
    expect(b.startedUtc).toBe(a.startedUtc);
    expect(b.createdUtc).not.toBe(a.createdUtc);
    await new Promise((r) => setTimeout(r, 15));
    inner.restartExercise();
    inner.onState({ ...defaultState('FSB1'), t: 0.5, tick: 25 }, []);
    const c = JSON.parse(inner.sessionRecordJson()) as { startedUtc?: string };
    expect(c.startedUtc).not.toBe(a.startedUtc);
  });
});

describe('(2) 雙評一致率要求整張表都已評分', () => {
  it('第二評估員只評 1 項 → 不判定達成,畫面顯示未完成(缺 14 項)', () => {
    const a = fullForm('k');
    const b = createMarkerForm({ record: { key: 'k' }, trainee: { id: 'T001', name: '王大明' }, assessor: { name: '林船長', role: 'second' } });
    ratingOf(b, 'leadership.standards')!.score = 3;
    const res = computeAgreement(a, b);
    expect(res.compared).toBe(1);
    expect(res.total).toBe(15);
    expect(res.complete).toBe(false);
    expect(res.missing).toHaveLength(14);
    expect(res.meetsTarget).toBe(false);
    const div = document.createElement('div');
    render(renderAgreement(b, res), div);
    expect(div.textContent).toContain('未完成(缺 14 項)');
    expect(div.textContent).not.toMatch(/:達成/);
    // 兩表都評完 → 照常判定
    b.ratings.forEach((r) => (r.score = 3));
    const full = computeAgreement(a, b);
    expect(full).toMatchObject({ compared: 15, total: 15, complete: true, meetsTarget: true });
  });
});

describe('(3) 不完整或不合規的評估表不寫入班次行為標記分數', () => {
  it('只評 1 分且無觀察 → 結果標 formIncomplete,不帶平均/最低;個人紀錄與 CSV 顯示未完成', () => {
    const data = parseReplay(E07);
    const key = recordKey(data);
    const form = createMarkerForm({ record: { key }, trainee: { id: 'T001', name: '王大明' }, assessor: { name: '陳船長', role: 'primary' } });
    ratingOf(form, 'communication.closedLoop')!.score = 1;
    const s = oneTraineeSession();
    const ex = upsertExerciseFromReport(s, buildDebriefReport({ data, meta: metaWithT001(), forms: [form] }), [form]);
    expect(ex.results['T001']!.markerMean).toBeUndefined();
    expect(ex.results['T001']!.markerMin).toBeUndefined();
    expect(ex.results['T001']!.formIncomplete).toBe(true);
    expect(ex.results['T001']!.formId).toBe(form.formId);
    const rec = buildIndividualRecord(s, s.trainees[0]!);
    expect(rec.results[0]!.markerMean).toBeUndefined();
    const div = document.createElement('div');
    render(renderIndividualDoc(rec), div);
    expect(div.querySelector('table.results tbody')!.textContent).toContain('評估表未完成');
    const rows = csvRows(sessionToCsv(s));
    const col = SESSION_CSV_COLUMNS.indexOf('markerFormComplete');
    expect(col).toBeGreaterThan(0);
    expect(rows[1]!.split(',')[col]).toBe('N');
  });

  it('完整的評估表照常寫入平均與最低,markerFormComplete = Y', () => {
    const data = parseReplay(E07);
    const form = fullForm(recordKey(data), 4);
    const s = oneTraineeSession();
    const ex = upsertExerciseFromReport(s, buildDebriefReport({ data, meta: metaWithT001(), forms: [form] }), [form]);
    expect(ex.results['T001']).toMatchObject({ markerMean: 4, markerMin: 4, formId: form.formId });
    expect(ex.results['T001']!.formIncomplete).toBeUndefined();
    expect(csvRows(sessionToCsv(s))[1]!.split(',')[SESSION_CSV_COLUMNS.indexOf('markerFormComplete')]).toBe('Y');
  });
});

describe('(4) 沒有紀錄鍵的評估表不套到任何報告或班次', () => {
  it('E07 與 E05 報告都不列入,班次結果也不帶', () => {
    const f = fullForm('', 2);
    const s = oneTraineeSession();
    for (const text of [E07, E05]) {
      const rep = buildDebriefReport({ data: parseReplay(text), meta: metaWithT001(), forms: [f] });
      expect(rep.behavioural).toHaveLength(0);
      const ex = upsertExerciseFromReport(s, rep, [f]);
      expect(ex.results['T001']!.formId).toBeUndefined();
      expect(ex.results['T001']!.markerMean).toBeUndefined();
      expect(ex.assessors).toEqual([]);
    }
  });

  it('報告頁:載入紀錄後提示未綁定的評估表,按「綁定到目前紀錄」後才列入;未載入紀錄時不能新增評估表', async () => {
    const app = await mount('/report.html?tab=form');
    app.data = null; // 記憶體儲存可能還原了前一個測試的紀錄
    await app.updateComplete;
    expect((app.querySelector('.new-form') as HTMLButtonElement).disabled).toBe(true);
    app.ws.forms.push(fullForm('', 3));
    app.loadRecordText(E07, 'e07.jsonl');
    await app.updateComplete;
    expect(app.formsForRecord()).toHaveLength(0);
    expect(app.querySelector('.unbound-forms')!.textContent).toContain('1 份評估表未綁定練習紀錄');
    click(app.querySelector('.bind-forms'));
    await app.updateComplete;
    expect(app.ws.forms[0]!.record.key).toBe(app.key);
    expect(app.ws.forms[0]!.record.scenarioId).toBe('E07_steering_failure');
    expect(app.formsForRecord()).toHaveLength(1);
    expect(app.querySelector('.unbound-forms')).toBeNull();
    expect(app.report!.behavioural).toHaveLength(1);
    document.body.innerHTML = '';
  });
});

describe('(5) 個人訓練紀錄:評估員判定(通過/未通過)與備註(R9)', () => {
  it('判定與備註列為正式欄位,自動評分標為輔助;CSV 有 verdict 與 remark;重新加入班次時保留', () => {
    const data = parseReplay(E07);
    const s = oneTraineeSession();
    const report = buildDebriefReport({ data, meta: metaWithT001() });
    upsertExerciseFromReport(s, report, []);
    const r = s.exercises[0]!.results['T001']!;
    r.verdict = 'fail';
    r.remark = '閉環溝通需補訓';
    upsertExerciseFromReport(s, report, []);
    expect(s.exercises[0]!.results['T001']).toMatchObject({ score: 85, passed: true, verdict: 'fail', remark: '閉環溝通需補訓' });
    const div = document.createElement('div');
    render(renderIndividualDoc(buildIndividualRecord(s, s.trainees[0]!)), div);
    const heads = [...div.querySelectorAll('table.results thead th')].map((th) => th.textContent!.trim());
    expect(heads).toContain('自動評分(輔助)');
    expect(heads).toContain('評估員判定');
    expect(heads).toContain('備註');
    const cells = [...div.querySelectorAll('table.results tbody td')].map((td) => td.textContent!.trim());
    expect(cells[heads.indexOf('評估員判定')]).toBe('未通過');
    expect(cells[heads.indexOf('備註')]).toBe('閉環溝通需補訓');
    const row = csvRows(sessionToCsv(s))[1]!.split(',');
    expect(row[SESSION_CSV_COLUMNS.indexOf('verdict')]).toBe('fail');
    expect(row[SESSION_CSV_COLUMNS.indexOf('remark')]).toBe('閉環溝通需補訓');
    // JSON 往返
    const back = normalizeCourseSession(JSON.parse(JSON.stringify(s))).session!;
    expect(back.exercises[0]!.results['T001']).toMatchObject({ verdict: 'fail', remark: '閉環溝通需補訓' });
    expect(normalizeCourseSession({ ...JSON.parse(JSON.stringify(s)), exercises: [{ results: { T001: { verdict: 'maybe' } } }] }).session!.exercises[0]!.results['T001']!.verdict).toBeUndefined();
  });

  it('報告頁個人訓練紀錄分頁可輸入判定與備註', async () => {
    const app = await mount();
    app.ws.session = oneTraineeSession();
    app.loadRecordText(E07, 'e07.jsonl');
    app.meta.participants.push({ traineeId: 'T001', name: '王大明', rank: '船長', role: 'master' });
    app.addToSession();
    app.tab = 'individual';
    await app.updateComplete;
    change(app.querySelector('.verdict-edit .verdict-sel'), 'pass');
    change(app.querySelector('.verdict-edit .verdict-remark'), '表現穩定');
    await app.updateComplete;
    expect(app.ws.session.exercises[0]!.results['T001']).toMatchObject({ verdict: 'pass', remark: '表現穩定' });
    expect(app.querySelector('.sheet[data-doc="individual"] table.results')!.textContent).toContain('表現穩定');
    change(app.querySelector('.verdict-edit .verdict-sel'), '');
    await app.updateComplete;
    expect(app.ws.session.exercises[0]!.results['T001']!.verdict).toBeUndefined();
    document.body.innerHTML = '';
  });
});

describe('(6) 新增觀察的時刻預設空白', () => {
  it('1 分 + 只填描述 → 仍要求時間戳', async () => {
    const app = await mount('/report.html?tab=form');
    app.loadRecordText(E07, 'e07.jsonl');
    app.newForm('primary');
    await app.updateComplete;
    const el = () => app.querySelector('[data-element="communication.closedLoop"]')!;
    click(el().querySelector('.score-btn.s1'));
    await app.updateComplete;
    click(el().querySelector('.add-obs'));
    await app.updateComplete;
    change(el().querySelector('.obs-text'), '未複誦');
    await app.updateComplete;
    const r = ratingOf(app.activeForm!, 'communication.closedLoop')!;
    expect(r.observations).toEqual([{ t_s: null, text: '未複誦' }]);
    expect((el().querySelector('.obs-t') as HTMLInputElement).value).toBe('');
    expect(el().querySelector('.el-issues')!.textContent).toContain('2 分以下必須附時間戳');
    document.body.innerHTML = '';
  });
});

describe('(7) 損壞的教官站紀錄不丟未處理例外', () => {
  it('commands 非陣列或含 null → 顯示錯誤訊息,不丟例外', async () => {
    const app = await mount();
    for (const bad of [{ commands: 5 }, { commands: [null] }]) {
      expect(() => app.importText(JSON.stringify({ format: 'simosa-brm-instructor-record', states: [], ...bad }), 'bad.json')).not.toThrow();
      await app.updateComplete;
      expect(app.messageLevel).toBe('error');
      expect(app.message).toContain('bad.json');
    }
    // 有狀態時 commands 損壞只略過指令
    const withStates = parseReplay(JSON.stringify({ format: 'simosa-brm-instructor-record', states: [{ ...defaultState('FSB1'), t: 1, tick: 50 }], commands: [null, 3, { t: 1 }] }));
    expect(withStates.states).toHaveLength(1);
    expect(withStates.inputs).toEqual([]);
    expect(parseReplay(JSON.stringify({ format: 'simosa-brm-instructor-record', states: [], commands: 5 })).inputs).toEqual([]);
    document.body.innerHTML = '';
  });

  it('多選檔案:其中一個讀取失敗時顯示錯誤並繼續處理其餘檔案', async () => {
    const app = await mount();
    const broken = { name: 'broken.json', text: () => Promise.reject(new Error('讀取失敗')) } as unknown as File;
    const roster = new File([JSON.stringify({ format: 'simosa-brm-roster', trainees: [{ id: 'T009', name: '甲' }], staff: [] })], 'roster.json');
    await app.openFiles([broken, roster]);
    expect(app.ws.session.trainees.map((t) => t.id)).toEqual(['T009']);
    expect(app.fileErrors.some((e) => e.includes('broken.json'))).toBe(true);
    await app.updateComplete;
    expect(app.querySelector('.msg.error')!.textContent).toContain('broken.json');
    document.body.innerHTML = '';
  });
});

describe('(8) @page 頁首字串跳脫', () => {
  it('cssString 把 CSS 換行(\\r \\n \\f)與控制字元換成空白,跳脫反斜線與引號', () => {
    expect(cssString('A\r} } .sheet{display:none} @page{')).toBe('A } } .sheet{display:none} @page{');
    expect(cssString('a\nb\fc\u0000d\u007fe')).toBe('a b c d e');
    expect(cssString('引號"與\\反斜線')).toBe('引號\\"與\\\\反斜線');
  });

  it('惡意課程名稱只留在 content 字串內', async () => {
    const app = await mount();
    app.data = null; // 未載入紀錄時頁首取班次的課程名稱
    app.ws.session.courseName = 'A課程\r} } .sheet{display:none} @page{';
    app.ws = { ...app.ws };
    await app.updateComplete;
    const css = document.getElementById('report-page-style')!.textContent!;
    expect(css).not.toMatch(/[\r\n\f]/);
    const m = /@top-right \{ content: "((?:[^"\\]|\\.)*)"; font-size: 8pt; color: #333; \}/.exec(css);
    expect(m?.[1]).toBe('A課程 } } .sheet{display:none} @page{');
    document.body.innerHTML = '';
  });

  it('匯入班次時單行欄位去掉控制字元', () => {
    const s = normalizeCourseSession({ format: 'simosa-brm-course-session', sessionId: 'S\r1', courseName: 'A\r}x', courseVersion: '1\n0', trainees: [{ id: 'T\t1', name: '王\r大明', rank: '船\u0000長' }] }).session!;
    expect(s.sessionId).toBe('S 1');
    expect(s.courseName).toBe('A }x');
    expect(s.courseVersion).toBe('1 0');
    expect(s.trainees[0]).toMatchObject({ id: 'T 1', name: '王 大明', rank: '船 長' });
  });
});

describe('(9) CSV 防公式注入', () => {
  it('以 = + - @ Tab CR 開頭的文字欄位前加單引號;數值欄位不變', () => {
    expect(csvSafeText('=HYPERLINK("http://x","點我")')).toBe('\'=HYPERLINK("http://x","點我")');
    for (const lead of ['=', '+', '-', '@', '\t', '\r']) expect(csvSafeText(`${lead}x`)).toBe(`'${lead}x`);
    expect(csvSafeText('王大明')).toBe('王大明');
    expect(csvSafeText('')).toBe('');
    const data = parseReplay(E07);
    const s = emptyCourseSession();
    s.sessionId = 'S1';
    s.evaluation = '@SUM(1+1)';
    s.trainees.push({ id: 'T001', name: '=HYPERLINK("http://x","點我")', rank: '+1' });
    const meta = metaWithT001();
    meta.instructor = '-陳';
    upsertExerciseFromReport(s, buildDebriefReport({ data, meta }), []);
    const csv = sessionToCsv(s);
    const line = csvRows(csv)[1]!;
    expect(line).toContain(',"\'=HYPERLINK(""http://x"",""點我"")",\'+1,');
    expect(line).toContain(',\'-陳,');
    expect(line.endsWith(",'@SUM(1+1)")).toBe(true);
    expect(line).toContain(',85,Y,N,'); // score / passed / hardFailed 原樣
    expect(csv).not.toMatch(/,[=+@]/);
  });

  it('BRM 標記 CSV 的備註同樣防護', () => {
    const store = new BrmMarkerStore();
    store.add({ category: 'communication', t: 10, note: '=1+1', wall: new Date('2026-01-01T00:00:00Z') });
    expect(store.toCsv()).toContain(",'=1+1\r\n");
  });
});

describe('(10) 教官站紀錄的報告編號尾碼取紀錄鍵雜湊', () => {
  it('同情境同日兩次練習 → 報告編號不同;不是情境 id 字首', () => {
    const a = defaultReportNo(parseReplay(instructorText('2026-10-17T02:00:00.000Z', '2026-10-17T03:00:00Z')));
    const b = defaultReportNo(parseReplay(instructorText('2026-10-17T05:00:00.000Z', '2026-10-17T06:00:00Z')));
    expect(a).not.toBe(b);
    expect(a).toMatch(/^RPT-20261017-E07-[0-9A-Z]{6}$/);
    expect(a.endsWith('E07STE')).toBe(false);
    // 核心紀錄維持 SHA-256 前 6 碼
    expect(defaultReportNo(parseReplay(E07))).toBe('RPT-20261017-E07-502CC5');
  });
});

describe('(11) 完成 = summary 定稿或評估表完整', () => {
  it('未定稿 summary 且無評估表 → 不算完成;未定稿 summary + 完整評估表 → 完成', () => {
    const data = parseReplay(E07.replace('"final":true', '"final":false'));
    expect(data.summary?.final).toBe(false);
    const s = oneTraineeSession();
    expect(upsertExerciseFromReport(s, buildDebriefReport({ data, meta: metaWithT001() }), []).completed).toEqual([]);
    const f = fullForm(recordKey(data));
    expect(upsertExerciseFromReport(s, buildDebriefReport({ data, meta: metaWithT001(), forms: [f] }), [f]).completed).toEqual(['T001']);
  });
});

describe('(12) 課程班次紀錄的出席矩陣不隨學員數加寬', () => {
  it('每列一位學員;每張表最多 ATTENDANCE_COLUMNS 個練習欄', () => {
    const s = emptyCourseSession();
    s.sessionId = 'S1';
    for (let i = 1; i <= 14; i++) s.trainees.push({ id: `T${String(i).padStart(3, '0')}`, name: `學員${i}`, rank: '三副' });
    const nEx = ATTENDANCE_COLUMNS + 3;
    for (let i = 1; i <= nEx; i++) {
      s.exercises.push({ exerciseNo: `E${String(i).padStart(2, '0')}`, scenarioId: `E${i}_long_scenario_identifier_name`, date: '2026-10-17', hours: 1, recordKey: `k${i}`, reportNo: `RPT-${i}`, instructor: '陳', assessors: [], equipment: '', attended: ['T001', 'T002'], completed: ['T001'], results: {} });
    }
    const div = document.createElement('div');
    render(renderSessionDoc(s), div);
    const tables = [...div.querySelectorAll('table.attendance')];
    expect(tables).toHaveLength(2);
    for (const t of tables) {
      expect(t.querySelectorAll('thead th').length).toBeLessThanOrEqual(1 + ATTENDANCE_COLUMNS);
      expect(t.querySelectorAll('tbody tr')).toHaveLength(14);
    }
    expect(tables[0]!.querySelector('tbody tr')!.textContent).toContain('學員1');
    expect(tables[0]!.querySelector('tbody tr')!.textContent).toContain('完成');
    // 練習表不再每位學員一欄
    const exTable = div.querySelector('table.exercises')!;
    expect(exTable.querySelectorAll('thead th').length).toBeLessThan(10);
    expect(exTable.querySelectorAll('tbody tr')).toHaveLength(nEx);
  });
});

describe('(13) 報告編號日期與練習日期同為本地日期', () => {
  it('TZ=Asia/Taipei、UTC 17:30(本地次日 01:30)→ 兩者皆為 10-18', () => {
    const old = process.env.TZ;
    process.env.TZ = 'Asia/Taipei';
    try {
      const data = parseReplay(E07);
      data.header.createdUtc = '2026-10-17T17:30:00Z';
      const rep = buildDebriefReport({ data });
      expect(rep.reportNo).toBe('RPT-20261018-E07-502CC5');
      expect(isoDate(rep.exercise.createdUtc)).toBe('2026-10-18');
      const s = oneTraineeSession();
      expect(upsertExerciseFromReport(s, rep, []).date).toBe('2026-10-18');
    } finally {
      if (old === undefined) delete process.env.TZ;
      else process.env.TZ = old;
    }
  });
});
