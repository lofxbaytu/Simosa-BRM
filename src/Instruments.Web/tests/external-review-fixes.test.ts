// 拖船/錨/繫纜審查修正(webctl):
//  1. 示範模式載入 E13/E14:泊位幾何與航跡圖疊圖同一座標系(情境 origin、自船初始 x/y)、帶纜可用、E14 纜繩 initial: fast;
//  2. 拖船面板草稿在送出後/重設後不再蓋過 state 的命令值;簡化模式預設鈕以 state 命令值標示;
//  3. 轉盤 pointercancel 只還原草稿、不送指令;
//  4. 錨 heaving(著底)時拋錨鈕停用(核心回 anchorAlreadyOut);
//  5. 操船台碼頭接近速度用對地速度(含海流,與核心碰墊接觸速度、靠泊指標相同);
//  6. 泊位檔頂層 fenderTypes 逐鍵併入(泊位自己的型式優先,與核心 LoadBerth 的 TryAdd 相同)。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import type { AnchorState, TugState } from '../src/types/external.js';
import { COMMAND_EVENT } from '../src/components/shared.js';
import { anchorActionBlocked } from '../src/components/instructor/anchor-panel.js';
import { DemoSim } from '../src/demo/demo-sim.js';
import { demoOptionsFromScenario } from '../src/demo/demo-scenario.js';
import { berthGeometry, parseScenarioMooringLines, quayApproach, quayDistance, resolveScenarioBerth, wgs84ToEnu } from '../src/lib/external-forces.js';
import { overlayFromScenario } from '../src/lib/plot-overlay.js';
import { loadScenarioList, type ScenarioSummary } from '../src/lib/scenario.js';
import { knotsToMps } from '../src/lib/angles.js';

type Updatable = HTMLElement & { updateComplete: Promise<unknown> };

function scenario(id: string): ScenarioSummary {
  const s = loadScenarioList().find((x) => x.id === id);
  if (!s) throw new Error(`找不到情境 ${id}`);
  return s;
}

function rejections(sim: DemoSim): unknown[] {
  return sim
    .takeEvents()
    .filter((e) => e.kind === 'commandRejected')
    .map((e) => e.data?.['reason']);
}

describe('示範模式載入有泊位的情境(E13/E14)', () => {
  it('E13:示範泊位幾何與航跡圖疊圖一致,自船自情境初始 x/y 起算,帶纜不被拒', () => {
    const s = scenario('E13_tug_berthing_mailiao');
    const opts = demoOptionsFromScenario(s, 'FSB1');
    expect(opts.originLat).toBe(s.origin!.lat);
    expect(opts.originLon).toBe(s.origin!.lon);
    expect(opts.initialX).toBe(45.95);
    expect(opts.initialY).toBe(-66.25);
    const sim = new DemoSim(opts);
    const st = sim.current;
    expect(st.pos.x).toBeCloseTo(45.95, 6);
    expect(st.pos.y).toBeCloseTo(-66.25, 6);
    const ov = overlayFromScenario(s);
    const demoGeo = sim.berthGeometry!;
    expect(demoGeo).not.toBeNull();
    expect(demoGeo.quay[0]!.x).toBeCloseTo(ov.berth!.quay[0]!.x, 6);
    expect(demoGeo.quay[0]!.y).toBeCloseTo(ov.berth!.quay[0]!.y, 6);
    // 船舯離碼頭線約 70 m(情境註解),未觸碰碼頭
    expect(quayDistance(demoGeo, st.pos.x, st.pos.y)!.distance_m).toBeCloseTo(70, 0);
    expect(st.berth!.quayContact).toBe(false);
    sim.takeEvents();
    // 初始位置(里程 40 m、離碼頭 70 m):艏倒纜 fwdPort → B5 約 120 m,在纜長 150 m 內(修正前碼頭被換算到約 5 km 外而被拒)
    sim.apply({ type: 'mooring', args: { lineId: 'FWDSPRING', action: 'makeFast' } });
    const evs = sim.takeEvents();
    expect(evs.filter((e) => e.kind === 'commandRejected')).toEqual([]);
    expect(evs.find((e) => e.kind === 'mooringCommand')?.data).toMatchObject({ lineId: 'FWDSPRING', action: 'makeFast', status: 'fast', bollardId: 'B5' });
    // 艏纜 fwdPort → B7(里程約 250 m)此時約 182 m,確實超出纜長(核心同樣會拒絕);靠妥後才帶得到
    sim.apply({ type: 'mooring', args: { lineId: 'HEAD', action: 'makeFast', bollard: 'B7' } });
    expect(rejections(sim)).toEqual(['bollardOutOfReach']);
  });

  it('E14:情境纜繩 initial: fast 在示範開始(與重設後)即已帶纜,解纜不被拒', () => {
    const s = scenario('E14_unberthing_no_tug');
    expect(s.berth!.lines.map((l) => l.initial)).toEqual(['fast', 'fast', 'fast', 'fast']);
    const sim = new DemoSim(demoOptionsFromScenario(s, 'FSB1'));
    const lines = () => sim.current.berth!.lines.map((l) => [l.id, l.status, l.bollardId]);
    expect(lines()).toEqual([
      ['HEAD', 'fast', 'P6'],
      ['FWDSPRING', 'fast', 'P3'],
      ['AFTSPRING', 'fast', 'P4'],
      ['STERN', 'fast', 'P1'],
    ]);
    // 右舷靠在碰墊上:船舯距碼頭面約 碰墊 0.8 m + 半船寬
    expect(sim.current.pos.x).toBeCloseTo(-9.75, 6);
    sim.step();
    expect(sim.current.berth!.lines.every((l) => l.status === 'fast' && l.tension_t >= 0)).toBe(true);
    sim.takeEvents();
    sim.apply({ type: 'mooring', args: { lineId: 'HEAD', action: 'letGo' } });
    expect(rejections(sim)).toEqual([]);
    expect(sim.current.berth!.lines.find((l) => l.id === 'HEAD')!.status).toBe('letGo');
    sim.apply({ type: 'reset' });
    expect(lines().every(([, status]) => status === 'fast')).toBe(true);
    expect(sim.current.pos.x).toBeCloseTo(-9.75, 6);
  });

  it('情境 origin 與初始緯經度不同時(E07),示範自船位置換算到情境 origin 的 ENU', () => {
    const s = scenario('E07_steering_failure');
    const opts = demoOptionsFromScenario(s, 'FSB1');
    const p = s.initial.position!;
    const enu = wgs84ToEnu(s.origin!, p.lat!, p.lon!);
    expect(opts.originLat).toBe(s.origin!.lat);
    expect(opts.initialX).toBeCloseTo(enu.x, 6);
    expect(opts.initialY).toBeCloseTo(enu.y, 6);
    const sim = new DemoSim(opts);
    expect(sim.current.pos.x).toBeCloseTo(enu.x, 6);
    // 示範的緯經度以球面 111,320 m/度近似(與 wgs84ToEnu 約差 0.5 %,此處約 6 m),只比到 1e-3 度
    expect(sim.current.pos.lat).toBeCloseTo(p.lat!, 3);
    expect(sim.current.pos.lon).toBeCloseTo(p.lon!, 3);
  });

  it('沒有獨立 origin 的情境(E01)與省略初始位置時行為不變:自船自 (0,0) 起算', () => {
    const opts = demoOptionsFromScenario(scenario('E01_baseline'), 'FSB1');
    expect(opts.initialX ?? 0).toBe(0);
    expect(opts.initialY ?? 0).toBe(0);
    const a = new DemoSim({ initialHeading: 30, initialSpeed_kn: 8 });
    expect(a.current.pos.x).toBe(0);
    expect(a.current.pos.y).toBe(0);
  });

  it('自船初始位置平移時,示範目標船、假航線與內建示意泊位跟著平移(相對量不變)', () => {
    const base = new DemoSim({ initialHeading: 30, initialSpeed_kn: 8 });
    const moved = new DemoSim({ initialHeading: 30, initialSpeed_kn: 8, initialX: 1000, initialY: -500 });
    for (let i = 0; i < 100; i++) {
      base.step();
      moved.step();
    }
    const tb = base.current.targets.map((t) => [t.id, t.range_nm, t.bearing_deg, t.cpa_nm]);
    const tm = moved.current.targets.map((t) => [t.id, t.range_nm, t.bearing_deg, t.cpa_nm]);
    expect(tm).toEqual(tb);
    expect(moved.current.route!.xtd_m).toBe(base.current.route!.xtd_m);
    expect(moved.current.route!.distanceToWaypoint_nm).toBe(base.current.route!.distanceToWaypoint_nm);
    const gb = base.berthGeometry!;
    const gm = moved.berthGeometry!;
    expect(gm.quay[0]!.x - gb.quay[0]!.x).toBeCloseTo(1000, 6);
    expect(gm.quay[0]!.y - gb.quay[0]!.y).toBeCloseTo(-500, 6);
    // 示範泊位的 BREAST 到 B1 在纜長內(與 external-forces.test.ts 相同)
    moved.takeEvents();
    moved.apply({ type: 'mooring', args: { lineId: 'BREAST', action: 'makeFast', bollard: 'B1' } });
    expect(rejections(moved)).toEqual([]);
  });

  it('parseScenarioMooringLines 解析 initial(stowed | fast,其他值忽略)', () => {
    expect(parseScenarioMooringLines([{ id: 'A', initial: 'fast' }, { id: 'B', initial: 'stowed' }, { id: 'C', initial: 'tight' }, { id: 'D' }])).toEqual([
      { id: 'A', initial: 'fast' },
      { id: 'B', initial: 'stowed' },
      { id: 'C' },
      { id: 'D' },
    ]);
  });
});

// ------------------------------------------------------------------ 拖船面板

const TG = (over: Partial<TugState> = {}): TugState => ({ id: 'TG1', mode: 'standby', status: 'standby', force_t: 0, direction_deg: 270, directionOrder_deg: 270, power_pct: 0, efficiency: 1, bollardPull_t: 40, lineParted: false, ...over });

function captureCommands(): SimCommand[] {
  const cmds: SimCommand[] = [];
  document.body.addEventListener(COMMAND_EVENT, (ev) => cmds.push((ev as CustomEvent<SimCommand>).detail));
  return cmds;
}

type TugPanelEl = Updatable & { tugs: TugState[]; compact: boolean; epoch: number };

async function mountTug(compact: boolean, tugs: TugState[]): Promise<TugPanelEl> {
  const el = document.createElement('brm-tug-panel') as unknown as TugPanelEl;
  el.compact = compact;
  el.tugs = tugs;
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

function btn(el: HTMLElement, sel: string): HTMLButtonElement {
  const b = el.shadowRoot!.querySelector<HTMLButtonElement>(sel);
  if (!b) throw new Error(`找不到 ${sel}`);
  return b;
}

function activeButtons(el: HTMLElement): string[] {
  return [...el.shadowRoot!.querySelectorAll('button.active')].map((b) => b.className.replace('active', '').trim());
}

beforeAll(async () => {
  await import('../src/components/instructor/tug-panel.js');
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('<brm-tug-panel> 草稿與 state 同步', () => {
  it('重設/換情境(epoch 改變)後清除草稿:預設鈕以 state 命令值標示,推頂送出 state 的方向與出力', async () => {
    const cmds = captureCommands();
    const el = await mountTug(true, [TG()]);
    btn(el, 'button.pw-75').click();
    btn(el, 'button.dir-90').click();
    await el.updateComplete;
    // 核心回報新命令值
    el.tugs = [TG({ power_pct: 75, directionOrder_deg: 90 })];
    await el.updateComplete;
    expect(activeButtons(el)).toEqual(['mode-standby', 'dir-90', 'pw-75']);
    // 核心重設:出力令 0、方向令 270;教官站換 epoch
    el.epoch = 1;
    el.tugs = [TG()];
    await el.updateComplete;
    expect(activeButtons(el)).toEqual(['mode-standby', 'dir-270']);
    cmds.length = 0;
    btn(el, 'button.mode-push').click();
    expect(cmds).toEqual([{ type: 'tugControl', args: { id: 'TG1', mode: 'push', direction_deg: 270, power_pct: 0 } }]);
  });

  it('簡化模式預設鈕不以未回報的草稿標示(state 未變前不亮)', async () => {
    const el = await mountTug(true, [TG()]);
    btn(el, 'button.pw-75').click();
    btn(el, 'button.dir-90').click();
    await el.updateComplete;
    expect(activeButtons(el)).toEqual(['mode-standby', 'dir-270']);
  });

  it('送出值經核心回報後,其他來源改了命令(另一教官端)時面板跟著 state', async () => {
    const cmds = captureCommands();
    const el = await mountTug(false, [TG()]);
    const dir = el.shadowRoot!.querySelector<HTMLInputElement>('input.dir')!;
    dir.value = '90';
    dir.dispatchEvent(new Event('input'));
    dir.dispatchEvent(new Event('change'));
    await el.updateComplete;
    el.tugs = [TG({ directionOrder_deg: 90 })];
    await el.updateComplete;
    // 另一教官端改成 180°、出力 40 %
    el.tugs = [TG({ directionOrder_deg: 180, power_pct: 40 })];
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('svg.dial')!.getAttribute('aria-valuenow')).toBe('180');
    expect(el.shadowRoot!.querySelector<HTMLInputElement>('input.dir')!.value).toBe('180');
    cmds.length = 0;
    btn(el, 'button.mode-pull').click();
    expect(cmds).toEqual([{ type: 'tugControl', args: { id: 'TG1', mode: 'pull', direction_deg: 180, power_pct: 40 } }]);
  });

  it('核心一直沒回報(例如被拒)時,送出值的草稿在數次狀態更新後失效', async () => {
    const el = await mountTug(false, [TG()]);
    const dir = el.shadowRoot!.querySelector<HTMLInputElement>('input.dir')!;
    dir.value = '90';
    dir.dispatchEvent(new Event('input'));
    dir.dispatchEvent(new Event('change'));
    await el.updateComplete;
    el.tugs = [TG()];
    await el.updateComplete;
    // 回報前仍顯示送出值(避免畫面閃回舊值)
    expect(el.shadowRoot!.querySelector('svg.dial')!.getAttribute('aria-valuenow')).toBe('90');
    for (let i = 0; i < 60; i++) {
      el.tugs = [TG()];
      await el.updateComplete;
    }
    expect(el.shadowRoot!.querySelector('svg.dial')!.getAttribute('aria-valuenow')).toBe('270');
  });

  it('拖船 id 集合改變時清除草稿', async () => {
    const el = await mountTug(false, [TG()]);
    const dial = el.shadowRoot!.querySelector<SVGElement>('svg.dial')!;
    dial.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('svg.dial')!.getAttribute('aria-valuenow')).toBe('275');
    el.tugs = [TG({ id: 'TGX' })];
    await el.updateComplete;
    el.tugs = [TG()];
    await el.updateComplete;
    expect(el.shadowRoot!.querySelector('svg.dial')!.getAttribute('aria-valuenow')).toBe('270');
  });
});

describe('<brm-tug-panel> 轉盤取消', () => {
  function stubDial(el: HTMLElement): SVGElement {
    const dial = el.shadowRoot!.querySelector<SVGElement>('svg.dial')!;
    (dial as unknown as { getBoundingClientRect: () => Partial<DOMRect> }).getBoundingClientRect = () => ({ left: 0, top: 0, width: 96, height: 96, right: 96, bottom: 96 });
    return dial;
  }

  it('pointercancel 不送指令並還原按下前的草稿;pointerup 才送出', async () => {
    const cmds = captureCommands();
    const el = await mountTug(false, [TG()]);
    const dial = stubDial(el);
    dial.dispatchEvent(new PointerEvent('pointerdown', { clientX: 96, clientY: 48, pointerId: 1, bubbles: true }));
    await el.updateComplete;
    expect(dial.getAttribute('aria-valuenow')).toBe('90');
    dial.dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1, bubbles: true }));
    await el.updateComplete;
    expect(cmds).toEqual([]);
    expect(dial.getAttribute('aria-valuenow')).toBe('270');
    // lostpointercapture 同樣只取消
    dial.dispatchEvent(new PointerEvent('pointerdown', { clientX: 48, clientY: 96, pointerId: 2, bubbles: true }));
    dial.dispatchEvent(new PointerEvent('lostpointercapture', { pointerId: 2, bubbles: true }));
    await el.updateComplete;
    expect(cmds).toEqual([]);
    expect(dial.getAttribute('aria-valuenow')).toBe('270');
    dial.dispatchEvent(new PointerEvent('pointerdown', { clientX: 96, clientY: 48, pointerId: 3, bubbles: true }));
    dial.dispatchEvent(new PointerEvent('pointerup', { pointerId: 3, bubbles: true }));
    expect(cmds).toEqual([{ type: 'tugControl', args: { id: 'TG1', direction_deg: 90 } }]);
  });
});

// ------------------------------------------------------------------ 錨面板

describe('anchorActionBlocked', () => {
  it('起錨中(heaving,核心只在著底時回報)即使躺底鏈長 0 也停用拋錨', () => {
    const a: AnchorState = { side: 'starboard', status: 'heaving', length_m: 40, length_shackles: 1.45, maxLength_m: 247.5, tension_t: 5, onBottom_m: 0, holding_t: 0, brake: false, fullyOut: false };
    expect(anchorActionBlocked(a, 'letGo')).toBe('錨已拋出(著底或送出中)');
    expect(anchorActionBlocked(a, 'heave')).toBeNull();
    expect(anchorActionBlocked(a, 'brake')).toBeNull();
  });
});

// ------------------------------------------------------------------ 操船台接近速度(對地)

describe('quayApproach 對地速度', () => {
  // 碼頭沿 x 軸、水域在 −y(南);船航向 090 位於 y = −50
  const geo = berthGeometry({ id: 'Q', quay: [{ x: -500, y: 0 }, { x: 500, y: 0 }], waterSide: 'right' }, null)!;
  const pos = { lat: 0, lon: 0, x: 0, y: -50 };

  it('對水靜止、海流向北(朝碼頭)0.2 kn:艏/艉接近速度 = 海流法向分量', () => {
    const q = quayApproach({ pos, heading: 90, u: 0, v: 0, r: 0, current: { set: 0, drift: 0.2 } }, geo, 100)!;
    expect(q.bowApproach_mps).toBeCloseTo(knotsToMps(0.2), 9);
    expect(q.sternApproach_mps).toBeCloseTo(knotsToMps(0.2), 9);
  });

  it('海流與對水速度相加(核心 GroundVelocity 同式);無 current 欄位時退回對水', () => {
    const q = quayApproach({ pos, heading: 90, u: 0, v: -0.1, r: 0, current: { set: 180, drift: 0.1 } }, geo, 100)!;
    expect(q.bowApproach_mps).toBeCloseTo(0.1 - knotsToMps(0.1), 9);
    const w = quayApproach({ pos, heading: 90, u: 0, v: -0.1, r: 0 }, geo, 100)!;
    expect(w.bowApproach_mps).toBeCloseTo(0.1, 9);
  });

  it('E13 海流 0.3 kn / 250°、對水靜止:接近速度為海流在碼頭法向的投影(非 0)', () => {
    const s = scenario('E13_tug_berthing_mailiao');
    const g = berthGeometry(s.berth!.definition, s.origin!)!;
    const st = { pos: { lat: 0, lon: 0, x: 45.95, y: -66.25 }, heading: 85, u: 0, v: 0, r: 0, current: { set: 250, drift: 0.3 } };
    const q = quayApproach(st, g, 100)!;
    const n = quayDistance(g, st.pos.x, st.pos.y)!;
    const d = knotsToMps(0.3);
    const psi = (250 * Math.PI) / 180;
    const expected = -(d * Math.sin(psi) * n.nx + d * Math.cos(psi) * n.ny);
    expect(expected).toBeLessThan(-0.03);
    expect(q.bowApproach_mps).toBeCloseTo(expected, 6);
    expect(q.sternApproach_mps).toBeCloseTo(expected, 6);
  });
});

// ------------------------------------------------------------------ fenderTypes 合併

describe('resolveScenarioBerth fenderTypes 合併', () => {
  it('泊位自己的型式優先,缺少的鍵由泊位檔頂層補上', () => {
    const file = {
      fenderTypes: { 'cone-1000': { height_m: 1.6, width_m: 3, ratedDeflection_pct: 55 }, local: { height_m: 9 } },
      berths: [
        {
          id: 'B',
          quay: [
            { x: 0, y: 0 },
            { x: 100, y: 0 },
          ],
          waterSide: 'right',
          fenderTypes: { local: { height_m: 0.5 } },
          fenders: [
            { id: 'F1', type: 'cone-1000', chainage_m: 10 },
            { id: 'F2', type: 'local', chainage_m: 20 },
          ],
          bollards: [],
        },
      ],
    };
    const r = resolveScenarioBerth({ file: 'data/areas/x.json', id: 'B' }, { '/data/areas/x.json': file })!;
    const g = berthGeometry(r.definition, null)!;
    expect(g.fenders.map((f) => [f.id, f.height_m, f.width_m, f.ratedDeflection_pct])).toEqual([
      ['F1', 1.6, 3, 55],
      ['F2', 0.5, 2, 72],
    ]);
    // 不改動泊位檔原物件
    expect(Object.keys(file.berths[0]!.fenderTypes)).toEqual(['local']);
  });
});

// ------------------------------------------------------------------ 教官站整合

describe('教官站示範模式整合', () => {
  type App = HTMLElement & { updateComplete: Promise<unknown> };

  beforeAll(async () => {
    await import('../src/instructor-app.js');
  });

  it('載入 E13:航跡圖疊圖的泊位與示範運算的泊位同一座標系;重設/載入情境時拖船面板 epoch 遞增', async () => {
    history.replaceState(null, '', '/?demo=1');
    const app = document.createElement('brm-instructor-app') as App;
    document.body.appendChild(app);
    await app.updateComplete;
    const s = scenario('E13_tug_berthing_mailiao');
    app.dispatchEvent(new CustomEvent('scenario-load', { detail: s }));
    await new Promise((r) => setTimeout(r, 80));
    await app.updateComplete;
    const priv = app as unknown as { source: { simulator: DemoSim }; liveOverlay(): { berth?: { quay: Array<{ x: number; y: number }> } } | null; stopSource(): void };
    const sim = priv.source.simulator;
    const ov = priv.liveOverlay()!;
    expect(ov.berth!.quay.map((p) => [Math.round(p.x * 1000), Math.round(p.y * 1000)])).toEqual(sim.berthGeometry!.quay.map((p) => [Math.round(p.x * 1000), Math.round(p.y * 1000)]));
    expect(sim.current.pos.x).toBeCloseTo(45.95, 0);
    const tug = () => app.shadowRoot!.querySelector('brm-tug-panel') as unknown as { epoch: number } | null;
    expect(tug()).not.toBeNull();
    const before = tug()!.epoch;
    app.dispatchEvent(new CustomEvent('sim-reset'));
    await app.updateComplete;
    expect(tug()!.epoch).toBeGreaterThan(before);
    priv.stopSource();
  });
});
