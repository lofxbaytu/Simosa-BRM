// 錄影與紀錄/報告(docs/design/screen-recording.md 第 5、6 節;happy-dom):教官站紀錄 version 4 的 recordings → 重播解析 header.recordings →
// 講評報告「畫面錄影」列出檔名與長度;核心 JSON Lines 紀錄沒有錄影資訊時顯示說明。
// 教官站整合:以假依賴取代瀏覽器 getDisplayMedia / MediaRecorder / fetch,按面板「開始錄影」→ 標題列錄影中徽章 → 「停止錄影」→ 紀錄 JSON 含 recordings;
// 自動開始設定:簡化模式「開始練習」自動開始、「結束練習」自動停止。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { render } from 'lit';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { parseReplay } from '../src/lib/record-replay.js';
import { loadScenarioList } from '../src/lib/scenario.js';
import { buildSessionRecord, isSessionRecord } from '../src/lib/session-record.js';
import { recordingDepsProvider, RECORDING_SETTINGS_KEY, type MediaRecorderLike, type RecordingSummary, type ScreenRecordingDeps } from '../src/lib/screen-recording.js';
import { buildDebriefReport } from '../src/report/debrief-report.js';
import { renderReportDoc } from '../src/report/report-views.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const E07 = readFileSync(path.join(here, 'fixtures/record-E07-core.jsonl'), 'utf8');

const RECORDINGS: RecordingSummary[] = [
  { mediaId: '20261017-020000-instructor-E05-abc123', file: '20261017-020000-instructor-E05-abc123.webm', station: 'instructor', startedUtc: '2026-10-17T02:00:00.000Z', stoppedUtc: '2026-10-17T03:00:00.000Z', durationS: 3600, uploadedBytes: 734003200, chunks: 1800, truncated: false, record: '20261017-015959-E05_pilot_traffic.jsonl', sha256: 'AB' },
  { mediaId: '20261017-031000-instructor-E05-def456', station: 'instructor', startedUtc: '2026-10-17T03:10:00.000Z', uploadedBytes: 0, chunks: 0, truncated: false, error: '使用者取消分享畫面' },
];

function text(t: ReturnType<typeof renderReportDoc>): string {
  const div = document.createElement('div');
  render(t, div);
  return div.textContent ?? '';
}

describe('錄影摘要進紀錄與講評報告', () => {
  it('教官站紀錄 version 4 recordings → 重播 header.recordings → 報告「畫面錄影」表', () => {
    const rec = buildSessionRecord({ mode: 'full', source: 'ws://192.168.77.11:8765', scenario: null, shipId: 'FSB1', sampleInterval_s: 1, states: [], operations: [], markers: [], commands: [], summary: null, recordings: RECORDINGS });
    expect(rec.version).toBe(4);
    expect(rec.recordings).toEqual(RECORDINGS);
    expect(rec.recordings).not.toBe(RECORDINGS);
    expect(isSessionRecord(rec)).toBe(true);
    const data = parseReplay(JSON.stringify(rec));
    expect(data.header.recordings).toEqual(RECORDINGS);
    const report = buildDebriefReport({ data, now: new Date('2026-10-17T12:00:00Z') });
    expect(report.recordings).toHaveLength(2);
    const t = text(renderReportDoc(report));
    expect(t).toContain('畫面錄影');
    expect(t).toContain('20261017-020000-instructor-E05-abc123.webm');
    expect(t).toContain('1 h 00 min');
    expect(t).toContain('700 MB');
    expect(t).toContain('紀錄 20261017-015959-E05_pilot_traffic.jsonl');
    expect(t).toContain('失敗:使用者取消分享畫面');
    // 沒有 recordings 的舊紀錄與核心 JSON Lines 紀錄
    const old = buildSessionRecord({ mode: 'full', source: 'x', scenario: null, shipId: 'FSB1', sampleInterval_s: 1, states: [], operations: [], markers: [], commands: [], summary: null });
    expect(parseReplay(JSON.stringify(old)).header.recordings).toBeUndefined();
    const core = parseReplay(E07);
    expect(core.header.recordings).toBeUndefined();
    expect(text(renderReportDoc(buildDebriefReport({ data: core, now: new Date('2026-10-17T12:00:00Z') })))).toContain('紀錄沒有錄影資訊');
  });
});

// ------------------------------------------------------------ 教官站整合
type App = HTMLElement & { updateComplete: Promise<unknown> };

class FakeRecorder implements MediaRecorderLike {
  state: 'inactive' | 'recording' | 'paused' = 'inactive';
  ondataavailable: ((ev: { data: Blob }) => void) | null = null;
  onstop: (() => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  constructor(readonly mimeType: string) {}
  start(): void {
    this.state = 'recording';
  }
  stop(): void {
    this.state = 'inactive';
    queueMicrotask(() => this.onstop?.());
  }
  emit(size: number): void {
    this.ondataavailable?.({ data: new Blob(['x'.repeat(size)]) });
  }
}

function fakeDeps(): { deps: ScreenRecordingDeps; calls: string[]; recorders: FakeRecorder[]; starts: Array<Record<string, unknown>> } {
  const calls: string[] = [];
  const recorders: FakeRecorder[] = [];
  const starts: Array<Record<string, unknown>> = [];
  let n = 0;
  const deps: ScreenRecordingDeps = {
    fetch: async (url, init) => {
      calls.push(url);
      if (url.endsWith('/media/start')) {
        starts.push(JSON.parse(init.body as string) as Record<string, unknown>);
        n++;
        return { ok: true, status: 200, json: async () => ({ mediaId: `M${n}`, file: `M${n}.webm`, maxBytes: 1 }) };
      }
      if (url.includes('/media/stop/')) return { ok: true, status: 200, json: async () => ({ mediaId: `M${n}`, file: `M${n}.webm`, durationS: 12, sizeBytes: 30, chunks: 1, truncated: false }) };
      return { ok: true, status: 200, json: async () => ({}) };
    },
    getDisplayMedia: async () => ({ getTracks: () => [{ kind: 'video', stop: () => {}, onended: null }] }),
    createRecorder: (_s, mime) => {
      const r = new FakeRecorder(mime);
      recorders.push(r);
      return r;
    },
    isTypeSupported: () => true,
    now: () => new Date(),
    delay: async () => {},
  };
  return { deps, calls, recorders, starts };
}

async function mountApp(query: string): Promise<App> {
  history.replaceState(null, '', query);
  const el = document.createElement('brm-instructor-app') as App;
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

const panelOf = (app: App, within: string) => app.shadowRoot!.querySelector(`${within} brm-recording-panel`) as (HTMLElement & { updateComplete: Promise<unknown>; status: { state: string } }) | null;
const click = (el: HTMLElement, sel: string) => (el.shadowRoot!.querySelector(sel) as HTMLButtonElement).click();

describe('教官站錄影整合', () => {
  const originalCreate = recordingDepsProvider.create;

  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });

  afterEach(() => {
    document.body.innerHTML = '';
    localStorage.removeItem(RECORDING_SETTINGS_KEY);
    recordingDepsProvider.create = originalCreate;
  });

  it('完整教官站:面板開始 → 標題列錄影中 → 停止 → 紀錄 JSON 含 recordings;上傳端由 ?media= 指定', async () => {
    const f = fakeDeps();
    recordingDepsProvider.create = () => f.deps;
    const app = await mountApp('/?demo=1&ws=ws://192.168.77.11:8765&media=http://192.168.77.11:9999');
    const panel = panelOf(app, '.full')!;
    expect(panel).not.toBeNull();
    expect(panel.shadowRoot!.textContent).toContain('http://192.168.77.11:9999');
    click(panel, 'button.start');
    await vi.waitFor(() => expect(panel.status.state).toBe('recording'));
    await app.updateComplete;
    expect(f.calls[0]).toBe('http://192.168.77.11:9999/media/start');
    expect(f.starts[0]).toMatchObject({ station: 'instructor' });
    expect(String(f.starts[0]!['exerciseId'])).toMatch(/^(demo|FSB1)@20\d\d-/); // 示範來源的情境 id@練習開始時刻
    expect(app.shadowRoot!.querySelector('header .rec-badge')?.textContent).toContain('錄影中');
    f.recorders[0]!.emit(30);
    await vi.waitFor(() => expect(f.calls.some((u) => u.includes('/media/chunk/M1'))).toBe(true));
    click(panel, 'button.stop');
    await vi.waitFor(() => expect(panel.status.state).toBe('idle'));
    await app.updateComplete;
    expect(app.shadowRoot!.querySelector('header .rec-badge')).toBeNull();
    const record = JSON.parse((app as unknown as { sessionRecordJson(): string }).sessionRecordJson()) as { version: number; recordings: RecordingSummary[] };
    expect(record.version).toBe(4);
    expect(record.recordings).toHaveLength(1);
    expect(record.recordings[0]).toMatchObject({ mediaId: 'M1', file: 'M1.webm', durationS: 12, uploadedBytes: 30, station: 'instructor' });
  });

  it('瀏覽器不支援時面板顯示提示;?ws 主機推得預設上傳端 8768', async () => {
    recordingDepsProvider.create = () => null;
    const app = await mountApp('/?demo=1&ws=ws://192.168.77.12:8765');
    const panel = panelOf(app, '.full')!;
    expect(panel.shadowRoot!.textContent).toContain('不支援畫面擷取');
    expect(panel.shadowRoot!.textContent).toContain('http://192.168.77.12:8768');
    expect((panel.shadowRoot!.querySelector('button.start') as HTMLButtonElement).disabled).toBe(true);
  });

  it('簡化教官模式自動開始:載入情境 → 開始練習自動錄影(station instructor-simple)→ 結束練習自動停止', async () => {
    localStorage.setItem(RECORDING_SETTINGS_KEY, JSON.stringify({ autoStart: true, withAudio: false }));
    const f = fakeDeps();
    recordingDepsProvider.create = () => f.deps;
    const app = await mountApp('/?demo=1&mode=simple');
    const scenarioPanel = app.shadowRoot!.querySelector('brm-scenario-panel') as HTMLElement;
    const e05 = loadScenarioList().find((x) => x.id === 'E05_pilot_traffic');
    expect(e05).toBeDefined();
    scenarioPanel.dispatchEvent(new CustomEvent('scenario-load', { detail: e05, bubbles: true, composed: true }));
    await app.updateComplete;
    expect(f.starts).toHaveLength(0); // 簡報期間不錄
    click(app, 'button.start');
    await vi.waitFor(() => expect(f.starts).toHaveLength(1));
    expect(f.starts[0]).toMatchObject({ station: 'instructor-simple' });
    expect(String(f.starts[0]!['exerciseId'])).toMatch(/^E05_pilot_traffic@/);
    await vi.waitFor(() => expect(panelOf(app, '.simple')!.status.state).toBe('recording'));
    click(app, 'button.end');
    await app.updateComplete;
    click(app, 'button.end'); // 兩段確認
    await vi.waitFor(() => expect(f.calls.some((u) => u.includes('/media/stop/M1'))).toBe(true));
    await vi.waitFor(() => expect(f.recorders[0]!.state).toBe('inactive'));
  });
});
