// 參與者與休息時間確認(規劃書第 5.5 節;R9/R10):sessionInfo 指令組裝與驗證、確認時刻來源、紀錄往返(教官站 JSON
// 與核心 JSON Lines 標頭)、講評報告與個人訓練紀錄帶入、班次 CSV 欄位。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { render } from 'lit';
import { describe, expect, it } from 'vitest';
import { serializeCommand } from '../src/lib/command.js';
import { COMMAND_TYPES } from '../src/types/command.js';
import { sessionInfoCommand } from '../src/lib/instructor-command.js';
import { parseReplay } from '../src/lib/record-replay.js';
import { buildSessionRecord } from '../src/lib/session-record.js';
import { buildSessionInfo, confirmationTimeUtc, emptySessionInfoDraft, parseSessionInfo, restHoursSummary, SESSION_PARTICIPANT_ROLES, validateSessionInfoDraft, type SessionInfo } from '../src/lib/session-info.js';
import { defaultState } from '../src/lib/state-parser.js';
import { applySessionInfoToMeta, buildDebriefReport, emptyReportMeta } from '../src/report/debrief-report.js';
import { renderIndividualDoc, renderReportDoc } from '../src/report/report-views.js';
import { buildIndividualRecord, emptyCourseSession, normalizeCourseSession, SESSION_CSV_COLUMNS, sessionToCsv, upsertExerciseFromReport } from '../src/report/training-record.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const E07 = readFileSync(path.join(here, 'fixtures/record-E07-core.jsonl'), 'utf8');

const INFO: SessionInfo = {
  participants: [
    { name: '王大明', role: 'master', rank: '船長', certificateNo: 'TW-12345' },
    { name: '李小華', role: 'oow', rank: '二副' },
    { name: '陳船長', role: 'instructor' },
  ],
  restHoursConfirmation: { confirmedBy: '王大明', confirmedAtUtc: '2026-10-17T02:00:00.000Z', compliant: true, note: '對照 10/16 休息時間紀錄' },
  course: { id: 'BRM-FSB1-2026-01', title: '中塑海運 BRM 內部訓練課程', instructor: '陳船長' },
};

describe('sessionInfo 指令', () => {
  it('列舉含 sessionInfo;args 依契約:participants / restHoursConfirmation / course,選填欄位空白不放', () => {
    expect(COMMAND_TYPES).toContain('sessionInfo');
    const cmd = sessionInfoCommand(INFO);
    expect(cmd).toEqual({
      type: 'sessionInfo',
      args: {
        participants: [
          { name: '王大明', role: 'master', rank: '船長', certificateNo: 'TW-12345' },
          { name: '李小華', role: 'oow', rank: '二副' },
          { name: '陳船長', role: 'instructor' },
        ],
        restHoursConfirmation: { confirmedBy: '王大明', confirmedAtUtc: '2026-10-17T02:00:00.000Z', compliant: true, note: '對照 10/16 休息時間紀錄' },
        course: { id: 'BRM-FSB1-2026-01', title: '中塑海運 BRM 內部訓練課程', instructor: '陳船長' },
      },
    });
    expect(serializeCommand(cmd)).toContain('"type":"sessionInfo"');
    // 無 course、無備註、不符合
    const minimal = sessionInfoCommand({ participants: [{ name: ' 張三 ', role: 'helmsman', rank: ' ' }], restHoursConfirmation: { confirmedBy: '張三', confirmedAtUtc: '2026-10-17T02:00:00Z', compliant: false }, course: { id: '' } });
    expect(minimal.args).toEqual({ participants: [{ name: '張三', role: 'helmsman' }], restHoursConfirmation: { confirmedBy: '張三', confirmedAtUtc: '2026-10-17T02:00:00.000Z', compliant: false } });
  });

  it('驗證:缺確認人/確認時刻、職務不在列舉、無參與者、姓名空白、時刻非 ISO 皆丟 RangeError', () => {
    const rest = INFO.restHoursConfirmation!;
    expect(() => sessionInfoCommand({ participants: INFO.participants })).toThrow(RangeError);
    expect(() => sessionInfoCommand({ participants: INFO.participants, restHoursConfirmation: { ...rest, confirmedBy: ' ' } })).toThrow(RangeError);
    expect(() => sessionInfoCommand({ participants: INFO.participants, restHoursConfirmation: { ...rest, confirmedAtUtc: 'yesterday' } })).toThrow(/ISO-8601/);
    expect(() => sessionInfoCommand({ participants: [{ name: '甲', role: 'captain' }], restHoursConfirmation: rest })).toThrow(/列舉/);
    expect(() => sessionInfoCommand({ participants: [], restHoursConfirmation: rest })).toThrow(/至少一位/);
    expect(() => sessionInfoCommand({ participants: [{ name: '', role: 'oow' }], restHoursConfirmation: rest })).toThrow(/姓名/);
    expect(SESSION_PARTICIPANT_ROLES).toEqual(['master', 'oow', 'pilot', 'helmsman', 'lookout', 'observer', 'instructor']);
  });

  it('草稿驗證與組裝:整列空白的參與者略過;確認時刻取 state.utc,無則瀏覽器 UTC', () => {
    const draft = emptySessionInfoDraft();
    expect(validateSessionInfoDraft(draft)).toEqual(['至少填一位參與者', '未填休息時間確認人']);
    draft.participants.push({ name: '', role: 'master', rank: '', certificateNo: '' }, { name: '王大明', role: 'master', rank: '船長', certificateNo: '' }, { name: '乙', role: 'chief' });
    draft.restHours.confirmedBy = '王大明';
    expect(validateSessionInfoDraft(draft)).toEqual(['參與者 3(乙)職務不在列舉內:master|oow|pilot|helmsman|lookout|observer|instructor']);
    draft.participants.pop();
    expect(validateSessionInfoDraft(draft)).toEqual([]);
    const at = confirmationTimeUtc('2026-10-17T02:00:00Z', new Date('2026-01-01T00:00:00Z'));
    expect(at).toBe('2026-10-17T02:00:00.000Z');
    expect(confirmationTimeUtc('not-a-time', new Date('2026-01-01T00:00:00Z'))).toBe('2026-01-01T00:00:00.000Z');
    expect(confirmationTimeUtc(undefined, new Date('2026-01-01T00:00:00Z'))).toBe('2026-01-01T00:00:00.000Z');
    const info = buildSessionInfo(draft, at);
    expect(info).toEqual({ participants: [{ name: '王大明', role: 'master', rank: '船長' }], restHoursConfirmation: { confirmedBy: '王大明', confirmedAtUtc: at, compliant: true } });
    expect(() => buildSessionInfo(emptySessionInfoDraft(), at)).toThrow(RangeError);
    expect(restHoursSummary(info.restHoursConfirmation)).toContain('符合 STCW A-VIII/1');
    expect(restHoursSummary(undefined)).toBe('未確認休息時間');
  });

  it('parseSessionInfo:字串陣列 participants、舊欄名 confirmedUtc、垃圾項目略過;空的回傳 null', () => {
    expect(parseSessionInfo({ participants: ['王大副', 7, { name: '' }, { name: '李二副', role: 'OOW', rank: '二副' }], restHoursConfirmation: { confirmedBy: '船長', confirmedUtc: '2026-10-17T02:00:00Z' } })).toEqual({
      participants: [
        { name: '王大副', role: 'observer' },
        { name: '李二副', role: 'oow', rank: '二副' },
      ],
      restHoursConfirmation: { confirmedBy: '船長', confirmedAtUtc: '2026-10-17T02:00:00Z', compliant: false },
    });
    expect(parseSessionInfo({})).toBeNull();
    expect(parseSessionInfo({ participants: [], restHoursConfirmation: {} })).toBeNull();
    expect(parseSessionInfo('x')).toBeNull();
  });
});

describe('紀錄往返', () => {
  it('教官站紀錄版本 3 含 sessionInfo;parseReplay 讀回 header.sessionInfo;未送出時為 null', () => {
    const base = { mode: 'full' as const, source: 'ws://x', scenario: null, shipId: 'FSB1', sampleInterval_s: 1, states: [defaultState('FSB1')], operations: [], markers: [], commands: [], summary: null };
    const record = buildSessionRecord({ ...base, sessionInfo: INFO });
    expect(record.version).toBe(3);
    expect(record.sessionInfo).toEqual(INFO);
    expect(record.sessionInfo).not.toBe(INFO);
    const r = parseReplay(JSON.stringify(record));
    expect(r.errors).toEqual([]);
    expect(r.header.sessionInfo).toEqual(INFO);
    expect(buildSessionRecord(base).sessionInfo).toBeNull();
    expect(parseReplay(JSON.stringify(buildSessionRecord(base))).header.sessionInfo).toBeUndefined();
  });

  it('核心 JSON Lines 標頭的 participants / restHoursConfirmation / course(攤平或整包 sessionInfo)→ header.sessionInfo', () => {
    const lines = E07.split('\n');
    const header = JSON.parse(lines[0]!) as Record<string, unknown>;
    const flat = { ...header, participants: INFO.participants, restHoursConfirmation: INFO.restHoursConfirmation, course: INFO.course };
    const r1 = parseReplay([JSON.stringify(flat), ...lines.slice(1)].join('\n'));
    expect(r1.header.sessionInfo).toEqual(INFO);
    const nested = { ...header, sessionInfo: INFO };
    const r2 = parseReplay([JSON.stringify(nested), ...lines.slice(1)].join('\n'));
    expect(r2.header.sessionInfo).toEqual(INFO);
    expect(parseReplay(E07).header.sessionInfo).toBeUndefined();
  });
});

describe('講評報告與個人訓練紀錄帶入', () => {
  const withInfo = () => {
    const lines = E07.split('\n');
    const header = JSON.parse(lines[0]!) as Record<string, unknown>;
    return parseReplay([JSON.stringify({ ...header, sessionInfo: INFO }), ...lines.slice(1)].join('\n'));
  };

  it('報告含 sessionInfo;列印版顯示休息時間確認與教官站登錄之參與者;無 sessionInfo 時提示', () => {
    const data = withInfo();
    const r = buildDebriefReport({ data, now: new Date('2026-10-17T12:00:00Z') });
    expect(r.sessionInfo).toEqual(INFO);
    const div = document.createElement('div');
    render(renderReportDoc(r), div);
    const text = div.textContent!;
    expect(text).toContain('休息時間確認(STCW A-VIII/1');
    expect(text).toContain('王大明');
    expect(text).toContain('符合 STCW A-VIII/1 與公司規定');
    expect(text).toContain('對照 10/16 休息時間紀錄');
    expect(div.querySelector('table.session-participants')!.textContent).toContain('TW-12345');
    expect(div.querySelector('table.session-participants')!.textContent).toContain('教官/帶領者');
    const plain = buildDebriefReport({ data: parseReplay(E07) });
    expect(plain.sessionInfo).toBeNull();
    const div2 = document.createElement('div');
    render(renderReportDoc(plain), div2);
    expect(div2.querySelector('.rest-hours')!.textContent).toContain('紀錄沒有休息時間確認');
  });

  it('applySessionInfoToMeta:只在無參與者時帶入,姓名對應名冊 ID,instructor → 帶領者;課程 id/教官填到空白欄', () => {
    const meta = emptyReportMeta();
    const changed = applySessionInfoToMeta(meta, INFO, [{ id: 'T001', name: '王大明', rank: '船長' }]);
    expect(changed).toBe(true);
    expect(meta.participants).toEqual([
      { traineeId: 'T001', name: '王大明', rank: '船長', role: 'master' },
      { traineeId: '', name: '李小華', rank: '二副', role: 'oow' },
      { traineeId: '', name: '陳船長', rank: '', role: 'leader' },
    ]);
    expect(meta.instructor).toBe('陳船長');
    expect(meta.sessionId).toBe('BRM-FSB1-2026-01');
    // 已有參與者/教官時不覆寫
    const meta2 = emptyReportMeta();
    meta2.participants.push({ traineeId: 'T009', name: '既有', rank: '', role: 'oow' });
    meta2.instructor = '訪船船長';
    expect(applySessionInfoToMeta(meta2, INFO)).toBe(true); // sessionId 仍帶入
    expect(meta2.participants).toHaveLength(1);
    expect(meta2.instructor).toBe('訪船船長');
    expect(applySessionInfoToMeta(emptyReportMeta(), undefined)).toBe(false);
  });

  it('加入班次後:練習含休息時間確認與角色;個人訓練紀錄列印顯示;CSV 有 participantRole / restHours* 欄位;JSON 往返', () => {
    const s = emptyCourseSession();
    s.sessionId = 'BRM-01';
    s.trainees = [
      { id: 'T001', name: '王大明', rank: '船長' },
      { id: 'T002', name: '李小華', rank: '二副' },
    ];
    const data = withInfo();
    const meta = emptyReportMeta();
    applySessionInfoToMeta(meta, data.header.sessionInfo, s.trainees);
    meta.participants[1]!.traineeId = 'T002';
    const report = buildDebriefReport({ data, meta, now: new Date('2026-10-17T12:00:00Z') });
    const ex = upsertExerciseFromReport(s, report, []);
    expect(ex.attended).toEqual(['T001', 'T002']);
    expect(ex.roles).toEqual({ T001: 'master', T002: 'oow' });
    expect(ex.restHoursConfirmation).toEqual(INFO.restHoursConfirmation);
    const rec = buildIndividualRecord(s, s.trainees[0]!);
    expect(rec.results[0]).toMatchObject({ role: 'master', restHoursConfirmation: INFO.restHoursConfirmation });
    const div = document.createElement('div');
    render(renderIndividualDoc(rec), div);
    expect(div.querySelector('table.results')!.textContent).toContain('船長');
    expect(div.querySelector('table.results td.rest-hours')!.textContent).toContain('王大明');
    expect(div.querySelector('table.results td.rest-hours')!.textContent).toContain('符合');
    // CSV
    const csv = sessionToCsv(s);
    const lines = csv.slice(1).trimEnd().split('\r\n');
    const col = (name: string) => SESSION_CSV_COLUMNS.indexOf(name as (typeof SESSION_CSV_COLUMNS)[number]);
    expect(lines[0]!.split(',')).toHaveLength(SESSION_CSV_COLUMNS.length);
    const row1 = lines[1]!.split(',');
    expect(row1[col('participantRole')]).toBe('master');
    expect(row1[col('restHoursConfirmedBy')]).toBe('王大明');
    expect(row1[col('restHoursConfirmedAtUtc')]).toBe('2026-10-17T02:00:00.000Z');
    expect(row1[col('restHoursCompliant')]).toBe('Y');
    expect(lines[2]!.split(',')[col('participantRole')]).toBe('oow');
    // 無練習的學員列欄位數仍一致
    const empty = sessionToCsv({ ...emptyCourseSession(), trainees: s.trainees }).slice(1).trimEnd().split('\r\n');
    expect(empty[1]!.split(',')).toHaveLength(SESSION_CSV_COLUMNS.length);
    // JSON 往返保留 roles 與 restHoursConfirmation
    const back = normalizeCourseSession(JSON.parse(JSON.stringify(s))).session!;
    expect(back.exercises[0]!.roles).toEqual(ex.roles);
    expect(back.exercises[0]!.restHoursConfirmation).toEqual(ex.restHoursConfirmation);
    // 休息時間備註含換行 → 單行
    const dirty = normalizeCourseSession({ format: 'simosa-brm-course-session', exercises: [{ exerciseNo: 'E01', restHoursConfirmation: { confirmedBy: 'A\nB', confirmedAtUtc: 'x', compliant: true, note: 'n\r\n' }, roles: { T1: 'oow', T2: 5 } }] }).session!;
    expect(dirty.exercises[0]!.restHoursConfirmation).toEqual({ confirmedBy: 'A B', confirmedAtUtc: 'x', compliant: true, note: 'n' });
    expect(dirty.exercises[0]!.roles).toEqual({ T1: 'oow' });
  });
});
