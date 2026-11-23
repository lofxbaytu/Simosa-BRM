// 教官站頁首的發行版本與降版提醒(lib/release-status.ts、components/instructor/release-status.ts;happy-dom)。
// 三種情況:取不到狀態(開發模式:simosa-web 回 200 與各欄 null;其他伺服器的 404、不是 JSON、格式不符)→ 什麼都不顯示;正常 → 小字「發行版 <版本>」
// (套用過比發行版新的軟體更新包時改寫「軟體更新 <版次>」:實際執行的是更新包的程式,發行版在 title);
// 降版(simosa-pkg status 的 releaseDowngrade)→ 警示徽章,按一下展開重點、處理方式與 status 的完整訊息,可收合(收合鈕、Esc、點說明以外的地方)。
// 另:教官站完整 / 簡化模式的頁首有這個元件,重播檢視與駕駛台頁沒有;版本比較與 Tools.Packaging 的 PackageVersion 相同規則。
// 降版夾具 tests/fixtures/release-status-downgrade.json 取自真實的 simosa-pkg status --json(2026.12.1 套用軟體更新包 2026.12.2 後以 2026.11.1 重新安裝)。
// 第三十一輪:教官筆電的教官站(頁面由本機的 simosa-web 提供、核心在船上筆電)另外跨來源取船上筆電的狀態 —— 船上網頁位址怎麼得到
// (?shipWeb= 或由 ?ws= 的主機推得;同一台電腦時不取)、兩台版本的比較、頁首「船上 <版本>」與 船上降版 / 本機降版 / 兩台降版 / 版本不同 的徽章、
// 取不到船上的狀態時畫面與只有本機時相同(不顯示錯誤)、連上核心才取與重試。
// 審查後修正:?shipWeb= 只寫埠號、船上「沒有發行版狀態」與「取不到」分開(不重試、說明不同)、重試涵蓋船上啟動腳本最慢的情形、較早的請求晚回來要丟棄、
// 較舊的那一台有降版提醒時「版本不同」的處理指向降版提醒、各台 status 的完整訊息預設收合、說明的最大高度依 detailLimit 縮小。
// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import {
  RELEASE_STATUS_URL,
  SHIP_NO_STATUS,
  browserReleaseStatus,
  browserShipReleaseStatus,
  compareFleet,
  compareVersions,
  downgradeSummary,
  fetchReleaseStatus,
  fetchShipReleaseStatus,
  formatStatusTime,
  isLoopbackHost,
  isVersion,
  mismatchSummary,
  parseReleaseStatus,
  releaseStatusProvider,
  runningDescription,
  runningLabel,
  runningVersion,
  shipReleaseStatusUrl,
  shipWebBaseUrl,
  type ReleaseStatus,
  type ShipStatusResult,
  type StatusFetch,
} from '../src/lib/release-status.js';
import { DETAIL_LIMIT_GAP_PX, DETAIL_MAX_HEIGHT_PX, DETAIL_MIN_HEIGHT_PX, SHIP_RETRY_DELAYS_MS, type ReleaseStatusBadge } from '../src/components/instructor/release-status.js';

const downgradeJson = JSON.parse(readFileSync(path.join(import.meta.dirname, 'fixtures', 'release-status-downgrade.json'), 'utf8')) as Record<string, unknown>;
const normalJson = { format: 'simosa-release-status/1', generatedUtc: '2026-11-23T04:00:00Z', releaseVersion: '2026.11.30', softwareVersion: null, releaseDowngrade: null };
/** 套用過軟體更新包(沒有重新安裝):真實 simosa-web 對「發行版 2026.12.1 + 軟體更新包 2026.12.2」的輸出 */
const updatedJson = { format: 'simosa-release-status/1', generatedUtc: '2026-11-23T05:31:00Z', releaseVersion: '2026.12.1', softwareVersion: '2026.12.2', releaseDowngrade: null };
/** simosa-web 在狀態檔不存在或不合格時的輸出(「沒有發行版狀態」) */
const emptyJson = { format: 'simosa-release-status/1', generatedUtc: null, releaseVersion: null, softwareVersion: null, releaseDowngrade: null };
const NORMAL: ReleaseStatus = { releaseVersion: '2026.11.30', softwareUpdateVersion: null, generatedUtc: '2026-11-23T04:00:00Z', downgrade: null };

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };
const originalLoad = releaseStatusProvider.load;
const originalLoadShip = releaseStatusProvider.loadShip;

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
  releaseStatusProvider.loadShip = originalLoadShip;
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
      expect(releaseStatusProvider.loadShip).toBe(browserShipReleaseStatus);
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

// ------------------------------------------------------------------ 第三十一輪:船上筆電的發行版狀態
const SHIP_WEB = 'http://192.168.77.11:8080';
const st = (releaseVersion: string, softwareVersion: string | null = null): ReleaseStatus => parseReleaseStatus({ ...normalJson, releaseVersion, softwareVersion })!;
const DOWN = (): ReleaseStatus => parseReleaseStatus(downgradeJson)!;

describe('船上筆電的發行版狀態:位址、版本比較、取得', () => {
  const page = (url: string) => {
    const u = new URL(url);
    return { protocol: u.protocol, hostname: u.hostname, origin: u.origin };
  };
  const LOCAL_PAGE = page('http://127.0.0.1:8080/instructor.html');

  it('迴路位址的主機名', () => {
    for (const h of ['localhost', 'LOCALHOST', '127.0.0.1', '127.5.6.7', '[::1]', '::1']) expect(isLoopbackHost(h), h).toBe(true);
    for (const h of ['', '192.168.77.11', '128.0.0.1', '127.0.0.1.evil.example', 'localhost.evil.example', 'mylocalhost', '1127.0.0.1', '[fe80::1]', '0.0.0.0']) expect(isLoopbackHost(h), h).toBe(false);
  });

  it('沒有 shipWeb 參數:由 ws 的主機推得 http://<主機>:8080(既有的 instructor-laptop.json 不必改)', () => {
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://192.168.77.11:8765', null)).toBe('http://192.168.77.11:8080');
    expect(shipWebBaseUrl(page('http://localhost:8080/instructor.html'), 'ws://192.168.77.12:9000/', undefined)).toBe('http://192.168.77.12:8080');
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://ship-laptop:8765', '')).toBe('http://ship-laptop:8080');
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://[fe80::1]:8765', null)).toBe('http://[fe80::1]:8080');
  });

  it('ws 的主機就是這台電腦(standalone、開發模式、船上筆電自己開的教官站)→ null:不發跨來源請求', () => {
    for (const ws of ['ws://localhost:8765', 'ws://127.0.0.1:8765', 'ws://[::1]:8765', 'ws://127.0.0.2:8765', 'ws://LOCALHOST:8765']) {
      expect(shipWebBaseUrl(LOCAL_PAGE, ws, null), ws).toBeNull();
      expect(shipWebBaseUrl(page('http://localhost:5173/instructor.html'), ws, null), ws).toBeNull();
    }
    // 船上筆電自己開的(或教官筆電的瀏覽器直接開船上的網址):頁面不是本機迴路位址提供的 —— 同源取到的就是船上的,不另外取
    expect(shipWebBaseUrl(page('http://192.168.77.11:8080/instructor.html?mode=simple'), 'ws://192.168.77.11:8765', null)).toBeNull();
    expect(shipWebBaseUrl(page('http://192.168.77.11:8080/instructor.html'), 'ws://192.168.77.12:8765', 'http://192.168.77.12:8080')).toBeNull();
    // 不是 http 的頁面(file://、https)與無法解析的 ws
    expect(shipWebBaseUrl({ protocol: 'file:', hostname: '', origin: 'null' }, 'ws://192.168.77.11:8765', null)).toBeNull();
    expect(shipWebBaseUrl(page('https://localhost:8443/instructor.html'), 'ws://192.168.77.11:8765', null)).toBeNull();
    for (const ws of ['', 'not a url', null, undefined]) expect(shipWebBaseUrl(LOCAL_PAGE, ws, null)).toBeNull();
  });

  it('明確的 shipWeb 參數優先(船上網頁埠不是 8080);0 / off 不取;與頁面同一個來源 → null;不合格的值當成沒給', () => {
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://192.168.77.11:8765', 'http://192.168.77.11:8081')).toBe('http://192.168.77.11:8081');
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://192.168.77.11:8765', 'http://192.168.77.11:8081/instructor.html?x=1')).toBe('http://192.168.77.11:8081');
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://192.168.77.11:8765', ' 192.168.77.11:8081 ')).toBe('http://192.168.77.11:8081');
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://192.168.77.11:8765', '192.168.77.11')).toBe('http://192.168.77.11');
    for (const off of ['0', 'off', 'OFF']) expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://192.168.77.11:8765', off), off).toBeNull();
    // ws 在本機而 shipWeb 指到另一個來源(端對端以 127.0.0.1 / localhost 做出跨來源):照取
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://127.0.0.1:8765', 'http://localhost:9090')).toBe('http://localhost:9090');
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://127.0.0.1:8765', 'http://127.0.0.1:8080/')).toBeNull();
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://192.168.77.11:8765', 'http://127.0.0.1:8080')).toBeNull();
    for (const bad of ['https://192.168.77.11:8443', 'ftp://192.168.77.11', 'http://user:pw@192.168.77.11:8080', 'http://', '::::', 'javascript:alert(1)']) {
      expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://192.168.77.11:8765', bad), bad).toBe('http://192.168.77.11:8080');
      expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://localhost:8765', bad), bad).toBeNull();
    }
  });

  it('shipWeb 只寫埠號(設定檔的 ports.shipWeb 就是一個埠號,手動補參數時容易照著寫):ws 的主機配這個埠;不可以把數字當成主機(8081 → 0.0.31.145)', () => {
    const ws = 'ws://192.168.77.11:8765';
    expect(shipWebBaseUrl(LOCAL_PAGE, ws, '8081')).toBe('http://192.168.77.11:8081');
    expect(shipWebBaseUrl(LOCAL_PAGE, ws, ':8081')).toBe('http://192.168.77.11:8081');
    expect(shipWebBaseUrl(LOCAL_PAGE, ws, ' 8081 ')).toBe('http://192.168.77.11:8081');
    expect(shipWebBaseUrl(LOCAL_PAGE, ws, '1')).toBe('http://192.168.77.11:1');
    expect(shipWebBaseUrl(LOCAL_PAGE, ws, '65535')).toBe('http://192.168.77.11:65535');
    expect(shipWebBaseUrl(LOCAL_PAGE, 'ws://ship-laptop:8765', '8081')).toBe('http://ship-laptop:8081');
    // 不是 1–65535 的埠號:當成沒給(預設的 8080);「0」照舊是不取
    for (const bad of ['65536', '00', '123456', '99999999999', ':0']) expect(shipWebBaseUrl(LOCAL_PAGE, ws, bad), bad).toBe('http://192.168.77.11:8080');
    expect(shipWebBaseUrl(LOCAL_PAGE, ws, '0')).toBeNull();
    // 主機只有數字的其他寫法(URL 會把數字解析成 IPv4 位址):當成沒給,請求不可以送到 0.0.x.x
    for (const bad of ['8081/', '8081:80', 'http://8081', 'http://8081:8080/x', '8081?x=1', '8081#f']) {
      const got = shipWebBaseUrl(LOCAL_PAGE, ws, bad);
      expect(got, bad).toBe('http://192.168.77.11:8080');
      expect(got, bad).not.toMatch(/\/\/0\./);
    }
    // ws 在本機(同一台電腦):只寫埠號也不取(沒有另一台);數字開頭的主機名與 IP 照常
    for (const wsLocal of ['ws://localhost:8765', 'ws://127.0.0.1:8765']) expect(shipWebBaseUrl(LOCAL_PAGE, wsLocal, '8081'), wsLocal).toBeNull();
    expect(shipWebBaseUrl(LOCAL_PAGE, ws, '192.168.77.12:8081')).toBe('http://192.168.77.12:8081');
    expect(shipWebBaseUrl(LOCAL_PAGE, ws, '2ship:8081')).toBe('http://2ship:8081');
  });

  it('實際執行的版本:套用過較新的軟體更新包時是更新包的版次,否則(含降版)是發行版', () => {
    expect(runningVersion(st('2026.11.30'))).toBe('2026.11.30');
    expect(runningVersion(st('2026.12.1', '2026.12.2'))).toBe('2026.12.2');
    expect(runningVersion(DOWN())).toBe('2026.11.1');
    expect(runningLabel(st('2026.11.30'))).toBe('發行版 2026.11.30');
    expect(runningLabel(st('2026.12.1', '2026.12.2'))).toBe('軟體更新 2026.12.2');
    expect(runningDescription(st('2026.12.1', '2026.12.2'))).toBe('軟體更新包 2026.12.2 的程式(套用在發行版 2026.12.1 之上)');
    expect(runningDescription(DOWN())).toBe('發行版 2026.11.1');
  });

  it('兩台的比較:版本不同時哪一台較舊;徽章的標記取 兩台降版 > 船上降版 > 本機降版 > 版本不同', () => {
    const same = compareFleet(st('2026.12.1', '2026.12.2'), st('2026.12.2'));
    expect(same).toEqual({ shipDowngrade: false, localDowngrade: false, mismatch: false, older: null, tag: null });
    // 缺的段當 0:2026.12 與 2026.12.0 是同一版
    expect(compareFleet(st('2026.12'), st('2026.12.0')).tag).toBeNull();
    expect(compareFleet(st('2026.11.30'), st('2026.12.1', '2026.12.2'))).toMatchObject({ mismatch: true, older: 'ship', tag: '版本不同' });
    expect(compareFleet(st('2026.12.1', '2026.12.2'), st('2026.12.1'))).toMatchObject({ mismatch: true, older: 'local', tag: '版本不同' });
    expect(compareFleet(st('2026.11.9'), st('2026.11.10'))).toMatchObject({ older: 'ship' }); // 逐段比數值
    // 降版:降版那一台實際執行的是較舊的發行版(status 的軟體版次不算)
    expect(compareFleet(DOWN(), st('2026.12.1', '2026.12.2'))).toEqual({ shipDowngrade: true, localDowngrade: false, mismatch: true, older: 'ship', tag: '船上降版' });
    expect(compareFleet(st('2026.12.1', '2026.12.2'), DOWN())).toEqual({ shipDowngrade: false, localDowngrade: true, mismatch: true, older: 'local', tag: '本機降版' });
    expect(compareFleet(DOWN(), DOWN())).toEqual({ shipDowngrade: true, localDowngrade: true, mismatch: false, older: null, tag: '兩台降版' });
    expect(compareFleet(DOWN(), st('2026.11.1'))).toMatchObject({ mismatch: false, tag: '船上降版' });
    // 本機沒有發行版狀態(教官筆電是開發模式):無從比較
    expect(compareFleet(st('2026.11.30'), null)).toEqual({ shipDowngrade: false, localDowngrade: false, mismatch: false, older: null, tag: null });
    expect(compareFleet(DOWN(), null)).toMatchObject({ mismatch: false, tag: '船上降版' });
  });

  it('版本不同的說明:哪一台較舊、更新較舊的那一台、同步見 packaging-sync.md 第 6 節、不擋操作;版本相同時沒有', () => {
    const a = mismatchSummary(st('2026.11.30'), st('2026.12.1', '2026.12.2'))!;
    expect(a.what).toBe('船上筆電執行的是發行版 2026.11.30,這台電腦(本機)執行的是軟體更新 2026.12.2 — 船上筆電較舊');
    expect(a.advice).toContain('更新較舊的那一台(船上筆電)');
    expect(a.advice).toContain('以較新的安裝程式重新安裝,或套用軟體更新包');
    expect(a.advice).toContain('docs/design/packaging-sync.md 第 6 節');
    expect(a.advice).toContain('這是提醒,不擋操作');
    const b = mismatchSummary(st('2026.12.1', '2026.12.2'), st('2026.12.1'))!;
    expect(b.what).toContain('這台電腦(本機)較舊');
    expect(b.advice).toContain('更新較舊的那一台(這台電腦)');
    expect(mismatchSummary(st('2026.12.2'), st('2026.12.1', '2026.12.2'))).toBeNull();
  });

  it('版本不同而較舊的那一台有降版提醒:處理指向它的降版提醒(刻意回到舊版時不必處理),不叫人「更新較舊的那一台」(兩段不可以互相矛盾)', () => {
    // 船上降版(執行 2026.11.1)、本機 2026.12.2:較舊的是船上,而且它有降版提醒
    const a = mismatchSummary(DOWN(), st('2026.12.1', '2026.12.2'))!;
    expect(a.what).toContain('船上筆電較舊');
    expect(a.advice).toContain('較舊的是有降版提醒的那一台(船上筆電),依上面降版提醒的處理');
    expect(a.advice).toContain('刻意回到舊版時兩台版本不同是預期的,需要兩台同一版就把另一台也換成那一版');
    expect(a.advice).toContain('docs/design/packaging-sync.md 第 6 節');
    expect(a.advice).toContain('這是提醒,不擋操作');
    expect(a.advice).not.toContain('更新較舊的那一台');
    // 本機降版、船上較新
    const b = mismatchSummary(st('2026.12.1', '2026.12.2'), DOWN())!;
    expect(b.advice).toContain('較舊的是有降版提醒的那一台(這台電腦),依上面降版提醒的處理');
    expect(b.advice).not.toContain('更新較舊的那一台');
    // 有降版提醒的是「較新」的那一台(船上由 2026.12.2 降到 2026.11.1,本機更舊):較舊的那一台沒有降版提醒,照常建議更新它
    const c = mismatchSummary(DOWN(), st('2026.10.5'))!;
    expect(c.what).toContain('這台電腦(本機)較舊');
    expect(c.advice).toContain('更新較舊的那一台(這台電腦)');
    expect(c.advice).not.toContain('依上面降版提醒的處理');
  });

  it('降版重點可以寫明是哪一台(預設「這台電腦」)', () => {
    const d = DOWN().downgrade!;
    expect(downgradeSummary(d).running).toBe('這台電腦目前執行的是較舊的發行版 2026.11.1');
    expect(downgradeSummary(d, '船上筆電').running).toBe('船上筆電目前執行的是較舊的發行版 2026.11.1');
  });

  it('跨來源取船上的狀態:<船上網頁位址>/_status/release.json,簡單請求(只有 cache / credentials / signal,沒有自訂標頭 → 瀏覽器不送 preflight)', async () => {
    expect(shipReleaseStatusUrl('http://192.168.77.11:8080')).toBe('http://192.168.77.11:8080/_status/release.json');
    expect(shipReleaseStatusUrl('http://192.168.77.11:8080/')).toBe('http://192.168.77.11:8080/_status/release.json');
    const calls: Array<{ url: string; init: Record<string, unknown> }> = [];
    vi.stubGlobal('fetch', async (url: string, init: Record<string, unknown>) => {
      calls.push({ url, init });
      return { ok: true, json: async () => updatedJson };
    });
    try {
      // 預設的 provider 不發請求
      expect(await originalLoadShip(SHIP_WEB)).toBeNull();
      expect(calls).toEqual([]);
      expect(await browserShipReleaseStatus(SHIP_WEB)).toEqual(parseReleaseStatus(updatedJson));
      expect(calls).toHaveLength(1);
      expect(calls[0]!.url).toBe('http://192.168.77.11:8080/_status/release.json');
      expect(Object.keys(calls[0]!.init).sort()).toEqual(['cache', 'credentials', 'signal']);
      expect(calls[0]!.init['cache']).toBe('no-store');
      expect(calls[0]!.init['credentials']).toBe('omit');
      expect(await browserShipReleaseStatus('')).toBeNull();
      expect(calls).toHaveLength(1);
      // 取不到(CORS 被擋、連不上:fetch 丟 TypeError;船上是舊版回 404)→ null,不丟例外
      vi.stubGlobal('fetch', async () => {
        throw new TypeError('Failed to fetch');
      });
      expect(await browserShipReleaseStatus(SHIP_WEB)).toBeNull();
      vi.stubGlobal('fetch', async () => ({ ok: false, json: async () => updatedJson }));
      expect(await browserShipReleaseStatus(SHIP_WEB)).toBeNull();
      // 船上連得到、格式也對,但各欄是 null(船上是開發模式,或船上啟動時沒有取到 simosa-pkg status):「沒有發行版狀態」,不是取不到
      vi.stubGlobal('fetch', async () => ({ ok: true, json: async () => emptyJson }));
      expect(await browserShipReleaseStatus(SHIP_WEB)).toBe(SHIP_NO_STATUS);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('船上的結果分三種:狀態、沒有發行版狀態(格式相符而沒有發行版本)、取不到(其餘全部)', async () => {
    const ok = (json: unknown): StatusFetch => async () => ({ ok: true, json: async () => json });
    const url = 'http://192.168.77.11:8080/_status/release.json';
    expect(await fetchShipReleaseStatus(ok(normalJson), url)).toEqual(NORMAL);
    expect(await fetchShipReleaseStatus(ok(downgradeJson), url)).toEqual(parseReleaseStatus(downgradeJson));
    expect(SHIP_NO_STATUS).toBe('none');
    expect(await fetchShipReleaseStatus(ok(emptyJson), url)).toBe(SHIP_NO_STATUS);
    // 格式相符但版本欄不是版本(simosa-web 不會送出;當成沒有發行版狀態)
    expect(await fetchShipReleaseStatus(ok({ format: 'simosa-release-status/1', releaseVersion: 'abc' }), url)).toBe(SHIP_NO_STATUS);
    // 取不到:格式不符、不是物件、404、不是 JSON、連不上 / 被 CORS 擋下、逾時
    for (const json of [{ ...normalJson, format: 'simosa-release-status/2' }, {}, [], null, 'text', 42]) expect(await fetchShipReleaseStatus(ok(json), url), JSON.stringify(json)).toBeNull();
    expect(await fetchShipReleaseStatus(async () => ({ ok: false, json: async () => emptyJson }), url)).toBeNull();
    expect(await fetchShipReleaseStatus(async () => ({ ok: true, json: async () => Promise.reject(new SyntaxError('Unexpected token <')) }), url)).toBeNull();
    expect(await fetchShipReleaseStatus(async () => Promise.reject(new TypeError('Failed to fetch')), url)).toBeNull();
    // 同源的 fetchReleaseStatus 不分這兩種(沒有就是不顯示)
    expect(await fetchReleaseStatus(ok(emptyJson))).toBeNull();
  });
});

describe('<brm-release-status>:船上筆電 + 本機', () => {
  type Badge = ReleaseStatusBadge & Updatable;
  const flush = async (el: Badge): Promise<void> => {
    for (let i = 0; i < 6; i++) await Promise.resolve();
    await el.updateComplete;
  };
  /** 掛上元件:本機的狀態 local、船上的由 shipLoad 回應;shipWebUrl / shipLinked 由參數指定 */
  async function mountFleet(local: ReleaseStatus | null, shipLoad: (url: string) => Promise<ShipStatusResult>, opts: { url?: string | null; linked?: boolean } = {}): Promise<Badge> {
    releaseStatusProvider.load = async () => local;
    releaseStatusProvider.loadShip = shipLoad;
    const el = document.createElement('brm-release-status') as Badge;
    el.shipWebUrl = opts.url === undefined ? SHIP_WEB : opts.url;
    el.shipLinked = opts.linked ?? true;
    document.body.appendChild(el);
    await flush(el);
    return el;
  }
  const text = (el: Badge): string => el.shadowRoot!.textContent!.replace(/\s+/g, ' ').trim();

  it('同一台電腦(shipWebUrl 是 null):連上核心也不取船上的狀態,畫面與只有本機時相同', async () => {
    const calls: string[] = [];
    const el = await mountFleet(st('2026.11.30'), async (u) => (calls.push(u), st('2026.12.9')), { url: null, linked: true });
    expect(calls).toEqual([]);
    expect(text(el)).toBe('發行版 2026.11.30');
    expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).not.toContain('船上');
    // 斷線、重新連上也一樣
    el.shipLinked = false;
    await flush(el);
    el.shipLinked = true;
    await flush(el);
    expect(calls).toEqual([]);
  });

  it('連上核心才取(每次重新連上重取一次);兩台版本相同:小字「船上 <版本>」,不是按鈕,本機的在 title', async () => {
    const calls: string[] = [];
    let ship = st('2026.12.1', '2026.12.2');
    const el = await mountFleet(st('2026.12.1', '2026.12.2'), async (u) => (calls.push(u), ship), { linked: false });
    // 還沒連上核心:不取;畫面是本機的,title 註明船上的還沒取到
    expect(calls).toEqual([]);
    expect(text(el)).toBe('軟體更新 2026.12.2');
    expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).toContain(`船上筆電的版本:未取得(連上船上的核心後向 ${SHIP_WEB} 取`);
    el.shipLinked = true;
    await flush(el);
    expect(calls).toEqual([SHIP_WEB]);
    const label = el.shadowRoot!.querySelector<HTMLElement>('.version.onboard')!;
    expect(label.textContent).toBe('船上 2026.12.2');
    expect(label.dataset['running']).toBe('軟體更新 2026.12.2');
    expect(label.title).toContain('船上筆電實際執行的是軟體更新包 2026.12.2 的程式(套用在發行版 2026.12.1 之上)');
    expect(label.title).toContain('這台電腦(本機)相同(軟體更新 2026.12.2)');
    expect(label.title).toContain(`船上筆電:${SHIP_WEB}`);
    expect(label.title).toContain('本機:提供本頁面的電腦');
    expect(el.shadowRoot!.querySelector('button')).toBeNull();
    expect(text(el)).not.toMatch(/降版|版本不同|未取得/);
    // 斷線:保留上一次取到的內容、不再取;重新連上:重取(船上可能套用更新後重新啟動過)
    el.shipLinked = false;
    await flush(el);
    expect(calls).toHaveLength(1);
    expect(text(el)).toBe('船上 2026.12.2');
    ship = st('2026.12.1', '2026.12.3');
    el.shipLinked = true;
    await flush(el);
    expect(calls).toHaveLength(2);
    expect(el.shadowRoot!.querySelector('button.badge')!.textContent).toBe('船上 2026.12.3 · 版本不同');
  });

  it('取不到船上的狀態(舊版、連不上、格式不符 → null;provider 丟例外也一樣):畫面與只有本機時相同、不顯示錯誤,title 寫「船上筆電的版本:未取得」', async () => {
    for (const shipLoad of [async () => null, () => Promise.reject(new Error('Failed to fetch')), () => { throw new Error('sync boom'); }] as Array<(u: string) => Promise<ReleaseStatus | null>>) {
      const el = await mountFleet(st('2026.11.30'), shipLoad);
      expect(text(el)).toBe('發行版 2026.11.30');
      const label = el.shadowRoot!.querySelector<HTMLElement>('.version')!;
      expect(label.classList.contains('onboard')).toBe(false);
      expect(label.title).toContain('這是提供本頁面的電腦');
      expect(label.title).toContain('船上筆電的版本:未取得');
      expect(el.shadowRoot!.querySelector('button, .detail')).toBeNull();
      expect(text(el)).not.toMatch(/錯誤|失敗|未取得/);
      el.remove();
    }
    // 本機有降版而船上的取不到:照舊是本機的降版徽章,展開的說明註明船上的未取得
    const down = await mountFleet(DOWN(), async () => null);
    const badge = down.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!;
    expect(badge.textContent).toBe('發行版 2026.11.1 · 降版提醒');
    badge.click();
    await down.updateComplete;
    // 展開的說明只加短句(長的原因在徽章的 title):長句在 1093×614 讓說明多折兩行(211 → 253 px)、蓋住航跡圖的鈕
    expect(badge.title).toContain(`船上筆電的版本:未取得(連上船上的核心後向 ${SHIP_WEB} 取;船上是較舊的版本或網頁伺服沒有啟動時取不到)`);
    expect(down.shadowRoot!.querySelector('.detail .foot .source')!.textContent!.endsWith('重新啟動才會更新。船上筆電的版本:未取得')).toBe(true);
    // 兩邊都沒有:什麼都不顯示
    const none = await mountFleet(null, async () => null);
    expect(text(none)).toBe('');
  });

  it('取不到時依 SHIP_RETRY_DELAYS_MS 重試(連上後約 4、19、49、109、169 秒:涵蓋船上啟動腳本最慢約 36 秒才啟動網頁伺服),之後不再試;中途取到就顯示;元件移除或斷線後不再試', async () => {
    expect(SHIP_RETRY_DELAYS_MS).toEqual([4000, 15000, 30000, 60000, 60000]);
    // 船上的啟動腳本在核心就緒後:操控台 1 s、等雷達最多 15 s、simosa-pkg status 最多 20 s,然後才啟動網頁伺服 —— 重試必須涵蓋這 36 秒再加餘裕
    const offsets = SHIP_RETRY_DELAYS_MS.reduce<number[]>((acc, d) => [...acc, (acc.at(-1) ?? 0) + d], []);
    expect(offsets).toEqual([4000, 19000, 49000, 109000, 169000]);
    expect(offsets.filter((t) => t > (1 + 15 + 20) * 1000 + 10_000).length, '啟動腳本最慢的情形(36 秒)加 10 秒餘裕之後至少還有兩次').toBeGreaterThanOrEqual(2);
    vi.useFakeTimers();
    try {
      let calls = 0;
      let answer: ShipStatusResult = null;
      const el = await mountFleet(st('2026.11.30'), async () => (calls++, answer));
      expect(calls).toBe(1);
      await vi.advanceTimersByTimeAsync(3999);
      expect(calls).toBe(1);
      await vi.advanceTimersByTimeAsync(1);
      expect(calls).toBe(2);
      await vi.advanceTimersByTimeAsync(14999);
      expect(calls).toBe(2);
      await vi.advanceTimersByTimeAsync(1);
      expect(calls).toBe(3);
      // 審查發現:船上的網頁伺服比核心晚 25 秒就緒 —— 原本只試到 19 秒,之後一直是本機的版本;現在 49 秒那一次取到
      await vi.advanceTimersByTimeAsync(29999);
      expect(calls).toBe(3);
      expect(text(el)).toBe('發行版 2026.11.30');
      answer = st('2026.11.30');
      await vi.advanceTimersByTimeAsync(1);
      expect(calls).toBe(4);
      await flush(el);
      expect(text(el)).toBe('船上 2026.11.30');
      await vi.advanceTimersByTimeAsync(600_000);
      expect(calls).toBe(4); // 取到之後不再試
      el.remove();

      // 每一次都取不到:連同第一次共 6 次(連上後 0、4、19、49、109、169 秒),之後不再試,畫面維持只有本機的
      calls = 0;
      answer = null;
      const el2 = await mountFleet(st('2026.11.30'), async () => (calls++, answer));
      const seen: number[] = [];
      for (const t of [0, ...offsets]) {
        await vi.advanceTimersByTimeAsync(t - (seen.length ? [0, ...offsets][seen.length - 1]! : 0));
        seen.push(calls);
      }
      expect(seen).toEqual([1, 2, 3, 4, 5, 6]);
      await vi.advanceTimersByTimeAsync(3_600_000);
      expect(calls).toBe(1 + SHIP_RETRY_DELAYS_MS.length);
      expect(text(el2)).toBe('發行版 2026.11.30');
      el2.remove();

      // 等待重試時斷線 / 元件移除:取消
      calls = 0;
      const el3 = await mountFleet(st('2026.11.30'), async () => (calls++, null));
      expect(calls).toBe(1);
      el3.shipLinked = false;
      await flush(el3);
      await vi.advanceTimersByTimeAsync(600_000);
      expect(calls).toBe(1);
      el3.shipLinked = true;
      await flush(el3);
      expect(calls).toBe(2);
      el3.remove();
      await vi.advanceTimersByTimeAsync(600_000);
      expect(calls).toBe(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('船上連得到但沒有發行版狀態(各欄 null):不重試;畫面與只有本機時相同,說明寫「船上筆電沒有發行版狀態」而不是「未取得 / 船上是較舊的版本」', async () => {
    vi.useFakeTimers();
    try {
      let calls = 0;
      let answer: ShipStatusResult = SHIP_NO_STATUS;
      const el = await mountFleet(st('2026.11.30'), async () => (calls++, answer));
      expect(calls).toBe(1);
      expect(text(el)).toBe('發行版 2026.11.30');
      const label = el.shadowRoot!.querySelector<HTMLElement>('.version')!;
      expect(label.classList.contains('onboard')).toBe(false);
      expect(label.title).toContain(`船上筆電沒有發行版狀態(已向 ${SHIP_WEB} 取到回應:船上是開發模式,或船上啟動時沒有取到 simosa-pkg status)`);
      expect(label.title).not.toMatch(/未取得|較舊的版本|網頁伺服沒有啟動/);
      expect(el.shadowRoot!.querySelector('button, .detail')).toBeNull();
      await vi.advanceTimersByTimeAsync(3_600_000);
      expect(calls, '沒有發行版狀態時不重試(狀態檔在船上下次啟動前不會出現)').toBe(1);
      // 重新連上核心(船上重新啟動過)才再取一次;這次取到就顯示
      el.shipLinked = false;
      await flush(el);
      expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).toContain('船上筆電沒有發行版狀態'); // 斷線期間保留
      answer = st('2026.11.30');
      el.shipLinked = true;
      await flush(el);
      expect(calls).toBe(2);
      expect(text(el)).toBe('船上 2026.11.30');
      // 取到之後船上又變成沒有發行版狀態(重新連上):上一次的版本不留
      el.shipLinked = false;
      await flush(el);
      answer = SHIP_NO_STATUS;
      el.shipLinked = true;
      await flush(el);
      expect(calls).toBe(3);
      expect(text(el)).toBe('發行版 2026.11.30');
      expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).toContain('船上筆電沒有發行版狀態');
      calls = 2; // 以下的次數照原本的算法
      // 之後變成取不到(重新連上):改寫未取得,並且照常重試
      el.shipLinked = false;
      await flush(el);
      answer = null;
      el.shipLinked = true;
      await flush(el);
      expect(calls).toBe(3);
      expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).toContain('船上筆電的版本:未取得');
      expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).not.toContain('沒有發行版狀態');
      await vi.advanceTimersByTimeAsync(4000);
      expect(calls).toBe(4);
      el.remove();

      // 本機有降版而船上沒有發行版狀態:照舊是本機的降版徽章;title 寫完整的原因,展開的說明只加短句
      const down = await mountFleet(DOWN(), async () => SHIP_NO_STATUS);
      const badge = down.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!;
      expect(badge.textContent).toBe('發行版 2026.11.1 · 降版提醒');
      expect(badge.title).toContain('船上筆電沒有發行版狀態(已向');
      badge.click();
      await down.updateComplete;
      const source = down.shadowRoot!.querySelector('.detail .foot .source')!.textContent!;
      expect(source.endsWith('。船上筆電沒有發行版狀態')).toBe(true);
      expect(source).not.toContain('未取得');
      // 換一台船、改回同一台電腦:這個註記不留
      down.shipWebUrl = null;
      await flush(down);
      expect(down.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!.title).not.toContain('船上');
    } finally {
      vi.useRealTimers();
    }
  });

  it('較早的請求晚回來要丟棄:斷線又連上之後,第一次的請求才回來(取不到)—— 不可以清掉剛取到的船上狀態,也不可以另外排重試', async () => {
    vi.useFakeTimers();
    try {
      const pending: Array<(v: ShipStatusResult) => void> = [];
      let calls = 0;
      const el = await mountFleet(st('2026.11.30'), () => (calls++, new Promise<ShipStatusResult>((resolve) => pending.push(resolve))));
      expect(calls).toBe(1); // 請求 A:還沒回來
      el.shipLinked = false;
      await flush(el);
      el.shipLinked = true;
      await flush(el);
      expect(calls).toBe(2); // 請求 B
      pending[1]!(st('2026.12.5'));
      await flush(el);
      expect(el.shadowRoot!.querySelector('button.badge')!.textContent).toBe('船上 2026.12.5 · 版本不同');
      // A 這時才回來,而且是取不到
      pending[0]!(null);
      await flush(el);
      expect(el.shadowRoot!.querySelector('button.badge')!.textContent, '畫面仍是 B 的結果').toBe('船上 2026.12.5 · 版本不同');
      await vi.advanceTimersByTimeAsync(3_600_000);
      expect(calls, 'A 的失敗不排重試').toBe(2);
      // A 晚回來的是另一個版本、或「沒有發行版狀態」:一樣丟棄
      el.shipLinked = false;
      await flush(el);
      el.shipLinked = true;
      await flush(el);
      expect(calls).toBe(3); // 請求 C:未回
      el.shipLinked = false;
      await flush(el);
      el.shipLinked = true;
      await flush(el);
      expect(calls).toBe(4); // 請求 D
      pending[3]!(st('2026.12.6'));
      await flush(el);
      pending[2]!(SHIP_NO_STATUS);
      await flush(el);
      expect(el.shadowRoot!.querySelector('button.badge')!.textContent).toBe('船上 2026.12.6 · 版本不同');
      el.remove();
    } finally {
      vi.useRealTimers();
    }
  });

  it('換了一台船:新的請求還沒回來時,上一台的內容已經不在(不把上一台的版本當成新的那一台);上一台晚回來的結果也丟棄', async () => {
    const pending = new Map<string, (v: ShipStatusResult) => void>();
    const urls: string[] = [];
    const el = await mountFleet(st('2026.11.30'), (u) => (urls.push(u), new Promise<ShipStatusResult>((resolve) => pending.set(u, resolve))));
    pending.get(SHIP_WEB)!(st('2026.12.5'));
    await flush(el);
    expect(el.shadowRoot!.querySelector('button.badge')!.textContent).toBe('船上 2026.12.5 · 版本不同');
    // 換到另一台船:請求停在未回
    const other = 'http://192.168.77.12:8080';
    el.shipWebUrl = other;
    await flush(el);
    expect(urls).toEqual([SHIP_WEB, other]);
    expect(text(el), '新的那一台還沒回來:只有本機的').toBe('發行版 2026.11.30');
    expect(el.shadowRoot!.querySelector('button')).toBeNull();
    expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).toContain(`向 ${other} 取`);
    pending.get(other)!(st('2026.11.30'));
    await flush(el);
    expect(text(el)).toBe('船上 2026.11.30');
    // 再換回第一台(請求未回)時,第二台的舊請求不會再影響畫面
    el.shipWebUrl = SHIP_WEB;
    await flush(el);
    expect(text(el)).toBe('發行版 2026.11.30');
    pending.get(other)!(st('2026.12.9'));
    await flush(el);
    expect(text(el)).toBe('發行版 2026.11.30');
  });

  it('重新連上後取不到:不留上一次的內容(船上可能已經換成別的版本);換了一台船也不留', async () => {
    let answer: ReleaseStatus | null = st('2026.11.30');
    const urls: string[] = [];
    const el = await mountFleet(st('2026.11.30'), async (u) => (urls.push(u), answer));
    expect(text(el)).toBe('船上 2026.11.30');
    el.shipLinked = false;
    await flush(el);
    answer = null;
    el.shipLinked = true;
    await flush(el);
    expect(text(el)).toBe('發行版 2026.11.30');
    expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).toContain('船上筆電的版本:未取得');
    // 換一台船(位址改變):立即取新的那一台
    answer = st('2026.12.5');
    el.shipWebUrl = 'http://192.168.77.12:8080';
    await flush(el);
    expect(urls.at(-1)).toBe('http://192.168.77.12:8080');
    expect(el.shadowRoot!.querySelector('button.badge')!.textContent).toBe('船上 2026.12.5 · 版本不同');
    // 改回同一台電腦(null):船上的不再顯示
    el.shipWebUrl = null;
    await flush(el);
    expect(text(el)).toBe('發行版 2026.11.30');
    expect(el.shadowRoot!.querySelector<HTMLElement>('.version')!.title).not.toContain('船上');
  });

  it('版本不同:警示徽章「船上 <版本> · 版本不同」;展開依序寫船上、本機、哪一台較舊與處理;可收合', async () => {
    const el = await mountFleet(st('2026.12.1', '2026.12.2'), async () => st('2026.11.30'));
    const badge = el.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!;
    expect(badge.textContent).toBe('船上 2026.11.30 · 版本不同');
    expect(badge.dataset['tag']).toBe('版本不同');
    expect(badge.classList.contains('mismatch')).toBe(true);
    expect(badge.classList.contains('downgrade')).toBe(false);
    expect(badge.getAttribute('aria-expanded')).toBe('false');
    expect(badge.title).toContain('版本不同:船上筆電執行的是發行版 2026.11.30,這台電腦(本機)執行的是軟體更新 2026.12.2 — 船上筆電較舊');
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull(); // 預設收合,不佔版面
    badge.click();
    await el.updateComplete;
    const detail = el.shadowRoot!.querySelector<HTMLElement>('.detail')!;
    const blocks = [...detail.children].map((c) => c.className);
    expect(blocks).toEqual(['machine onboard', 'machine local', 'mismatch', 'foot']);
    expect(detail.querySelector('.machine.onboard .line')!.textContent).toBe('船上筆電:實際執行的是發行版 2026.11.30。');
    expect(detail.querySelector('.machine.local .line')!.textContent).toBe('這台電腦(本機):實際執行的是軟體更新包 2026.12.2 的程式(套用在發行版 2026.12.1 之上)。');
    expect(detail.querySelector('.mismatch .lead')!.textContent).toContain('版本不同:船上筆電執行的是發行版 2026.11.30,這台電腦(本機)執行的是軟體更新 2026.12.2 — 船上筆電較舊。');
    expect(detail.querySelector('.mismatch .advice')!.textContent).toContain('更新較舊的那一台(船上筆電)');
    expect(detail.querySelector('.mismatch .advice')!.textContent).toContain('docs/design/packaging-sync.md 第 6 節');
    expect(detail.querySelector('.mismatch .advice')!.textContent).toContain('這是提醒,不擋操作');
    expect(detail.querySelector('.foot .source')!.textContent).toContain(`船上筆電:${SHIP_WEB}`);
    expect(detail.textContent).not.toContain('降版');
    detail.querySelector<HTMLButtonElement>('button.collapse')!.click();
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull();
    badge.click();
    await el.updateComplete;
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('.detail')).toBeNull();
    // 本機較舊
    const el2 = await mountFleet(st('2026.11.30'), async () => st('2026.12.1', '2026.12.2'));
    const badge2 = el2.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!;
    expect(badge2.textContent).toBe('船上 2026.12.2 · 版本不同');
    badge2.click();
    await el2.updateComplete;
    expect(el2.shadowRoot!.querySelector('.mismatch .lead')!.textContent).toContain('這台電腦(本機)較舊');
  });

  it('船上降版:與本機降版同樣醒目的徽章、寫明是船上;展開先寫船上的(重點、處理、status 的完整訊息),再寫本機的', async () => {
    const el = await mountFleet(st('2026.12.1', '2026.12.2'), async () => DOWN());
    const badge = el.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!;
    expect(badge.textContent).toBe('船上 2026.11.1 · 船上降版');
    expect(badge.dataset['tag']).toBe('船上降版');
    expect(badge.classList.contains('downgrade')).toBe(true);
    expect(badge.title).toContain('船上筆電目前執行的是較舊的發行版 2026.11.1');
    expect(el.shadowRoot!.querySelector('.version')).toBeNull();
    badge.click();
    await el.updateComplete;
    const detail = el.shadowRoot!.querySelector<HTMLElement>('.detail')!;
    expect([...detail.children].map((c) => c.className)).toEqual(['machine onboard down', 'machine local', 'mismatch', 'foot']);
    const ship = detail.querySelector('.machine.onboard')!;
    expect(ship.querySelector('.lead strong')!.textContent).toBe('船上筆電 · 降版提醒');
    expect(ship.querySelector('.lead')!.textContent).toBe('船上筆電 · 降版提醒:船上筆電目前執行的是較舊的發行版 2026.11.1;先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 比它新。');
    expect(ship.querySelector('.advice')!.textContent).toContain('請以版本不低於 2026.12.2 的安裝程式重新安裝');
    // status 的完整訊息預設收合(兩台的完整訊息都攤開時,1093×614 的說明高 320 px、蓋住執行控制):<details>,標題列可點
    const full = ship.querySelector<HTMLDetailsElement>('details.full')!;
    expect(full.open).toBe(false);
    expect(full.querySelector('summary.k')!.textContent).toBe('完整訊息(船上筆電的 simosa-pkg status)');
    const message = (downgradeJson['releaseDowngrade'] as { message: string }).message;
    expect(full.querySelector('.message')!.textContent).toBe(message);
    // 「status 顯示的軟體版次不是實際執行的程式」:完整訊息裡已經有這一句,重點列不再重複(少折一行)
    expect(message).toContain('simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式');
    expect(ship.querySelector('.lead')!.textContent).not.toContain('不是實際執行的程式');
    expect(full.querySelector('.stale')).toBeNull();
    expect(detail.querySelector('.machine.local .line')!.textContent).toContain('這台電腦(本機):實際執行的是軟體更新包 2026.12.2 的程式');
    expect(detail.querySelector('.machine.local .lead')).toBeNull();
    expect(detail.querySelector('.mismatch .lead')!.textContent).toContain('船上筆電較舊');
    // 較舊的船上筆電有降版提醒:「版本不同」的處理指向上面的降版提醒,不叫人更新船上筆電(與「刻意回到舊版時不必處理」矛盾)
    expect(ship.querySelector('.advice')!.textContent).toContain('刻意回到舊版時不必處理');
    expect(detail.querySelector('.mismatch .advice')!.textContent).toContain('較舊的是有降版提醒的那一台(船上筆電),依上面降版提醒的處理');
    expect(detail.querySelector('.mismatch .advice')!.textContent).not.toContain('更新較舊的那一台');
    // 完整訊息沒有那一句時(其他版本的 status):放在收合的那一段裡;沒有完整訊息時(沒有可收合的段落)留在重點列
    const dg = downgradeJson['releaseDowngrade'] as object;
    const other = await mountFleet(st('2026.12.2'), async () => parseReleaseStatus({ ...downgradeJson, releaseDowngrade: { ...dg, message: '其他說明' } }));
    other.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!.click();
    await other.updateComplete;
    expect(other.shadowRoot!.querySelector('.machine.onboard details.full .stale')!.textContent).toBe('simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式。');
    expect(other.shadowRoot!.querySelector('.machine.onboard .lead')!.textContent).not.toContain('不是實際執行的程式');
    const bare = await mountFleet(st('2026.12.2'), async () => parseReleaseStatus({ ...downgradeJson, releaseDowngrade: { ...dg, message: '' } }));
    bare.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!.click();
    await bare.updateComplete;
    expect(bare.shadowRoot!.querySelector('.machine.onboard details')).toBeNull();
    expect(bare.shadowRoot!.querySelector('.machine.onboard .lead')!.textContent).toContain('simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式。');
    // status 的訊息是文字,不當成 HTML
    const evil = parseReleaseStatus({ ...downgradeJson, releaseDowngrade: { ...(downgradeJson['releaseDowngrade'] as object), message: '<img src=x onerror=alert(1)>船上' } });
    const el2 = await mountFleet(st('2026.11.1'), async () => evil);
    el2.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!.click();
    await el2.updateComplete;
    expect(el2.shadowRoot!.querySelector('.detail img')).toBeNull();
    expect(el2.shadowRoot!.querySelector('.detail .machine.onboard .full .message')!.textContent).toBe('<img src=x onerror=alert(1)>船上');
    expect(el2.shadowRoot!.querySelector('.detail .mismatch')).toBeNull(); // 兩台都執行 2026.11.1:沒有「版本不同」
  });

  it('本機降版、兩台都降版:徽章分得出是哪一台;本機沒有發行版狀態時只寫船上的', async () => {
    // 只有本機降版(船上正常)
    const el = await mountFleet(DOWN(), async () => st('2026.12.1', '2026.12.2'));
    const badge = el.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!;
    expect(badge.textContent).toBe('船上 2026.12.2 · 本機降版');
    expect(badge.title).toContain('這台電腦(本機)目前執行的是較舊的發行版 2026.11.1');
    badge.click();
    await el.updateComplete;
    let detail = el.shadowRoot!.querySelector<HTMLElement>('.detail')!;
    expect([...detail.children].map((c) => c.className)).toEqual(['machine onboard', 'machine local down', 'mismatch', 'foot']);
    expect(detail.querySelector('.machine.local .lead strong')!.textContent).toBe('這台電腦(本機) · 降版提醒');
    expect(detail.querySelector('.machine.local details.full summary.k')!.textContent).toBe('完整訊息(這台電腦(本機)的 simosa-pkg status)');
    expect(detail.querySelector<HTMLDetailsElement>('.machine.local details.full')!.open).toBe(false);
    expect(detail.querySelector('.mismatch .lead')!.textContent).toContain('這台電腦(本機)較舊');
    expect(detail.querySelector('.mismatch .advice')!.textContent).toContain('較舊的是有降版提醒的那一台(這台電腦),依上面降版提醒的處理');
    // 兩台都降版
    const both = await mountFleet(DOWN(), async () => DOWN());
    const bb = both.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!;
    expect(bb.textContent).toBe('船上 2026.11.1 · 兩台降版');
    expect(bb.title).toContain('船上筆電目前執行的是較舊的發行版 2026.11.1;這台電腦(本機)目前執行的是較舊的發行版 2026.11.1');
    bb.click();
    await both.updateComplete;
    detail = both.shadowRoot!.querySelector<HTMLElement>('.detail')!;
    expect([...detail.children].map((c) => c.className)).toEqual(['machine onboard down', 'machine local down', 'foot']);
    expect(detail.querySelectorAll('.machine .lead strong')[0]!.textContent).toBe('船上筆電 · 降版提醒');
    expect(detail.querySelectorAll('.machine .lead strong')[1]!.textContent).toBe('這台電腦(本機) · 降版提醒');
    // 本機沒有發行版狀態(教官筆電是開發模式):只寫船上的;船上降版時照樣是徽章
    const dev = await mountFleet(null, async () => st('2026.12.1', '2026.12.2'));
    expect(text(dev)).toBe('船上 2026.12.2');
    expect(dev.shadowRoot!.querySelector<HTMLElement>('.version.onboard')!.title).toContain('這台電腦(本機)沒有發行版狀態');
    const devDown = await mountFleet(null, async () => DOWN());
    const db = devDown.shadowRoot!.querySelector<HTMLButtonElement>('button.badge')!;
    expect(db.textContent).toBe('船上 2026.11.1 · 船上降版');
    db.click();
    await devDown.updateComplete;
    expect(devDown.shadowRoot!.querySelector('.detail .machine.local .line')!.textContent).toBe('這台電腦(本機):沒有發行版狀態(開發模式,或啟動時沒有取到)。');
    expect(devDown.shadowRoot!.querySelector('.detail .mismatch')).toBeNull();
  });
});

describe('<brm-release-status>:展開的說明不蓋住關鍵控制(detailLimit)', () => {
  type Badge = ReleaseStatusBadge & Updatable;
  async function openBadge(limit: (() => number | null) | null): Promise<{ el: Badge; detail: HTMLElement }> {
    const el = await mount(async () => parseReleaseStatus(downgradeJson));
    el.detailLimit = limit;
    el.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!.click();
    await el.updateComplete;
    return { el: el as Badge, detail: el.shadowRoot!.querySelector<HTMLElement>('.detail')! };
  }

  it('有 detailLimit:說明的最大高度縮到底邊在 limit 之上(留空隙),不小於最小高度、不大於 320 px 與視窗高度的 60%;沒有 / null / 丟例外:只用樣式表的最大高度', async () => {
    expect([DETAIL_LIMIT_GAP_PX, DETAIL_MIN_HEIGHT_PX, DETAIL_MAX_HEIGHT_PX]).toEqual([4, 120, 320]);
    const realRect = Element.prototype.getBoundingClientRect;
    // 測試環境沒有版面:讓說明的上緣在 y = 81(1093×614 完整教官站實測的位置)
    Element.prototype.getBoundingClientRect = function (this: Element): DOMRect {
      const top = this.classList.contains('detail') ? 81 : 0;
      return { top, bottom: top, left: 0, right: 0, width: 0, height: 0, x: 0, y: top, toJSON: () => ({}) } as DOMRect;
    };
    const realInner = window.innerHeight;
    const setInner = (h: number): void => {
      Object.defineProperty(window, 'innerHeight', { value: h, configurable: true });
    };
    try {
      setInner(614);
      // 執行控制第一個鈕的上緣 345:345 − 81 − 4 = 260
      let { el, detail } = await openBadge(() => 345);
      expect(detail.style.maxHeight).toBe('260px');
      // 視窗大小改變(執行控制移到 384):重新算;上限 320
      el.detailLimit = () => 384;
      window.dispatchEvent(new Event('resize'));
      expect(detail.style.maxHeight).toBe('299px');
      el.detailLimit = () => 2000;
      setInner(1440);
      window.dispatchEvent(new Event('resize'));
      expect(detail.style.maxHeight).toBe('320px');
      // 視窗很矮:不超過視窗高度的 60%
      setInner(400);
      window.dispatchEvent(new Event('resize'));
      expect(detail.style.maxHeight).toBe('240px');
      setInner(614);
      // 位置太低(limit 幾乎貼著說明的上緣):不小於最小高度
      el.detailLimit = () => 100;
      window.dispatchEvent(new Event('resize'));
      expect(detail.style.maxHeight).toBe('120px');
      // 收合後不再聽 resize;重新展開時依當時的 limit 計算
      window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      await el.updateComplete;
      expect(el.shadowRoot!.querySelector('.detail')).toBeNull();
      el.detailLimit = () => 300;
      window.dispatchEvent(new Event('resize'));
      el.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!.click();
      await el.updateComplete;
      expect(el.shadowRoot!.querySelector<HTMLElement>('.detail')!.style.maxHeight).toBe('215px');
      el.remove();
      // 沒有 detailLimit(簡化模式、其他頁面)、回傳 null、不是數字、丟例外:不加行內樣式(樣式表的 min(60vh, 320px))
      for (const limit of [null, () => null, () => Number.NaN, () => { throw new Error('boom'); }] as Array<(() => number | null) | null>) {
        ({ el, detail } = await openBadge(limit));
        expect(detail.style.maxHeight).toBe('');
        el.remove();
      }
      // 原本有限制、之後變成沒有(例:切到沒有執行控制的畫面):拿掉行內樣式
      ({ el, detail } = await openBadge(() => 345));
      expect(detail.style.maxHeight).toBe('260px');
      el.detailLimit = null;
      await el.updateComplete;
      expect(detail.style.maxHeight).toBe('');
    } finally {
      Element.prototype.getBoundingClientRect = realRect;
      setInner(realInner);
    }
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

  it('教官站把船上網頁位址交給頁首元件:ws 在另一台電腦 → http://<ws 主機>:8080(或 ?shipWeb=);ws 在本機 → null;連上核心才取', async () => {
    const shipCalls: string[] = [];
    const badgeOf = async (query: string) => {
      const app = await mountPage('brm-instructor-app', query);
      releaseStatusProvider.loadShip = async (u) => (shipCalls.push(u), null);
      const badge = app.shadowRoot!.querySelector<ReleaseStatusBadge & Updatable>('header brm-release-status')!;
      await badge.updateComplete;
      return badge;
    };
    // 示範模式不開 WebSocket:位址照算,但沒有連上核心 → 不取
    let badge = await badgeOf('/?demo=1&ws=ws://192.168.77.11:8765');
    expect(badge.shipWebUrl).toBe('http://192.168.77.11:8080');
    expect(badge.shipLinked).toBe(false);
    document.body.innerHTML = '';
    badge = await badgeOf('/?demo=1&mode=simple&ws=ws://192.168.77.11:8765&shipWeb=http://192.168.77.11:8081');
    expect(badge.shipWebUrl).toBe('http://192.168.77.11:8081');
    document.body.innerHTML = '';
    badge = await badgeOf('/?demo=1&ws=ws://192.168.77.11:8765&shipWeb=0');
    expect(badge.shipWebUrl).toBeNull();
    document.body.innerHTML = '';
    // 預設的 ws(ws://localhost:8765)與明確指到本機的 ws:同一台電腦
    for (const q of ['/?demo=1', '/?demo=1&ws=ws://127.0.0.1:8765']) {
      badge = await badgeOf(q);
      expect(badge.shipWebUrl, q).toBeNull();
      document.body.innerHTML = '';
    }
    expect(shipCalls).toEqual([]);

    // 即時連線(假的 WebSocket:建立後立刻連上):連上核心 → 向船上取一次;ws 在本機時連上也不取
    class FakeSocket {
      static readonly OPEN = 1;
      readyState = 0;
      onopen: (() => void) | null = null;
      onclose: ((ev: unknown) => void) | null = null;
      onerror: (() => void) | null = null;
      onmessage: ((ev: unknown) => void) | null = null;
      constructor(readonly url: string) {
        setTimeout(() => {
          this.readyState = 1;
          this.onopen?.();
        }, 0);
      }
      send(): void {}
      close(): void {}
    }
    vi.stubGlobal('WebSocket', FakeSocket);
    try {
      releaseStatusProvider.loadShip = async (u) => (shipCalls.push(u), parseReleaseStatus(updatedJson));
      const live = async (query: string) => {
        const app = await mountPage('brm-instructor-app', query);
        releaseStatusProvider.loadShip = async (u) => (shipCalls.push(u), parseReleaseStatus(updatedJson));
        const b = app.shadowRoot!.querySelector<ReleaseStatusBadge & Updatable>('header brm-release-status')!;
        await new Promise((r) => setTimeout(r, 20)); // 假的 WebSocket 連上
        await app.updateComplete;
        for (let i = 0; i < 6; i++) await Promise.resolve();
        await b.updateComplete;
        return b;
      };
      const remote = await live('/?ws=ws://192.168.77.11:8765&vhf=0');
      expect(remote.shipLinked).toBe(true);
      expect(shipCalls).toEqual(['http://192.168.77.11:8080']);
      // 本機是降版夾具(mountPage 的 load)、船上是 2026.12.2:頁首是「船上 … · 本機降版」
      expect(remote.shadowRoot!.querySelector('button.badge')!.textContent).toBe('船上 2026.12.2 · 本機降版');
      document.body.innerHTML = '';
      const same = await live('/?ws=ws://127.0.0.1:8765&vhf=0');
      expect(same.shipLinked).toBe(true);
      expect(same.shipWebUrl).toBeNull();
      expect(shipCalls).toEqual(['http://192.168.77.11:8080']); // 沒有多一次
      expect(same.shadowRoot!.querySelector('button.downgrade')!.textContent).toBe('發行版 2026.11.1 · 降版提醒');
    } finally {
      vi.unstubAllGlobals();
    }
  }, 30_000);

  it('完整教官站把執行控制第一個鈕(凍結)的上緣交給頁首元件:展開的說明不蓋住「凍結 / 恢復 / 重設 / 結束練習」;簡化模式沒有執行控制:不限制', async () => {
    const realRect = Element.prototype.getBoundingClientRect;
    // 測試環境沒有版面:讓「凍結」鈕在 y = 345、高 30(1093×614 實測的位置),說明的上緣在 81
    Element.prototype.getBoundingClientRect = function (this: Element): DOMRect {
      const freeze = this.matches('button.freeze');
      const top = freeze ? 345 : this.classList.contains('detail') ? 81 : 0;
      const height = freeze ? 30 : 0;
      return { top, bottom: top + height, left: 0, right: 0, width: 0, height, x: 0, y: top, toJSON: () => ({}) } as DOMRect;
    };
    try {
      const full = await mountPage('brm-instructor-app', '/?demo=1');
      const run = full.shadowRoot!.querySelector<Updatable>('brm-run-control')!;
      await run.updateComplete;
      expect(run.shadowRoot!.querySelector('button')!.className).toContain('freeze'); // 第一個鈕就是「凍結」
      const badge = full.shadowRoot!.querySelector<ReleaseStatusBadge & Updatable>('header brm-release-status')!;
      await badge.updateComplete;
      expect(badge.detailLimit?.()).toBe(345);
      badge.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!.click();
      await badge.updateComplete;
      expect(badge.shadowRoot!.querySelector<HTMLElement>('.detail')!.style.maxHeight).toBe(`${345 - 81 - DETAIL_LIMIT_GAP_PX}px`);
      document.body.innerHTML = '';
      const simple = await mountPage('brm-instructor-app', '/?demo=1&mode=simple');
      expect(simple.shadowRoot!.querySelector('brm-run-control')).toBeNull();
      const simpleBadge = simple.shadowRoot!.querySelector<ReleaseStatusBadge & Updatable>('header brm-release-status')!;
      await simpleBadge.updateComplete;
      expect(simpleBadge.detailLimit?.()).toBeNull();
      simpleBadge.shadowRoot!.querySelector<HTMLButtonElement>('button.downgrade')!.click();
      await simpleBadge.updateComplete;
      expect(simpleBadge.shadowRoot!.querySelector<HTMLElement>('.detail')!.style.maxHeight).toBe('');
    } finally {
      Element.prototype.getBoundingClientRect = realRect;
    }
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
