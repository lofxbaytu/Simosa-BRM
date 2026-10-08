// 顯示格式化:航向、度/分、速度、時間。所有文字用繁體中文,縮寫保留英文。

import { normalizeHeading } from './angles.js';

const DEGREE = '°';

function isNum(v: unknown): v is number {
  return typeof v === 'number' && Number.isFinite(v);
}

/** 航向三位數整數,例如 7.4 → "007"、359.6 → "000"。無效值回傳 "---"。 */
export function formatHeading(deg: number | undefined | null): string {
  if (!isNum(deg)) return '---';
  const h = Math.round(normalizeHeading(deg)) % 360;
  return h.toString().padStart(3, '0');
}

/** 航向含小數,例如 7.44 → "007.4°"。 */
export function formatHeadingDecimal(deg: number | undefined | null, digits = 1): string {
  if (!isNum(deg)) return '---.-' + DEGREE;
  const h = normalizeHeading(deg);
  const factor = 10 ** digits;
  const rounded = Math.round(h * factor) / factor;
  const whole = Math.floor(rounded) % 360;
  const frac = (rounded - Math.floor(rounded)).toFixed(digits).slice(1);
  return whole.toString().padStart(3, '0') + frac + DEGREE;
}

/** 帶正負號的角度,例如舵角 −12.3 → "−12.3°"(用 Unicode 減號)。 */
export function formatSignedDeg(deg: number | undefined | null, digits = 1): string {
  if (!isNum(deg)) return '--' + DEGREE;
  const rounded = Number(deg.toFixed(digits));
  const sign = rounded < 0 ? '−' : rounded > 0 ? '+' : '';
  return sign + Math.abs(rounded).toFixed(digits) + DEGREE;
}

/** 舵角以左右表示:−12 → "P 12°"、+5 → "S 5°"、0 → "0°"。 */
export function formatRudder(deg: number | undefined | null, digits = 0): string {
  if (!isNum(deg)) return '--';
  const rounded = Number(deg.toFixed(digits));
  if (rounded === 0) return '0' + DEGREE;
  const side = rounded < 0 ? 'P' : 'S';
  return `${side} ${Math.abs(rounded).toFixed(digits)}${DEGREE}`;
}

/** 迴轉率:右轉正,例如 12.3 → "→ 12.3"、−5 → "← 5.0"。 */
export function formatRot(rot: number | undefined | null, digits = 1): string {
  if (!isNum(rot)) return '--.-';
  const rounded = Number(rot.toFixed(digits));
  if (rounded === 0) return (0).toFixed(digits);
  const arrow = rounded < 0 ? '← ' : '→ ';
  return arrow + Math.abs(rounded).toFixed(digits);
}

/** 速度(節),一位小數;負值(倒退)保留負號。 */
export function formatKnots(kn: number | undefined | null, digits = 1): string {
  if (!isNum(kn)) return '--.-';
  const v = Number(kn.toFixed(digits));
  return (Object.is(v, -0) ? 0 : v).toFixed(digits);
}

/** 轉速整數,倒車負。 */
export function formatRpm(rpm: number | undefined | null): string {
  if (!isNum(rpm)) return '---';
  const v = Math.round(rpm);
  return (v === 0 ? 0 : v).toString();
}

/** 公尺,一位小數。 */
export function formatMeters(m: number | undefined | null, digits = 1): string {
  if (!isNum(m)) return '--.-';
  return m.toFixed(digits);
}

/**
 * 緯度/經度的度分格式,例如 25.153 → "25°09.180'N",121.39 → "121°23.400'E"。
 * 緯度度數兩位、經度三位;分三位小數。
 */
export function formatLatitude(lat: number | undefined | null, minuteDigits = 3): string {
  if (!isNum(lat) || Math.abs(lat) > 90) return "--°--.---'-";
  return formatDegMin(lat, 2, minuteDigits, lat < 0 ? 'S' : 'N');
}

export function formatLongitude(lon: number | undefined | null, minuteDigits = 3): string {
  if (!isNum(lon) || Math.abs(lon) > 180) return "---°--.---'-";
  return formatDegMin(lon, 3, minuteDigits, lon < 0 ? 'W' : 'E');
}

function formatDegMin(value: number, degDigits: number, minuteDigits: number, hemi: string): string {
  const abs = Math.abs(value);
  let deg = Math.floor(abs);
  let min = (abs - deg) * 60;
  // 四捨五入後分若達 60 要進位
  const factor = 10 ** minuteDigits;
  min = Math.round(min * factor) / factor;
  if (min >= 60) {
    min -= 60;
    deg += 1;
  }
  const minStr = min.toFixed(minuteDigits).padStart(minuteDigits + 3, '0');
  return `${deg.toString().padStart(degDigits, '0')}${DEGREE}${minStr}'${hemi}`;
}

/** 模擬時間(秒)→ "HH:MM:SS";超過 24 小時照加。 */
export function formatSimTime(seconds: number | undefined | null): string {
  if (!isNum(seconds)) return '--:--:--';
  const total = Math.max(0, Math.floor(seconds));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return [h, m, s].map((n) => n.toString().padStart(2, '0')).join(':');
}

/** 本地時鐘 "HH:MM:SS"。 */
export function formatClock(date: Date): string {
  return [date.getHours(), date.getMinutes(), date.getSeconds()]
    .map((n) => n.toString().padStart(2, '0'))
    .join(':');
}

/** 風向/流向(度)整數三位。 */
export function formatBearing(deg: number | undefined | null): string {
  return formatHeading(deg);
}

/** 側推 −1..1 → 百分比,例如 0.45 → "S 45%"。 */
export function formatThruster(value: number | undefined | null): string {
  if (!isNum(value)) return '--';
  const pct = Math.round(value * 100);
  if (pct === 0) return '0%';
  return `${pct < 0 ? 'P' : 'S'} ${Math.abs(pct)}%`;
}
