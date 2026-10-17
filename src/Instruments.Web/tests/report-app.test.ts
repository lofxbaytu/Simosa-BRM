// 講評報告頁 <brm-report-app>(happy-dom):載入核心紀錄 → 報告預覽;評估表評分與即時驗證、BRM 標記帶入、雙評一致率;
// 名冊與個人訓練紀錄(聲明文字);教官站經本機儲存交接紀錄;教官站標題列的「講評報告」入口。
// happy-dom 沒有 IndexedDB,報告頁退回記憶體儲存(畫面標示「本機儲存不可用」)。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { ReportApp } from '../src/report/report-app.js';
import { emptyWorkspace, parseMarkerExport, restoreWorkspace } from '../src/report/report-app.js';
import { createMarkerForm, exportForms } from '../src/report/marker-form.js';
import { saveHandoff } from '../src/report/report-store.js';
import { INTERNAL_TRAINING_STATEMENT } from '../src/report/training-record.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const E07 = readFileSync(path.join(here, 'fixtures/record-E07-core.jsonl'), 'utf8');

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

describe('講評報告頁', () => {
  beforeAll(async () => {
    await import('../src/report/report-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('未載入紀錄時提示;載入 E07 核心紀錄後顯示報告預覽', async () => {
    const app = await mount();
    expect(app.querySelector('.rp-status')!.textContent).toContain('尚未載入練習紀錄');
    expect(app.querySelector('.rp-status')!.textContent).toContain('本機儲存不可用');
    expect(app.querySelector('.sheet')).toBeNull();
    expect(app.loadRecordText(E07, 'record-E07-core.jsonl')).toBe(true);
    await app.updateComplete;
    const sheet = app.querySelector('.sheet[data-doc="report"]')!;
    expect(sheet.textContent).toContain('E07_steering_failure');
    expect(sheet.textContent).toContain('中塑海運內部訓練');
    expect(app.querySelector('.rp-status')!.textContent).toContain('核心 summary');
    // 課程版本改動即反映在頁首
    change(app.querySelector('.f-course-version'), '1.2');
    await app.updateComplete;
    expect(app.querySelector('.sheet .doc-sub')!.textContent).toContain('第 1.2 版');
    expect(document.getElementById('report-page-style')!.textContent).toContain('第 1.2 版');
    expect(document.getElementById('report-page-style')!.textContent).toContain('size: A4');
    // 參與者
    click(app.querySelector('.add-participant'));
    await app.updateComplete;
    change(app.querySelector('.participants-edit tbody tr td:nth-child(2) input'), '王大明');
    await app.updateComplete;
    expect(app.querySelector('.sheet table.participants')!.textContent).toContain('王大明');
    expect(app.querySelector('.sheet table.participants')!.textContent).toContain('船長');
    // 講評五段
    change(app.querySelector('.f-debrief-reaction'), '大家覺得很緊張');
    await app.updateComplete;
    expect(app.querySelector('[data-sec="debrief"]')!.textContent).toContain('大家覺得很緊張');
  });

  it('壞檔顯示錯誤、不清掉已載入的紀錄', async () => {
    const app = await mount();
    app.loadRecordText(E07, 'a.jsonl');
    app.importText('this is not a record', 'bad.txt');
    await app.updateComplete;
    expect(app.querySelector('.msg.error')!.textContent).toContain('無法載入紀錄');
    expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
  });

  it('評估表:評 2 分即要求時間戳與觀察描述;補上後錯誤消失;BRM 標記帶入;第二評估員一致率', async () => {
    const app = await mount('/report.html?tab=form');
    app.loadRecordText(E07, 'record-E07-core.jsonl');
    await app.updateComplete;
    expect(app.querySelector('.form-editor')!.textContent).toContain('尚無評估表');
    expect(app.querySelector('.draft-badge')!.textContent).toContain('草案,待訓練負責人核准');
    click(app.querySelector('.new-form'));
    await app.updateComplete;
    change(app.querySelector('.f-assessor'), '陳船長');
    await app.updateComplete;
    const el = () => app.querySelector('[data-element="communication.closedLoop"]')!;
    // 錨定描述 1/3/5 顯示於評分畫面
    expect(el().querySelectorAll('.anchors > div')).toHaveLength(3);
    click(el().querySelector('.score-btn[data-score="2"]'));
    await app.updateComplete;
    expect(el().querySelector('.el-issues')!.textContent).toContain('2 分以下必須附時間戳與觀察描述');
    expect(app.querySelector('.err-count')!.textContent).toBe('錯誤 1');
    click(el().querySelector('.add-obs'));
    await app.updateComplete;
    change(el().querySelector('.obs-t'), '00:03:01');
    change(el().querySelector('.obs-text'), '右舵 10 未複誦');
    await app.updateComplete;
    expect(app.querySelector('.err-count')).toBeNull();
    // 人員缺失需子類
    change(el().querySelector('.def-type'), 'human');
    await app.updateComplete;
    expect(el().querySelector('.el-issues')!.textContent).toContain('人員缺失須再分');
    change(el().querySelector('.def-human'), 'stressLanguage');
    await app.updateComplete;
    expect(el().querySelector('.el-issues .issue.error')).toBeNull();
    // 列印版
    expect(app.querySelector('.sheet[data-doc="form"]')!.textContent).toContain('00:03:01 · 右舵 10 未複誦');

    // BRM 快速標記(教官站匯出 JSON)帶入
    app.importText(JSON.stringify({ format: 'simosa-brm-markers', version: 1, markers: [{ id: 5, category: 'communication', t: 200, wall: '2026-10-17T08:00:00Z', note: '未分享意圖', polarity: 'delta' }] }), 'm.json');
    await app.updateComplete;
    const qm = app.querySelector('.cat-communication .qm')!;
    expect(qm.textContent).toContain('未分享意圖');
    change(qm.querySelector('select'), 'communication.intentions');
    click(qm.querySelector('.attach-marker'));
    await app.updateComplete;
    expect((app.querySelector('[data-element="communication.intentions"] .obs-text') as HTMLInputElement).value).toBe('(Δ)未分享意圖');

    // 全部評 3 分後匯入第二評估員的表
    const form = app.activeForm!;
    for (const r of form.ratings) if (r.score === null) r.score = 3;
    const second = createMarkerForm({ record: { key: form.record.key }, trainee: { ...form.trainee }, assessor: { name: '林船長', role: 'second' } });
    second.ratings.forEach((r) => (r.score = 3));
    app.importText(exportForms([second]), 'second.json');
    app.activeFormId = form.formId;
    await app.updateComplete;
    expect(app.querySelector('.agreement-box')!.textContent).toContain('15/15');
    expect(app.querySelector('.agreement-box')!.textContent).toContain('達成');
    expect(app.querySelectorAll('.form-chip')).toHaveLength(2);
  });

  it('個人訓練紀錄:名冊、加入班次、聲明文字;班次分頁可匯出', async () => {
    const app = await mount();
    app.loadRecordText(E07, 'record-E07-core.jsonl');
    app.tab = 'individual';
    await app.updateComplete;
    change(app.querySelector('.f-session-id'), 'BRM-01');
    click(app.querySelector('.add-trainee'));
    await app.updateComplete;
    change(app.querySelector('.roster-edit .t-name'), '王大明');
    await app.updateComplete;
    const doc = app.querySelector('.sheet[data-doc="individual"]')!;
    expect(doc.querySelector('.statement')!.textContent).toBe(INTERNAL_TRAINING_STATEMENT);
    expect(doc.textContent).toContain('BRM-01-T001');
    // 報告分頁加入參與者並加入班次
    app.tab = 'report';
    await app.updateComplete;
    click(app.querySelector('.add-participant'));
    await app.updateComplete;
    change(app.querySelector('.participants-edit tbody tr td:nth-child(1) select'), 'T001');
    await app.updateComplete;
    click(app.querySelector('.add-to-session'));
    await app.updateComplete;
    expect(app.ws.session.exercises[0]).toMatchObject({ exerciseNo: 'E07', attended: ['T001'] });
    app.tab = 'session';
    await app.updateComplete;
    expect(app.querySelector('.export-csv')).not.toBeNull();
    expect(app.querySelector('.sheet[data-doc="session"]')!.textContent).toContain('E07');
    expect(app.querySelector('.sheet[data-doc="session"] table.attendance')!.textContent).toContain('完成');
  });

  it('教官站交接:from=instructor 讀取本機儲存中的紀錄', async () => {
    await saveHandoff(E07);
    const app = await mount('/report.html?from=instructor');
    expect(app.data?.header.scenarioId).toBe('E07_steering_failure');
    expect(app.recordName).toContain('教官站紀錄');
    // 交接後刪除,重新開頁不會再讀到
    document.body.innerHTML = '';
    const again = await mount('/report.html?from=instructor');
    expect(again.querySelector('.msg.error')!.textContent).toContain('沒有找到教官站交接的紀錄');
  });

  it('工作資料還原與 BRM 標記檔解析', () => {
    const ws = restoreWorkspace({ metas: { k: { courseName: 'C', debrief: { reaction: 'R' } } }, forms: [createMarkerForm({ record: { key: 'k' } })], session: { format: 'simosa-brm-course-session', sessionId: 'S1' } });
    expect(ws.metas['k']!.courseName).toBe('C');
    expect(ws.metas['k']!.debrief.reaction).toBe('R');
    expect(ws.metas['k']!.debrief.summaryActions).toBe('');
    expect(ws.forms).toHaveLength(1);
    expect(ws.session.sessionId).toBe('S1');
    expect(restoreWorkspace(null)).toEqual(emptyWorkspace());
    expect(parseMarkerExport({ markers: [{ id: 9, category: 'teamwork', t: 5, note: ' x ' }, { category: 'bad', t: 1 }] })).toEqual([{ id: 9, category: 'teamwork', t: 5, wall: '', note: 'x' }]);
  });
});

describe('教官站「講評報告」入口', () => {
  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('完整教官站標題列有「講評報告」;簡化模式標題列沒有(結束畫面才有)', async () => {
    history.replaceState(null, '', '/?demo=1');
    const full = document.createElement('brm-instructor-app') as HTMLElement & { updateComplete: Promise<unknown> };
    document.body.appendChild(full);
    await full.updateComplete;
    expect(full.shadowRoot!.querySelector('header .open-report')!.textContent).toBe('講評報告');
    document.body.innerHTML = '';
    history.replaceState(null, '', '/?demo=1&mode=simple');
    const simple = document.createElement('brm-instructor-app') as HTMLElement & { updateComplete: Promise<unknown> };
    document.body.appendChild(simple);
    await simple.updateComplete;
    expect(simple.shadowRoot!.querySelector('header .open-report')).toBeNull();
  });
});
