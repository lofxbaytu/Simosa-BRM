// 發行版狀態(教官站頁首的「發行版 <版本>」與降版提醒;docs/design/packaging-sync.md 第 5.1 節)。
//
// 資料路徑:啟動腳本(tools/launch/start-*-laptop.*)在啟動網頁伺服前執行一次 `simosa-pkg status --json`,存成
// <安裝根目錄>/build/run/release-status.json;靜態網頁伺服 simosa-web(--status-file)以同源的固定網址 /_status/release.json
// 提供其中「網頁需要的欄位」(格式 simosa-release-status/1:發行版本、軟體更新包版次、降版資訊、狀態檔的時間)。
// 開發模式(沒有 manifest.json 的儲存庫)沒有這個檔:simosa-web 回 200 與各欄 null(「沒有發行版狀態」;不回 404 —— 教官站每次載入都取這個網址,
// 404 會讓瀏覽器每次都記一筆 console error,網頁端消不掉)。取不到(其他伺服器的 404、不是 JSON、格式不符、沒有發行版本)一律當成沒有,畫面什麼都不顯示。
// 同源取到的是**提供這個頁面的那台電腦**(本機)的發行版:船上筆電自己開的簡化教官站是船上筆電的;教官筆電以 start-instructor-laptop 開的
// 教官站由教官筆電自己的 simosa-web 提供(核心的 WebSocket 才指向船上),所以是教官筆電的。
//
// 船上筆電的發行版(第三十一輪):頁面由教官筆電自己的 simosa-web 提供(http://127.0.0.1:<埠>)而核心在另一台電腦時,另外**跨來源**向船上筆電的
// simosa-web 取同一個網址(http://<船上 IP>:8080/_status/release.json;shipWebBaseUrl 決定位址,browserShipReleaseStatus 取)。船上的 simosa-web
// 只對本機迴路位址的頁面回 CORS 標頭(tools/launch/StaticWeb/Program.cs)。請求是簡單請求(GET、沒有自訂標頭、不帶認證),瀏覽器不送 preflight。
// 取不到(船上是沒有這個網址或沒有 CORS 標頭的舊版、網頁伺服還沒啟動、逾時、格式不符)一律當成沒有:畫面只顯示本機的,不顯示錯誤
// (瀏覽器主控台會有跨來源請求失敗的紀錄,網頁端消不掉)。船上連得到、格式也對,但各欄是 null(船上是開發模式,或船上啟動時 simosa-pkg status
// 沒有取到)是另一回事(SHIP_NO_STATUS):畫面一樣只顯示本機的,但不必重試,說明寫「船上筆電沒有發行版狀態」而不是「未取得」。
// 頁面與核心在同一台電腦(船上筆電自己開的教官站、教官筆電的 standalone、開發模式)時不發這個請求。

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

/** who:這是哪一台的降版(預設「這台電腦」;船上筆電的寫「船上筆電」) */
export function downgradeSummary(d: ReleaseDowngradeInfo, who = '這台電腦'): DowngradeSummary {
  const newerThanRelease = (v: string | null): v is string => v !== null && compareVersions(v, d.releaseVersion) > 0;
  const parts: string[] = [];
  const previous = newerThanRelease(d.previousReleaseVersion) ? d.previousReleaseVersion : null;
  const software = newerThanRelease(d.softwareVersion) ? d.softwareVersion : null;
  if (previous) parts.push(`先前安裝的發行版 ${previous}`);
  if (software) parts.push(`曾套用的軟體更新包 ${software}`);
  if (newerThanRelease(d.highestVersion) && d.highestVersion !== software && d.highestVersion !== previous) parts.push(`被安裝覆蓋的軟體更新包最高版本 ${d.highestVersion}`);
  return {
    running: `${who}目前執行的是較舊的發行版 ${d.releaseVersion}`,
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

/** 取狀態的 JSON;任何失敗(連不上、被 CORS 擋下、404、不是 JSON、逾時)都回 undefined,不丟例外 */
async function fetchStatusJson(fetchImpl: StatusFetch, url: string, timeoutMs: number): Promise<unknown> {
  try {
    const signal = typeof AbortSignal !== 'undefined' && typeof AbortSignal.timeout === 'function' ? AbortSignal.timeout(timeoutMs) : undefined;
    const res = await fetchImpl(url, { cache: 'no-store', credentials: 'omit', ...(signal ? { signal } : {}) });
    if (!res.ok) return undefined;
    const data: unknown = await res.json();
    return data;
  } catch {
    return undefined;
  }
}

/** 取同源的發行版狀態;任何失敗(連不上、404、不是 JSON、格式不符、逾時)都回 null,不丟例外、不在畫面上顯示錯誤 */
export async function fetchReleaseStatus(fetchImpl: StatusFetch, url: string = RELEASE_STATUS_URL, timeoutMs = 5000): Promise<ReleaseStatus | null> {
  return parseReleaseStatus(await fetchStatusJson(fetchImpl, url, timeoutMs));
}

/** 瀏覽器實作:向同源的 simosa-web 取狀態(file:// 等不是 http(s) 的頁面沒有同源的伺服器,不發請求) */
export function browserReleaseStatus(): Promise<ReleaseStatus | null> {
  if (typeof fetch !== 'function' || typeof location === 'undefined' || !/^https?:$/.test(location.protocol)) return Promise.resolve(null);
  return fetchReleaseStatus((url, init) => fetch(url, init));
}

// ------------------------------------------------------------------ 船上筆電的發行版狀態(第三十一輪)

/** 船上筆電 simosa-web 的預設埠(config/ship-laptop.json 的 ports.web) */
export const DEFAULT_SHIP_WEB_PORT = 8080;
/** 教官站網址的查詢參數:船上筆電 simosa-web 的位址(例:shipWeb=http://192.168.77.11:8081);0 / off = 不取船上的發行版狀態 */
export const SHIP_WEB_PARAM = 'shipWeb';

/** 本機迴路位址的主機名(URL 的 hostname:IPv6 帶方括號) */
export function isLoopbackHost(hostname: string): boolean {
  const h = hostname.toLowerCase();
  return h === 'localhost' || h === '[::1]' || h === '::1' || /^127(\.\d{1,3}){3}$/.test(h);
}

/** 頁面位址的最小介面(瀏覽器的 location 符合) */
export interface PageLocation {
  protocol: string;
  hostname: string;
  origin: string;
}

/**
 * 船上筆電 simosa-web 的位址(scheme://主機:埠,沒有結尾斜線);null = 不向船上取發行版狀態(這個頁面顯示的「本機」就是船上 / 唯一的一台)。
 *  1. 頁面不是 http、或不是由本機迴路位址提供(例:教官筆電的瀏覽器直接開船上筆電的 http://<船上 IP>:8080/instructor.html,
 *     那時同源取到的就是船上的)→ null。船上的 simosa-web 也只對本機迴路位址的頁面回 CORS 標頭,其他頁面發了請求也讀不到。
 *  2. 明確的 ?shipWeb=:0 / off → null;http 網址(可以只寫 主機[:埠],沒寫埠是 80)→ 它的來源(與頁面同一個來源時 null);
 *     只寫埠號(8081 或 :8081;設定檔的 ports.shipWeb 就是一個埠號,手動在網址補參數時容易照著寫)→ 第 3 點的主機配這個埠;
 *     不是 http、帶帳號密碼、主機只有數字(URL 會把「8081」解析成 IP 位址 0.0.31.145)、埠號不在 1–65535 或無法解析的值當成沒給。
 *  3. 沒給:由核心的 WebSocket 位址(?ws=)的主機推得 http://<主機>:8080 — 既有的 config\instructor-laptop.json(openPages 只有 ws 參數)不必改。
 *     ws 的主機是本機迴路位址(教官筆電的 standalone、開發模式;核心就在這台電腦)或無法解析 → null。
 */
export function shipWebBaseUrl(page: PageLocation, wsUrl: string | null | undefined, explicit?: string | null): string | null {
  if (page.protocol !== 'http:' || !isLoopbackHost(page.hostname)) return null;
  const given = (explicit ?? '').trim();
  if (given === '0' || given.toLowerCase() === 'off') return null;
  let port = DEFAULT_SHIP_WEB_PORT;
  const portOnly = /^:?(\d+)$/.exec(given);
  if (portOnly) {
    const n = Number(portOnly[1]);
    if (n >= 1 && n <= 65535) port = n;
  } else if (given && !/^(?:[a-z][a-z0-9+.-]*:\/\/)?\d+(?:[:/?#]|$)/i.test(given)) {
    try {
      const u = new URL(/^[a-z][a-z0-9+.-]*:\/\//i.test(given) ? given : `http://${given}`);
      if (u.protocol === 'http:' && u.hostname && !u.username && !u.password) return u.origin === page.origin ? null : u.origin;
    } catch {
      /* 無法解析:當成沒給 */
    }
  }
  try {
    const host = new URL(wsUrl ?? '').hostname;
    if (!host || isLoopbackHost(host)) return null;
    return `http://${host}:${port}`;
  } catch {
    return null;
  }
}

/** 實際執行的版本:套用過比發行版新的軟體更新包(沒有降版)時是更新包的版次,否則是發行版(降版時是目前較舊的發行版) */
export function runningVersion(st: ReleaseStatus): string {
  return st.softwareUpdateVersion ?? st.releaseVersion;
}

/** 「軟體更新 <版次>」或「發行版 <版本>」(實際執行的那一版;與只有本機時頁首的寫法相同) */
export function runningLabel(st: ReleaseStatus): string {
  return st.softwareUpdateVersion ? `軟體更新 ${st.softwareUpdateVersion}` : `發行版 ${st.releaseVersion}`;
}

/** 實際執行的那一版的完整說法(title 與展開的說明用):「軟體更新包 Y 的程式(套用在發行版 X 之上)」或「發行版 X」 */
export function runningDescription(st: ReleaseStatus): string {
  return st.softwareUpdateVersion ? `軟體更新包 ${st.softwareUpdateVersion} 的程式(套用在發行版 ${st.releaseVersion} 之上)` : `發行版 ${st.releaseVersion}`;
}

/** 船上筆電與本機(提供頁面的電腦)的比較;頁首徽章與展開的說明共用 */
export interface FleetComparison {
  /** 船上筆電有降版提醒 */
  shipDowngrade: boolean;
  /** 本機有降版提醒 */
  localDowngrade: boolean;
  /** 兩台實際執行的版本不同(各取 runningVersion 比較;本機沒有發行版狀態時無從比較 → false) */
  mismatch: boolean;
  /** 版本不同時較舊的那一台;相同或無從比較時 null */
  older: 'ship' | 'local' | null;
  /** 頁首徽章的標記(最需要處理的一項):兩台降版 > 船上降版 > 本機降版 > 版本不同;都沒有時 null(頁首只寫船上的版本,不是徽章) */
  tag: '兩台降版' | '船上降版' | '本機降版' | '版本不同' | null;
}

export function compareFleet(ship: ReleaseStatus, local: ReleaseStatus | null): FleetComparison {
  const shipDowngrade = ship.downgrade !== null;
  const localDowngrade = local?.downgrade != null;
  const cmp = local ? compareVersions(runningVersion(ship), runningVersion(local)) : 0;
  const older = cmp < 0 ? 'ship' : cmp > 0 ? 'local' : null;
  const tag = shipDowngrade && localDowngrade ? '兩台降版' : shipDowngrade ? '船上降版' : localDowngrade ? '本機降版' : older ? '版本不同' : null;
  return { shipDowngrade, localDowngrade, mismatch: older !== null, older, tag };
}

/**
 * 版本不同時的說明(哪一台較舊)與建議的處理;這是提醒,不擋操作。
 * 較舊的那一台同時有降版提醒時,處理指向它的降版提醒(刻意回到舊版時不必處理):不可以一段說「不必處理」、下一段又叫人「更新較舊的那一台」。
 */
export function mismatchSummary(ship: ReleaseStatus, local: ReleaseStatus): { what: string; advice: string } | null {
  const c = compareFleet(ship, local);
  if (!c.older) return null;
  const olderDowngraded = c.older === 'ship' ? c.shipDowngrade : c.localDowngrade;
  const sync = '兩台之間的同步步驟見 docs/design/packaging-sync.md 第 6 節';
  return {
    what: `船上筆電執行的是${runningLabel(ship)},這台電腦(本機)執行的是${runningLabel(local)} — ${c.older === 'ship' ? '船上筆電' : '這台電腦(本機)'}較舊`,
    advice: olderDowngraded
      ? `較舊的是有降版提醒的那一台(${c.older === 'ship' ? '船上筆電' : '這台電腦'}),依上面降版提醒的處理;刻意回到舊版時兩台版本不同是預期的,需要兩台同一版就把另一台也換成那一版(${sync})。這是提醒,不擋操作`
      : `更新較舊的那一台(${c.older === 'ship' ? '船上筆電' : '這台電腦'}):以較新的安裝程式重新安裝,或套用軟體更新包(${sync})。這是提醒,不擋操作`,
  };
}

/** 船上筆電發行版狀態的網址(跨來源;位址來自 shipWebBaseUrl) */
export function shipReleaseStatusUrl(shipWebBase: string): string {
  return `${shipWebBase.replace(/\/+$/, '')}${RELEASE_STATUS_URL}`;
}

/**
 * 船上筆電連得到、回應的格式也對,但沒有發行版狀態(各欄 null):船上從儲存庫啟動(開發模式),或船上啟動時 simosa-pkg status 失敗 / 逾時
 * (啟動腳本只警告;狀態檔在船上下次啟動前不會出現)。與「取不到」(null)分開:不必重試,說明也不寫「船上是較舊的版本或網頁伺服沒有啟動」。
 */
export const SHIP_NO_STATUS = 'none';
/** 向船上筆電取發行版狀態的結果:狀態、SHIP_NO_STATUS(連得到但沒有發行版狀態)、null(取不到) */
export type ShipStatusResult = ReleaseStatus | typeof SHIP_NO_STATUS | null;

/** 取船上筆電的發行版狀態:與 fetchReleaseStatus 相同,另外分出「格式相符但沒有發行版本」(SHIP_NO_STATUS) */
export async function fetchShipReleaseStatus(fetchImpl: StatusFetch, url: string, timeoutMs = 5000): Promise<ShipStatusResult> {
  const data = await fetchStatusJson(fetchImpl, url, timeoutMs);
  const status = parseReleaseStatus(data);
  if (status) return status;
  const formatOk = typeof data === 'object' && data !== null && !Array.isArray(data) && (data as Record<string, unknown>)['format'] === RELEASE_STATUS_FORMAT;
  return formatOk ? SHIP_NO_STATUS : null;
}

/** 瀏覽器實作:跨來源向船上筆電的 simosa-web 取狀態(簡單請求,不送 preflight;取不到回 null,連得到但沒有發行版狀態回 SHIP_NO_STATUS) */
export function browserShipReleaseStatus(shipWebBase: string): Promise<ShipStatusResult> {
  if (typeof fetch !== 'function' || !shipWebBase) return Promise.resolve(null);
  return fetchShipReleaseStatus((url, init) => fetch(url, init), shipReleaseStatusUrl(shipWebBase));
}

/**
 * 元件以此取得狀態。預設不發請求(回 null):只有教官站的進入點 instructor-main.ts 經 lib/release-status-enable.ts 換成
 * browserReleaseStatus / browserShipReleaseStatus — 單元測試掛上教官站元件時不會對測試環境的位址發出真的 HTTP 請求;測試可換成假的實作。
 * load:本機(同源);loadShip:船上筆電(跨來源;參數是 shipWebBaseUrl 的結果)。
 */
export const releaseStatusProvider: { load: () => Promise<ReleaseStatus | null>; loadShip: (shipWebBase: string) => Promise<ShipStatusResult> } = {
  load: () => Promise.resolve(null),
  loadShip: () => Promise.resolve(null),
};
