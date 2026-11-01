// 畫面錄影控制器(lib/screen-recording.ts;docs/design/screen-recording.md):以假 MediaRecorder / getDisplayMedia / fetch 測狀態機與上傳佇列 —
// start → 片段依序上傳(X-Seq 連續)→ stop 帶長度並以核心旁檔做摘要;網路錯誤指數退避重試且順序不變;核心 413 → 截斷並提前結束;
// 離線緩衝超限 → 錯誤結束(不丟片段);409 致命;取得畫面被拒 / 核心拒絕開始;使用者按瀏覽器「停止分享」視同停止;工具函式。
import { describe, expect, it, vi } from 'vitest';
import {
  ScreenRecordingController,
  formatRecordingDuration,
  loadRecordingSettings,
  mediaBaseUrl,
  parseRecordings,
  pickMime,
  saveRecordingSettings,
  type MediaRecorderLike,
  type MediaStreamLike,
  type MediaTrackLike,
  type RecordingStatus,
  type ScreenRecordingDeps,
  type UploadResponse,
} from '../src/lib/screen-recording.js';

class FakeTrack implements MediaTrackLike {
  kind = 'video';
  stopped = false;
  onended: (() => void) | null = null;
  stop(): void {
    this.stopped = true;
  }
}

class FakeStream implements MediaStreamLike {
  tracks = [new FakeTrack(), new FakeTrack()];
  getTracks(): MediaTrackLike[] {
    return this.tracks;
  }
}

class FakeRecorder implements MediaRecorderLike {
  state: 'inactive' | 'recording' | 'paused' = 'inactive';
  ondataavailable: ((ev: { data: Blob }) => void) | null = null;
  onstop: (() => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  started: number[] = [];
  constructor(
    readonly stream: MediaStreamLike,
    readonly mimeType: string,
  ) {}
  start(timeslice?: number): void {
    this.state = 'recording';
    this.started.push(timeslice ?? 0);
  }
  stop(): void {
    this.state = 'inactive';
    // 真實 MediaRecorder:stop 後先吐最後一片再 onstop
    queueMicrotask(() => {
      this.emit(5);
      this.onstop?.();
    });
  }
  emit(size: number): void {
    this.ondataavailable?.({ data: new Blob(['x'.repeat(size)]) });
  }
}

interface Call {
  url: string;
  method: string;
  headers: Record<string, string>;
  bodyText?: string;
  bodySize: number;
}

function res(status: number, body: unknown = {}): UploadResponse {
  return { ok: status >= 200 && status < 300, status, json: async () => body };
}

interface Harness {
  deps: ScreenRecordingDeps;
  calls: Call[];
  delays: number[];
  recorders: FakeRecorder[];
  streams: FakeStream[];
  statuses: RecordingStatus[];
  /** 每次 fetch 的回應(依序;可丟例外模擬網路錯誤) */
  respond: (call: Call, index: number) => UploadResponse | Promise<UploadResponse>;
  displayMediaError?: unknown;
  displayMediaCalls: Array<{ video: Record<string, unknown>; audio: boolean }>;
}

function harness(): Harness {
  let t = Date.parse('2026-10-17T02:00:00Z');
  const h: Harness = {
    calls: [],
    delays: [],
    recorders: [],
    streams: [],
    statuses: [],
    displayMediaCalls: [],
    respond: (call) => {
      if (call.url.endsWith('/media/start')) return res(200, { mediaId: 'M1', file: 'M1.webm', maxBytes: 1000, record: 'r.jsonl' });
      if (call.url.includes('/media/chunk/')) return res(200, { seq: Number(call.headers['X-Seq']), bytes: 0 });
      if (call.url.includes('/media/stop/')) return res(200, { mediaId: 'M1', file: 'M1.webm', durationS: 61.5, sizeBytes: 65, chunks: 4, sha256: 'AB', truncated: false, stoppedUtc: '2026-10-17T02:01:01Z', record: { file: 'r.jsonl' } });
      return res(404, { error: 'unknown' });
    },
    deps: {
      fetch: async (url, init) => {
        const body = init.body;
        const call: Call = { url, method: init.method, headers: init.headers ?? {}, bodySize: body instanceof Blob ? body.size : typeof body === 'string' ? body.length : 0 };
        if (typeof body === 'string') call.bodyText = body;
        h.calls.push(call);
        return h.respond(call, h.calls.length - 1);
      },
      getDisplayMedia: async (c) => {
        h.displayMediaCalls.push(c);
        if (h.displayMediaError) throw h.displayMediaError;
        const s = new FakeStream();
        h.streams.push(s);
        return s;
      },
      createRecorder: (stream, mime) => {
        const r = new FakeRecorder(stream, mime);
        h.recorders.push(r);
        return r;
      },
      isTypeSupported: (m) => m === 'video/webm;codecs=vp9,opus',
      now: () => new Date((t += 1000)),
      delay: async (ms) => {
        h.delays.push(ms);
      },
    },
  };
  return h;
}

const chunkCalls = (h: Harness) => h.calls.filter((c) => c.url.includes('/media/chunk/'));

async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

describe('畫面錄影控制器', () => {
  it('start → 片段依序上傳(X-Seq 0,1,2,…)→ stop 帶長度並以核心旁檔做摘要', async () => {
    const h = harness();
    const c = new ScreenRecordingController('http://192.168.77.11:8768', h.deps);
    c.onStatus((s) => h.statuses.push(s));
    expect(c.status.state).toBe('idle');
    expect(await c.start({ exerciseId: 'E05@2026-10-17T02:00:00Z', station: 'instructor', withAudio: true })).toBe(true);
    expect(h.displayMediaCalls[0]).toEqual({ video: { frameRate: 15 }, audio: true });
    expect(h.calls[0]!.url).toBe('http://192.168.77.11:8768/media/start');
    expect(JSON.parse(h.calls[0]!.bodyText!)).toEqual({ exerciseId: 'E05@2026-10-17T02:00:00Z', station: 'instructor', mime: 'video/webm;codecs=vp9,opus' });
    expect(h.recorders[0]!.started).toEqual([2000]);
    expect(c.status).toMatchObject({ state: 'recording', mediaId: 'M1', uploadedBytes: 0, withAudio: true });
    expect(c.status.startedUtc).toBe('2026-10-17T02:00:01.000Z');

    const rec = h.recorders[0]!;
    rec.emit(10);
    rec.emit(20);
    rec.emit(0); // 空片段不佔序號
    rec.emit(30);
    await vi.waitFor(() => expect(c.status.chunks).toBe(3));
    expect(chunkCalls(h).map((x) => [x.headers['X-Seq'], x.bodySize, x.headers['Content-Type']])).toEqual([
      ['0', 10, 'application/octet-stream'],
      ['1', 20, 'application/octet-stream'],
      ['2', 30, 'application/octet-stream'],
    ]);
    expect(chunkCalls(h)[0]!.url).toBe('http://192.168.77.11:8768/media/chunk/M1');
    expect(c.status.uploadedBytes).toBe(60);
    expect(c.status.queuedBytes).toBe(0);

    const summary = await c.stop();
    expect(rec.state).toBe('inactive');
    expect(h.streams[0]!.tracks.every((t) => (t as FakeTrack).stopped)).toBe(true);
    // stop 前最後一片(5 bytes,seq 3)也送出
    expect(chunkCalls(h).map((x) => x.headers['X-Seq'])).toEqual(['0', '1', '2', '3']);
    const stopCall = h.calls.find((x) => x.url.endsWith('/media/stop/M1'))!;
    expect(stopCall.method).toBe('POST');
    const stopBody = JSON.parse(stopCall.bodyText!) as { durationS: number; reason: string };
    expect(stopBody.reason).toBe('client');
    expect(stopBody.durationS).toBeGreaterThan(0);
    expect(summary).toMatchObject({ mediaId: 'M1', file: 'M1.webm', station: 'instructor', durationS: 61.5, uploadedBytes: 65, chunks: 4, sha256: 'AB', truncated: false, record: 'r.jsonl', stoppedUtc: '2026-10-17T02:01:01Z' });
    expect(summary!.error).toBeUndefined();
    expect(c.status.state).toBe('idle');
    expect(c.summaries).toHaveLength(1);
    expect(h.statuses.map((s) => s.state)).toContain('stopping');
    expect(await c.stop()).toBeNull(); // 未在錄影
  });

  it('網路錯誤:指數退避重試、順序不變、重試次數計入狀態', async () => {
    const h = harness();
    let failures = 0;
    const base = h.respond;
    h.respond = (call, i) => {
      if (call.url.includes('/media/chunk/') && call.headers['X-Seq'] === '0' && failures < 2) {
        failures++;
        throw new TypeError('Failed to fetch');
      }
      return base(call, i);
    };
    const c = new ScreenRecordingController('http://h:8768', h.deps, { retryBaseMs: 500 });
    await c.start({ exerciseId: 'x', station: 'instructor' });
    const rec = h.recorders[0]!;
    rec.emit(10);
    rec.emit(20);
    await vi.waitFor(() => expect(c.status.chunks).toBe(2));
    expect(h.delays).toEqual([500, 1000]);
    expect(c.status.retries).toBe(2);
    expect(chunkCalls(h).map((x) => x.headers['X-Seq'])).toEqual(['0', '0', '0', '1']);
    expect(c.status.uploadedBytes).toBe(30);
    expect(c.status.error).toBeNull();
    await c.stop();
    expect(c.status.state).toBe('idle');
  });

  it('核心 413(超過 --media-max-mb):標 truncated、停止錄影並送 stop(reason=limit)', async () => {
    const h = harness();
    const base = h.respond;
    h.respond = (call, i) => {
      if (call.url.includes('/media/chunk/') && call.headers['X-Seq'] === '1') return res(413, { error: '超過上限', truncated: true, bytes: 10, maxBytes: 15 });
      if (call.url.includes('/media/stop/')) return res(200, { mediaId: 'M1', file: 'M1.webm', durationS: 4, sizeBytes: 10, chunks: 1, truncated: true });
      return base(call, i);
    };
    const c = new ScreenRecordingController('http://h:8768', h.deps);
    await c.start({ exerciseId: 'x', station: 'instructor' });
    const rec = h.recorders[0]!;
    rec.emit(10);
    rec.emit(10);
    rec.emit(10);
    await vi.waitFor(() => expect(c.status.state).toBe('idle'));
    expect(rec.state).toBe('inactive');
    expect(c.status.truncated).toBe(true);
    expect(chunkCalls(h).map((x) => x.headers['X-Seq'])).toEqual(['0', '1']); // 413 後不再送
    const stopCall = h.calls.find((x) => x.url.includes('/media/stop/'))!;
    expect(JSON.parse(stopCall.bodyText!).reason).toBe('limit');
    const s = c.summaries[0]!;
    expect(s.truncated).toBe(true);
    expect(s.uploadedBytes).toBe(10);
    expect(s.error).toBeUndefined();
  });

  it('離線緩衝超過上限:停止錄影並標錯誤(不丟片段)', async () => {
    const h = harness();
    const base = h.respond;
    h.respond = (call, i) => {
      if (call.url.includes('/media/chunk/')) throw new TypeError('offline');
      return base(call, i);
    };
    const c = new ScreenRecordingController('http://h:8768', h.deps, { maxBufferedBytes: 100, maxRetries: 1000 });
    await c.start({ exerciseId: 'x', station: 'instructor' });
    const rec = h.recorders[0]!;
    rec.emit(60);
    await settle();
    expect(c.status.queuedBytes).toBe(60);
    expect(c.status.state).toBe('recording');
    rec.emit(60);
    await vi.waitFor(() => expect(c.status.state).toBe('error'));
    expect(rec.state).toBe('inactive');
    expect(c.status.error).toContain('離線緩衝');
    expect(c.summaries[0]!.error).toContain('離線緩衝');
    expect(h.calls.some((x) => x.url.includes('/media/stop/'))).toBe(true); // 仍請核心定稿已收到的部分
    // 可再開始
    h.respond = base;
    expect(await c.start({ exerciseId: 'y', station: 'instructor' })).toBe(true);
    expect(c.status).toMatchObject({ state: 'recording', error: null, uploadedBytes: 0, queuedBytes: 0 });
  });

  it('上傳重試達上限、核心 409 / 404:致命錯誤', async () => {
    const h = harness();
    const base = h.respond;
    h.respond = (call, i) => (call.url.includes('/media/chunk/') ? res(500, { error: 'disk' }) : base(call, i));
    const c = new ScreenRecordingController('http://h:8768', h.deps, { maxRetries: 2, retryBaseMs: 100 });
    await c.start({ exerciseId: 'x', station: 'instructor' });
    h.recorders[0]!.emit(10);
    await vi.waitFor(() => expect(c.status.state).toBe('error'));
    expect(c.status.error).toMatch(/上傳失敗 3 次/);
    expect(h.delays.slice(0, 2)).toEqual([100, 200]); // 之後的 5000 為等 MediaRecorder onstop 的上限

    const h2 = harness();
    const base2 = h2.respond;
    h2.respond = (call, i) => (call.url.includes('/media/chunk/') ? res(409, { error: '序號不連續', expectedSeq: 5 }) : base2(call, i));
    const c2 = new ScreenRecordingController('http://h:8768', h2.deps);
    await c2.start({ exerciseId: 'x', station: 'instructor' });
    h2.recorders[0]!.emit(10);
    await vi.waitFor(() => expect(c2.status.state).toBe('error'));
    expect(c2.status.error).toContain('HTTP 409');
    expect(c2.summaries[0]!.error).toContain('序號不連續');
  });

  it('開始失敗:使用者拒絕分享畫面 / 核心拒絕 start(403)/ 無法連線', async () => {
    const h = harness();
    const c = new ScreenRecordingController('http://h:8768', h.deps);
    h.displayMediaError = { name: 'NotAllowedError', message: 'Permission denied' };
    expect(await c.start({ exerciseId: 'x', station: 'instructor' })).toBe(false);
    expect(c.status.state).toBe('error');
    expect(c.status.error).toContain('分享畫面');
    expect(c.summaries[0]!.error).toContain('分享畫面');
    expect(h.calls).toHaveLength(0);

    h.displayMediaError = undefined;
    const base = h.respond;
    h.respond = (call, i) => (call.url.endsWith('/media/start') ? res(403, { error: '只接受本機或直連網段' }) : base(call, i));
    expect(await c.start({ exerciseId: 'x', station: 'instructor' })).toBe(false);
    expect(c.status.error).toContain('HTTP 403');
    expect(c.status.error).toContain('只接受本機或直連網段');
    expect(h.streams[0]!.tracks.every((t) => (t as FakeTrack).stopped)).toBe(true);
    expect(h.recorders[0]!.started).toEqual([]);

    h.respond = (call, i) => {
      if (call.url.endsWith('/media/start')) throw new TypeError('Failed to fetch');
      return base(call, i);
    };
    expect(await c.start({ exerciseId: 'x', station: 'instructor' })).toBe(false);
    expect(c.status.error).toContain('無法連線錄影上傳端 http://h:8768');
    expect(c.summaries).toHaveLength(3);
  });

  it('使用者按瀏覽器「停止分享」:視同停止錄影', async () => {
    const h = harness();
    const c = new ScreenRecordingController('http://h:8768', h.deps);
    await c.start({ exerciseId: 'x', station: 'instructor-simple', withAudio: false });
    expect(h.displayMediaCalls[0]!.audio).toBe(false);
    expect(c.status.withAudio).toBe(false);
    (h.streams[0]!.tracks[0] as FakeTrack).onended?.();
    await vi.waitFor(() => expect(c.status.state).toBe('idle'));
    expect(c.summaries).toHaveLength(1);
    expect(JSON.parse(h.calls.find((x) => x.url.includes('/media/stop/'))!.bodyText!).reason).toBe('shareEnded');
    expect(c.summaries[0]!.station).toBe('instructor-simple');
  });

  it('工具:上傳端網址推導、設定存取、recordings 解析、長度格式、MIME 選擇', () => {
    expect(mediaBaseUrl('ws://192.168.77.11:8765')).toBe('http://192.168.77.11:8768');
    expect(mediaBaseUrl('ws://localhost:8765/')).toBe('http://localhost:8768');
    expect(mediaBaseUrl('wss://ship.local:8765')).toBe('https://ship.local:8768');
    expect(mediaBaseUrl('ws://192.168.77.11:8765', 'http://192.168.77.11:9000/')).toBe('http://192.168.77.11:9000');
    expect(mediaBaseUrl('not a url')).toBe('http://localhost:8768');

    const store = new Map<string, string>();
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) };
    expect(loadRecordingSettings(storage)).toEqual({ autoStart: false, withAudio: true });
    saveRecordingSettings({ autoStart: true, withAudio: false }, storage);
    expect(loadRecordingSettings(storage)).toEqual({ autoStart: true, withAudio: false });
    store.set('simosa-brm:instructor:recording', '{bad');
    expect(loadRecordingSettings(storage)).toEqual({ autoStart: false, withAudio: true });
    expect(loadRecordingSettings(null)).toEqual({ autoStart: false, withAudio: true });

    const parsed = parseRecordings([
      { mediaId: 'A', startedUtc: '2026-10-17T02:00:00Z', file: 'A.webm', durationS: 3661, uploadedBytes: 1048576, chunks: 3, truncated: false, record: 'r.jsonl', sha256: 'X' },
      { mediaId: 'B', startedUtc: '2026-10-17T03:00:00Z', error: '失敗' },
      { nope: 1 },
      'x',
    ]);
    expect(parsed).toHaveLength(2);
    expect(parsed[0]).toEqual({ mediaId: 'A', station: '', startedUtc: '2026-10-17T02:00:00Z', file: 'A.webm', durationS: 3661, uploadedBytes: 1048576, chunks: 3, truncated: false, record: 'r.jsonl', sha256: 'X' });
    expect(parsed[1]).toMatchObject({ mediaId: 'B', uploadedBytes: 0, chunks: 0, truncated: false, error: '失敗' });
    expect(parseRecordings(undefined)).toEqual([]);

    expect(formatRecordingDuration(3661)).toBe('1 h 01 min');
    expect(formatRecordingDuration(65)).toBe('1 min 05 s');
    expect(formatRecordingDuration(undefined)).toBe('—');
    expect(pickMime((m) => m === 'video/webm')).toBe('video/webm');
    expect(pickMime(() => false)).toBe('');
    expect(
      pickMime(() => {
        throw new Error('bad');
      }),
    ).toBe('');
  });
});
