// 情境 YAML 解析與摘要(data/scenarios/*.yaml;schema src/Contracts/scenario.schema.json)。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import {
  briefingText,
  environmentSummary,
  initialSummary,
  loadScenarioList,
  parseScenario,
  parseScenarioMap,
  scenarioPathFromKey,
} from '../src/lib/scenario.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const e01Path = path.resolve(here, '../../../data/scenarios/E01_baseline.yaml');

describe('parseScenario', () => {
  it('解析 E01_baseline.yaml 的 id、船、裝載、初始與環境', () => {
    const s = parseScenario(readFileSync(e01Path, 'utf-8'), e01Path);
    expect(s.issues).toEqual([]);
    expect(s.id).toBe('E01_baseline');
    expect(s.name).toContain('麥寮外海');
    expect(s.path).toBe('data/scenarios/E01_baseline.yaml');
    expect(s.shipId).toBe('FSB1');
    expect(s.loading).toBe('ballast');
    expect(s.seed).toBe(20260101);
    expect(s.timeScale).toBe(1);
    expect(s.startTimeUtc).toBe('2026-01-01T02:00:00Z');
    expect(s.initial).toEqual({ position: { lat: 23.8, lon: 120.05 }, heading: 0, speed: 7.8, telegraph: 'HAH', rudder: 0 });
    expect(s.environment).toEqual({ wind: { trueSpeed: 15, trueDir: 45, gustiness: 0.1 }, current: { set: 200, drift: 0.5 }, waterDepth: 30 });
    expect(s.briefing).toBe('');
  });

  it('摘要文字', () => {
    const s = parseScenario(readFileSync(e01Path, 'utf-8'), e01Path);
    expect(environmentSummary(s.environment)).toBe('風 15 kn/045°(陣風 0.1) · 流 200°/0.5 kn · 水深 30 m');
    expect(initialSummary(s.initial)).toBe('HDG 000 · 7.8 kn · HAH · 23.800, 120.050');
    const b = briefingText(s);
    expect(b).toContain('船:FSB1(壓載)');
    expect(b).toContain('初始:HDG 000');
    expect(b).toContain('環境:風 15 kn');
  });

  it('briefing 欄位優先作為簡報;能見度與 x/y 位置可解析', () => {
    const yaml = `
id: T01
name: 測試
briefing: |
  本練習請於 10 分鐘內轉向 090。
ship: { id: FSB2, loading: full }
origin: { lat: 23.8, lon: 120.05 }
initial:
  position: { x: 100, y: -200 }
  heading: 370
environment:
  waterDepth: 12
  visibility_nm: 0.5
  wind: { trueSpeed: 20, trueDir: 40 }
`;
    const s = parseScenario(yaml, 'T01.yaml');
    expect(s.issues).toEqual([]);
    expect(briefingText(s)).toBe('本練習請於 10 分鐘內轉向 090。');
    expect(s.initial.heading).toBe(10);
    expect(s.initial.position).toEqual({ x: 100, y: -200 });
    expect(s.environment.visibility_nm).toBe(0.5);
    expect(environmentSummary(s.environment)).toBe('風 20 kn/040° · 無流 · 水深 12 m · 能見度 0.5 nm');
    expect(initialSummary(s.initial)).toBe('HDG 010 · x 100 m, y -200 m');
    expect(s.loading).toBe('full');
  });

  it('缺欄位時不丟例外並列出 issues,裝載預設壓載', () => {
    const s = parseScenario('id: BAD\nship: { id: XYZ }\n', 'BAD.yaml');
    expect(s.id).toBe('BAD');
    expect(s.loading).toBe('ballast');
    expect(s.issues).toEqual(expect.arrayContaining(['ship.id 無效', '缺 initial', '缺 initial.position', '缺 initial.heading', '缺 environment']));
  });

  it('YAML 語法錯誤與非物件內容', () => {
    expect(parseScenario('id: [unclosed', 'x.yaml').issues[0]).toMatch(/YAML 解析失敗/);
    const s = parseScenario('- 1\n- 2\n', 'list.yaml');
    expect(s.issues).toContain('情境不是物件');
    expect(s.id).toBe('list');
  });

  it('scenarioPathFromKey 由 glob 鍵或絕對路徑取檔名', () => {
    expect(scenarioPathFromKey('/home/x/Simosa-BRM/data/scenarios/E02.yaml')).toBe('data/scenarios/E02.yaml');
    expect(scenarioPathFromKey('D:\\Simosa BRM\\data\\scenarios\\E03.yml')).toBe('data/scenarios/E03.yml');
    expect(scenarioPathFromKey('../../data/scenarios/E04.yaml')).toBe('data/scenarios/E04.yaml');
  });

  it('parseScenarioMap 依 id 排序', () => {
    const list = parseScenarioMap({ 'b.yaml': 'id: B\n', 'a.yaml': 'id: A\n' });
    expect(list.map((s) => s.id)).toEqual(['A', 'B']);
  });

  it('loadScenarioList 於建置時匯入 data/scenarios 的全部情境(至少含 E01)', () => {
    const list = loadScenarioList();
    expect(list.length).toBeGreaterThanOrEqual(1);
    const e01 = list.find((s) => s.id === 'E01_baseline');
    expect(e01).toBeDefined();
    expect(e01!.path).toBe('data/scenarios/E01_baseline.yaml');
    for (const s of list) expect(s.issues).toEqual([]);
  });
});
