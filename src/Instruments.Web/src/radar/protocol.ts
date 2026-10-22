// 雷達服務 JSON 協定(src/Contracts/radar.schema.json):伺服器訊息 status / tracks / event / ack 的型別與防禦式解析,
// 客戶端控制指令的型別與序列化。單位:距離 nm(_m 為公尺)、速度 kn、角度度(真方位;rel 前綴為相對艏向)、時間 s(_min 為分)。

export type RadarId = 'x' | 's';

export interface ShadowSector {
  from: number;
  to: number;
  attenuation_dB: number;
}

export interface GuardZone {
  enabled: boolean;
  inner_nm: number;
  outer_nm: number;
  from: number;
  to: number;
  relative: boolean;
}

export interface TrialSetting {
  course: number;
  speed: number;
  delay_min: number;
}

export interface RadarStatus {
  type: 'status';
  radar: RadarId;
  band: 'X' | 'S';
  frequency_GHz: number;
  transmitting: boolean;
  standby: boolean;
  failed: boolean;
  degraded: boolean;
  headingValid: boolean;
  range_nm: number;
  rangeScales: number[];
  pulse: string;
  pulseLength_m: number;
  gain: number;
  sea: number;
  rain: number;
  tune: number;
  autoTune: boolean;
  rpm: number;
  spokes: number;
  cells: number;
  beamwidth_deg: number;
  antennaHeight_m: number;
  antennaFwd_m: number;
  antennaStbd_m: number;
  shadowSectors: ShadowSector[];
  guardZone: GuardZone;
  trial?: TrialSetting;
  cpaLimit_nm: number;
  tcpaLimit_min: number;
  arpaRange_nm: number;
  maxTracks: number;
  seaState: number;
  rainCells: number;
  landPolygons: number;
}

export interface PastPosition {
  t: number;
  x: number;
  y: number;
  lat: number;
  lon: number;
}

export type TrackState = 'acquiring' | 'tracking' | 'lost';

/** AIS 融合(tracks[].ais;radar.schema.json trackAis):候選或已關聯時提供。 */
export interface TrackAis {
  mmsi: number;
  name: string;
  /** 已關聯(連續 3 次通過門檻);false = 候選中或保持期(差異過大) */
  associated: boolean;
  /** ARPA 預測位置與 AIS 報告位置的距離(m) */
  positionOffset_m: number;
}

export interface RadarTrack {
  id: number;
  label: string;
  state: TrackState;
  acquisition: 'manual' | 'auto';
  age_s: number;
  stable: boolean;
  manoeuvring: boolean;
  dangerous: boolean;
  swapRisk: boolean;
  misses: number;
  x: number;
  y: number;
  lat: number;
  lon: number;
  range_nm: number;
  bearing_deg: number;
  relBearing_deg: number;
  course_deg?: number;
  speed_kn?: number;
  relCourse_deg?: number;
  relSpeed_kn?: number;
  cpa_nm?: number;
  tcpa_min?: number;
  bcr_nm?: number;
  bct_min?: number;
  trialCpa_nm?: number;
  trialTcpa_min?: number;
  past: PastPosition[];
  ais?: TrackAis;
}

export interface OwnShipReport {
  x: number;
  y: number;
  lat: number;
  lon: number;
  heading: number;
  cog: number;
  sog: number;
}

export interface RadarTracks {
  type: 'tracks';
  radar: RadarId;
  t: number;
  scan: number;
  transmitting: boolean;
  ownShip: OwnShipReport;
  cpaLimit_nm: number;
  tcpaLimit_min: number;
  trial?: TrialSetting;
  targets: RadarTrack[];
}

export const RADAR_EVENT_KINDS = [
  'acquireStarted',
  'tracking',
  'newTarget',
  'acquireFailed',
  'targetLost',
  'deleted',
  'cancelled',
  'dangerousTarget',
  'radarFailure',
  'radarRestored',
  'radarDegraded',
  'headingLost',
] as const;
export type RadarEventKind = (typeof RADAR_EVENT_KINDS)[number];

export interface RadarEvent {
  type: 'event';
  radar: RadarId;
  event: { kind: RadarEventKind | string; t: number; targetId?: number; message: string };
}

export interface RadarAck {
  type: 'ack';
  radar: RadarId;
  cmd: string;
  ok: boolean;
  error?: string;
  reqId?: unknown;
  id?: number;
  value?: number;
}

export type RadarMessage = RadarStatus | RadarTracks | RadarEvent | RadarAck;

// ---------------------------------------------------------------------------------------------
// 解析(未知欄位忽略;必填欄位缺漏時回傳 null,不丟例外)

type Obj = Record<string, unknown>;

function isObj(v: unknown): v is Obj {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}
function num(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}
function bool(v: unknown, d = false): boolean {
  return typeof v === 'boolean' ? v : d;
}
function str(v: unknown, d = ''): string {
  return typeof v === 'string' ? v : d;
}
function radarId(v: unknown): RadarId {
  return v === 's' || v === 'S' ? 's' : 'x';
}

function parseGuard(v: unknown): GuardZone {
  const o = isObj(v) ? v : {};
  return {
    enabled: bool(o['enabled']),
    inner_nm: num(o['inner_nm']) ?? 0,
    outer_nm: num(o['outer_nm']) ?? 0,
    from: num(o['from']) ?? 0,
    to: num(o['to']) ?? 360,
    relative: bool(o['relative'], true),
  };
}

function parseTrial(v: unknown): TrialSetting | undefined {
  if (!isObj(v)) return undefined;
  const course = num(v['course']);
  const speed = num(v['speed']);
  if (course === undefined || speed === undefined) return undefined;
  return { course, speed, delay_min: num(v['delay_min']) ?? 0 };
}

export function parseStatus(o: Obj): RadarStatus | null {
  const range = num(o['range_nm']);
  if (range === undefined) return null;
  const scales = Array.isArray(o['rangeScales'])
    ? (o['rangeScales'] as unknown[]).map(num).filter((x): x is number => x !== undefined && x > 0)
    : [];
  const shadows = Array.isArray(o['shadowSectors'])
    ? (o['shadowSectors'] as unknown[]).filter(isObj).map((s) => ({
        from: num(s['from']) ?? 0,
        to: num(s['to']) ?? 0,
        attenuation_dB: num(s['attenuation_dB']) ?? 0,
      }))
    : [];
  const st: RadarStatus = {
    type: 'status',
    radar: radarId(o['radar']),
    band: o['band'] === 'S' ? 'S' : 'X',
    frequency_GHz: num(o['frequency_GHz']) ?? 0,
    transmitting: bool(o['transmitting']),
    standby: bool(o['standby']),
    failed: bool(o['failed']),
    degraded: bool(o['degraded']),
    headingValid: bool(o['headingValid'], true),
    range_nm: range,
    rangeScales: scales.length > 0 ? scales : [...STANDARD_RANGES_NM],
    pulse: str(o['pulse'], 'medium'),
    pulseLength_m: num(o['pulseLength_m']) ?? 0,
    gain: num(o['gain']) ?? 0,
    sea: num(o['sea']) ?? 0,
    rain: num(o['rain']) ?? 0,
    tune: num(o['tune']) ?? 50,
    autoTune: bool(o['autoTune'], true),
    rpm: num(o['rpm']) ?? 24,
    spokes: num(o['spokes']) ?? 2048,
    cells: num(o['cells']) ?? 512,
    beamwidth_deg: num(o['beamwidth_deg']) ?? 1,
    antennaHeight_m: num(o['antennaHeight_m']) ?? 0,
    antennaFwd_m: num(o['antennaFwd_m']) ?? 0,
    antennaStbd_m: num(o['antennaStbd_m']) ?? 0,
    shadowSectors: shadows,
    guardZone: parseGuard(o['guardZone']),
    cpaLimit_nm: num(o['cpaLimit_nm']) ?? 1,
    tcpaLimit_min: num(o['tcpaLimit_min']) ?? 12,
    arpaRange_nm: num(o['arpaRange_nm']) ?? 16,
    maxTracks: num(o['maxTracks']) ?? 40,
    seaState: num(o['seaState']) ?? 0,
    rainCells: num(o['rainCells']) ?? 0,
    landPolygons: num(o['landPolygons']) ?? 0,
  };
  const trial = parseTrial(o['trial']);
  if (trial) st.trial = trial;
  return st;
}

const OPTIONAL_TRACK_FIELDS = [
  'course_deg',
  'speed_kn',
  'relCourse_deg',
  'relSpeed_kn',
  'cpa_nm',
  'tcpa_min',
  'bcr_nm',
  'bct_min',
  'trialCpa_nm',
  'trialTcpa_min',
] as const;

export function parseTrack(o: Obj): RadarTrack | null {
  const id = num(o['id']);
  const x = num(o['x']);
  const y = num(o['y']);
  const range = num(o['range_nm']);
  const brg = num(o['bearing_deg']);
  if (id === undefined || x === undefined || y === undefined || range === undefined || brg === undefined) return null;
  const s = o['state'];
  const state: TrackState = s === 'acquiring' || s === 'lost' ? s : 'tracking';
  const tr: RadarTrack = {
    id,
    label: str(o['label'], String(id).padStart(2, '0')),
    state,
    acquisition: o['acquisition'] === 'auto' ? 'auto' : 'manual',
    age_s: num(o['age_s']) ?? 0,
    stable: bool(o['stable']),
    manoeuvring: bool(o['manoeuvring']),
    dangerous: bool(o['dangerous']),
    swapRisk: bool(o['swapRisk']),
    misses: num(o['misses']) ?? 0,
    x,
    y,
    lat: num(o['lat']) ?? 0,
    lon: num(o['lon']) ?? 0,
    range_nm: range,
    bearing_deg: brg,
    relBearing_deg: num(o['relBearing_deg']) ?? 0,
    past: Array.isArray(o['past'])
      ? (o['past'] as unknown[]).filter(isObj).flatMap((p) => {
          const px = num(p['x']);
          const py = num(p['y']);
          return px === undefined || py === undefined
            ? []
            : [{ t: num(p['t']) ?? 0, x: px, y: py, lat: num(p['lat']) ?? 0, lon: num(p['lon']) ?? 0 }];
        })
      : [],
  };
  for (const k of OPTIONAL_TRACK_FIELDS) {
    const v = num(o[k]);
    if (v !== undefined) tr[k] = v;
  }
  const ais = o['ais'];
  if (isObj(ais)) {
    const mmsi = num(ais['mmsi']);
    if (mmsi !== undefined) tr.ais = { mmsi, name: str(ais['name']), associated: bool(ais['associated']), positionOffset_m: num(ais['positionOffset_m']) ?? 0 };
  }
  return tr;
}

export function parseTracks(o: Obj): RadarTracks | null {
  const own = o['ownShip'];
  const t = num(o['t']);
  if (!isObj(own) || t === undefined || !Array.isArray(o['targets'])) return null;
  const tracks: RadarTracks = {
    type: 'tracks',
    radar: radarId(o['radar']),
    t,
    scan: num(o['scan']) ?? 0,
    transmitting: bool(o['transmitting']),
    ownShip: {
      x: num(own['x']) ?? 0,
      y: num(own['y']) ?? 0,
      lat: num(own['lat']) ?? 0,
      lon: num(own['lon']) ?? 0,
      heading: num(own['heading']) ?? 0,
      cog: num(own['cog']) ?? 0,
      sog: num(own['sog']) ?? 0,
    },
    cpaLimit_nm: num(o['cpaLimit_nm']) ?? 1,
    tcpaLimit_min: num(o['tcpaLimit_min']) ?? 12,
    targets: (o['targets'] as unknown[]).filter(isObj).flatMap((x) => {
      const tr = parseTrack(x);
      return tr ? [tr] : [];
    }),
  };
  const trial = parseTrial(o['trial']);
  if (trial) tracks.trial = trial;
  return tracks;
}

/** 解析一個伺服器 JSON 訊息(已 JSON.parse);無法辨識時回傳 null。 */
export function parseRadarMessage(obj: unknown): RadarMessage | null {
  if (!isObj(obj)) return null;
  switch (obj['type']) {
    case 'status':
      return parseStatus(obj);
    case 'tracks':
      return parseTracks(obj);
    case 'event': {
      const e = obj['event'];
      if (!isObj(e) || typeof e['kind'] !== 'string') return null;
      const ev: RadarEvent = {
        type: 'event',
        radar: radarId(obj['radar']),
        event: { kind: e['kind'], t: num(e['t']) ?? 0, message: str(e['message']) },
      };
      const tid = num(e['targetId']);
      if (tid !== undefined) ev.event.targetId = tid;
      return ev;
    }
    case 'ack': {
      if (typeof obj['cmd'] !== 'string') return null;
      const ack: RadarAck = { type: 'ack', radar: radarId(obj['radar']), cmd: obj['cmd'], ok: bool(obj['ok']) };
      if (typeof obj['error'] === 'string') ack.error = obj['error'];
      if ('reqId' in obj) ack.reqId = obj['reqId'];
      const id = num(obj['id']);
      if (id !== undefined) ack.id = id;
      const value = num(obj['value']);
      if (value !== undefined) ack.value = value;
      return ack;
    }
    default:
      return null;
  }
}

// ---------------------------------------------------------------------------------------------
// 客戶端指令

export interface RainCellCmd {
  lat?: number;
  lon?: number;
  x?: number;
  y?: number;
  radius_m: number;
  rate_mmh: number;
}

export type RadarCommand =
  | { cmd: 'range'; value: number }
  | { cmd: 'gain' | 'sea' | 'rain'; value: number }
  | { cmd: 'tune'; value: number }
  | { cmd: 'tune'; auto: true }
  | { cmd: 'standby' }
  | { cmd: 'transmit' }
  | { cmd: 'acquire'; bearing: number; range: number; relative?: boolean }
  | { cmd: 'cancel'; id: number | 'all' }
  | { cmd: 'guardZone'; enabled: boolean; inner_nm?: number; outer_nm?: number; from?: number; to?: number; relative?: boolean }
  | { cmd: 'trialManoeuvre'; course: number; speed: number; delay: number }
  | { cmd: 'trialManoeuvre'; active: false }
  | { cmd: 'cpaLimits'; cpa_nm: number; tcpa_min: number }
  | { cmd: 'environment'; seaState?: number | null; rainCells?: RainCellCmd[] }
  | { cmd: 'getStatus' }
  | { cmd: 'getTracks' };

export type RadarCommandWithReq = RadarCommand & { reqId?: string | number };

/** 序列化指令;角度與距離四捨五入到合理位數(避免浮點長尾)。 */
export function serializeRadarCommand(cmd: RadarCommandWithReq): string {
  return JSON.stringify(cmd, (_k, v: unknown) => (typeof v === 'number' ? Math.round(v * 1e4) / 1e4 : v));
}

// ---------------------------------------------------------------------------------------------
// 量程與距離圈

/** 伺服器標準量程(nm;與 RadarControls.StandardRanges 相同) */
export const STANDARD_RANGES_NM: readonly number[] = [0.125, 0.25, 0.5, 0.75, 1.5, 3, 6, 12, 24, 48, 96];
/** 畫面可選量程(規劃書任務:0.25–96 nm) */
export const DISPLAY_RANGES_NM: readonly number[] = [0.25, 0.5, 0.75, 1.5, 3, 6, 12, 24, 48, 96];

/** 最近的標準量程(對數距離最近;與伺服器換算一致) */
export function nearestRange(nm: number, scales: readonly number[] = STANDARD_RANGES_NM): number {
  let best = scales[0] ?? nm;
  let bestD = Infinity;
  for (const s of scales) {
    const d = Math.abs(Math.log(s / nm));
    if (d < bestD - 1e-12) {
      bestD = d;
      best = s;
    }
  }
  return best;
}

/** 不小於 nm 的最小標準量程(找不到時回傳最大者) */
export function rangeAtLeast(nm: number, scales: readonly number[] = STANDARD_RANGES_NM): number {
  const sorted = [...scales].sort((a, b) => a - b);
  for (const s of sorted) if (s >= nm - 1e-9) return s;
  return sorted[sorted.length - 1] ?? nm;
}

/** 下一個(dir = +1)或上一個(dir = −1)量程 */
export function stepRange(current: number, dir: 1 | -1, scales: readonly number[] = DISPLAY_RANGES_NM): number {
  const sorted = [...scales].sort((a, b) => a - b);
  const i = sorted.findIndex((s) => Math.abs(s - current) < 1e-9);
  if (i < 0) return nearestRange(current, sorted);
  return sorted[Math.min(sorted.length - 1, Math.max(0, i + dir))] ?? current;
}

/** 距離圈間隔(nm;IEC 62388 慣例:0.25→0.05、0.5→0.1、0.75→0.25、其餘 量程/6) */
export function ringInterval(rangeNm: number): number {
  if (rangeNm <= 0.125 + 1e-9) return 0.025;
  if (rangeNm <= 0.25 + 1e-9) return 0.05;
  if (rangeNm <= 0.5 + 1e-9) return 0.1;
  if (rangeNm <= 0.75 + 1e-9) return 0.25;
  return rangeNm / 6;
}

/** 量程顯示文字(0.25、0.75、1.5、12) */
export function formatRange(nm: number): string {
  return Number.isInteger(nm) ? String(nm) : String(Number(nm.toFixed(3)));
}
