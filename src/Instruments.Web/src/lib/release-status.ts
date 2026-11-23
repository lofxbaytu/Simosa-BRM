// 發行版狀態(教官站頁首的「發行版 <版本>」與降版提醒;docs/design/packaging-sync.md 第 5.1 節)。
//
// 資料路徑:啟動腳本(tools/launch/start-*-laptop.*)在啟動網頁伺服前執行一次 `simosa-pkg status --json`,存成
// <安裝根目錄>/build/run/release-status.json;靜態網頁伺服 simosa-web(--status-file)以同源的固定網址 /_status/release.json
// 提供其中「網頁需要的欄位」(格式 simosa-release-status/1:發行版本、軟體更新包版次、降版資訊、狀態檔的時間)。
// 開發模式(沒有 manifest.json 的儲存庫)沒有這個檔:simosa-web 回 200 與各欄 null(「沒有發行版狀態」;不回 404 —— 教官站每次載入都取這個網址,
// 404 會讓瀏覽器每次都記一筆 console error,網頁端消不掉)。取不到(其他伺服器的 404、不是 JSON、格式不符、沒有發行版本)一律當成沒有,畫面什麼都不顯示。
// 顯示的是**提供這個頁面的那台電腦**的發行版:船上筆電自己開的簡化教官站是船上筆電的;教官筆電以 start-instructor-laptop 開的
// 教官站由教官筆電自己的 simosa-web 提供(核心的 WebSocket 才指向船上),所以是教官筆電的。

/** simosa-web 提供發行版狀態的固定網址(同源;與 tools/launch/StaticWeb/Program.cs 的 ReleaseStatusFile.Url 相同) */
export const RELEASE_STATUS_URL = '/_status/release.json';
export const RELEASE_STATUS_FORMAT = 'simosa-release-status/1';

/** 降版(simosa-pkg status 的 releaseDowngrade):套用更新包之後以較舊的安裝程式重新安裝過,status 顯示的軟體版次不是實際執行的程式 */
export interface ReleaseDowngradeInfo {
  /** 目前(實際執行)的發行版 */
  releaseVersion: string;
  /** 比較基準:曾安裝或套用過、比目前發行版新的版本中最大者(「版本不低於它的安裝程式」才回到降版之前) */
  basisVersion: string;
  /** 重新安裝之前的發行版(沒有時 null;不一定比目前的新) */
  previousReleaseVersion: string | null;
  /** 重新安裝當時的軟體更新包版次(status 顯示的 software 版次;沒有時 null) */
  softwareVersion: string | null;
  /** 被重新安裝覆蓋的軟體更新包最高版本(沒有時 null) */
  highestVersion: string | null;
  /** status 顯示的軟體版次比目前的發行版新:那個版次不是實際執行的程式 */
  softwareStale: boolean;
  /** status 的完整說明(與 verify-install 的「狀態」提醒同一段文字) */
  message: string;
}

export interface ReleaseStatus {
  /** 安裝根目錄 manifest.json 的發行版本(安裝程式的版本;套用軟體更新包不會改它) */
  releaseVersion: string;
  /**
   * 套用在這個發行版之上的軟體更新包版次(status 的 components.software.version):**沒有降版而且比發行版新**時才有 ——
   * 這時實際執行的是這個更新包的程式,頁首改寫「軟體更新 <版次>」(只寫發行版的話,船上靠更新包升級之後數字永遠停在安裝程式的版本,看不出更新有沒有套用)。
   * 其餘情況 null:沒套用過、比發行版舊或相同(之後以更新的安裝程式重新安裝過,那個更新包已被覆蓋)、或有降版(由降版提醒說明)。
   */
  softwareUpdateVersion: string | null;
  /** 狀態檔的時間(啟動時取得;ISO UTC;沒有或格式不對時 null) */
  generatedUtc: string | null;
  downgrade: ReleaseDowngradeInfo | null;
}

/** 版本:1 至 4 段數字(例:2026.11.30;與 Tools.Packaging 的 PackageVersion 相同) */
export function isVersion(v: unknown): v is string {
  return typeof v === 'string' && /^\d{1,9}(\.\d{1,9}){0,3}$/.test(v);
}

/** 逐段比較,缺的段當 0(2026.11 = 2026.11.0);a 較新回傳正數。兩者都必須是 isVersion 的字串 */
export function compareVersions(a: string, b: string): number {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

const versionOrNull = (v: unknown): string | null => (isVersion(v) ? v : null);

/** /_status/release.json 的內容 → ReleaseStatus;格式不符或沒有發行版本時 null(畫面不顯示) */
export function parseReleaseStatus(data: unknown): ReleaseStatus | null {
  if (typeof data !== 'object' || data === null || Array.isArray(data)) return null;
  const o = data as Record<string, unknown>;
  if (o['format'] !== RELEASE_STATUS_FORMAT) return null;
  const d = o['releaseDowngrade'];
  let downgrade: ReleaseDowngradeInfo | null = null;
  if (typeof d === 'object' && d !== null && !Array.isArray(d)) {
    const g = d as Record<string, unknown>;
    // 降版資訊至少要有目前的發行版與比較基準,而且基準確實比較新;否則當成沒有降版(不顯示半套的提醒)
    if (isVersion(g['releaseVersion']) && isVersion(g['basisVersion']) && compareVersions(g['basisVersion'], g['releaseVersion']) > 0) {
      downgrade = {
        releaseVersion: g['releaseVersion'],
        basisVersion: g['basisVersion'],
        previousReleaseVersion: versionOrNull(g['previousReleaseVersion']),
        softwareVersion: versionOrNull(g['softwareVersion']),
        highestVersion: versionOrNull(g['highestVersion']),
        softwareStale: g['softwareStale'] === true,
        message: typeof g['message'] === 'string' ? g['message'] : '',
      };
    }
  }
  const releaseVersion = versionOrNull(o['releaseVersion']) ?? downgrade?.releaseVersion ?? null;
  if (releaseVersion === null) return null;
  const t = o['generatedUtc'];
  const generatedUtc = typeof t === 'string' && !Number.isNaN(Date.parse(t)) ? t : null;
  const software = versionOrNull(o['softwareVersion']);
  const softwareUpdateVersion = downgrade === null && software !== null && compareVersions(software, releaseVersion) > 0 ? software : null;
  return { releaseVersion, softwareUpdateVersion, generatedUtc, downgrade };
}

/** 降版提醒的重點(徽章的 title 與展開後的說明共用;與 status 的說明同一套規則:只列比目前的發行版新的項目) */
export interface DowngradeSummary {
  /** 「目前執行的是較舊的發行版 X」 */
  running: string;
  /** 比目前的發行版新的各項(例:先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2) */
  newer: string;
  /** status 顯示的軟體版次不是實際執行的程式(softwareStale 時才有) */
  stale: string | null;
  /** 建議的處理 */
  advice: string;
}

export function downgradeSummary(d: ReleaseDowngradeInfo): DowngradeSummary {
  const newerThanRelease = (v: string | null): v is string => v !== null && compareVersions(v, d.releaseVersion) > 0;
  const parts: string[] = [];
  const previous = newerThanRelease(d.previousReleaseVersion) ? d.previousReleaseVersion : null;
  const software = newerThanRelease(d.softwareVersion) ? d.softwareVersion : null;
  if (previous) parts.push(`先前安裝的發行版 ${previous}`);
  if (software) parts.push(`曾套用的軟體更新包 ${software}`);
  if (newerThanRelease(d.highestVersion) && d.highestVersion !== software && d.highestVersion !== previous) parts.push(`被安裝覆蓋的軟體更新包最高版本 ${d.highestVersion}`);
  return {
    running: `這台電腦目前執行的是較舊的發行版 ${d.releaseVersion}`,
    newer: parts.length > 0 ? `${parts.join('、')} 比它新` : `曾安裝或套用過較新的版本 ${d.basisVersion}`,
    stale: d.softwareStale && d.softwareVersion ? `simosa-pkg status 顯示的軟體版次 ${d.softwareVersion} 不是實際執行的程式` : null,
    advice: `刻意回到舊版時不必處理(請訪船船長確認);不是刻意的話,請以版本不低於 ${d.basisVersion} 的安裝程式重新安裝,或請岸上派送版本更高的軟體更新包`,
  };
}

/** ISO UTC → 本地時間 "YYYY-MM-DD HH:MM";無法解析時空字串 */
export function formatStatusTime(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** 回應的最小介面(fetch 的 Response 符合;測試用假的) */
export interface StatusResponse {
  ok: boolean;
  json(): Promise<unknown>;
}
export type StatusFetch = (url: string, init: { cache: 'no-store'; credentials: 'omit'; signal?: AbortSignal }) => Promise<StatusResponse>;

/** 取同源的發行版狀態;任何失敗(連不上、404、不是 JSON、格式不符、逾時)都回 null,不丟例外、不在畫面上顯示錯誤 */
export async function fetchReleaseStatus(fetchImpl: StatusFetch, url: string = RELEASE_STATUS_URL, timeoutMs = 5000): Promise<ReleaseStatus | null> {
  try {
    const signal = typeof AbortSignal !== 'undefined' && typeof AbortSignal.timeout === 'function' ? AbortSignal.timeout(timeoutMs) : undefined;
    const res = await fetchImpl(url, { cache: 'no-store', credentials: 'omit', ...(signal ? { signal } : {}) });
    if (!res.ok) return null;
    return parseReleaseStatus(await res.json());
  } catch {
    return null;
  }
}

/** 瀏覽器實作:向同源的 simosa-web 取狀態(file:// 等不是 http(s) 的頁面沒有同源的伺服器,不發請求) */
export function browserReleaseStatus(): Promise<ReleaseStatus | null> {
  if (typeof fetch !== 'function' || typeof location === 'undefined' || !/^https?:$/.test(location.protocol)) return Promise.resolve(null);
  return fetchReleaseStatus((url, init) => fetch(url, init));
}

/**
 * 元件以此取得狀態。預設不發請求(回 null):只有教官站的進入點 instructor-main.ts 經 lib/release-status-enable.ts 換成
 * browserReleaseStatus — 單元測試掛上教官站元件時不會對測試環境的位址發出真的 HTTP 請求;測試可換成假的實作。
 */
export const releaseStatusProvider: { load: () => Promise<ReleaseStatus | null> } = { load: () => Promise.resolve(null) };
