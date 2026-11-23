// 教官站頁首的發行版本與降版提醒(lib/release-status.ts、components/instructor/release-status.ts;happy-dom)。
// 三種情況:取不到狀態(開發模式:simosa-web 回 200 與各欄 null;其他伺服器的 404、不是 JSON、格式不符)→ 什麼都不顯示;正常 → 小字「發行版 <版本>」
// (套用過比發行版新的軟體更新包時改寫「軟體更新 <版次>」:實際執行的是更新包的程式,發行版在 title);
// 降版(simosa-pkg status 的 releaseDowngrade)→ 警示徽章,按一下展開重點、處理方式與 status 的完整訊息,可收合(收合鈕、Esc、點說明以外的地方)。
// 另:教官站完整 / 簡化模式的頁首有這個元件,重播檢視與駕駛台頁沒有;版本比較與 Tools.Packaging 的 PackageVersion 相同規則。
// 降版夾具 tests/fixtures/release-status-downgrade.json 取自真實的 simosa-pkg status --json(2026.12.1 套用軟體更新包 2026.12.2 後以 2026.11.1 重新安裝)。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import {
  RELEASE_STATUS_URL,
  browserReleaseStatus,
  compareVersions,
  downgradeSummary,
  fetchReleaseStatus,
  formatStatusTime,
  isVersion,
  parseReleaseStatus,
  releaseStatusProvider,
  type ReleaseStatus,
  type StatusFetch,
} from '../src/lib/release-status.js';
import type { ReleaseStatusBadge } from '../src/components/instructor/release-status.js';

const downgradeJson = JSON.parse(readFileSync(path.join(import.meta.dirname, 'fixtures', 'release-status-downgrade.json'), 'utf8')) as Record<string, unknown>;
const normalJson = { format: 'simosa-release-status/1', generatedUtc: '2026-11-23T04:00:00Z', releaseVersion: '2026.11.30', softwareVersion: null, releaseDowngrade: null };
/** 套用過軟體更新包(沒有重新安裝):真實 simosa-web 對「發行版 2026.12.1 + 軟體更新包 2026.12.2」的輸出 */
const updatedJson = { format: 'simosa-release-status/1', generatedUtc: '2026-11-23T05:31:00Z', releaseVersion: '2026.12.1', softwareVersion: '2026.12.2', releaseDowngrade: null };
/** simosa-web 在狀態檔不存在或不合格時的輸出(「沒有發行版狀態」) */
const emptyJson = { format: 'simosa-release-status/1', generatedUtc: null, releaseVersion: null, softwareVersion: null, releaseDowngrade: null };
const NORMAL: ReleaseStatus = { releaseVersion: '2026.11.30', softwareUpdateVersion: null, generatedUtc: '2026-11-23T04:00:00Z', downgrade: null };

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };
const originalLoad = releaseStatusProvider.load;

async function mount(load: () => Promise<ReleaseStatus | null>): Promise<ReleaseStatusBadge & Updatable> {
  releaseStatusProvider.load = load;
  const el = document.createElement('brm-release-status') as ReleaseStatusBadge & Updatable;
  document.body.appendChild(el);
  await Promise.resolve(); // provider 的 Promise
  await Promise.resolve();
  await el.updateComplete;
  return el;
}

beforeAll(async () => {
  await import('../src/components/instructor/release-status.js');
});

afterEach(() => {
  releaseStatusProvider.load = originalLoad;
  document.body.innerHTML = '';
});

describe('發行版狀態:解析與版本比較', () => {
  it('版本:1 至 4 段數字;逐段比較,缺的段當 0', () => {
    for (const v of ['2026', '2026.11', '2026.11.30', '2026.11.30.1']) expect(isVersion(v)).toBe(true);
    for (const v of ['', '2026.', '.11', '2026.11.30.1.2', 'v2026.11', '2026.11-rc1', '2026.11.30 ', null, 20261130, '１.２']) expect(isVersion(v)).toBe(false);
    expect(compareVersions('2026.12.2', '2026.11.30')).toBeGreaterThan(0);
    expect(compareVersions('2026.11.9', '2026.11.10')).toBeLessThan(0); // 逐段比數值,不是字串
    expect(compareVersions('2026.11', '2026.11.0')).toBe(0);
    expect(compareVersions('2026.11.0.1', '2026.11')).toBeGreaterThan(0);
  });

  it('正常:只有發行版本;降版:帶 releaseDowngrade(真實 status 的輸出)', () => {
    expect(parseReleaseStatus(normalJson)).toEqual(NORMAL);
    // 舊格式(沒有 softwareVersion 欄)照樣讀
    expect(parseReleaseStatus({ format: 'simosa-release-status/1', generatedUtc: '2026-11-23T04:00:00Z', releaseVersion: '2026.11.30', releaseDowngrade: null })).toEqual(NORMAL);
    const st = parseReleaseStatus(downgradeJson)!;
    expect(st.releaseVersion).toBe('2026.11.1');
    // 降版時 status 仍顯示的軟體版次不是實際執行的程式:不當成「軟體更新」(由降版提醒說明)
    expect(st.softwareUpdateVersion).toBeNull();
    expect(parseReleaseStatus({ ...downgradeJson, softwareVersion: '2026.12.2' })!.softwareUpdateVersion).toBeNull();
    expect(st.downgrade).toMatchObject({ releaseVersion: '2026.11.1', basisVersion: '2026.12.2', previousReleaseVersion: '2026.12.1', softwareVersion: '2026.12.2', highestVersion: '2026.12.2', softwareStale: true });
    expect(st.downgrade!.message).toContain('simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式');
  });

  it('軟體更新包版次:沒有降版而且比發行版新才算(實際執行的是它);相同、較舊、不是版本、沒有 → null', () => {
    expect(parseReleaseStatus(updatedJson)).toEqual({ releaseVersion: '2026.12.1', softwareUpdateVersion: '2026.12.2', generatedUtc: '2026-11-23T05:31:00Z', downgrade: null });
    const sw = (softwareVersion: unknown) => parseReleaseStatus({ ...updatedJson, softwareVersion })!.softwareUpdateVersion;
    expect(sw('2026.12.1.1')).toBe('2026.12.1.1');
    expect(sw('2027.1')).toBe('2027.1');
    // 之後以更新(或相同版本)的安裝程式重新安裝過:installed.json 還記著舊的更新包,但它已被安裝覆蓋
    expect(sw('2026.12.1')).toBeNull();
    expect(sw('2026.12')).toBeNull();
    expect(sw('2026.11.30')).toBeNull();
    for (const bad of [null, undefined, '', 'v2026.12.2', '2026.12.2-rc1', 20261202, { version: '2026.12.2' }]) expect(sw(bad)).toBeNull();
  });

  it('格式不符、沒有發行版本、不是物件 → null(畫面不顯示)', () => {
    // simosa-web 在狀態檔不存在(開發模式)或不合格時回 200 與各欄 null:當成沒有
    expect(parseReleaseStatus(emptyJson)).toBeNull();
    expect(parseReleaseStatus({ ...emptyJson, softwareVersion: '2026.12.2' })).toBeNull();
    for (const bad of [null, undefined, 'x', 42, [], {}, { format: 'other/1', releaseVersion: '2026.11.30' }, { format: 'simosa-release-status/1' }, { format: 'simosa-release-status/1', releaseVersion: null, releaseDowngrade: null }, { format: 'simosa-release-status/1', releaseVersion: '<script>' }]) {
      expect(parseReleaseStatus(bad)).toBeNull();
    }
    // simosa-pkg status 的完整輸出(沒有經過 simosa-web 縮成網頁用的格式)不接受
    expect(parseReleaseStatus({ type: 'statusResult', releaseVersion: '2026.11.30' })).toBeNull();
  });

  it('降版資訊不完整或比較基準不比目前的發行版新 → 當成沒有降版;產生時間格式不對 → null', () => {
    const base = { format: 'simosa-release-status/1', generatedUtc: 'not-a-time', releaseVersion: '2026.11.30' };
    expect(parseReleaseStatus({ ...base, releaseDowngrade: { releaseVersion: '2026.11.30' } })).toEqual({ releaseVersion: '2026.11.30', softwareUpdateVersion: null, generatedUtc: null, downgrade: null });
    expect(parseReleaseStatus({ ...base, releaseDowngrade: { releaseVersion: '2026.11.30', basisVersion: '2026.11.30' } })!.downgrade).toBeNull();
    expect(parseReleaseStatus({ ...base, releaseDowngrade: { releaseVersion: '2026.11.30', basisVersion: 'x' } })!.downgrade).toBeNull();
    // 欄位型別不對的個別項目以 null / false / 空字串帶過,不整個丟掉
    const st = parseReleaseStatus({ ...base, releaseDowngrade: { releaseVersion: '2026.11.30', basisVersion: '2026.12.1', previousReleaseVersion: 7, softwareVersion: '2026.12.1', softwareStale: 'yes', message: 5 } })!;
    expect(st.downgrade).toEqual({ releaseVersion: '2026.11.30', basisVersion: '2026.12.1', previousReleaseVersion: null, softwareVersion: '2026.12.1', highestVersion: null, softwareStale: false, message: '' });
    // 最上層沒有 releaseVersion 但降版資訊有:用降版資訊的
    expect(parseReleaseStatus({ format: 'simosa-release-status/1', releaseDowngrade: { releaseVersion: '2026.11.1', basisVersion: '2026.12.2' } })!.releaseVersion).toBe('2026.11.1');
  });

  it('降版重點:只列比目前的發行版新的項目;status 的軟體版次不是實際執行的程式;建議以不低於比較基準的安裝程式重新安裝', () => {
    const sum = downgradeSummary(parseReleaseStatus(downgradeJson)!.downgrade!);
    expect(sum.running).toBe('這台電腦目前執行的是較舊的發行版 2026.11.1');
    expect(sum.newer).toBe('先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 比它新');
    expect(sum.stale).toBe('simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式');
    expect(sum.advice).toContain('版本不低於 2026.12.2 的安裝程式重新安裝');
    expect(sum.advice).toContain('請訪船船長確認');
    // 先前的發行版比目前的舊(不列)、被覆蓋的最高版本另外列出;沒有 softwareStale 時不說 status 的版次
    const other = downgradeSummary({ releaseVersion: '2026.11.5', basisVersion: '2026.12.3', previousReleaseVersion: '2026.11.1', softwareVersion: '2026.12.2', highestVersion: '2026.12.3', softwareStale: false, message: '' });
    expect(other.newer).toBe('曾套用的軟體更新包 2026.12.2、被安裝覆蓋的軟體更新包最高版本 2026.12.3 比它新');
    expect(other.stale).toBeNull();
    // 三項都沒有可列的(舊版 simosa-pkg 的輸出只有比較基準)
    expect(downgradeSummary({ releaseVersion: '2026.11.5', basisVersion: '2026.12.3', previousReleaseVersion: null, softwareVersion: null, highestVersion: null, softwareStale: false, message: '' }).newer).toBe('曾安裝或套用過較新的版本 2026.12.3');
  });

  it('時間:ISO UTC → 本地時間 YYYY-MM-DD HH:MM;沒有或無法解析時空字串', () => {
    const d = new Date('2026-11-23T04:05:20Z');
    const p = (n: number) => n.toString().padStart(2, '0');
    expect(formatStatusTime('2026-11-23T04:05:20Z')).toBe(`${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`);
    expect(formatStatusTime(null)).toBe('');
    expect(formatStatusTime('x')).toBe('');
  });
});

describe('發行版狀態:取得(同源的 /_status/release.json)', () => {
  const calls: Array<{ url: string; cache: string; credentials: string }> = [];
  const fake = (res: { ok: boolean; body?: unknown; throwOnJson?: boolean } | Error): StatusFetch => {
    calls.length = 0;
    return async (url, init) => {
      calls.push({ url, cache: init.cache, credentials: init.credentials });
      if (res instanceof Error) throw res;
      return {
        ok: res.ok,
        json: async () => {
          if (res.throwOnJson) throw new SyntaxError('Unexpected token <');
          return res.body;
        },
      };
    };
  };

  it('200 且格式正確 → 狀態;固定網址、不快取、不帶認證', async () => {
    expect(await fetchReleaseStatus(fake({ ok: true, body: normalJson }))).toEqual(NORMAL);
    expect(calls).toEqual([{ url: RELEASE_STATUS_URL, cache: 'no-store', credentials: 'omit' }]);
    expect(RELEASE_STATUS_URL).toBe('/_status/release.json');
  });

  it('simosa-web 的「沒有發行版狀態」(200、各欄 null)、404、連不上、不是 JSON(開發伺服器回 HTML)、格式不符 → null,不丟例外', async () => {
    expect(await fetchReleaseStatus(fake({ ok: true, body: emptyJson }))).toBeNull();
    expect(await fetchReleaseStatus(fake({ ok: false }))).toBeNull();
    expect(await fetchReleaseStatus(fake({ ok: false, body: normalJson })), '非 2xx 的回應即使本體是合格的 JSON 也不採用').toBeNull();
    expect(await fetchReleaseStatus(fake(new TypeError('Failed to fetch')))).toBeNull();
    expect(await fetchReleaseStatus(fake({ ok: true, throwOnJson: true }))).toBeNull();
    expect(await fetchReleaseStatus(fake({ ok: true, body: { hello: 'world' } }))).toBeNull();
  });

  it('預設的 provider 不發請求(單元測試掛上教官站元件時不會真的連線);教官站進入點啟用後才向同源的固定網址取', async () => {
    const urls: string[] = [];
    vi.stubGlobal('fetch', async (url: string) => {
      urls.push(url);
      return { ok: true, json: async () => normalJson };
    });
    try {
      expect(await originalLoad()).toBeNull();
      expect(urls).toEqual([]);
      // instructor-main.ts 匯入 lib/release-status-enable.ts → provider 換成瀏覽器實作(vitest 的 happy-dom 頁面是 http://localhost:3000/)
      await import('../src/lib/release-status-enable.js');
      expect(releaseStatusProvider.load).toBe(browserReleaseStatus);
      expect(location.protocol).toBe('http:');
      expect(await releaseStatusProvider.load()).toEqual(NORMAL);
      expect(urls).toEqual(['/_status/release.json']);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('教官站的進入點在 instructor-app 之前匯入 release-status-enable;駕駛台的進入點不匯入', () => {
    const src = (f: string) => readFileSync(path.join(import.meta.dirname, '..', 'src', f), 'utf8');
    const main = src('instructor-main.ts');
    const enable = main.indexOf("import './lib/release-status-enable.js'");
    expect(enable).toBeGreaterThan(-1);
    expect(enable).toBeLessThan(main.indexOf("import './instructor-app.js'"));
    expect(src('main.ts')).not.toContain('release-status');
  });
});

describe('<brm-release-status>', () => {
  it('取不到狀態(沒有檔案:simosa-web 回各欄 null;或 404):什麼都不顯示', async () => {
    const el = await mount(async () => null);
    expect(el.shadowRoot!.querySelector('.version, button, .detail')).toBeNull();
    expect(el.shadowRoot!.textContent!.trim()).toBe('');
    const empty = await mount(async () => parseReleaseStatus(emptyJson));
    expect(empty.shadowRoot!.textContent!.trim()).toBe('');
    // provider 丟例外也一樣
    const el2 = await mount(() => Promise.reject(new Error('boom')));
    expect(el2.shadowRoot!.textContent!.trim()).toBe('');
  });

  it('正常:小字「發行版 <版本>」,不是按鈕、沒有提醒;來源與取得時間在 title', async () => {
    const el = await mount(async () => parseReleaseStatus(normalJson));
    const label = el.shadowRoot!.querySelector<HTMLElement>('.version')!;
    expect(label.textContent).toBe('發行版 2026.11.30');
    expect(label.title).toContain('這是提供本頁面的電腦');
    expect(label.title).toContain(`啟動時取得(${formatStatusTime('2026-11-23T04:00:00Z')})`);
    expect(label.title).toContain('發行版是安裝程式的版本');
    expect(label.classList.contains('update')).toBe(false);
    expect(el.shadowRoot!.querySelector('button')).toBeNull();
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull();
    expect(el.shadowRoot!.textContent).not.toContain('降版');
    expect(el.shadowRoot!.textContent).not.toContain('軟體更新');
  });

  it('套用過軟體更新包(比發行版新、沒有降版):頁首寫實際執行的那一版「軟體更新 <版次>」,發行版在 title;不是按鈕、沒有提醒', async () => {
    // 審查 BC-rBC-4:只寫發行版的話,船上靠更新包升級之後頁首一直停在安裝程式的版本,看不出實際執行的軟體版次。
    // 不把兩個版本並排(頁首會多折一行,簡化模式簡報頁的提示被擠出簡報框):寫實際執行的那一版,寬度與「發行版 <版本>」相近
    const el = await mount(async () => parseReleaseStatus(updatedJson));
    const labels = el.shadowRoot!.querySelectorAll<HTMLElement>('.version');
    expect(labels).toHaveLength(1);
    const update = labels[0]!;
    expect(update.textContent).toBe('軟體更新 2026.12.2');
    expect(update.classList.contains('update')).toBe(true);
    expect(update.dataset['release']).toBe('2026.12.1');
    expect(update.title).toContain('實際執行的是軟體更新包 2026.12.2 的程式(套用在發行版 2026.12.1 之上');
    expect(update.title).toContain('這是提供本頁面的電腦');
    expect(el.shadowRoot!.textContent).not.toContain('發行版');
    expect(el.shadowRoot!.querySelector('button')).toBeNull();
    expect(el.shadowRoot!.textContent).not.toContain('降版');
    // 降版時不寫「軟體更新」(那個版次不是實際執行的程式,由降版提醒說明)
    const down = await mount(async () => parseReleaseStatus({ ...downgradeJson, softwareVersion: '2026.12.2' }));
    expect(down.shadowRoot!.querySelector('.update')).toBeNull();
    expect(down.shadowRoot!.querySelector('button.downgrade')!.textContent).toBe('發行版 2026.11.1 · 降版提醒');
  });

  it('降版:警示徽章一直顯示;按一下展開重點、處理方式與 status 的完整訊息,再按「收合」收起', async () => {
    const el = await mount(async () => parseReleaseStatus(downgradeJson));
    const badge = el.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!;
    expect(badge.textContent).toBe('發行版 2026.11.1 · 降版提醒');
    expect(badge.getAttribute('aria-expanded')).toBe('false');
    expect(badge.title).toContain('這台電腦目前執行的是較舊的發行版 2026.11.1');
    expect(badge.title).toContain('曾套用的軟體更新包 2026.12.2');
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull(); // 預設收合,不佔版面
    expect(el.shadowRoot!.querySelector('.version')).toBeNull();

    badge.click();
    await el.updateComplete;
    expect(badge.getAttribute('aria-expanded')).toBe('true');
    const detail = el.shadowRoot!.querySelector<HTMLElement>('.detail')!;
    expect(detail.getAttribute('role')).toBe('region');
    const lead = detail.querySelector('.lead')!.textContent!;
    expect(lead).toContain('降版提醒:這台電腦目前執行的是較舊的發行版 2026.11.1;先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 比它新。');
    expect(lead).toContain('simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式。');
    expect(detail.querySelector('.advice')!.textContent).toContain('請以版本不低於 2026.12.2 的安裝程式重新安裝,或請岸上派送版本更高的軟體更新包');
    // 全文:status 的訊息原文
    expect(detail.querySelector('.full')!.textContent).toContain((downgradeJson['releaseDowngrade'] as { message: string }).message);
    expect(detail.querySelector('.foot .source')!.textContent).toContain('套用更新包或重新安裝後,重新啟動才會更新');

    detail.querySelector<HTMLButtonElement>('button.collapse')!.click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull();
    expect(el.shadowRoot!.querySelector('button.downgrade')!.getAttribute('aria-expanded')).toBe('false');
    // 再按徽章也能收合;展開時按 Esc 收合(收合後的 Esc 不再處理)
    badge.click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).not.toBeNull();
    badge.click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull();
    badge.click();
    await el.updateComplete;
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'a' }));
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).not.toBeNull();
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull();
    expect(badge.getAttribute('aria-expanded')).toBe('false');
  });

  it('展開後點說明以外的地方就收合(那一下點按照常作用在被點的控制項上);點說明裡面或徽章不算', async () => {
    // 審查 BC-rBC-6:展開的說明蓋住頁首下方的控制項(簡化模式執行階段的 BRM 標記鈕、航跡圖的鈕),點別處要能收起
    const el = await mount(async () => parseReleaseStatus(downgradeJson));
    const other = document.createElement('button');
    let clicks = 0;
    let prevented = false;
    other.addEventListener('pointerdown', (ev) => {
      ev.stopPropagation(); // 被點的元件自己停止傳遞也要收得到(本元件在擷取階段聽)
      clicks++;
      prevented = ev.defaultPrevented;
    });
    document.body.appendChild(other);
    const badge = el.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!;
    const pointer = (target: Element) => target.dispatchEvent(new Event('pointerdown', { bubbles: true, composed: true }));
    // 收合時點別處:沒有反應(沒有監聽)
    pointer(other);
    expect(clicks).toBe(1);
    badge.click();
    await el.updateComplete;
    const detail = el.shadowRoot!.querySelector<HTMLElement>('.detail')!;
    // 點說明裡面(文字、收合鈕以外的地方)與徽章本身:不因為 pointerdown 收合
    pointer(detail.querySelector('.lead')!);
    pointer(badge);
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).not.toBeNull();
    // 點別處:收合,而且那一下照常送到被點的控制項(不攔截)
    pointer(other);
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull();
    expect(badge.getAttribute('aria-expanded')).toBe('false');
    expect(clicks).toBe(2);
    expect(prevented).toBe(false);
    // 收合後不再監聽;元件移除時展開中的監聽也拿掉
    badge.click();
    await el.updateComplete;
    el.remove();
    pointer(other);
    expect(clicks).toBe(3);
    expect((el as unknown as { open: boolean }).open).toBe(false);
  });

  it('status 的訊息是文字,不當成 HTML(訊息內的標記原樣顯示)', async () => {
    const st = parseReleaseStatus({ ...downgradeJson, releaseDowngrade: { ...(downgradeJson['releaseDowngrade'] as object), message: '<img src=x onerror=alert(1)>降版' } });
    const el = await mount(async () => st);
    el.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!.click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail img')).toBeNull();
    expect(el.shadowRoot!.querySelector('.detail .full')!.textContent).toContain('<img src=x onerror=alert(1)>降版');
  });
});

describe('發行版狀態在哪些頁面', () => {
  type App = HTMLElement & { updateComplete: Promise<unknown> };
  async function mountPage(tag: string, query: string): Promise<App> {
    releaseStatusProvider.load = async () => parseReleaseStatus(downgradeJson);
    history.replaceState(null, '', query); // 示範模式:不開 WebSocket
    const el = document.createElement(tag) as App;
    document.body.appendChild(el);
    await el.updateComplete;
    await Promise.resolve();
    await Promise.resolve();
    await el.updateComplete;
    return el;
  }

  // 教官站與駕駛台的整個元件樹在這裡匯入(hook 的逾時另計、明確給 60 s):放在 it 裡的話匯入的時間算在該案例預設 5 s 的逾時內,
  // 機器忙碌(CI 執行器、其他測試同時在跑)時匯入就要 5–7 s,案例會以「Test timed out in 5000ms」失敗(審查 BC-rBC-3)
  beforeAll(async () => {
    await import('../src/instructor-app.js');
    await import('../src/app.js');
  }, 60_000);

  it('教官站完整模式與簡化模式的頁首有;重播檢視沒有', async () => {
    for (const query of ['/?demo=1', '/?demo=1&mode=simple']) {
      const app = await mountPage('brm-instructor-app', query);
      const badge = app.shadowRoot!.querySelector<ReleaseStatusBadge & Updatable>('header brm-release-status');
      expect(badge, query).not.toBeNull();
      await badge!.updateComplete;
      expect(badge!.shadowRoot!.querySelector('button.downgrade')!.textContent).toBe('發行版 2026.11.1 · 降版提醒');
      document.body.innerHTML = '';
    }
    const replay = await mountPage('brm-instructor-app', '/?mode=replay');
    expect(replay.shadowRoot!.querySelector('brm-release-status')).toBeNull();
  }, 30_000);

  it('駕駛台頁沒有(學員不需要),也不取狀態', async () => {
    let loads = 0;
    const app = await mountPage('brm-app', '/?demo=1');
    releaseStatusProvider.load = async () => {
      loads++;
      return null;
    };
    await app.updateComplete;
    expect(app.shadowRoot!.querySelector('brm-release-status')).toBeNull();
    expect(app.shadowRoot!.textContent).not.toContain('發行版');
    expect(loads).toBe(0);
  }, 30_000);
});
