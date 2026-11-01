// 畫面錄影面板(components/instructor/recording-panel.ts;happy-dom):按鈕依狀態啟用/停用並發 recording-start / recording-stop、
// 顯示錄影中與已上傳 MB / 緩衝 / 重試 / 錯誤 / 截斷、設定核取方塊發 recording-settings-changed、瀏覽器不支援時的提示、已完成錄影清單。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { RecordingPanel } from '../src/components/instructor/recording-panel.js';
import { idleRecordingStatus, type RecordingSettings } from '../src/lib/screen-recording.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };

async function mount(props: Record<string, unknown> = {}): Promise<RecordingPanel & Updatable> {
  const el = document.createElement('brm-recording-panel') as RecordingPanel & Updatable;
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

const q = <T extends Element>(el: HTMLElement, sel: string): T => {
  const found = el.shadowRoot!.querySelector<T>(sel);
  if (!found) throw new Error(`找不到 ${sel}`);
  return found;
};

beforeAll(async () => {
  await import('../src/components/instructor/recording-panel.js');
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('畫面錄影面板', () => {
  it('未錄影:開始可按並發 recording-start;錄影中:停止可按並發 recording-stop,顯示已上傳/緩衝/重試', async () => {
    const events: string[] = [];
    document.body.addEventListener('recording-start', () => events.push('start'));
    document.body.addEventListener('recording-stop', () => events.push('stop'));
    const el = await mount({ baseUrl: 'http://192.168.77.11:8768' });
    expect(q<HTMLButtonElement>(el, 'button.start').disabled).toBe(false);
    expect(q<HTMLButtonElement>(el, 'button.stop').disabled).toBe(true);
    expect(el.shadowRoot!.textContent).toContain('未錄影');
    expect(el.shadowRoot!.textContent).toContain('http://192.168.77.11:8768');
    q<HTMLButtonElement>(el, 'button.start').click();
    expect(events).toEqual(['start']);

    el.status = { ...idleRecordingStatus(), state: 'recording', mediaId: 'M1', startedUtc: new Date(Date.now() - 65_500).toISOString(), uploadedBytes: 12.3 * 1048576, queuedBytes: 2 * 1048576, chunks: 7, retries: 2 };
    await el.updateComplete;
    expect(q<HTMLButtonElement>(el, 'button.start').disabled).toBe(true);
    expect(q<HTMLButtonElement>(el, 'button.stop').disabled).toBe(false);
    const text = el.shadowRoot!.textContent!;
    expect(text).toContain('錄影中');
    expect(text).toContain('已上傳 12.3 MB');
    expect(text).toContain('緩衝 2.0 MB');
    expect(text).toContain('重試 2');
    expect(text).toContain('含系統音訊');
    expect(q(el, '.elapsed').textContent).toBe('1 min 05 s');
    q<HTMLButtonElement>(el, 'button.stop').click();
    expect(events).toEqual(['start', 'stop']);

    el.status = { ...idleRecordingStatus(), state: 'stopping', mediaId: 'M1', startedUtc: new Date().toISOString(), uploadedBytes: 1, chunks: 1 };
    await el.updateComplete;
    expect(q<HTMLButtonElement>(el, 'button.start').disabled).toBe(true);
    expect(q<HTMLButtonElement>(el, 'button.stop').disabled).toBe(true);
    expect(el.shadowRoot!.textContent).toContain('停止中');
  });

  it('錯誤與截斷訊息;設定核取方塊發 recording-settings-changed;已完成錄影清單', async () => {
    const settings: RecordingSettings[] = [];
    document.body.addEventListener('recording-settings-changed', (ev) => settings.push((ev as CustomEvent<RecordingSettings>).detail));
    const el = await mount({
      status: { ...idleRecordingStatus(), state: 'error', error: '核心拒絕片段 3(HTTP 409)', truncated: true },
      summaries: [
        { mediaId: 'A', file: 'A.webm', station: 'instructor', startedUtc: '2026-10-17T02:00:00Z', durationS: 3600, uploadedBytes: 500 * 1048576, chunks: 1800, truncated: false },
        { mediaId: 'B', station: 'instructor', startedUtc: '2026-10-17T03:00:00Z', uploadedBytes: 0, chunks: 0, truncated: false, error: '取消' },
        { mediaId: 'C', file: 'C.webm', station: 'instructor', startedUtc: '2026-10-17T04:00:00Z', durationS: 30, uploadedBytes: 1048576, chunks: 15, truncated: true },
      ],
    });
    const text = el.shadowRoot!.textContent!;
    expect(text).toContain('核心拒絕片段 3(HTTP 409)');
    expect(text).toContain('已達核心錄影上限');
    const items = Array.from(el.shadowRoot!.querySelectorAll('.list .item'));
    expect(items).toHaveLength(2); // 沒有檔名(未成功開始)的不列
    expect(items[0]!.textContent).toContain('A.webm');
    expect(items[0]!.textContent).toContain('1 h 00 min');
    expect(items[0]!.textContent).toContain('500 MB');
    expect(items[1]!.textContent).toContain('截斷');
    expect(q<HTMLButtonElement>(el, 'button.start').disabled).toBe(false); // 錯誤後可再開始

    const auto = q<HTMLInputElement>(el, 'input.auto-start');
    expect(auto.checked).toBe(false);
    auto.checked = true;
    auto.dispatchEvent(new Event('change', { bubbles: true }));
    const audio = q<HTMLInputElement>(el, 'input.with-audio');
    audio.checked = false;
    audio.dispatchEvent(new Event('change', { bubbles: true }));
    expect(settings).toEqual([
      { autoStart: true, withAudio: true },
      { autoStart: true, withAudio: false },
    ]);
  });

  it('瀏覽器不支援:開始停用並顯示提示;compact 隱藏清單與設定', async () => {
    const el = await mount({ supported: false, compact: true, summaries: [{ mediaId: 'A', file: 'A.webm', station: 'instructor', startedUtc: '2026-10-17T02:00:00Z', uploadedBytes: 1, chunks: 1, truncated: false }] });
    expect(q<HTMLButtonElement>(el, 'button.start').disabled).toBe(true);
    expect(el.shadowRoot!.textContent).toContain('不支援畫面擷取');
    expect(el.hasAttribute('compact')).toBe(true);
  });
});
