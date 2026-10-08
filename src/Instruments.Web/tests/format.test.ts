import { describe, expect, it } from 'vitest';
import {
  formatClock,
  formatHeading,
  formatHeadingDecimal,
  formatKnots,
  formatLatitude,
  formatLongitude,
  formatMeters,
  formatRot,
  formatRpm,
  formatRudder,
  formatSignedDeg,
  formatSimTime,
  formatThruster,
} from '../src/lib/format.js';

describe('航向格式', () => {
  it('三位整數', () => {
    expect(formatHeading(7.4)).toBe('007');
    expect(formatHeading(359.6)).toBe('000');
    expect(formatHeading(-1)).toBe('359');
    expect(formatHeading(180)).toBe('180');
  });
  it('含小數', () => {
    expect(formatHeadingDecimal(7.44)).toBe('007.4°');
    expect(formatHeadingDecimal(359.96)).toBe('000.0°');
    expect(formatHeadingDecimal(123.456, 2)).toBe('123.46°');
  });
  it('無效值顯示佔位', () => {
    expect(formatHeading(undefined)).toBe('---');
    expect(formatHeading(Number.NaN)).toBe('---');
    expect(formatHeadingDecimal(null)).toBe('---.-°');
  });
});

describe('舵角 / ROT / 速度 / 轉速', () => {
  it('舵角左右', () => {
    expect(formatRudder(-12.4)).toBe('P 12°');
    expect(formatRudder(5)).toBe('S 5°');
    expect(formatRudder(0)).toBe('0°');
    expect(formatRudder(-0.2, 1)).toBe('P 0.2°');
    expect(formatRudder(undefined)).toBe('--');
  });
  it('帶號角度', () => {
    expect(formatSignedDeg(-12.34)).toBe('−12.3°');
    expect(formatSignedDeg(5)).toBe('+5.0°');
    expect(formatSignedDeg(0)).toBe('0.0°');
    expect(formatSignedDeg(-0.04)).toBe('0.0°');
  });
  it('ROT 箭頭', () => {
    expect(formatRot(12.34)).toBe('→ 12.3');
    expect(formatRot(-5)).toBe('← 5.0');
    expect(formatRot(0)).toBe('0.0');
    expect(formatRot(null)).toBe('--.-');
  });
  it('速度與轉速', () => {
    expect(formatKnots(14.456)).toBe('14.5');
    expect(formatKnots(-0.04)).toBe('0.0');
    expect(formatKnots(-3.2)).toBe('-3.2');
    expect(formatRpm(134.6)).toBe('135');
    expect(formatRpm(-80)).toBe('-80');
    expect(formatRpm(-0.3)).toBe('0');
    expect(formatMeters(3.456)).toBe('3.5');
    expect(formatThruster(0.45)).toBe('S 45%');
    expect(formatThruster(-1)).toBe('P 100%');
    expect(formatThruster(0)).toBe('0%');
  });
});

describe('緯經度度分格式', () => {
  it('北緯東經', () => {
    expect(formatLatitude(25.153)).toBe("25°09.180'N");
    expect(formatLongitude(121.39)).toBe("121°23.400'E");
  });
  it('南緯西經與補零', () => {
    expect(formatLatitude(-3.5)).toBe("03°30.000'S");
    expect(formatLongitude(-0.0125)).toBe("000°00.750'W");
  });
  it('分進位到 60 時度加一', () => {
    expect(formatLatitude(24.9999999)).toBe("25°00.000'N");
    expect(formatLongitude(119.9999999)).toBe("120°00.000'E");
  });
  it('超出範圍或無效顯示佔位', () => {
    expect(formatLatitude(95)).toBe("--°--.---'-");
    expect(formatLongitude(undefined)).toBe("---°--.---'-");
  });
});

describe('時間格式', () => {
  it('模擬時間 HH:MM:SS', () => {
    expect(formatSimTime(0)).toBe('00:00:00');
    expect(formatSimTime(3725.8)).toBe('01:02:05');
    expect(formatSimTime(90000)).toBe('25:00:00');
    expect(formatSimTime(-5)).toBe('00:00:00');
    expect(formatSimTime(undefined)).toBe('--:--:--');
  });
  it('本地時鐘', () => {
    const d = new Date(2026, 9, 8, 9, 5, 3);
    expect(formatClock(d)).toBe('09:05:03');
  });
});
