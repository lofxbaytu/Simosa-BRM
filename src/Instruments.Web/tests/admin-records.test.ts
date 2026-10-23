// 教官資格紀錄、證書效期雙軌追蹤與複訓排程(規劃書 R9/R10、第 8.0 節複訓每 5 年與換證同步、第 8.3 節評估者校準):
// 日期計算、雙軌到期排序與 90/180 天警示、對齊換證、季度排程、CSV 公式注入防護、教官資格 CRUD 與 JSON 匯入/匯出、名冊同步。
import { describe, expect, it } from 'vitest';
import {
  addYears,
  alignWithStatutory,
  applyRefresherDue,
  buildRefresherSchedule,
  daysBetween,
  dualTrackRows,
  emptyAdminRecords,
  emptyInstructor,
  emptyTrack,
  expiryFlag,
  exportAdminRecords,
  INSTRUCTOR_CSV_COLUMNS,
  instructorRows,
  instructorsToCsv,
  latestCalibration,
  parseAdminRecords,
  quarterOf,
  refresherDueFrom,
  SCHEDULE_CSV_COLUMNS,
  scheduleToCsv,
  scheduleToJson,
  syncTracksWithRoster,
  TRACK_CSV_COLUMNS,
  tracksToCsv,
  unalignFromStatutory,
  validateAdminRecords,
  type AdminRecords,
} from '../src/report/admin-records.js';

const TODAY = '2026-10-23';

function track(id: string, patch: Partial<ReturnType<typeof emptyTrack>>): ReturnType<typeof emptyTrack> {
  return { ...emptyTrack(id), name: `學員${id}`, rank: '二副', ...patch };
}

describe('日期計算', () => {
  it('加年(閏日取 2/28)、天數差、複訓到期 = 上次完成 + 5 年', () => {
    expect(addYears('2024-02-29', 5)).toBe('2029-02-28');
    expect(addYears('2021-10-01', 5)).toBe('2026-10-01');
    expect(addYears('bad', 5)).toBe('');
    expect(refresherDueFrom('2021-10-01')).toBe('2026-10-01');
    expect(refresherDueFrom('2021-10-01', 3)).toBe('2024-10-01');
    expect(daysBetween(TODAY, '2026-10-24')).toBe(1);
    expect(daysBetween(TODAY, '2026-10-22')).toBe(-1);
    expect(daysBetween(TODAY, '2026-13-01')).toBeNull();
    expect(daysBetween(TODAY, '2026-02-30')).toBeNull();
    expect(quarterOf('2027-01-15')).toBe('2027-Q1');
    expect(quarterOf('2027-12-31')).toBe('2027-Q4');
  });

  it('到期旗標:逾期 / 90 天內 / 180 天內 / 有效 / 未填', () => {
    expect(expiryFlag('2026-10-22', TODAY)).toBe('expired');
    expect(expiryFlag(TODAY, TODAY)).toBe('soon');
    expect(expiryFlag('2027-01-21', TODAY)).toBe('soon'); // +90
    expect(expiryFlag('2027-01-22', TODAY)).toBe('upcoming'); // +91
    expect(expiryFlag('2027-04-21', TODAY)).toBe('upcoming'); // +180
    expect(expiryFlag('2027-04-22', TODAY)).toBe('ok'); // +181
    expect(expiryFlag('', TODAY)).toBe('none');
  });
});

describe('雙軌追蹤', () => {
  it('依較早到期日排序、無日期排最後;旗標取兩軌較急者;nextDueKind 標示哪一軌', () => {
    const admin = emptyAdminRecords();
    admin.tracks = [
      track('T003', { statutoryExpiry: '2028-01-01', lastCompletedDate: '2022-06-15' }), // 公司 2027-06-15
      track('T001', { statutoryExpiry: '2026-12-01', lastCompletedDate: '2024-01-01' }), // 法定 39 天
      track('T004', {}),
      track('T002', { statutoryExpiry: '2026-09-30', lastCompletedDate: '2021-09-30' }), // 兩軌皆逾期且同日
    ];
    admin.tracks.forEach((t) => applyRefresherDue(t, 5));
    const rows = dualTrackRows(admin, TODAY);
    expect(rows.map((r) => r.track.traineeId)).toEqual(['T002', 'T001', 'T003', 'T004']);
    expect(rows[0]).toMatchObject({ flag: 'expired', nextDue: '2026-09-30', nextDueKind: 'both', statutoryFlag: 'expired', companyFlag: 'expired' });
    expect(rows[1]).toMatchObject({ flag: 'soon', nextDue: '2026-12-01', nextDueKind: 'statutory', statutoryDays: 39, companyFlag: 'ok' });
    expect(rows[2]).toMatchObject({ flag: 'ok', nextDue: '2027-06-15', nextDueKind: 'company' });
    expect(rows[3]).toMatchObject({ flag: 'none', nextDue: '', nextDueKind: 'none', statutoryDays: null, companyDays: null });
  });

  it('對齊換證:公司複訓到期 = 法定到期;解除後回到上次完成 + 週期;applyRefresherDue 不覆寫人工填的到期日', () => {
    const t = track('T001', { statutoryExpiry: '2029-03-31', lastCompletedDate: '2024-01-01' });
    applyRefresherDue(t, 5);
    expect(t.refresherDue).toBe('2029-01-01');
    alignWithStatutory(t);
    expect(t).toMatchObject({ alignedWithStatutory: true, refresherDue: '2029-03-31' });
    unalignFromStatutory(t, 5);
    expect(t).toMatchObject({ alignedWithStatutory: false, refresherDue: '2029-01-01' });
    t.refresherDue = '2028-06-30'; // 人工調整
    applyRefresherDue(t, 5);
    expect(t.refresherDue).toBe('2028-06-30');
    // 對齊但法定到期未填:保留原值
    const u = track('T002', { lastCompletedDate: '2024-01-01', alignedWithStatutory: true });
    applyRefresherDue(u, 5);
    expect(u.refresherDue).toBe('');
  });
});

describe('複訓排程', () => {
  it('到期前 90 天為建議日並依季度分組;逾期者排本季並標 overdue;對齊同日合為一項;CSV/JSON 匯出', () => {
    const admin = emptyAdminRecords();
    admin.tracks = [
      track('T001', { statutoryExpiry: '2027-05-15', lastCompletedDate: '2022-05-15', alignedWithStatutory: true }), // both 2027-05-15 → 建議 2027-02-14 Q1
      track('T002', { statutoryExpiry: '2026-11-10', lastCompletedDate: '2022-02-01' }), // 法定 → 建議 2026-08-12 < 今日 → 今日 Q4;公司 2027-02-01 → 2026-11-03 Q4
      track('T003', { statutoryExpiry: '2026-01-01' }), // 已逾期
      track('T004', {}),
    ];
    admin.tracks.forEach((t) => applyRefresherDue(t, 5));
    const schedule = buildRefresherSchedule(admin, TODAY);
    expect(schedule.map((q) => q.quarter)).toEqual(['2026-Q4', '2027-Q1']);
    const q4 = schedule[0]!.items;
    expect(q4.map((i) => [i.traineeId, i.kind, i.plannedDate, i.overdue])).toEqual([
      ['T003', 'statutory', TODAY, true],
      ['T002', 'statutory', TODAY, false],
      ['T002', 'company', '2026-11-03', false],
    ]);
    expect(schedule[1]!.items).toEqual([{ traineeId: 'T001', name: '學員T001', rank: '二副', kind: 'both', dueDate: '2027-05-15', plannedDate: '2027-02-14', quarter: '2027-Q1', overdue: false }]);
    const csv = scheduleToCsv(schedule);
    expect(csv.startsWith('﻿')).toBe(true);
    const lines = csv.slice(1).trimEnd().split('\r\n');
    expect(lines[0]).toBe(SCHEDULE_CSV_COLUMNS.join(','));
    expect(lines).toHaveLength(1 + 4);
    expect(lines[1]).toBe(`2026-Q4,${TODAY},2026-01-01,statutory,T003,學員T003,二副,Y`);
    const json = JSON.parse(scheduleToJson(schedule, TODAY, new Date('2026-10-23T01:00:00Z'))) as { format: string; asOf: string; quarters: unknown[]; generatedUtc: string };
    expect(json).toMatchObject({ format: 'simosa-brm-refresher-schedule', asOf: TODAY, generatedUtc: '2026-10-23T01:00:00.000Z' });
    expect(json.quarters).toHaveLength(2);
    expect(buildRefresherSchedule(emptyAdminRecords(), TODAY)).toEqual([]);
  });
});

describe('CSV 公式注入防護', () => {
  it('姓名/職級/備註等文字欄位以 = + - @ 開頭時前加單引號;含逗號與引號者依 RFC 4180 包引號', () => {
    const admin = emptyAdminRecords();
    admin.tracks = [track('T001', { name: '=HYPERLINK("x")', rank: '+二副', statutoryType: '@cmd', statutoryNo: '-1', statutoryExpiry: '2027-01-01', note: '李, "小華"' })];
    admin.instructors = [{ ...emptyInstructor('I001'), name: '=SUM(A1)', trainerCourse: '-課程', calibrations: [{ date: '2026-01-10', referenceVideo: '@video', agreementRate: 0.85 }] }];
    const tcsv = tracksToCsv(admin, TODAY).slice(1).trimEnd().split('\r\n');
    expect(tcsv[0]).toBe(TRACK_CSV_COLUMNS.join(','));
    expect(tcsv[1]).toContain(`"'=HYPERLINK(""x"")"`);
    expect(tcsv[1]).toContain(`'+二副`);
    expect(tcsv[1]).toContain(`'@cmd`);
    expect(tcsv[1]).toContain(`'-1`);
    expect(tcsv[1]).toContain(`"李, ""小華"""`);
    const icsv = instructorsToCsv(admin).slice(1).trimEnd().split('\r\n');
    expect(icsv[0]).toBe(INSTRUCTOR_CSV_COLUMNS.join(','));
    expect(icsv[1]).toContain(`'=SUM(A1)`);
    expect(icsv[1]).toContain(`'-課程`);
    expect(icsv[1]).toContain(`'@video`);
    expect(icsv[1]).toContain(',0.85,1,');
    const s = scheduleToCsv(buildRefresherSchedule(admin, TODAY)).slice(1).trimEnd().split('\r\n');
    expect(s[1]).toContain(`"'=HYPERLINK(""x"")"`);
  });
});

describe('教官資格紀錄', () => {
  it('最近校準、一致率 ≥80% 方可獨立評分、逾一年需再校準、資格到期旗標', () => {
    const admin = emptyAdminRecords();
    const a = { ...emptyInstructor('I001'), name: '陳船長', expiryDate: '2026-12-31', calibrations: [{ date: '2025-01-10', referenceVideo: 'V1', agreementRate: 0.7 }, { date: '2026-09-01', referenceVideo: 'V2', agreementRate: 0.87 }] };
    const b = { ...emptyInstructor('I002'), name: '林船長', calibrations: [{ date: '2025-06-01', referenceVideo: 'V1', agreementRate: 0.93 }] };
    const c = { ...emptyInstructor('I003'), name: '新教官', expiryDate: '2026-01-01' };
    admin.instructors = [a, b, c];
    expect(latestCalibration(a)?.referenceVideo).toBe('V2');
    const rows = instructorRows(admin, TODAY);
    expect(rows[0]).toMatchObject({ calibrated: true, recalibrationDue: false, flag: 'soon', expiryDays: 69 });
    expect(rows[1]).toMatchObject({ calibrated: true, recalibrationDue: true, flag: 'none' });
    expect(rows[2]).toMatchObject({ calibrated: false, recalibrationDue: true, flag: 'expired', latestCalibration: null });
  });

  it('新增/修改/刪除後匯出 JSON 並匯入還原;一致率以百分比輸入自動換成 0–1;垃圾項目略過;format 不符報錯', () => {
    const admin: AdminRecords = emptyAdminRecords();
    const i = emptyInstructor('I001');
    i.name = '陳船長';
    i.trainerCourse = 'IMO 示範課程 6.09';
    i.trainerDate = '2024-03-01';
    i.assessorCourse = 'IMO 示範課程 3.12(A-I/6 第 7–8 段)';
    i.assessorDate = '2024-03-05';
    i.calibrations.push({ date: '2026-09-01', referenceVideo: 'E20 基準錄影 1/3', agreementRate: 0.87, note: '外部評估員監督' });
    admin.instructors.push(i, { ...emptyInstructor('I002'), name: '將刪除' });
    admin.instructors = admin.instructors.filter((x) => x.id !== 'I002');
    admin.tracks.push(track('T001', { statutoryType: 'A-II/1 BRM', statutoryNo: 'TW-1', statutoryExpiry: '2029-03-31', lastCompletedDate: '2024-04-01' }));
    applyRefresherDue(admin.tracks[0]!, 5);
    const text = exportAdminRecords(admin, TODAY, new Date('2026-10-23T01:00:00Z'));
    const obj = JSON.parse(text) as { format: string; exportedUtc: string; summary: { asOf: string; soon: number } };
    expect(obj).toMatchObject({ format: 'simosa-brm-admin-records', exportedUtc: '2026-10-23T01:00:00.000Z', summary: { asOf: TODAY, expired: 0, soon: 0, upcoming: 0 } });
    const back = parseAdminRecords(text).admin!;
    expect(back.instructors).toEqual(admin.instructors);
    expect(back.tracks).toEqual(admin.tracks);
    expect(back.refresherIntervalYears).toBe(5);
    // 百分比與垃圾
    const loose = parseAdminRecords(
      JSON.stringify({
        format: 'simosa-brm-admin-records',
        refresherIntervalYears: 3,
        instructors: [{ name: '甲', calibrations: [{ date: '2026-01-01', agreementRate: 85 }, { date: 'bad' }, 'x'] }, null, {}],
        tracks: [{ traineeId: 'T9', name: 'Z\nY', lastCompletedDate: '2024-01-01', statutoryExpiry: '2024-02-30' }, 5],
      }),
    ).admin!;
    expect(loose.instructors).toHaveLength(1);
    expect(loose.instructors[0]).toMatchObject({ id: 'I001', name: '甲' });
    expect(loose.instructors[0]!.calibrations).toEqual([{ date: '2026-01-01', referenceVideo: '', agreementRate: 0.85 }]);
    expect(loose.tracks).toEqual([{ ...emptyTrack('T9'), name: 'Z Y', lastCompletedDate: '2024-01-01', refresherDue: '2027-01-01' }]);
    expect(parseAdminRecords('{"format":"x"}').error).toContain('不是教官與證書紀錄');
    expect(parseAdminRecords('not json').error).toBe('JSON 解析失敗');
  });

  it('由名冊同步:新增缺少的學員並帶法定到期日、更新姓名/職級、不覆寫已填到期日;檢查重複 ID 與一致率範圍', () => {
    const admin = emptyAdminRecords();
    admin.tracks.push(track('T001', { name: '舊名', statutoryExpiry: '2030-01-01' }));
    const added = syncTracksWithRoster(admin, [
      { id: 'T001', name: '王大明', rank: '船長', certificateExpiry: '2029-03-31' },
      { id: 'T002', name: '李小華', rank: '二副', certificateExpiry: '2029-03-31' },
      { id: 'T003', name: '張三', rank: '三副' },
    ]);
    expect(added).toBe(2);
    expect(admin.tracks.map((t) => [t.traineeId, t.name, t.rank, t.statutoryExpiry])).toEqual([
      ['T001', '王大明', '船長', '2030-01-01'],
      ['T002', '李小華', '二副', '2029-03-31'],
      ['T003', '張三', '三副', ''],
    ]);
    expect(validateAdminRecords(admin)).toEqual([]);
    admin.tracks.push(track('T001', {}));
    admin.instructors.push({ ...emptyInstructor('I001'), calibrations: [{ date: '2026-01-01', referenceVideo: '', agreementRate: 1.5 }] }, emptyInstructor('I001'));
    expect(validateAdminRecords(admin)).toEqual(['教官 I001 未填姓名', 'I001 校準 2026-01-01 一致率超出 0–100%', '教官 I001 未填姓名', '教官 ID 重複:I001', '學員 ID 重複:T001']);
  });
});
