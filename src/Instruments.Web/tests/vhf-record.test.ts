// VHF 角色扮演在紀錄與講評的呈現:紀錄 vhf 行解析(record-replay)、事件文字/嚴重度/來源(sim-event)、講評報告的事件時間軸與
// 「通訊」統計(debrief-report、report-views)、training record 的通話次數/總秒數欄位與 CSV(training-record)、事件流來源篩選鈕。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { render } from 'lit';
import { beforeAll, describe, expect, it } from 'vitest';
import { parseJsonLines, parseReplay } from '../src/lib/record-replay.js';
import { defaultSourceFor, eventLabel, eventSeverity, parseSimEvent, SOURCE_LABEL } from '../src/lib/sim-event.js';
import { SIM_EVENT_KINDS, SIM_EVENT_SOURCES } from '../src/types/event.js';
import { buildDebriefReport, buildTimeline, emptyReportMeta, reportOutline, vhfActivity } from '../src/report/debrief-report.js';
import { renderIndividualDoc, renderReportDoc } from '../src/report/report-views.js';
import { buildIndividualRecord, emptyCourseSession, SESSION_CSV_COLUMNS, sessionToCsv, upsertExerciseFromReport } from '../src/report/training-record.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const E01 = readFileSync(path.join(here, 'fixtures/record-E01-core.jsonl'), 'utf8');

/** 在核心紀錄的 footer 前插入 vhf 行(與 C# RecordVhf 形狀相同)。 */
function withVhfLines(record: string): string {
  const lines = record.trimEnd().split('\n');
  const footerIdx = lines.findIndex((l) => l.startsWith('{"kind":"footer"'));
  const vhf = [
    { kind: 'vhf', tick: 150, t: 3, event: { kind: 'vhfCall', tick: 150, t: 3, source: 'vhf', itemId: '16', message: 'VHF 16 bridge 通話 2.5 s', visible: true, seq: -1, data: { id: 's1', station: 'bridge', channel: '16', duration_s: 2.5, endReason: 'pttEnd' } } },
    { kind: 'vhf', tick: 200, t: 4, event: { kind: 'vhfText', tick: 200, t: 4, source: 'vhf', itemId: '16', message: 'VHF 16 vts:Bridge, proceed', visible: true, seq: -1, data: { id: 's2', station: 'vts', channel: '16', text: 'Bridge, proceed' } } },
    { kind: 'vhf', tick: 250, t: 5, event: { kind: 'vhfCall', tick: 250, t: 5, source: 'vhf', itemId: '16', visible: true, seq: -1, data: { id: 's2', station: 'vts', channel: '16', duration_s: 1.25, endReason: 'disconnected' } } },
    { kind: 'vhf', tick: 300, t: 6, event: { kind: 'vhfDsc', source: 'vhf', itemId: '70', visible: true, seq: -1, data: { id: 's1', station: 'bridge', channel: '70', category: 'distress', text: 'MAYDAY' } } },
  ].map((l) => JSON.stringify(l));
  const idx = footerIdx < 0 ? lines.length : footerIdx;
  return [...lines.slice(0, idx), ...vhf, ...lines.slice(idx)].join('\n') + '\n';
}

beforeAll(async () => {
  await import('../src/components/instructor/event-stream.js');
});

describe('VHF 事件(sim-event)', () => {
  it('來源 vhf 與三種 kind 列在清單;缺 source 時由 kind 推得', () => {
    expect(SIM_EVENT_SOURCES).toContain('vhf');
    expect(SOURCE_LABEL.vhf).toBe('VHF');
    for (const k of ['vhfCall', 'vhfText', 'vhfDsc']) {
      expect(SIM_EVENT_KINDS).toContain(k);
      expect(defaultSourceFor(k)).toBe('vhf');
    }
    const ev = parseSimEvent({ kind: 'vhfText', tick: 10, data: { station: 'vts', channel: '12', text: 'hi' } })!;
    expect(ev.source).toBe('vhf');
    expect(ev.t).toBeCloseTo(0.2);
  });

  it('文字與嚴重度:通話秒數與結束原因、文字、DSC(distress 紅、urgency 黃)', () => {
    const call = parseSimEvent({ kind: 'vhfCall', tick: 0, t: 0, source: 'vhf', itemId: '16', data: { station: 'bridge', channel: '16', duration_s: 2.5, endReason: 'pttEnd' } })!;
    expect(eventLabel(call)).toBe('VHF CH16 bridge 通話 2.5 s');
    expect(eventSeverity(call)).toBe('info');
    const dropped = parseSimEvent({ kind: 'vhfCall', tick: 0, t: 0, source: 'vhf', data: { station: 'vts', channel: '12', duration_s: 1.25, endReason: 'disconnected' } })!;
    expect(eventLabel(dropped)).toBe('VHF CH12 vts 通話 1.3 s(斷線結束)');
    const text = parseSimEvent({ kind: 'vhfText', tick: 0, t: 0, data: { station: 'vts', channel: '16', text: 'Bridge, proceed' } })!;
    expect(eventLabel(text)).toBe('VHF CH16 vts 文字:Bridge, proceed');
    const distress = parseSimEvent({ kind: 'vhfDsc', tick: 0, t: 0, data: { station: 'bridge', category: 'distress', text: 'MAYDAY' } })!;
    expect(eventLabel(distress)).toBe('DSC DISTRESS 遇險 由 bridge:MAYDAY');
    expect(eventSeverity(distress)).toBe('alarm');
    const urgency = parseSimEvent({ kind: 'vhfDsc', tick: 0, t: 0, data: { station: 'bridge', category: 'urgency' } })!;
    expect(eventLabel(urgency)).toBe('DSC URGENCY 緊急 由 bridge');
    expect(eventSeverity(urgency)).toBe('warning');
    expect(eventSeverity(parseSimEvent({ kind: 'vhfDsc', tick: 0, t: 0, data: { category: 'safety' } })!)).toBe('info');
  });
});

describe('紀錄 vhf 行(record-replay)', () => {
  it('vhf 行的 event 併入 events(依檔案順序)、不列為未知 kind;壞行記入 errors', () => {
    const r = parseJsonLines(withVhfLines(E01));
    expect(r.unknownKinds).toEqual({});
    expect(r.errors).toEqual([]);
    const vhf = r.events.filter((e) => e.source === 'vhf');
    expect(vhf.map((e) => e.kind)).toEqual(['vhfCall', 'vhfText', 'vhfCall', 'vhfDsc']);
    expect(vhf[3]!.tick).toBe(300); // 行的 tick/t 補到事件
    expect(vhf[3]!.t).toBe(6);
    expect(vhf[0]!.itemId).toBe('16');
    const bad = parseJsonLines('{"kind":"header","dt":0.02,"shipId":"FSB1"}\n{"kind":"vhf","tick":1,"t":0.02,"event":{"nope":true}}\n');
    expect(bad.errors).toEqual(['第 2 行:vhf 無法解析']);
    expect(bad.events).toEqual([]);
  });

  it('倒帶時 t > 倒帶點的 VHF 事件與其他事件一樣丟棄', () => {
    const text = [
      '{"kind":"header","dt":0.02,"shipId":"FSB1","scenario":{"id":"T"}}',
      '{"kind":"state","state":{"tick":100,"t":2,"heading":0,"pos":{"x":0,"y":0},"cog":0,"sog":0,"stw":0,"rot":0,"rudder":0,"rudderOrder":0,"rpm":0,"rpmOrder":0,"telegraph":"STOP","depthBelowKeel":10,"wind":{"trueSpeed":0,"trueDir":0},"current":{"set":0,"drift":0},"thruster":{"order":0,"actual":0}}}',
      '{"kind":"vhf","tick":100,"t":2,"event":{"kind":"vhfText","source":"vhf","tick":100,"t":2,"data":{"station":"a","channel":"16","text":"before"}}}',
      '{"kind":"state","state":{"tick":300,"t":6,"heading":0,"pos":{"x":0,"y":0},"cog":0,"sog":0,"stw":0,"rot":0,"rudder":0,"rudderOrder":0,"rpm":0,"rpmOrder":0,"telegraph":"STOP","depthBelowKeel":10,"wind":{"trueSpeed":0,"trueDir":0},"current":{"set":0,"drift":0},"thruster":{"order":0,"actual":0}}}',
      '{"kind":"vhf","tick":300,"t":6,"event":{"kind":"vhfText","source":"vhf","tick":300,"t":6,"data":{"station":"a","channel":"16","text":"old branch"}}}',
      '{"kind":"input","tick":300,"command":{"type":"restore","args":{"tick":150}}}',
      '{"kind":"state","state":{"tick":175,"t":3.5,"heading":0,"pos":{"x":0,"y":0},"cog":0,"sog":0,"stw":0,"rot":0,"rudder":0,"rudderOrder":0,"rpm":0,"rpmOrder":0,"telegraph":"STOP","depthBelowKeel":10,"wind":{"trueSpeed":0,"trueDir":0},"current":{"set":0,"drift":0},"thruster":{"order":0,"actual":0}}}',
    ].join('\n');
    const r = parseJsonLines(text);
    expect(r.rewinds).toHaveLength(1);
    expect(r.events.map((e) => e.data?.['text'])).toEqual(['before']);
  });
});

describe('講評報告與 training record', () => {
  it('事件時間軸列出 VHF 事件(來源 VHF、嚴重度);通訊統計為 vhf 段;無活動時 null', () => {
    const data = parseReplay(withVhfLines(E01));
    const tl = buildTimeline(data.events.filter((e) => e.source === 'vhf'));
    expect(tl.rows.map((r) => [r.source, r.label, r.severity])).toEqual([
      ['VHF', 'VHF CH16 bridge 通話 2.5 s', 'info'],
      ['VHF', 'VHF CH16 vts 文字:Bridge, proceed', 'info'],
      ['VHF', 'VHF CH16 vts 通話 1.3 s(斷線結束)', 'info'],
      ['VHF', 'DSC DISTRESS 遇險 由 bridge:MAYDAY', 'alarm'],
    ]);
    const report = buildDebriefReport({ data, meta: emptyReportMeta(), now: new Date('2026-10-30T00:00:00Z') });
    expect(report.vhf).toEqual({
      calls: 2,
      duration_s: 3.8,
      texts: 1,
      dsc: 1,
      distress: 1,
      byStation: [
        { station: 'bridge', calls: 1, duration_s: 2.5, texts: 0, dsc: 1 },
        { station: 'vts', calls: 1, duration_s: 1.3, texts: 1, dsc: 0 },
      ],
    });
    expect(report.timeline.some((r) => r.source === 'VHF')).toBe(true);
    expect(reportOutline(report)).toContain('VHF 通話 2 次 3.8 s / 文字 1 / DSC 1');
    const plain = buildDebriefReport({ data: parseReplay(E01), now: new Date('2026-10-30T00:00:00Z') });
    expect(plain.vhf).toBeNull();
    expect(vhfActivity([])).toBeNull();
    expect(reportOutline(plain).some((l) => l.startsWith('VHF'))).toBe(false);
    // 報告文件含「通訊(VHF 角色扮演)」小節與各站表;無活動時不列
    const host = document.createElement('div');
    render(renderReportDoc(report), host);
    expect(host.textContent).toContain('通訊(VHF 角色扮演)');
    expect(host.textContent).toContain('2 次');
    expect(host.querySelectorAll('table.vhf tbody tr')).toHaveLength(2);
    expect(host.textContent).toContain('MAYDAY');
    const host2 = document.createElement('div');
    render(renderReportDoc(plain), host2);
    expect(host2.textContent).not.toContain('通訊(VHF 角色扮演)');
  });

  it('training record:練習帶入 vhf 統計、個人紀錄 results 含 vhf、CSV 多四欄', () => {
    const data = parseReplay(withVhfLines(E01));
    const session = emptyCourseSession();
    session.sessionId = 'BRM-2026-01';
    session.trainees = [{ id: 'T001', name: '王大明', rank: '船長' }];
    const meta = emptyReportMeta();
    meta.participants = [{ traineeId: 'T001', name: '王大明', rank: '船長', role: 'master' }];
    const report = buildDebriefReport({ data, meta, now: new Date('2026-10-30T00:00:00Z') });
    const ex = upsertExerciseFromReport(session, report, []);
    expect(ex.vhf).toEqual({ calls: 2, duration_s: 3.8, texts: 1, dsc: 1 });
    const rec = buildIndividualRecord(session, session.trainees[0]!);
    expect(rec.results[0]!.vhf).toEqual({ calls: 2, duration_s: 3.8, texts: 1, dsc: 1 });
    expect(SESSION_CSV_COLUMNS.slice(-5)).toEqual(['vhfCalls', 'vhfDuration_s', 'vhfTexts', 'vhfDsc', 'evaluation']);
    const csv = sessionToCsv(session);
    const lines = csv.replace(/^﻿/, '').trimEnd().split('\r\n');
    const col = (name: string) => SESSION_CSV_COLUMNS.indexOf(name as (typeof SESSION_CSV_COLUMNS)[number]);
    const row = lines[1]!.split(',');
    expect(row).toHaveLength(SESSION_CSV_COLUMNS.length);
    expect(row[col('vhfCalls')]).toBe('2');
    expect(row[col('vhfDuration_s')]).toBe('3.8');
    expect(row[col('vhfTexts')]).toBe('1');
    expect(row[col('vhfDsc')]).toBe('1');
    // 無 VHF 活動的練習:欄位空白、不帶 vhf
    const plainReport = buildDebriefReport({ data: parseReplay(E01), meta, now: new Date('2026-10-30T00:00:00Z') });
    const s2 = emptyCourseSession();
    s2.sessionId = 'X';
    s2.trainees = [{ id: 'T001', name: '王大明', rank: '船長' }];
    const ex2 = upsertExerciseFromReport(s2, plainReport, []);
    expect(ex2.vhf).toBeUndefined();
    const row2 = sessionToCsv(s2).replace(/^﻿/, '').trimEnd().split('\r\n')[1]!.split(',');
    expect(row2).toHaveLength(SESSION_CSV_COLUMNS.length);
    expect(row2[col('vhfCalls')]).toBe('');
    const host = document.createElement('div');
    render(renderIndividualDoc(rec), host);
    expect(host.textContent).toContain('王大明');
  });

  it('教官站事件流:VHF 來源篩選鈕與列樣式', async () => {
    const el = document.createElement('brm-event-stream') as unknown as HTMLElement & { events: unknown[]; updateComplete: Promise<unknown> };
    el.events = [parseSimEvent({ kind: 'vhfText', tick: 10, t: 0.2, source: 'vhf', data: { station: 'vts', channel: '16', text: 'hello' } })];
    document.body.appendChild(el);
    await el.updateComplete;
    const btn = [...el.shadowRoot!.querySelectorAll<HTMLButtonElement>('.filters button')].find((b) => b.classList.contains('src-vhf'));
    expect(btn?.textContent?.replace(/\s+/g, ' ').trim()).toBe('VHF 1');
    const item = el.shadowRoot!.querySelector('.item.vhf');
    expect(item?.textContent).toContain('VHF CH16 vts 文字:hello');
    el.remove();
  });
});
