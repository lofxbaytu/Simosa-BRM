// 報告頁「教官與證書」分頁(happy-dom;?tab=admin):教官資格紀錄 CRUD 與校準紀錄、證書效期雙軌追蹤的輸入與旗標、
// 對齊換證、由名冊同步、排程匯出鈕、JSON 匯入/匯出、admin 存放區與 kv 分開;講評報告以教官站交接的 sessionInfo 帶入參與者。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { ReportApp } from '../src/report/report-app.js';
import { ADMIN_KEY, ADMIN_STORE, MemoryKvStore, resetReportStoreForTest, saveHandoff } from '../src/report/report-store.js';
import { parseAdminRecords } from '../src/report/admin-records.js';
import { buildSessionRecord } from '../src/lib/session-record.js';
import { defaultState } from '../src/lib/state-parser.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const E07 = readFileSync(path.join(here, 'fixtures/record-E07-core.jsonl'), 'utf8');

type App = ReportApp & { updateComplete: Promise<unknown> };

async function mount(query = '/report.html?tab=admin'): Promise<App> {
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

describe('教官與證書分頁', () => {
  beforeAll(async () => {
    await import('../src/report/report-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
    resetReportStoreForTest();
  });

  it('記憶體儲存:admin 存放區與 kv 的同名鍵互不干擾', async () => {
    const s = new MemoryKvStore();
    await s.set('k', { a: 1 });
    await s.set('k', { b: 2 }, ADMIN_STORE);
    expect(await s.get('k')).toEqual({ a: 1 });
    expect(await s.get('k', ADMIN_STORE)).toEqual({ b: 2 });
    await s.delete('k', ADMIN_STORE);
    expect(await s.get('k', ADMIN_STORE)).toBeUndefined();
    expect(await s.get('k')).toEqual({ a: 1 });
  });

  it('?tab=admin 顯示分頁;新增教官與校準紀錄、新增學員與到期日、旗標與對齊換證、排程匯出鈕', async () => {
    const app = await mount();
    expect(app.tab).toBe('admin');
    expect(app.querySelector('.rp-tabs .tab-admin.active')!.textContent).toBe('教官與證書');
    change(app.querySelector('.f-asof'), '2026-10-23');
    await app.updateComplete;
    // 教官
    click(app.querySelector('.add-instructor'));
    await app.updateComplete;
    change(app.querySelector('.instructor-edit .in-name'), '陳船長');
    change(app.querySelector('.instructor-edit .in-trainer'), 'IMO 示範課程 6.09');
    change(app.querySelector('.instructor-edit .in-trainer-date'), '2024-03-01');
    change(app.querySelector('.instructor-edit .in-assessor'), 'A-I/6 第 7–8 段評估者課程');
    change(app.querySelector('.instructor-edit .in-expiry'), '2026-12-15');
    await app.updateComplete;
    expect(app.querySelector('.instructor-edit legend')!.textContent).toContain('未校準');
    click(app.querySelector('.add-calibration'));
    await app.updateComplete;
    change(app.querySelector('.calibration-edit .cal-video'), 'E20 基準錄影 1/3');
    change(app.querySelector('.calibration-edit .cal-rate'), '87');
    await app.updateComplete;
    expect(app.admin.instructors[0]).toMatchObject({ name: '陳船長', trainerCourse: 'IMO 示範課程 6.09', trainerDate: '2024-03-01', expiryDate: '2026-12-15' });
    expect(app.admin.instructors[0]!.calibrations[0]).toMatchObject({ date: '2026-10-23', referenceVideo: 'E20 基準錄影 1/3', agreementRate: 0.87 });
    expect(app.querySelector('.instructor-edit legend')!.textContent).toContain('已校準');
    const doc = () => app.querySelector('.sheet[data-doc="admin"]')!;
    expect(doc().querySelector('table.instructors')!.textContent).toContain('87%');
    expect(doc().querySelector('table.instructors')!.textContent).toContain('90 天內到期');
    expect(doc().querySelector('table.calibrations')!.textContent).toContain('達成');
    // 學員
    click(app.querySelector('.add-track'));
    await app.updateComplete;
    change(app.querySelector('.tracks-edit .tr-name'), '王大明');
    change(app.querySelector('.tracks-edit .tr-no'), 'TW-12345');
    change(app.querySelector('.tracks-edit .tr-statutory'), '2027-03-31');
    change(app.querySelector('.tracks-edit .tr-last'), '2022-01-10');
    await app.updateComplete;
    const t = app.admin.tracks[0]!;
    expect(t).toMatchObject({ traineeId: 'T001', name: '王大明', statutoryNo: 'TW-12345', statutoryExpiry: '2027-03-31', lastCompletedDate: '2022-01-10', refresherDue: '2027-01-10', alignedWithStatutory: false });
    // 法定 159 天(180 天內)、公司複訓 79 天(90 天內)→ 取較急者並顯示較早到期日
    expect(app.querySelector('.tracks-edit .flag-cell')!.textContent).toContain('90 天內到期');
    expect(app.querySelector('.tracks-edit .flag-cell')!.textContent).toContain('2027-01-10');
    expect((app.querySelector('.tracks-edit .flag-cell') as HTMLElement).title).toBe('法定:180 天內到期;公司:90 天內到期');
    click(app.querySelector('.tracks-edit .align-btn'));
    await app.updateComplete;
    expect(t).toMatchObject({ alignedWithStatutory: true, refresherDue: '2027-03-31' });
    expect(app.querySelector('.tracks-edit .align-btn')!.textContent).toBe('已對齊換證');
    expect(doc().querySelector('table.tracks')!.textContent).toContain('已對齊換證');
    // 到期 2027-03-31 − 90 天 = 2026-12-31 → 2026-Q4
    expect(doc().querySelector('[data-sec="schedule"]')!.textContent).toContain('2026-Q4');
    expect(doc().querySelector('[data-sec="schedule"]')!.textContent).toContain('2026-12-31');
    expect(doc().querySelector('[data-sec="schedule"]')!.textContent).toContain('複訓與換證同步');
    expect((app.querySelector('.export-schedule-csv') as HTMLButtonElement).disabled).toBe(false);
    expect((app.querySelector('.export-schedule-json') as HTMLButtonElement).disabled).toBe(false);
    // 匯出 JSON 內容可再匯入
    await app.saveAdminNow();
    const exported = JSON.stringify({ ...app.admin, format: 'simosa-brm-admin-records' });
    expect(parseAdminRecords(exported).admin!.tracks[0]!.refresherDue).toBe('2027-03-31');
    // 列印標題
    expect(document.getElementById('report-page-style')!.textContent).toContain('教官與證書');
  });

  it('由名冊同步學員並寫回法定到期日;匯入 JSON 取代;admin 存於 ADMIN_STORE 而非工作資料', async () => {
    const app = await mount('/report.html?tab=individual');
    click(app.querySelector('.add-trainee'));
    await app.updateComplete;
    change(app.querySelector('.roster-edit .t-name'), '李小華');
    change(app.querySelector('.roster-edit input[type="date"]'), '2029-03-31');
    await app.updateComplete;
    app.tab = 'admin';
    await app.updateComplete;
    expect(app.admin.tracks).toHaveLength(0);
    click(app.querySelector('.sync-roster'));
    await app.updateComplete;
    expect(app.admin.tracks).toEqual([expect.objectContaining({ traineeId: 'T001', name: '李小華', statutoryExpiry: '2029-03-31' })]);
    // 改法定到期日 → 名冊同步
    change(app.querySelector('.tracks-edit .tr-statutory'), '2030-06-30');
    await app.updateComplete;
    expect(app.ws.session.trainees[0]!.certificateExpiry).toBe('2030-06-30');
    // 匯入取代
    app.importText(JSON.stringify({ format: 'simosa-brm-admin-records', refresherIntervalYears: 5, instructors: [{ id: 'I001', name: '林船長' }], tracks: [{ traineeId: 'T009', name: '外部', lastCompletedDate: '2023-05-01' }] }), 'admin.json');
    await app.updateComplete;
    expect(app.message).toContain('已匯入教官與證書紀錄:教官 1、學員 1');
    expect(app.admin.instructors.map((i) => i.name)).toEqual(['林船長']);
    expect(app.admin.tracks[0]).toMatchObject({ traineeId: 'T009', refresherDue: '2028-05-01' });
    await app.saveAdminNow();
    const store = (app as unknown as { store: MemoryKvStore }).store;
    expect(await store.get(ADMIN_KEY, ADMIN_STORE)).toMatchObject({ format: 'simosa-brm-admin-records' });
    expect(await store.get(ADMIN_KEY)).toBeUndefined();
    // 重新開頁:從 admin 存放區還原
    document.body.innerHTML = '';
    const again = await mount();
    expect(again.admin.instructors.map((i) => i.name)).toEqual(['林船長']);
    expect(again.admin.tracks[0]!.traineeId).toBe('T009');
  });

  it('講評報告:教官站交接的紀錄含 sessionInfo → 參與者、教官、班次自動帶入,列印版有休息時間確認', async () => {
    const record = buildSessionRecord({
      mode: 'full',
      source: 'ws://x',
      scenario: null,
      shipId: 'FSB1',
      sampleInterval_s: 1,
      states: [defaultState('FSB1'), { ...defaultState('FSB1'), t: 60, tick: 3000 }],
      operations: [],
      markers: [],
      commands: [],
      summary: null,
      startedUtc: '2026-10-17T01:00:00Z',
      sessionInfo: {
        participants: [
          { name: '王大明', role: 'master', rank: '船長' },
          { name: '陳船長', role: 'instructor' },
        ],
        restHoursConfirmation: { confirmedBy: '王大明', confirmedAtUtc: '2026-10-17T00:55:00Z', compliant: false, note: '昨日靠泊作業,經訓練負責人同意照常' },
        course: { id: 'BRM-FSB1-2026-01', instructor: '陳船長' },
      },
    });
    await saveHandoff(JSON.stringify(record));
    const app = await mount('/report.html?from=instructor');
    expect(app.data?.header.sessionInfo?.participants).toHaveLength(2);
    expect(app.meta.participants).toEqual([
      { traineeId: '', name: '王大明', rank: '船長', role: 'master' },
      { traineeId: '', name: '陳船長', rank: '', role: 'leader' },
    ]);
    expect(app.meta.instructor).toBe('陳船長');
    expect(app.meta.sessionId).toBe('BRM-FSB1-2026-01');
    const sheet = app.querySelector('.sheet[data-doc="report"]')!;
    expect(sheet.querySelector('table.participants')!.textContent).toContain('帶領者');
    expect(sheet.querySelector('.rest-hours')!.textContent).toContain('否(由訓練負責人決策照常執行)');
    expect(sheet.querySelector('.rest-hours')!.textContent).toContain('昨日靠泊作業');
    // 核心紀錄(無 sessionInfo)不帶入
    document.body.innerHTML = '';
    resetReportStoreForTest();
    const plain = await mount('/report.html');
    plain.loadRecordText(E07, 'E07.jsonl');
    expect(plain.meta.participants).toEqual([]);
  });
});
