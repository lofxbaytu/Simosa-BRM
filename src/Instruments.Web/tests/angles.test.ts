import { describe, expect, it } from 'vitest';
import { clamp, headingDifference, knotsToMps, mpsToKnots, normalizeHeading, normalizeRelative, relativeBearing } from '../src/lib/angles.js';

describe('normalizeHeading(航向正規化)', () => {
  it('範圍內不變', () => {
    expect(normalizeHeading(0)).toBe(0);
    expect(normalizeHeading(123.4)).toBeCloseTo(123.4);
    expect(normalizeHeading(359.9)).toBeCloseTo(359.9);
  });
  it('360 與倍數回到 0', () => {
    expect(normalizeHeading(360)).toBe(0);
    expect(normalizeHeading(720)).toBe(0);
    expect(normalizeHeading(-360)).toBe(0);
  });
  it('負角度換成正角度', () => {
    expect(normalizeHeading(-10)).toBe(350);
    expect(normalizeHeading(-370)).toBe(350);
  });
  it('超過 360 折回', () => {
    expect(normalizeHeading(370)).toBe(10);
    expect(normalizeHeading(1085)).toBe(5);
  });
  it('-0 變成 0,NaN/Infinity 回傳 NaN', () => {
    expect(Object.is(normalizeHeading(-0), 0)).toBe(true);
    expect(normalizeHeading(Number.NaN)).toBeNaN();
    expect(normalizeHeading(Number.POSITIVE_INFINITY)).toBeNaN();
  });
});

describe('normalizeRelative / headingDifference(最短角差)', () => {
  it('(-180, 180] 範圍', () => {
    expect(normalizeRelative(180)).toBe(180);
    expect(normalizeRelative(-180)).toBe(180);
    expect(normalizeRelative(190)).toBe(-170);
    expect(normalizeRelative(-190)).toBe(170);
  });
  it('跨 000 的右轉為正、左轉為負', () => {
    expect(headingDifference(350, 10)).toBe(20);
    expect(headingDifference(10, 350)).toBe(-20);
    expect(headingDifference(0, 180)).toBe(180);
    expect(headingDifference(90, 270)).toBe(180);
    expect(headingDifference(45, 45)).toBe(0);
  });
  it('相對方位右舷正', () => {
    expect(relativeBearing(30, 60)).toBe(30);
    expect(relativeBearing(30, 350)).toBe(-40);
  });
});

describe('clamp 與單位換算', () => {
  it('clamp 限幅,NaN 回傳下限', () => {
    expect(clamp(5, -1, 1)).toBe(1);
    expect(clamp(-5, -1, 1)).toBe(-1);
    expect(clamp(0.5, -1, 1)).toBe(0.5);
    expect(clamp(Number.NaN, -1, 1)).toBe(-1);
  });
  it('節與 m/s 互換', () => {
    expect(knotsToMps(1)).toBeCloseTo(0.514444, 5);
    expect(mpsToKnots(knotsToMps(14.5))).toBeCloseTo(14.5, 9);
  });
});
