// 個人訓練紀錄與課程班次紀錄(規劃書第 9.1 節、第 14 章評估列):必備聲明文字、由講評報告加入班次(出席/完成/結果)、
// 時數、CSV 欄位與跳脫、JSON 匯出/匯入、學員名冊。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { render } from 'lit';
import { describe, expect, it } from 'vitest';
import { parseReplay } from '../src/lib/record-replay.js';
import { buildDebriefReport, emptyReportMeta, recordKey } from '../src/report/debrief-report.js';
import { createMarkerForm } from '../src/report/marker-form.js';
import { renderIndividualDoc, renderSessionDoc } from '../src/report/report-views.js';
import {
  buildIndividualRecord,
  buildIndividualRecords,
  defaultExerciseHours,
  emptyCourseSession,
  exportCourseSession,
  exportRoster,
  INTERNAL_TRAINING_STATEMENT,
  parseCourseSession,
  parseRoster,
  SESSION_CSV_COLUMNS,
  sessionHours,
  sessionToCsv,
  upsertExerciseFromReport,
  validateCourseSession,
  type CourseSession,
} from '../src/report/training-record.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const E07 = readFileSync(path.join(here, 'fixtures/record-E07-core.jsonl'), 'utf8');
const NOW = new Date('2026-10-17T12:00:00Z');

function session(): CourseSession {
  const s = emptyCourseSession();
  s.sessionId = 'BRM-FSB1-2026-01';
  s.courseVersion = '1.0';
  s.instructors = ['陳船長'];
  s.trainees = [
    { id: 'T001', name: '王大明', rank: '船長' },
    { id: 'T002', name: '李, "小華"', rank: '二副', certificateExpiry: '2029-03-31' },
    { id: 'T003', name: '張三', rank: '三副' },
  ];
  return s;
}

function e07Report(s: CourseSession) {
  const data = parseReplay(E07);
  const meta = emptyReportMeta();
  meta.instructor = '陳船長';
  meta.participants = [
    { traineeId: 'T001', name: '王大明', rank: '船長', role: 'master' },
    { traineeId: 'T002', name: '李小華', rank: '二副', role: 'oow' },
    { traineeId: '', name: '輪機員甲', rank: '', role: 'ecr' },
    { traineeId: 'T999', name: '不在名冊', rank: '', role: 'lookout' },
  ];
  const form = createMarkerForm({ record: { key: recordKey(data) }, trainee: { id: 'T001', name: '王大明' }, assessor: { name: '林船長' }, now: NOW });
  form.ratings.forEach((r, i) => (r.score = i % 2 === 0 ? 4 : 3));
  void s;
  return { report: buildDebriefReport({ data, meta, forms: [form], now: NOW }), forms: [form] };
}

describe('內部訓練聲明', () => {
  it('與規劃書第 9.1 節原文相同,且固定出現在每位學員的紀錄與列印版', () => {
    expect(INTERNAL_TRAINING_STATEMENT).toBe('中塑海運公司內部訓練紀錄,非依《船員訓練檢覈及申請核發證書辦法》核發之專業訓練證書');
    const s = session();
    const recs = buildIndividualRecords(s);
    expect(recs).toHaveLength(3);
    for (const r of recs) expect(r.statement).toBe(INTERNAL_TRAINING_STATEMENT);
    const div = document.createElement('div');
    render(renderIndividualDoc(recs[0]!), div);
    expect(div.querySelector('.statement')!.textContent).toBe(INTERNAL_TRAINING_STATEMENT);
    expect(div.querySelector('.doc-org')!.textContent).toBe('中塑海運內部訓練');
    const sdiv = document.createElement('div');
    render(renderSessionDoc(s), sdiv);
    expect(sdiv.textContent).toContain(INTERNAL_TRAINING_STATEMENT);
  });
});

describe('由講評報告加入課程班次', () => {
  it('出席 = 名冊內的參與者;完成 = 有核心 summary;結果含核心評分與評估表平均', () => {
    const s = session();
    const { report, forms } = e07Report(s);
    const ex = upsertExerciseFromReport(s, report, forms);
    expect(ex).toMatchObject({ exerciseNo: 'E07', scenarioId: 'E07_steering_failure', date: '2026-10-17', hours: 1, reportNo: report.reportNo, instructor: '陳船長', assessors: ['林船長'], attended: ['T001', 'T002'], completed: ['T001', 'T002'] });
    expect(ex.results['T001']).toMatchObject({ score: 85, passed: true, hardFailed: false, markerMean: 3.53, markerMin: 3, formId: forms[0]!.formId });
    expect(ex.results['T002']).toEqual({ score: 85, passed: true, hardFailed: false });
    expect(s.shipId).toBe('FSB1');
    expect(s.startDate).toBe('2026-10-17');
    expect(s.assessors).toEqual(['林船長']);
    // 同一紀錄再加入:更新而非重複,保留人工修改的時數
    s.exercises[0]!.hours = 2;
    upsertExerciseFromReport(s, report, forms);
    expect(s.exercises).toHaveLength(1);
    expect(s.exercises[0]!.hours).toBe(2);
    expect(sessionHours(s)).toBe(2);
  });

  it('預設練習時數 = 執行時間 + 0.75 h,進位到 0.25 h', () => {
    expect(defaultExerciseHours(300)).toBe(1);
    expect(defaultExerciseHours(3600)).toBe(1.75);
    expect(defaultExerciseHours(3000)).toBe(1.75);
    expect(defaultExerciseHours(0)).toBe(0.75);
  });
});

describe('個人訓練紀錄內容', () => {
  it('課程名稱與版本、日期與時數、出席與完成之練習編號、教官與評估員、評估結果與紀錄編號', () => {
    const s = session();
    const { report, forms } = e07Report(s);
    upsertExerciseFromReport(s, report, forms);
    s.exercises.push({ exerciseNo: 'E01', scenarioId: 'E01_baseline', date: '2026-10-01', hours: 1.5, recordKey: 'k1', reportNo: 'RPT-E01', instructor: '訪船船長', assessors: [], equipment: '', attended: ['T001', 'T003'], completed: ['T001'], results: { T001: { score: 92, passed: true } } });
    const rec = buildIndividualRecord(s, s.trainees[0]!);
    expect(rec.recordNo).toBe('BRM-FSB1-2026-01-T001');
    expect(rec.courseName).toBe('中塑海運 BRM 內部訓練課程');
    expect(rec.courseVersion).toBe('1.0');
    expect(rec.attendedExercises).toEqual(['E07', 'E01']);
    expect(rec.completedExercises).toEqual(['E07', 'E01']);
    expect(rec.attendedHours).toBe(2.5);
    expect(rec.sessionHours).toBe(2.5);
    expect(rec.instructors).toEqual(['陳船長', '訪船船長']);
    expect(rec.assessors).toEqual(['林船長']);
    expect(rec.results.map((r) => [r.exerciseNo, r.score, r.reportNo])).toEqual([
      ['E07', 85, report.reportNo],
      ['E01', 92, 'RPT-E01'],
    ]);
    const t3 = buildIndividualRecord(s, s.trainees[2]!);
    expect(t3.attendedExercises).toEqual(['E01']);
    expect(t3.completedExercises).toEqual([]);
    const div = document.createElement('div');
    render(renderIndividualDoc(rec), div);
    const text = div.textContent ?? '';
    for (const sub of ['個人訓練紀錄', '紀錄編號 BRM-FSB1-2026-01-T001', '第 1.0 版', '出席 2.5 h', 'E07、E01', '陳船長、訪船船長', '林船長', report.reportNo]) expect(text).toContain(sub);
  });
});

describe('課程班次紀錄 CSV / JSON', () => {
  it('CSV:UTF-8 BOM、固定欄位、一列 = 學員 × 練習、RFC 4180 跳脫', () => {
    const s = session();
    const { report, forms } = e07Report(s);
    upsertExerciseFromReport(s, report, forms);
    s.evaluation = '問卷平均 4.5/5';
    const csv = sessionToCsv(s);
    expect(csv.startsWith('﻿')).toBe(true);
    const lines = csv.slice(1).trimEnd().split('\r\n');
    expect(lines[0]).toBe(SESSION_CSV_COLUMNS.join(','));
    expect(SESSION_CSV_COLUMNS).toEqual(
      expect.arrayContaining(['sessionId', 'courseName', 'courseVersion', 'startDate', 'sessionHours', 'traineeId', 'traineeName', 'rank', 'instructors', 'exerciseNo', 'scenarioId', 'equipment', 'attended', 'completed', 'score', 'evaluation']),
    );
    expect(lines).toHaveLength(1 + 3);
    const col = (name: string) => SESSION_CSV_COLUMNS.indexOf(name as (typeof SESSION_CSV_COLUMNS)[number]);
    const row1 = lines[1]!.split(',');
    expect(row1[col('sessionId')]).toBe('BRM-FSB1-2026-01');
    expect(row1[col('traineeId')]).toBe('T001');
    expect(row1[col('exerciseNo')]).toBe('E07');
    expect(row1[col('attended')]).toBe('Y');
    expect(row1[col('score')]).toBe('85');
    expect(row1[col('passed')]).toBe('Y');
    expect(row1[col('markerMean')]).toBe('3.53');
    expect(row1[col('evaluation')]).toBe('問卷平均 4.5/5');
    expect(row1[col('individualRecordNo')]).toBe('BRM-FSB1-2026-01-T001');
    // 姓名含逗號與引號 → 以引號包住並重複引號
    expect(lines[2]).toContain('"李, ""小華"""');
    // T003 未出席
    const row3 = lines[3]!.split(',');
    expect(row3[col('attended')]).toBe('N');
    expect(row3[col('score')]).toBe('');
    // 無練習時每位學員仍有一列
    const empty = sessionToCsv(session()).slice(1).trimEnd().split('\r\n');
    expect(empty).toHaveLength(4);
    expect(empty[1]!.split(',')).toHaveLength(SESSION_CSV_COLUMNS.length);
  });

  it('JSON 往返;匯出含聲明與總時數;非班次檔報錯', () => {
    const s = session();
    const { report, forms } = e07Report(s);
    upsertExerciseFromReport(s, report, forms);
    const text = exportCourseSession(s, NOW);
    const obj = JSON.parse(text) as { statement: string; totalHours: number; format: string };
    expect(obj.format).toBe('simosa-brm-course-session');
    expect(obj.statement).toBe(INTERNAL_TRAINING_STATEMENT);
    expect(obj.totalHours).toBe(1);
    const back = parseCourseSession(text).session!;
    expect(back.trainees).toEqual(s.trainees);
    expect(back.exercises).toEqual(s.exercises);
    expect(back.sessionId).toBe(s.sessionId);
    expect(parseCourseSession('{"format":"other"}').error).toContain('不是課程班次紀錄');
  });

  it('檢查:缺班次編號、課程版本、無出席、重複學員 ID', () => {
    const s = emptyCourseSession();
    expect(validateCourseSession(s)).toEqual(['未填班次編號(個人訓練紀錄編號以此組成)', '未填課程版本', '名冊沒有學員']);
    const s2 = session();
    s2.trainees.push({ id: 'T001', name: '重複', rank: '' });
    s2.exercises.push({ exerciseNo: 'E02', scenarioId: 'E02', date: '', hours: 0, recordKey: 'x', reportNo: '', instructor: '', assessors: [], equipment: '', attended: [], completed: [], results: {} });
    expect(validateCourseSession(s2)).toEqual(['學員 ID 重複:T001', 'E02 沒有出席學員', 'E02 時數未填']);
  });
});

describe('學員名冊 JSON', () => {
  it('匯出/匯入往返;缺 id 以姓名代替;非名冊報錯', () => {
    const roster = { format: 'simosa-brm-roster' as const, version: 1 as const, trainees: session().trainees, staff: [{ name: '陳船長', role: 'instructor' as const }, { name: '林船長', role: 'assessor' as const }] };
    const back = parseRoster(exportRoster(roster)).roster!;
    expect(back).toEqual(roster);
    expect(parseRoster(JSON.stringify({ format: 'simosa-brm-roster', trainees: [{ name: '無 ID' }, {}] })).roster!.trainees).toEqual([{ id: '無 ID', name: '無 ID', rank: '' }]);
    expect(parseRoster('{"format":"x"}').error).toContain('不是學員名冊');
  });
});
