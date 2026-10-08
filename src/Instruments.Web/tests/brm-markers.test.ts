// BRM 快速標記:五大類、備註、JSON/CSV 匯出與載回。
import { describe, expect, it } from 'vitest';
import { BRM_CATEGORIES, BrmMarkerStore, csvEscape, brmCategory } from '../src/lib/brm-markers.js';

describe('BrmMarkerStore', () => {
  it('五大類對應規劃書第 8.2 節', () => {
    expect(BRM_CATEGORIES.map((c) => c.label)).toEqual(['領導', '狀況覺知', '溝通', '團隊合作', '決策']);
    expect(brmCategory('communication').en).toBe('Communication');
    expect(() => brmCategory('x' as never)).toThrow(RangeError);
  });

  it('新增/備註/刪除', () => {
    const store = new BrmMarkerStore();
    const wall = new Date('2026-01-01T02:03:04Z');
    const m = store.add({ category: 'leadership', t: 125.5, tick: 6275, note: ' 船長未指定優先順序 ', wall, scenarioId: 'E01_baseline' });
    expect(m).toEqual({ id: 1, category: 'leadership', t: 125.5, tick: 6275, wall: '2026-01-01T02:03:04.000Z', note: '船長未指定優先順序', scenarioId: 'E01_baseline' });
    store.add({ category: 'teamwork', t: 200, polarity: 'plus' });
    expect(store.length).toBe(2);
    expect(store.updateNote(2, '交叉核對良好')).toBe(true);
    expect(store.updateNote(9, 'x')).toBe(false);
    expect(store.remove(1)).toBe(true);
    expect(store.all()[0]).toMatchObject({ id: 2, note: '交叉核對良好', polarity: 'plus' });
    expect(() => store.add({ category: 'nope' as never, t: 0 })).toThrow(RangeError);
  });

  it('JSON 匯出可載回', () => {
    const store = new BrmMarkerStore();
    store.add({ category: 'situationalAwareness', t: 10, note: 'a', wall: new Date(0) });
    store.add({ category: 'decisionMaking', t: 20, polarity: 'delta', wall: new Date(0) });
    const json = JSON.parse(store.toJson({ scenarioId: 'E01' })) as Record<string, unknown>;
    expect(json['format']).toBe('simosa-brm-markers');
    expect(json['scenarioId']).toBe('E01');
    expect((json['markers'] as unknown[]).length).toBe(2);
    const again = new BrmMarkerStore();
    expect(again.load(json)).toBe(2);
    expect(again.all()[1]).toMatchObject({ category: 'decisionMaking', t: 20, polarity: 'delta', wall: '1970-01-01T00:00:00.000Z' });
    expect(again.load([{ category: 'bad' }, { category: 'teamwork', t: 'x' }])).toBe(0);
  });

  it('CSV 匯出:BOM、標題列、RFC 4180 跳脫', () => {
    const store = new BrmMarkerStore();
    store.add({ category: 'communication', t: 3725, tick: 186250, note: '舵令 "右舵 10" 未複誦, 兩次', wall: new Date('2026-01-01T00:00:00Z') });
    const csv = store.toCsv();
    expect(csv.startsWith('﻿id,simTime_s,simTime,tick,wallClock,category,categoryLabel,polarity,scenarioId,note\r\n')).toBe(true);
    expect(csv).toContain('1,3725.00,01:02:05,186250,2026-01-01T00:00:00.000Z,communication,溝通,,,"舵令 ""右舵 10"" 未複誦, 兩次"\r\n');
    expect(csvEscape('plain')).toBe('plain');
    expect(csvEscape('a\nb')).toBe('"a\nb"');
  });
});
