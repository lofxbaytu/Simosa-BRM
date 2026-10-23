// 靠離泊預演自動組題(規劃書第 8.0 節、第 9.1 節;lib/berthing-rehearsal.ts):泊位清單、幾何(初始位置/艏向/繫船柱)、
// 各輸入組合的情境物件以 parseScenario 自我驗證 issues 為空、YAML 往返一致;兩個固定範例與 data/scenarios/templates/examples/ 同步
// (C# xunit BerthingRehearsalFixtureTests 以 --validate 與 300 s 重播固定;更新範例:SIMOSA_UPDATE_EXAMPLES=1 pnpm test)。
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse as parseYaml } from 'yaml';
import { describe, expect, it } from 'vitest';
import {
  berthChoiceLabel,
  chooseBollards,
  composeBerthingRehearsal,
  defaultRehearsalInput,
  listBerthChoices,
  normalizePlannedUtc,
  type AlongsideSide,
  type BerthingRehearsalInput,
  type RehearsalOperation,
} from '../src/lib/berthing-rehearsal.js';
import { wgs84ToEnu } from '../src/lib/external-forces.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const examplesDir = path.resolve(here, '../../../data/scenarios/templates/examples');

function base(over: Partial<BerthingRehearsalInput> = {}): BerthingRehearsalInput {
  return { ...defaultRehearsalInput(new Date('2026-11-13T00:00:00Z')), plannedUtc: '2026-11-13T01:00Z', ...over };
}

/** 固定範例輸入(與 data/scenarios/templates/examples/*.yaml 對應) */
export const EXAMPLE_INPUTS: Record<string, BerthingRehearsalInput> = {
  'berthing-rehearsal-berthing': base({ operation: 'berthing', berthId: 'MLO-OIL-3', side: 'port', tugCount: 2, wind: { speed_kn: 16, dir_deg: 20, gustiness: 0.3 }, current: { set_deg: 250, drift_kn: 0.3 }, tideHeight_m: 1.2, baseDepth_m: 14 }),
  'berthing-rehearsal-unberthing': base({
    operation: 'unberthing',
    shipId: 'FSB1',
    loading: 'ballast',
    berthId: 'MLO-SOB-1',
    side: 'starboard',
    plannedUtc: '2026-11-14T06:00Z',
    tugCount: 0,
    pilotOnBoard: false,
    tideHeight_m: 0.8,
    baseDepth_m: 11,
    wind: { speed_kn: 18, dir_deg: 270, gustiness: 0.2 },
    current: { set_deg: 180, drift_kn: 0.2 },
    windShift: { after_s: 300, speed_kn: 25, dir_deg: 30, ramp_s: 120 },
  }),
};

describe('泊位清單(data/areas)', () => {
  it('列出 MLO-OIL-3、MLO-SOB-1、KHH-SOB-1、KHH-E20-OIL,走向/水側/中心里程正確', () => {
    const list = listBerthChoices();
    const ids = list.map((b) => b.id);
    expect(ids).toEqual(expect.arrayContaining(['MLO-OIL-3', 'MLO-SOB-1', 'KHH-SOB-1', 'KHH-E20-OIL']));
    const mlo = list.find((b) => b.id === 'MLO-OIL-3')!;
    expect(mlo.quayBearing_deg).toBeCloseTo(85, 0);
    expect(mlo.waterSide).toBe('right');
    expect(mlo.centreChainage_m).toBe(130);
    expect(mlo.fenderCount).toBe(12);
    expect(mlo.fenderHeight_m).toBe(1);
    expect(mlo.bollards.map((b) => b.id)).toEqual(['B1', 'B2', 'B3', 'B4', 'B5', 'B6', 'B7']);
    expect(mlo.file).toBe('data/areas/mailiao-berth.json');
    expect(mlo.port).toBe('MLO');
    expect(mlo.defaultDepth_m).toBe(14);
    const sob = list.find((b) => b.id === 'MLO-SOB-1')!;
    expect(sob.quayBearing_deg).toBeCloseTo(0, 0);
    expect(sob.waterSide).toBe('left');
    expect(sob.centreChainage_m).toBe(75);
    expect(berthChoiceLabel(mlo)).toContain('碼頭線 085°');
    expect(berthChoiceLabel(mlo)).toContain('南');
    expect(berthChoiceLabel(sob)).toContain('西');
  });

  it('沒有 berths 的區域檔與碼頭點不足者略過', () => {
    expect(listBerthChoices({ 'x.json': { land: [] }, 'y.json': { berths: [{ id: 'Q', quay: [{ lat: 1, lon: 1 }] }] } })).toEqual([]);
  });
});

describe('幾何', () => {
  it('左舷靠 MLO-OIL-3(走向 085°、水側右)→ 艏向 085;右舷靠 → 265;MLO-SOB-1(走向 000°、水側左)右舷靠 → 000', () => {
    expect(composeBerthingRehearsal(base({ side: 'port' })).summary.initial.heading).toBeCloseTo(85, 0);
    expect(composeBerthingRehearsal(base({ side: 'starboard' })).summary.initial.heading).toBeCloseTo(265, 0);
    expect(composeBerthingRehearsal(base({ berthId: 'MLO-SOB-1', side: 'starboard' })).summary.initial.heading).toBeCloseTo(0, 0);
    expect(composeBerthingRehearsal(base({ berthId: 'MLO-SOB-1', side: 'port' })).summary.initial.heading).toBeCloseTo(180, 0);
  });

  it('靠泊初始位置離泊位中心約 0.8 nm 且在水側;離泊初始位置在碰墊上(與 E13/E14 的擺法相同)', () => {
    const r = composeBerthingRehearsal(base({ operation: 'berthing' }));
    const p = r.summary.initial.position!;
    // 泊位中心(里程 130、碼頭線上)
    const c = { x: 130 * Math.sin(85 * (Math.PI / 180)), y: 130 * Math.cos(85 * (Math.PI / 180)) };
    const d = Math.hypot(p.x! - c.x, p.y! - c.y) / 1852;
    expect(d).toBeGreaterThan(0.75);
    expect(d).toBeLessThan(0.85);
    expect(p.y).toBeLessThan(-400); // 水側(南)
    expect(r.summary.initial.speed).toBe(3);
    expect(r.summary.initial.telegraph).toBe('DSAH');
    const u = composeBerthingRehearsal(base({ operation: 'unberthing', berthId: 'MLO-SOB-1', side: 'starboard', shipId: 'FSB1', loading: 'ballast' }));
    const q = u.summary.initial.position!;
    expect(q.x).toBeCloseTo(-9.75, 2); // 碰墊 0.8 + 半船寬 9 − 0.05(E14)
    expect(q.y).toBeCloseTo(75, 2);
    expect(u.summary.initial.speed).toBe(0);
    expect(u.summary.initial.telegraph).toBe('STOP');
    expect(u.summary.berth?.lines.every((l) => l.initial === 'fast')).toBe(true);
  });

  it('繫船柱依艏艉位置挑選、4 條纜不同柱;艏向相反時艏艉對調', () => {
    const b = chooseBollards(
      [
        { id: 'B1', chainage_m: 10 },
        { id: 'B2', chainage_m: 45 },
        { id: 'B3', chainage_m: 80 },
        { id: 'B4', chainage_m: 130 },
        { id: 'B5', chainage_m: 180 },
        { id: 'B6', chainage_m: 215 },
        { id: 'B7', chainage_m: 250 },
      ],
      179.5,
      80.5,
      1,
    )!;
    expect(b.head).toBe('B7');
    expect(b.stern).toBe('B1');
    expect(b.fwdSpring).toBe('B4'); // 艏(179.5)後 40 m → 130
    expect(b.aftSpring).toBe('B3'); // 艉(80.5)前 40 m → 120:B4 已用,次近 B3(80)
    expect(new Set(Object.values(b)).size).toBe(4);
    const rev = chooseBollards(
      [
        { id: 'B1', chainage_m: 10 },
        { id: 'B2', chainage_m: 45 },
        { id: 'B3', chainage_m: 80 },
        { id: 'B4', chainage_m: 130 },
        { id: 'B5', chainage_m: 180 },
        { id: 'B6', chainage_m: 215 },
        { id: 'B7', chainage_m: 250 },
      ],
      80.5,
      179.5,
      -1,
    )!;
    expect(rev.head).toBe('B1');
    expect(rev.stern).toBe('B7');
    expect(new Set(Object.values(rev)).size).toBe(4);
    expect(chooseBollards([{ id: 'A', chainage_m: 0 }], 50, 10, 1)).toBeNull();
  });

  it('碼頭線原點為情境 origin,碼頭第二點換算後在走向上', () => {
    const r = composeBerthingRehearsal(base());
    const def = r.summary.berth!.definition!;
    const quay = def['quay'] as Array<{ lat: number; lon: number }>;
    const e = wgs84ToEnu(r.summary.origin!, quay[1]!.lat, quay[1]!.lon);
    expect(Math.atan2(e.x, e.y) * (180 / Math.PI)).toBeCloseTo(85, 0);
  });
});

describe('各輸入組合', () => {
  const berths = ['MLO-OIL-3', 'MLO-SOB-1', 'KHH-SOB-1', 'KHH-E20-OIL'];
  const ops: RehearsalOperation[] = ['berthing', 'unberthing'];
  const sides: AlongsideSide[] = ['port', 'starboard'];
  for (const op of ops)
    for (const berthId of berths)
      for (const side of sides)
        for (const tugCount of [0, 1, 2] as const)
          it(`${op} ${berthId} ${side} 拖船 ${tugCount}:parseScenario issues 空、YAML 往返相同`, () => {
            const input = base({ operation: op, berthId, side, tugCount, shipId: tugCount === 1 ? 'FSB2' : 'FSB1', loading: op === 'unberthing' ? 'ballast' : 'full', pilotOnBoard: side === 'port' });
            const r = composeBerthingRehearsal(input);
            expect(r.summary.issues).toEqual([]);
            expect(r.summary.id).toBe(r.id);
            expect(r.id).toMatch(/^BR_(berthing|unberthing)_[A-Z0-9-]+_\d{8}-\d{4}$/);
            expect(parseYaml(r.yaml)).toEqual(JSON.parse(JSON.stringify(r.scenario)));
            expect(r.summary.shipId).toBe(input.shipId);
            expect(r.summary.berth?.id).toBe(berthId);
            expect(r.summary.berth?.definition).not.toBeNull();
            expect(r.summary.berth?.lines.map((l) => l.id)).toEqual(['HEAD', 'FWDSPRING', 'AFTSPRING', 'STERN']);
            expect(new Set(r.summary.berth?.lines.map((l) => l.bollard)).size).toBe(4);
            const fairleadSide = side === 'port' ? 'Port' : 'Stbd';
            expect(r.summary.berth?.lines.map((l) => l.fairlead)).toEqual([`fwd${fairleadSide}`, `fwd${fairleadSide}`, `aft${fairleadSide}`, `aft${fairleadSide}`]);
            expect(r.summary.tugs?.length ?? 0).toBe(tugCount);
            if (tugCount > 0) {
              const tugSide = side === 'port' ? 'Stbd' : 'Port';
              expect(r.summary.tugs!.every((t) => String(t.attach).endsWith(tugSide))).toBe(true);
              expect(r.summary.timeline.some((i) => i.id === 'tugsInPosition')).toBe(true);
              expect(r.summary.assessment.required.some((q) => q.id === 'tugOrderConfirm')).toBe(true);
            } else {
              expect(r.summary.timeline.some((i) => i.id === 'tugsInPosition')).toBe(false);
              expect(r.summary.assessment.required.some((q) => q.id === 'tugOrderConfirm')).toBe(false);
            }
            const ids = r.summary.timeline.map((i) => i.id);
            expect(ids).toContain('pilotMpx');
            expect(ids).toContain('tideWindow');
            expect(ids).toContain('groundingEnd');
            if (op === 'berthing') {
              expect(ids).toContain('allFast');
              expect(ids).toContain('firstContact');
              expect(r.summary.assessment.bands.map((b) => b.metric)).toContain('berthing.maxLateralSpeed_mps');
            } else {
              expect(ids).toContain('clearOfBerth');
              expect(r.summary.assessment.bands.map((b) => b.metric)).toContain('berthing.maxReContactSpeed_mps');
              expect((r.scenario['berth'] as Record<string, unknown>)['departedDistance_m']).toBe(20);
            }
            expect(r.summary.assessment.bands.map((b) => b.metric)).toContain('ukc.min_m');
            expect(r.summary.paramRefs.map((p) => p.key)).toEqual(expect.arrayContaining(['berthing.approachSpeed_mps', 'navigation.ukc.minimum_m', 'assessment.passMark']));
            expect(r.summary.zones.map((z) => z.id)).toEqual(['harbourSpeed', 'berthArea']);
            expect(r.summary.briefing).toContain(side === 'port' ? '引水人在船' : '無引水人');
            expect(r.summary.briefingDoc.roles.some((x) => x.role === 'pilot')).toBe(side === 'port');
            expect(r.summary.environment.waterDepth).toBeCloseTo(input.baseDepth_m + input.tideHeight_m, 6);
            expect(r.notes.length).toBeGreaterThanOrEqual(3);
          });

  it('風變化加入 windShift 時間表(setEnvironment 漸變)、潮窗訊息含預定時刻', () => {
    const r = composeBerthingRehearsal(base({ windShift: { after_s: 600, speed_kn: 25, dir_deg: 30, ramp_s: 120 } }));
    const ws = r.summary.timeline.find((i) => i.id === 'windShift')!;
    expect(ws).toBeDefined();
    expect(ws.actions[0]!.kind).toBe('setEnvironment');
    const tide = r.summary.timeline.find((i) => i.id === 'tideWindow')!;
    expect(JSON.stringify(tide.actions[0]!.value)).toContain('2026-11-13 01:00 UTC');
    expect(r.summary.startTimeUtc).toBe('2026-11-13T00:40:00Z'); // 靠泊:預定時刻前 20 分鐘開始
    const u = composeBerthingRehearsal(base({ operation: 'unberthing' }));
    expect(u.summary.startTimeUtc).toBe('2026-11-13T01:00:00Z');
    expect(composeBerthingRehearsal(base({ windShift: { after_s: 0, speed_kn: 25, dir_deg: 30, ramp_s: 120 } })).summary.timeline.some((i) => i.id === 'windShift')).toBe(false);
  });

  it('同一輸入兩次組題結果相同(確定性,種子由預定時刻導出)', () => {
    const a = composeBerthingRehearsal(base());
    const b = composeBerthingRehearsal(base());
    expect(a.yaml).toBe(b.yaml);
    expect(a.summary.seed).toBe(611130100); // 202611130100 mod 2e9
  });

  it('錯誤輸入:泊位不存在、預定時刻無法解析', () => {
    expect(() => composeBerthingRehearsal(base({ berthId: 'NOPE' }))).toThrow(/找不到泊位/);
    expect(() => composeBerthingRehearsal(base({ plannedUtc: 'abc' }))).toThrow(/預定時刻/);
    expect(normalizePlannedUtc('2026-11-13T01:00')).toBe('2026-11-13T01:00:00Z');
    expect(normalizePlannedUtc('2026-11-13T01:00:30Z')).toBe('2026-11-13T01:00:30Z');
    expect(normalizePlannedUtc('')).toBeNull();
  });
});

describe('初始 UKC(與核心 ShipParticulars.MaxDraft_m 相同:以艏/艉/平均吃水的最大值計)', () => {
  it('FSB1 壓載:平均吃水 3.72 m 但艉吃水 5.41 m → 水深 11.8 m 時 UKC 6.39 m(簡報、潮窗提示同值),無警告', () => {
    const r = composeBerthingRehearsal(base({ operation: 'unberthing', loading: 'ballast', berthId: 'MLO-SOB-1', side: 'starboard', baseDepth_m: 11, tideHeight_m: 0.8 }));
    expect(r.draftMax_m).toBe(5.41);
    expect(r.ukc_m).toBe(6.39);
    expect(r.summary.briefing).toContain('UKC 約 6.39 m');
    expect(JSON.stringify(r.summary.timeline.find((i) => i.id === 'tideWindow')!.actions[0]!.value)).toContain('UKC 約 6.39 m');
    expect(r.warnings).toEqual([]);
    expect(r.summary.issues).toEqual([]);
    // FSB2 壓載:艉 6.5 m(平均 5.45 m);滿載平吃水 7.2 m
    expect(composeBerthingRehearsal(base({ shipId: 'FSB2', loading: 'ballast', baseDepth_m: 14, tideHeight_m: 1 })).draftMax_m).toBe(6.5);
    expect(composeBerthingRehearsal(base({ shipId: 'FSB2', loading: 'full', baseDepth_m: 14, tideHeight_m: 1 })).draftMax_m).toBe(7.2);
  });

  it('基底水深 4.3 m + 潮高 1.0 m(FSB1 壓載)→ 初始 UKC −0.11 m:summary.issues 含「初始 UKC … ≤ 0」(核心 t=0 即擱淺,面板不得載入)', () => {
    const r = composeBerthingRehearsal(base({ operation: 'unberthing', loading: 'ballast', berthId: 'MLO-SOB-1', side: 'starboard', baseDepth_m: 4.3, tideHeight_m: 1.0 }));
    expect(r.ukc_m).toBe(-0.11);
    expect(r.summary.issues).toHaveLength(1);
    expect(r.summary.issues[0]).toMatch(/初始 UKC -0\.11 m ≤ 0/);
    expect(r.summary.issues[0]).toContain('5.41');
    // 剛好 0 亦為初始擱淺(核心 ukc0 <= 0)
    expect(composeBerthingRehearsal(base({ operation: 'unberthing', loading: 'ballast', berthId: 'MLO-SOB-1', side: 'starboard', baseDepth_m: 4.41, tideHeight_m: 1.0 })).summary.issues).toHaveLength(1);
  });

  it('初始 UKC 0 < x < 公司下限 1.0 m(navigation.ukc.minimum_m)→ warnings 提示、issues 空(可載入,但 ukcMin 容許帶一開始即警告)', () => {
    const r = composeBerthingRehearsal(base({ operation: 'unberthing', loading: 'ballast', berthId: 'MLO-SOB-1', side: 'starboard', baseDepth_m: 5.5, tideHeight_m: 0.5 }));
    expect(r.ukc_m).toBe(0.59);
    expect(r.summary.issues).toEqual([]);
    expect(r.warnings).toHaveLength(1);
    expect(r.warnings[0]).toMatch(/初始 UKC 0\.59 m 低於公司 UKC 下限 1 m/);
    expect(r.notes).toContain(r.warnings[0]!);
  });
});

describe('固定範例(data/scenarios/templates/examples)', () => {
  for (const [name, input] of Object.entries(EXAMPLE_INPUTS))
    it(`${name}.yaml 與組題輸出相同`, () => {
      const r = composeBerthingRehearsal(input);
      expect(r.summary.issues).toEqual([]);
      const file = path.join(examplesDir, `${name}.yaml`);
      if (process.env['SIMOSA_UPDATE_EXAMPLES'] === '1') {
        mkdirSync(examplesDir, { recursive: true });
        writeFileSync(file, r.yaml, 'utf-8');
      }
      expect(existsSync(file), `缺少範例 ${file}(SIMOSA_UPDATE_EXAMPLES=1 pnpm test 產生)`).toBe(true);
      expect(readFileSync(file, 'utf-8').replace(/\r\n/g, '\n')).toBe(r.yaml);
    });
});
