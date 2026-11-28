// conning 條的端對端案例共用的合成數值(specs/01-bridge、02-instructor;第三十一輪,docs/design/web-layout.md 第 2.5 節)。
// 以 support/layout.ts 的 overrideConning 蓋在頁面目前的狀態上;選填欄位(route / visibility_nm、waves / waveMotion / shipInteraction、roll)決定格數。
// 檔案前半是單列(視窗寬於 1900 px)的案例用的(含比平常多一位數的值);檔尾 CONNING_TWO_ROW_* 是兩列(視窗 ≤ 1900 px)的案例用的,
// 只含「平常會出現」的位數與狀態 —— 兩列的方塊只有約 107 至 135 px 高,這些值一定要放得下。

/** 把選填欄位全部拿掉(10 格) */
export const CONNING_NONE = { route: null, visibility_nm: null, waves: null, waveMotion: null, shipInteraction: null, roll: null };

/** 航線:偏移五位數並超過限值、情境庫最長的航點名稱(8 個字元)、距離三位數 */
export const CONNING_ROUTE = { nextWaypoint: 'ENTRANCE', xtd_m: 12345, xtdLimit_m: 1500, distanceToWaypoint_nm: 123.45, bearingToWaypoint_deg: 359, legCourse_deg: 359, speedLimit_kn: 12.5, complete: false };

/** 航線:平常的值(偏移四位數、左偏) */
export const CONNING_ROUTE_USUAL = { nextWaypoint: 'W2', xtd_m: -1568, xtdLimit_m: 1500, distanceToWaypoint_nm: 4.76, bearingToWaypoint_deg: 15, legCourse_deg: 22, complete: false };

/** 浪、浪一階運動、船間交互作用(超限) */
export const CONNING_SEA = {
  waves: { significantHeight_m: 12.5, peakPeriod_s: 14.5, direction_deg: 359, wavelength_m: 300, spectrum: 'jonswap' },
  waveMotion: { heave_m: -3.25, pitch_deg: -12.5, heaveRate_mps: 0, pitchRate_dps: 0 },
  shipInteraction: { active: true, targetId: 'ANCH1', forceSway_t: -123.4, momentYaw_tm: -12345, event: true },
};

/** 橫搖:超過警報門檻(25°) */
export const CONNING_ROLL = { angle_deg: -35.5, rate_degps: -10.5, naturalPeriod_s: 12.5, staticHeel_deg: 12.5 };

/**
 * 最寬的:航向 359.9、ROT 三位數、倒俥兩位數與漂角三位數、滿舵 70°、緊急全速倒車 -150 與主機故障、艏側推 P 100% 不可用、
 * UKC 三位數與潮高、風 359° 99.9 kn、流場來源最長的標示、超過 99 小時並凍結、南緯西經、各感測器無效
 */
export const CONNING_WIDEST = {
  heading: 359.9, cog: 359.4, rot: -135.5, stw: -12.5, sog: 14.5, drift: -179.9, rudder: -70, rudderOrder: 70,
  rpm: -150, rpmOrder: -150, telegraph: 'EFAS', engine: { state: 'failed', load_pct: 100 }, thruster: { order: -1, actual: -1 }, faults: ['bowThruster'],
  depthBelowKeel: 994.3, waterDepth: 1000, tideHeight_m: 1.94, squat: 1.23, draft: { fore: 6.9, aft: 7.1 },
  wind: { trueDir: 359, trueSpeed: 99.9, relDir: 359, relSpeed: 99.9 }, current: { set: 359, drift: 9.9 }, currentSource: 'field+table',
  t: 359999, tick: 17999950, pos: { lat: -89.99998, lon: -179.99998, x: 0, y: 0 }, flags: { frozen: true, aground: false, collision: false },
  sensors: { headingValid: false, positionValid: false, depthValid: false, stwValid: false },
};

/** 平常會出現的位數裡最寬的(主要數值的字級以這一組斷言):ROT 兩位數、船速兩位數、半速倒車 -100、UKC 兩位數並擱淺 */
export const CONNING_USUAL = {
  heading: 288.8, cog: 288.4, rot: 99.9, stw: 14.5, sog: 14.5, drift: -12.5, rudder: 35, rudderOrder: -35,
  rpm: -100, rpmOrder: -135, telegraph: 'FAS', engine: { state: 'running', load_pct: 100 }, thruster: { order: 1, actual: 1 }, faults: [],
  depthBelowKeel: 19.2, waterDepth: 25, tideHeight_m: -0.34, squat: 0.45, draft: { fore: 5.7, aft: 5.7 },
  wind: { trueDir: 40, trueSpeed: 12, relDir: 327, relSpeed: 22.1 }, current: { set: 200, drift: 1.5 }, currentSource: 'tidal',
  t: 3723, tick: 186150, pos: { lat: 23.80006, lon: 120.3379, x: 0, y: 0 }, flags: { frozen: false, aground: true, collision: false },
  sensors: { headingValid: true, positionValid: true, depthValid: true, stwValid: true },
};

/**
 * 航行中平常的值(E20 出港時的位數;視窗較矮時的高度以這一組斷言):主機 HAH 運轉中、沒有狀態標示、
 * 航點名稱三個字元、目標船代號兩個字元
 */
export const CONNING_UNDERWAY = {
  heading: 74.3, cog: 68.6, rot: 2.4, stw: 7.5, sog: 7.2, drift: -0.2, rudder: -5, rudderOrder: -5,
  rpm: 100, rpmOrder: 100, telegraph: 'HAH', engine: { state: 'running', load_pct: 62 }, thruster: { order: 0, actual: 0 }, faults: [],
  depthBelowKeel: 19.2, waterDepth: 25, tideHeight_m: 0.8, squat: 0.04, draft: { fore: 5.69, aft: 5.71 },
  wind: { trueDir: 30, trueSpeed: 18, relDir: 327, relSpeed: 24 }, current: { set: 315, drift: 0.8 }, currentSource: 'tidal',
  t: 754, tick: 37700, pos: { lat: 22.5275, lon: 120.2384, x: 0, y: 0 }, flags: { frozen: false, aground: false, collision: false },
  sensors: { headingValid: true, positionValid: true, depthValid: true, stwValid: true },
  route: { nextWaypoint: 'FW1', xtd_m: -38, xtdLimit_m: 450, distanceToWaypoint_nm: 0.98, bearingToWaypoint_deg: 70, legCourse_deg: 70, speedLimit_kn: 8, complete: false },
  visibility_nm: 8,
  waves: { significantHeight_m: 2.5, peakPeriod_s: 8, direction_deg: 225, wavelength_m: 100, spectrum: 'jonswap' },
  waveMotion: { heave_m: -0.35, pitch_deg: 1.2, heaveRate_mps: 0, pitchRate_dps: 0 },
  shipInteraction: { active: true, targetId: 'T3', forceSway_t: -3.4, momentYaw_tm: -120, event: false },
  roll: { angle_deg: -3.5, rate_degps: 0.8, naturalPeriod_s: 9.6, staticHeel_deg: 1.2 },
};

/**
 * 兩列:各種狀態同時出現(都是平常會出現的)—— 電羅經 / 計程儀 / GNSS / 測深儀無效、凍結、擱淺(UKC 負值)、主機故障並緊急全速倒車、
 * 艏側推不可用且滿推力、最長的流場來源標示(流場網格·流場表)、模擬時間 99:59:59、南緯與西經 179°59.999'W、船速兩位數
 */
export const CONNING_TWO_ROW_STATUSES = {
  heading: 288.8, cog: 288.4, rot: 12.5, stw: 12.5, sog: 12.8, drift: 12.5, rudder: 35, rudderOrder: -35,
  rpm: -100, rpmOrder: -150, telegraph: 'EFAS', engine: { state: 'failed', load_pct: 100 }, thruster: { order: 1, actual: 1 }, faults: ['bowThruster', 'mainEngine'],
  depthBelowKeel: -0.3, waterDepth: 6.1, tideHeight_m: -0.34, squat: 0.45, draft: { fore: 5.7, aft: 6.1 },
  wind: { trueDir: 40, trueSpeed: 12, relDir: 327, relSpeed: 22.1 }, current: { set: 200, drift: 1.5 }, currentSource: 'field+table',
  t: 359999, tick: 17999950, pos: { lat: -89.99998, lon: -179.99998, x: 0, y: 0 }, flags: { frozen: true, aground: true, collision: true },
  sensors: { headingValid: false, positionValid: false, depthValid: false, stwValid: false },
};

/**
 * 兩列:倒俥中(漂角接近 ±180°)、滿舵 70°、緊急全速倒車 -150 並「啟動中 100%」、艏側推 P 100%、迴轉率兩位數、UKC 兩位數的上限、
 * 風 99.9 kn、流 9.9 kn 與「流場網格·潮流」、凍結(GNSS 有效)、西經
 */
export const CONNING_TWO_ROW_ASTERN = {
  heading: 359.9, cog: 179.4, rot: -99.9, stw: -5.5, sog: 5.4, drift: -179.9, rudder: -70, rudderOrder: 70,
  rpm: -150, rpmOrder: -150, telegraph: 'EFAS', engine: { state: 'starting', load_pct: 100 }, thruster: { order: -1, actual: -1 }, faults: [],
  depthBelowKeel: 99.9, waterDepth: 105.6, tideHeight_m: -1.94, squat: 1.23, draft: { fore: 10.9, aft: 11.1 },
  wind: { trueDir: 359, trueSpeed: 99.9, relDir: 359, relSpeed: 99.9 }, current: { set: 359, drift: 9.9 }, currentSource: 'field+tidal',
  t: 86399, tick: 4319950, pos: { lat: 25.153, lon: -179.99998, x: 0, y: 0 }, flags: { frozen: true, aground: false, collision: false },
  sensors: { headingValid: true, positionValid: true, depthValid: true, stwValid: true },
};

/** 兩列:航線偏移四位數並超過限值(L 偏左)、情境庫最長的航點名稱(8 個字元)、距離兩位數、有速限 */
export const CONNING_TWO_ROW_ROUTE = { nextWaypoint: 'APPROACH', xtd_m: -1568, xtdLimit_m: 1500, distanceToWaypoint_nm: 14.76, bearingToWaypoint_deg: 359, legCourse_deg: 359, speedLimit_kn: 12.5, complete: false };

/** 兩列:浪(JONSWAP、兩位數的 Hs / Tp)、浪一階運動、船間交互作用(情境庫最長的目標船代號 5 個字元、超限、力矩四位數) */
export const CONNING_TWO_ROW_SEA = {
  waves: { significantHeight_m: 12.5, peakPeriod_s: 14.5, direction_deg: 359, wavelength_m: 300, spectrum: 'jonswap' },
  waveMotion: { heave_m: -3.25, pitch_deg: -12.5, heaveRate_mps: 0, pitchRate_dps: 0 },
  shipInteraction: { active: true, targetId: 'ANCH1', forceSway_t: -123.4, momentYaw_tm: -1234, event: true },
};
