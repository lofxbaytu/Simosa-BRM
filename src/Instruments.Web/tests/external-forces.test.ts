// 拖船、錨、泊位的網頁端處理(docs/design/physics-tugs-anchors-mooring.md 第 5、6 節):
// 狀態解析(含舊狀態無欄位)、指令內容(command.schema.json $defs)、事件文字與嚴重度、19 種拒絕原因中文化、
// 泊位幾何(與核心 BerthModel 相同的法向與退縮)、艏/艉橫向速度、情境 berth 解析、示範模式的拖船與錨。
import { describe, expect, it } from 'vitest';
import { parseState } from '../src/lib/state-parser.js';
import {
  EXTERNAL_REJECT_REASON_LABEL,
  anchorCommand,
  berthGeometry,
  chainText,
  externalEventSeverity,
  fenderLoadLevel,
  hawsePoint,
  lateralSpeeds,
  lineLoadLevel,
  mooringCommand,
  parseAnchors,
  parseBerth,
  parseTugs,
  quayApproach,
  quayDistance,
  resolveBodyPoint,
  resolveScenarioBerth,
  tugControlCommand,
  tugDirectionText,
  tugLineBreakCommand,
  wgs84ToEnu,
} from '../src/lib/external-forces.js';
import { REJECT_REASON_LABEL, defaultSourceFor, eventLabel, eventSeverity, parseSimEvent } from '../src/lib/sim-event.js';
import { isValidCommand, serializeCommand } from '../src/lib/command.js';
import { COMMAND_TYPES } from '../src/types/command.js';
import { SIM_EVENT_KINDS, type SimEvent } from '../src/types/event.js';
import { parseScenario } from '../src/lib/scenario.js';
import { overlayFromHeaderScenario, overlayFromScenario, overlayHasContent } from '../src/lib/plot-overlay.js';
import { DemoSim } from '../src/demo/demo-sim.js';
import { DEMO_TUG_CONNECT_S } from '../src/demo/demo-external.js';
import { getShipConfig } from '../src/lib/ship-config.js';

/** 設計文件第 5 節的 state 範例(tugs/anchors/berth) */
const DOC_STATE = {
  t: 10,
  tick: 500,
  shipId: 'FSB1',
  pos: { lat: 23.8, lon: 120.17, x: 2000, y: 900 },
  heading: 85,
  cog: 85,
  sog: 1,
  stw: 1,
  rot: 0,
  u: 0.5,
  v: 0.1,
  r: 0.001,
  rudder: 0,
  rudderOrder: 0,
  rpm: 0,
  rpmOrder: 0,
  telegraph: 'STOP',
  depthBelowKeel: 10,
  wind: { trueSpeed: 0, trueDir: 0 },
  current: { set: 0, drift: 0 },
  loading: 'full',
  tugs: [
    {
      id: 'TG1',
      name: '麥寮1號',
      mode: 'push',
      status: 'working',
      force_t: 25.3,
      direction_deg: 270,
      directionOrder_deg: 270,
      power_pct: 50,
      efficiency: 1.0,
      bollardPull_t: 40,
      lineParted: false,
      pos: { lat: 23.8083, lon: 120.1702, x: 2008.3, y: 925.1 },
    },
  ],
  anchors: [
    { side: 'starboard', status: 'holding', length_m: 137.6, length_shackles: 5.0, maxLength_m: 247.5, tension_t: 4.1, horizontalTension_t: 3.6, onBottom_m: 88.2, holding_t: 17.3, brake: true, fullyOut: false, position: { lat: 23.8012, lon: 120.15, x: 2.7, y: 132.9 } },
    { side: 'port', status: 'stowed', length_m: 0, length_shackles: 0, maxLength_m: 247.5, tension_t: 0, horizontalTension_t: 0, onBottom_m: 0, holding_t: 0, brake: true, fullyOut: false },
  ],
  berth: {
    id: 'MLO-OIL-3',
    name: '麥寮油碼頭 3 號泊位(示意)',
    quayContact: false,
    contacts: [{ fenderId: 'F06', deflection_m: 0.21, deflection_pct: 21.0, reaction_kN: 694.0, overload: false }],
    lines: [{ id: 'HEAD', status: 'fast', tension_t: 2.0, mbl_t: 50, bollardId: 'B6', length_m: 45.2 }],
  },
};

function ev(kind: string, data: Record<string, unknown> = {}, extra: Partial<SimEvent> = {}): SimEvent {
  return { kind, tick: 100, t: 2, source: 'engine', visible: true, seq: 1, data, ...extra };
}

describe('狀態解析 tugs / anchors / berth', () => {
  it('設計文件範例無問題地解析,錨依左、右排序', () => {
    const r = parseState(DOC_STATE)!;
    expect(r.issues).toEqual([]);
    expect(r.state.tugs).toEqual([DOC_STATE.tugs[0]]);
    expect(r.state.anchors!.map((a) => a.side)).toEqual(['port', 'starboard']);
    expect(r.state.anchors![1]!.position).toEqual({ lat: 23.8012, lon: 120.15, x: 2.7, y: 132.9 });
    expect(r.state.anchors![0]!.position).toBeUndefined();
    expect(r.state.berth).toEqual(DOC_STATE.berth);
  });

  it('舊狀態沒有欄位時不產生欄位、不記問題(面板不顯示)', () => {
    const { tugs: _t, anchors: _a, berth: _b, ...old } = DOC_STATE;
    const r = parseState(old)!;
    expect(r.issues).toEqual([]);
    expect(r.state.tugs).toBeUndefined();
    expect(r.state.anchors).toBeUndefined();
    expect(r.state.berth).toBeUndefined();
  });

  it('型別錯誤記入 issues、以安全值補上;缺 id 的拖船與 side 無效的錨略過', () => {
    const issues: string[] = [];
    const tugs = parseTugs([{ id: 'TG1', mode: 'tow', status: 'x', force_t: 'a', direction_deg: 450 }, { mode: 'push' }], issues);
    expect(tugs).toEqual([{ id: 'TG1', mode: 'standby', status: 'standby', force_t: 0, direction_deg: 90 }]);
    expect(issues).toEqual(['tugs[0].mode', 'tugs[0].status', 'tugs[0].force_t', 'tugs[1].id']);
    const issues2: string[] = [];
    const anchors = parseAnchors([{ side: 'bow' }, { side: 'port', status: 'running', length_m: 55, tension_t: 1, onBottom_m: 0 }], issues2);
    expect(anchors).toHaveLength(1);
    expect(anchors[0]!.length_shackles).toBeCloseTo(2);
    expect(issues2).toEqual(['anchors[0].side']);
    const issues3: string[] = [];
    const berth = parseBerth({ id: 'B', contacts: [{ deflection_m: 1 }], lines: [{ id: 'HEAD', status: 'tight', tension_t: 3 }] }, issues3);
    expect(berth.contacts).toEqual([]);
    expect(berth.lines).toEqual([{ id: 'HEAD', status: 'stowed', tension_t: 3 }]);
    expect(issues3).toEqual(['berth.contacts[0]', 'berth.lines[0].status']);
  });

  it('非陣列/非物件的區塊記入 issues', () => {
    const r = parseState({ ...DOC_STATE, tugs: 3, anchors: {}, berth: [] })!;
    expect(r.issues).toEqual(['tugs', 'anchors', 'berth']);
  });
});

describe('指令內容(command.schema.json $defs)', () => {
  it('三個新指令在 COMMAND_TYPES 內且可序列化', () => {
    for (const t of ['tugControl', 'anchor', 'mooring']) expect((COMMAND_TYPES as readonly string[]).includes(t)).toBe(true);
    expect(serializeCommand(tugControlCommand('TG1', { mode: 'push', direction_deg: 270, power_pct: 50 }))).toBe('{"type":"tugControl","args":{"id":"TG1","mode":"push","direction_deg":270,"power_pct":50}}');
  });

  it('tugControl:只放有給的欄位,方向正規化 [0, 360)、出力限 0–100 取整數', () => {
    expect(tugControlCommand('TG2', { mode: 'standby' })).toEqual({ type: 'tugControl', args: { id: 'TG2', mode: 'standby' } });
    expect(tugControlCommand(' TG1 ', { direction_deg: 360 })).toEqual({ type: 'tugControl', args: { id: 'TG1', direction_deg: 0 } });
    expect(tugControlCommand('TG1', { direction_deg: -90.4, power_pct: 130 })).toEqual({ type: 'tugControl', args: { id: 'TG1', direction_deg: 270, power_pct: 100 } });
    expect(tugControlCommand('TG1', { power_pct: Number.NaN })).toEqual({ type: 'tugControl', args: { id: 'TG1' } });
    expect(() => tugControlCommand('  ')).toThrow(RangeError);
  });

  it('anchor:length_m 優先於節;brake/release 不帶目標;非法側/動作丟例外', () => {
    expect(anchorCommand('starboard', 'letGo', { shackles: 4 })).toEqual({ type: 'anchor', args: { side: 'starboard', action: 'letGo', targetLength_shackles: 4 } });
    expect(anchorCommand('starboard', 'veer', { shackles: 4, length_m: 150 })).toEqual({ type: 'anchor', args: { side: 'starboard', action: 'veer', length_m: 150 } });
    expect(anchorCommand('port', 'heave')).toEqual({ type: 'anchor', args: { side: 'port', action: 'heave' } });
    expect(anchorCommand('port', 'brake', { shackles: 3 })).toEqual({ type: 'anchor', args: { side: 'port', action: 'brake' } });
    expect(anchorCommand('port', 'letGo', { shackles: -1 })).toEqual({ type: 'anchor', args: { side: 'port', action: 'letGo' } });
    expect(() => anchorCommand('bow' as 'port', 'letGo')).toThrow(RangeError);
    expect(() => anchorCommand('port', 'drop' as 'letGo')).toThrow(RangeError);
  });

  it('mooring:依動作只放相關欄位', () => {
    expect(mooringCommand('HEAD', 'makeFast', { bollard: 'B6', tension_t: 2, length_m: 9 })).toEqual({ type: 'mooring', args: { lineId: 'HEAD', action: 'makeFast', bollard: 'B6', tension_t: 2 } });
    expect(mooringCommand('HEAD', 'heave', { tension_t: 8, bollard: 'B1' })).toEqual({ type: 'mooring', args: { lineId: 'HEAD', action: 'heave', tension_t: 8 } });
    expect(mooringCommand('HEAD', 'slack', { length_m: 0 })).toEqual({ type: 'mooring', args: { lineId: 'HEAD', action: 'slack' } });
    expect(mooringCommand('HEAD', 'slack', { length_m: 3 })).toEqual({ type: 'mooring', args: { lineId: 'HEAD', action: 'slack', length_m: 3 } });
    expect(mooringCommand('HEAD', 'letGo', { tension_t: 3 })).toEqual({ type: 'mooring', args: { lineId: 'HEAD', action: 'letGo' } });
    expect(isValidCommand(mooringCommand('X', 'letGo'))).toBe(true);
  });

  it('拖纜斷裂故障:指定 id 送 args,省略送 value', () => {
    expect(tugLineBreakCommand('TG2')).toEqual({ type: 'injectFault', args: { name: 'tugLineBreak', args: { id: 'TG2' } } });
    expect(tugLineBreakCommand()).toEqual({ type: 'injectFault', value: 'tugLineBreak' });
  });
});

describe('事件文字、嚴重度與拒絕原因', () => {
  it('新事件 kind 都在 SIM_EVENT_KINDS 內,指令類事件預設來源為教官', () => {
    const kinds = ['tugCommand', 'tugLineParted', 'anchorLetGo', 'anchorCommand', 'anchorBroughtUp', 'anchorDragging', 'anchorHolding', 'anchorAweigh', 'anchorHoused', 'anchorChainFullyOut', 'anchorBrakeSlipping', 'anchorWindlassStalled', 'fenderContact', 'quayContact', 'mooringCommand', 'mooringLineParted'];
    for (const k of kinds) expect((SIM_EVENT_KINDS as readonly string[]).includes(k)).toBe(true);
    expect(defaultSourceFor('tugCommand')).toBe('instructor');
    expect(defaultSourceFor('mooringCommand')).toBe('instructor');
    expect(defaultSourceFor('anchorDragging')).toBe('engine');
  });

  it('設計文件範例事件的中文標籤', () => {
    const fender = parseSimEvent({ kind: 'fenderContact', tick: 1972, t: 39.44, source: 'engine', itemId: 'F06', message: '碰墊 F06 接觸', visible: true, seq: 3, data: { fenderId: 'F06', normalSpeed_mps: 0.21, energy_kNm: 100.2, maxReaction_kN: 751.0, maxDeflection_m: 0.241, deflection_pct: 24.1, ratedEnergy_kNm: 551.4, ratedReaction_kN: 1000, overload: false } })!;
    expect(eventLabel(fender)).toBe('碰墊 F06 接觸 v 0.21 m/s 能量 100 kN·m(額定 551)壓縮 24 %');
    expect(eventSeverity(fender)).toBe('warning'); // 0.21 > 0.15 m/s
    const drag = parseSimEvent({ kind: 'anchorDragging', tick: 2210, t: 44.2, source: 'engine', itemId: 'starboard', message: 'starboard 走錨', visible: true, seq: 5, data: { side: 'starboard', tension_t: 21.4, holding_t: 6.3, length_m: 110 } })!;
    expect(eventLabel(drag)).toBe('右錨走錨 張力 21.4 t > 抓地力 6.3 t');
    expect(eventSeverity(drag)).toBe('alarm');
    const rejected = parseSimEvent({ kind: 'commandRejected', tick: 3000, t: 60, source: 'instructor', message: 'tugControl:tugLineParted', seq: 9, data: { command: 'tugControl', reason: 'tugLineParted', args: { id: 'TG2', mode: 'pull', direction_deg: 180, power_pct: 100 } } })!;
    expect(eventLabel(rejected)).toBe('指令被拒 拖船令(tugControl) TG2(拖纜已斷,不能拖拉)');
  });

  it('其他事件標籤', () => {
    expect(eventLabel(ev('tugCommand', { id: 'TG1', mode: 'push', from: 'standby', status: 'working', direction_deg: 270, power_pct: 50 }, { source: 'instructor' }))).toBe('拖船令 TG1 待命 → 推頂 270°(向左舷) 50 %(出力中)');
    expect(eventLabel(ev('tugLineParted', { id: 'TG2', force_t: 18.25, mode: 'pull', reason: 'fault', stillPushing: false }))).toBe('拖纜斷裂 TG2(斷前 18.3 t)');
    expect(eventLabel(ev('anchorLetGo', { side: 'starboard', action: 'letGo', targetLength_m: 110, targetLength_shackles: 4, length_m: 0, status: 'running' }))).toBe('右錨拋錨 目標 4.0 節(110 m)');
    expect(eventLabel(ev('anchorLetGo', { side: 'port', action: 'letGo', targetLength_m: null, length_m: 0 }))).toBe('左錨拋錨(至鏈端)');
    expect(eventLabel(ev('anchorCommand', { side: 'port', action: 'heave', targetLength_m: null, length_m: 82.5, status: 'heaving' }))).toBe('左錨 起錨 · 目前 83 m');
    expect(eventLabel(ev('anchorHoused', { side: 'port' }))).toBe('左錨收妥');
    expect(eventLabel(ev('anchorChainFullyOut', { side: 'port', length_m: 247.5, shackles: 9 }))).toBe('左錨錨鏈全出 9.0 節(248 m)');
    expect(eventLabel(ev('anchorWindlassStalled', { side: 'port', tension_t: 9.1, windlassLimit_t: 7.6 }))).toBe('左錨絞盤超載停轉 張力 9.1 t(限 7.6 t)');
    expect(eventLabel(ev('quayContact', { berthId: 'MLO-OIL-3', penetration_m: 0.05, normalSpeed_mps: 0.31 }))).toBe('船體觸碰碼頭 MLO-OIL-3 穿入 0.05 m v 0.31 m/s');
    expect(eventLabel(ev('mooringCommand', { lineId: 'HEAD', action: 'makeFast', status: 'fast', bollardId: 'B6', tension_t: 2 }))).toBe('繫纜 HEAD 帶纜 → B6 預張力 2.0 t');
    expect(eventLabel(ev('mooringCommand', { lineId: 'HEAD', action: 'heave', status: 'heaving', tension_t: 8 }))).toBe('繫纜 HEAD 絞纜 目標 8.0 t');
    expect(eventLabel(ev('mooringLineParted', { lineId: 'HEAD', tension_t: 52.1, mbl_t: 50, bollardId: 'B6' }))).toBe('纜繩斷裂 HEAD 張力 52.1 t > MBL 50 t(B6)');
  });

  it('嚴重度:斷纜/走錨/碼頭硬接觸/碰墊過載為 alarm,剎車滑出等為 warning,其餘 info', () => {
    expect(eventSeverity(ev('tugLineParted'))).toBe('alarm');
    expect(eventSeverity(ev('mooringLineParted'))).toBe('alarm');
    expect(eventSeverity(ev('quayContact'))).toBe('alarm');
    expect(eventSeverity(ev('fenderContact', { overload: true, normalSpeed_mps: 0.05 }))).toBe('alarm');
    expect(eventSeverity(ev('fenderContact', { overload: false, normalSpeed_mps: 0.05 }))).toBe('info');
    expect(eventSeverity(ev('anchorBrakeSlipping'))).toBe('warning');
    expect(eventSeverity(ev('anchorWindlassStalled'))).toBe('warning');
    expect(eventSeverity(ev('anchorBroughtUp'))).toBe('info');
    expect(externalEventSeverity(ev('cpaAlarm'))).toBeNull();
  });

  it('19 種拒絕原因都有中文標籤且併入 REJECT_REASON_LABEL', () => {
    const reasons = ['externalForcesUnsupported', 'unknownTug', 'unknownTugMode', 'tugModeUnavailable', 'tugLineParted', 'invalidPower', 'noAnchorData', 'unknownAnchor', 'anchorAlreadyOut', 'anchorStowed', 'chainFullyOut', 'invalidTarget', 'unknownAction', 'noBerth', 'unknownMooringLine', 'unknownBollard', 'bollardOutOfReach', 'mooringLineNotFast', 'mooringLineParted'];
    expect(Object.keys(EXTERNAL_REJECT_REASON_LABEL).sort()).toEqual([...reasons].sort());
    for (const r of reasons) {
      expect(REJECT_REASON_LABEL[r]).toMatch(/[一-鿿]/);
      const label = eventLabel(ev('commandRejected', { command: 'anchor', reason: r, args: { side: 'port', action: 'heave' } }, { source: 'instructor' }));
      expect(label).toBe(`指令被拒 錨令(anchor) 左錨(${REJECT_REASON_LABEL[r]})`);
    }
    // 既有指令的拒絕文字不變
    expect(eventLabel(ev('commandRejected', { command: 'autopilot', reason: 'emergencySteering', args: { enabled: true } }))).toBe('指令被拒 autopilot(應急操舵中不可自動舵)');
  });
});

describe('顯示分級與文字', () => {
  it('纜繩張力相對 MBL、碰墊壓縮相對額定', () => {
    expect(lineLoadLevel({ tension_t: 10, mbl_t: 50, status: 'fast' })).toBe('ok');
    expect(lineLoadLevel({ tension_t: 30, mbl_t: 50, status: 'fast' })).toBe('caution');
    expect(lineLoadLevel({ tension_t: 45, mbl_t: 50, status: 'fast' })).toBe('alarm');
    expect(lineLoadLevel({ tension_t: 0, mbl_t: 50, status: 'parted' })).toBe('alarm');
    expect(fenderLoadLevel({ deflection_pct: 21 })).toBe('ok');
    expect(fenderLoadLevel({ deflection_pct: 50 })).toBe('caution');
    expect(fenderLoadLevel({ deflection_pct: 75 })).toBe('alarm');
    expect(fenderLoadLevel({ deflection_pct: 10, overload: true })).toBe('alarm');
  });

  it('鏈長文字與拖船方向口語', () => {
    expect(chainText({ length_m: 137.6, length_shackles: 5.0 })).toBe('5.0 節(138 m)');
    expect(tugDirectionText(0)).toBe('向前');
    expect(tugDirectionText(90)).toBe('向右舷');
    expect(tugDirectionText(270)).toBe('向左舷');
    expect(tugDirectionText(200)).toBe('向後');
  });
});

describe('幾何', () => {
  it('WGS-84 曲率半徑換算與核心 LocalTangentPlane 相同(北緯 23.8°:1′ 緯度約 1,846 m)', () => {
    const p = wgs84ToEnu({ lat: 23.8, lon: 120.1 }, 23.8 + 1 / 60, 120.1 + 1 / 60);
    expect(p.y).toBeCloseTo(1846.3, 0);
    expect(p.x).toBeCloseTo(1698.5, 0);
  });

  it('船體具名點與核心 ScenarioBodyPoint 相同;錨鏈孔以 particulars 為準', () => {
    expect(resolveBodyPoint('fwd', 100, 20)).toEqual({ x: 45, y: 0 });
    expect(resolveBodyPoint('fwdPort', 100, 20)).toEqual({ x: 40, y: -9 });
    expect(resolveBodyPoint('aftStbd', 100, 20)).toEqual({ x: -40, y: 9 });
    expect(resolveBodyPoint({ x: 3, y: -2 }, 100, 20)).toEqual({ x: 3, y: -2 });
    expect(resolveBodyPoint('bridge', 100, 20)).toBeNull();
    const cfg = getShipConfig('FSB1');
    expect(cfg.anchors).toMatchObject({ shacklesPort: 9, shackleLength_m: 27.5, hawseX_m: 46.5, hawseY_m: 2.7 });
    expect(hawsePoint('port', cfg.lpp_m, cfg.breadth_m, cfg.anchors)).toEqual({ x: 46.5, y: -2.7 });
    expect(getShipConfig('FSB2').anchors).toMatchObject({ shacklesStbd: 10, anchorMass_kg: 3300 });
  });

  it('泊位幾何:碰墊在碼頭面里程處、向水域突出;繫船柱向陸側退縮(waterSide right)', () => {
    const geo = berthGeometry(
      {
        id: 'T',
        quay: [
          { x: 0, y: 0 },
          { x: 100, y: 0 },
          { x: 100, y: 100 },
        ],
        waterSide: 'right',
        fenderTypes: { c: { height_m: 1.5, width_m: 3, ratedDeflection_pct: 70 } },
        fenders: [
          { id: 'F1', type: 'c', chainage_m: 20 },
          { id: 'F2', type: 'c', chainage_m: 150 },
        ],
        bollards: [{ id: 'B1', chainage_m: 50, setback_m: 3, swl_t: 50 }],
      },
      null,
    )!;
    // 沿 +x 前進,右手側(水域)= −y
    expect(geo.fenders[0]).toMatchObject({ id: 'F1', x: 20, y: 0, nx: 0, ny: -1, height_m: 1.5, width_m: 3, ratedDeflection_pct: 70 });
    expect(geo.fenders[1]).toMatchObject({ x: 100, y: 50, nx: 1 });
    expect(geo.bollards[0]).toEqual({ id: 'B1', x: 50, y: 3, swl_t: 50 });
    expect(quayDistance(geo, 50, -30)!.distance_m).toBeCloseTo(30);
    expect(quayDistance(geo, 50, 10)!.distance_m).toBeCloseTo(-10);
    expect(berthGeometry({ id: 'x', quay: [{ lat: 23.8, lon: 120 }] }, null)).toBeNull();
  });

  it('艏/艉橫向速度由 v、r 與 LPP 推算;對碼頭的法向接近速度', () => {
    // r 右轉正 → 艏向右、艉向左
    const lat = lateralSpeeds({ v: 0.1, r: 0.002 }, 100);
    expect(lat.bow_mps).toBeCloseTo(0.2);
    expect(lat.stern_mps).toBeCloseTo(0);
    expect(lat.source).toBe('uvr');
    // 碼頭沿 x 軸、水域在 −y(南),船航向 090 位於 y = −50,向右舷(南 → 遠離)/ 向左舷(北 → 接近)
    const geo = berthGeometry({ id: 'Q', quay: [{ x: -500, y: 0 }, { x: 500, y: 0 }], waterSide: 'right' }, null)!;
    const q = quayApproach({ pos: { lat: 0, lon: 0, x: 0, y: -50 }, heading: 90, u: 0, v: -0.1, r: 0 }, geo, 100)!;
    expect(q.distance_m).toBeCloseTo(50);
    expect(q.bowApproach_mps).toBeCloseTo(0.1);
    expect(q.sternApproach_mps).toBeCloseTo(0.1);
    // 艏擺向碼頭(左轉 r < 0):艏接近較快
    const q2 = quayApproach({ pos: { lat: 0, lon: 0, x: 0, y: -50 }, heading: 90, u: 0, v: 0, r: -0.002 }, geo, 100)!;
    expect(q2.bowApproach_mps).toBeCloseTo(0.1);
    expect(q2.sternApproach_mps).toBeCloseTo(-0.1);
  });
});

const BERTH_YAML = `id: E13_tug_berthing
name: 拖船協助靠泊
ship: { id: FSB1, loading: full }
initial: { position: { lat: 23.80, lon: 120.16 }, heading: 85, speed: 3, telegraph: DSAH }
environment: { waterDepth: 15 }
tugs:
  - { id: TG1, name: 麥寮1號, bollardPull_t: 40, attach: fwdPort, modes: [push] }
  - { id: TG2, attach: aft }
anchors: { starboard: { length_shackles: 4 } }
berth:
  file: data/areas/mailiao-berth.json
  id: MLO-OIL-3
  lines:
    - { id: HEAD, fairlead: fwdPort, bollard: B6, mbl_t: 50 }
    - { id: STERN, fairlead: aftPort, bollard: B2 }
`;

describe('情境 tugs / berth 解析與疊圖', () => {
  it('berth.file + id 以建置時匯入的 data/areas 泊位檔解析(併入 fenderTypes)', () => {
    const s = parseScenario(BERTH_YAML, 'data/scenarios/E13_tug_berthing.yaml');
    expect(s.tugs).toEqual([
      { id: 'TG1', name: '麥寮1號', bollardPull_t: 40, attach: 'fwdPort', modes: ['push'] },
      { id: 'TG2', attach: 'aft' },
    ]);
    expect(s.anchorsInitial).toBe(true);
    expect(s.berth?.definition?.['id']).toBe('MLO-OIL-3');
    expect(s.berth?.lines.map((l) => l.id)).toEqual(['HEAD', 'STERN']);
    const ov = overlayFromScenario(s);
    expect(overlayHasContent(ov)).toBe(true);
    expect(ov.berth!.fenders).toHaveLength(12);
    expect(ov.berth!.bollards.map((b) => b.id)).toEqual(['B1', 'B2', 'B3', 'B4', 'B5', 'B6', 'B7']);
    expect(ov.berth!.fenders[0]!.ratedDeflection_pct).toBe(72);
    // 示意碼頭線約 085°、長約 260 m
    const [a, b] = ov.berth!.quay;
    expect(Math.hypot(b!.x - a!.x, b!.y - a!.y)).toBeGreaterThan(250);
    expect(ov.tugDefs?.map((t) => t.id)).toEqual(['TG1', 'TG2']);
  });

  it('找不到泊位檔時 definition 為 null(纜繩仍列出);紀錄 header 的 definition 直接使用', () => {
    const b = resolveScenarioBerth({ file: 'data/areas/none.json', lines: [{ id: 'HEAD', fairlead: 'fwd' }] }, {});
    expect(b).toEqual({ file: 'data/areas/none.json', definition: null, lines: [{ id: 'HEAD', fairlead: 'fwd' }] });
    const ov = overlayFromHeaderScenario({ id: 'x', origin: { lat: 23.8, lon: 120.1 }, berth: { definition: { id: 'D', quay: [{ x: 0, y: 0 }, { x: 50, y: 0 }], bollards: [{ id: 'B1', chainage_m: 10 }] }, lines: [{ id: 'L', fairlead: 'midPort', bollard: 'B1' }] } });
    expect(ov.berth?.bollards).toEqual([{ id: 'B1', x: 10, y: 2 }]);
    expect(ov.mooringLines).toEqual([{ id: 'L', fairlead: 'midPort', bollard: 'B1' }]);
    // 沒有外力區段的舊 header 不變
    expect(overlayFromHeaderScenario({ id: 'old' })).toEqual({ route: null, zones: [], events: [] });
  });
});

describe('示範模式的拖船、錨與繫纜', () => {
  function run(sim: DemoSim, seconds: number): void {
    for (let i = 0; i < Math.round(seconds / 0.02); i++) sim.step();
  }

  it('預設兩艘拖船與左右錨;狀態可被 parseState 無問題解析', () => {
    const sim = new DemoSim({ initialSpeed_kn: 0 });
    const s = sim.current;
    expect(s.tugs!.map((t) => t.id)).toEqual(['TG1', 'TG2']);
    expect(s.anchors!.map((a) => `${a.side}:${a.status}`)).toEqual(['port:stowed', 'starboard:stowed']);
    expect(s.berth!.lines.map((l) => l.id)).toEqual(['HEAD', 'BREAST', 'STERN']);
    expect(parseState(JSON.parse(JSON.stringify(sim.output())))!.issues).toEqual([]);
    // 未操作時不發外力事件
    expect(sim.takeEvents().filter((e) => e.kind.startsWith('tug') || e.kind.startsWith('anchor') || e.kind.startsWith('mooring'))).toEqual([]);
  });

  it('拖船出力一階上升(30 s ≈ 63 %)、事件 tugCommand;拒絕原因同核心', () => {
    const sim = new DemoSim({ initialSpeed_kn: 0 });
    sim.takeEvents();
    sim.apply(tugControlCommand('TG1', { mode: 'push', direction_deg: 270, power_pct: 50 }));
    const evs = sim.takeEvents();
    expect(evs.map((e) => e.kind)).toEqual(['tugCommand']);
    expect(evs[0]!.data).toMatchObject({ id: 'TG1', mode: 'push', from: 'standby', status: 'working', direction_deg: 270, power_pct: 50 });
    run(sim, 30);
    const tg1 = sim.current.tugs!.find((t) => t.id === 'TG1')!;
    expect(tg1.force_t).toBeGreaterThan(0.6 * 20);
    expect(tg1.force_t).toBeLessThan(0.66 * 20);
    // 方向以 3 °/s 走最短弧:090 → 270 需 60 s
    expect(tg1.direction_deg).toBe(180);
    run(sim, 30.1);
    expect(sim.current.tugs!.find((t) => t.id === 'TG1')!.direction_deg).toBe(270);
    sim.takeEvents();
    sim.apply(tugControlCommand('TGX', { mode: 'push' }));
    sim.apply({ type: 'tugControl', args: { id: 'TG1', power_pct: 120 } });
    sim.apply({ type: 'tugControl', args: { id: 'TG1', mode: 'tow' } });
    expect(sim.takeEvents().map((e) => e.data?.['reason'])).toEqual(['unknownTug', 'invalidPower', 'unknownTugMode']);
  });

  it('解纜後重新帶纜需時間;拖纜斷裂故障:拉中失力、故障中拒拉、清除後可帶纜', () => {
    const sim = new DemoSim({ initialSpeed_kn: 0 });
    sim.apply(tugControlCommand('TG2', { mode: 'pull', direction_deg: 180, power_pct: 100 }));
    run(sim, 20);
    expect(sim.current.tugs![1]!.force_t).toBeGreaterThan(10);
    sim.takeEvents();
    sim.apply({ type: 'injectFault', args: { name: 'tugLineBreak', args: { id: 'TG2' } } });
    const evs = sim.takeEvents();
    expect(evs.map((e) => e.kind)).toContain('tugLineParted');
    const tg2 = sim.current.tugs![1]!;
    expect(tg2).toMatchObject({ status: 'parted', mode: 'free', force_t: 0, lineParted: true });
    sim.apply(tugControlCommand('TG2', { mode: 'pull' }));
    expect(sim.takeEvents().map((e) => e.data?.['reason'])).toEqual(['tugLineParted']);
    sim.apply({ type: 'injectFault', args: { name: 'tugLineBreak', args: { id: 'NOPE' } } });
    expect(sim.takeEvents().map((e) => `${e.kind}:${String(e.data?.['reason'])}`)).toEqual(['commandRejected:unknownTug']);
    sim.apply({ type: 'clearFault', value: 'tugLineBreak' });
    expect(sim.current.tugs![1]!.status).toBe('free');
    sim.apply(tugControlCommand('TG2', { mode: 'pull' }));
    expect(sim.current.tugs![1]!.status).toBe('connecting');
    run(sim, DEMO_TUG_CONNECT_S + 0.1);
    expect(sim.current.tugs![1]!.status).toBe('working');
  });

  it('錨:拋錨 2 m/s 送出到目標並著底,起錨收妥;收存時鬆鏈被拒', () => {
    const sim = new DemoSim({ initialSpeed_kn: 0, waterDepth_m: 15, current: { set: 0, drift: 0 } });
    sim.apply(anchorCommand('port', 'veer'));
    expect(sim.takeEvents().filter((e) => e.kind === 'commandRejected').map((e) => e.data?.['reason'])).toEqual(['anchorStowed']);
    sim.apply(anchorCommand('starboard', 'letGo', { shackles: 2 }));
    const letGoEv = sim.takeEvents().find((e) => e.kind === 'anchorLetGo')!;
    expect(letGoEv.data).toMatchObject({ side: 'starboard', action: 'letGo', targetLength_m: 55, targetLength_shackles: 2 });
    run(sim, 10);
    let a = sim.current.anchors!.find((x) => x.side === 'starboard')!;
    expect(a.length_m).toBeCloseTo(20, 0);
    expect(a.status).toBe('running');
    run(sim, 30);
    a = sim.current.anchors!.find((x) => x.side === 'starboard')!;
    expect(a.length_m).toBe(55);
    expect(a.position).toBeDefined();
    expect(['holding', 'dragging']).toContain(a.status);
    sim.apply(anchorCommand('starboard', 'letGo'));
    expect(sim.takeEvents().filter((e) => e.kind === 'commandRejected').map((e) => e.data?.['reason'])).toEqual(['anchorAlreadyOut']);
    sim.apply(anchorCommand('starboard', 'heave'));
    run(sim, 160);
    a = sim.current.anchors!.find((x) => x.side === 'starboard')!;
    expect(a.status).toBe('stowed');
    expect(sim.takeEvents().map((e) => e.kind)).toEqual(expect.arrayContaining(['anchorCommand', 'anchorAweigh', 'anchorHoused']));
  });

  it('繫纜:帶纜(預張力)、超出纜長被拒、無泊位時 noBerth', () => {
    const sim = new DemoSim({ initialSpeed_kn: 0 });
    sim.apply(mooringCommand('BREAST', 'makeFast', { bollard: 'B9' }));
    sim.apply(mooringCommand('BREAST', 'heave', { tension_t: 5 }));
    sim.apply(mooringCommand('NOPE', 'letGo'));
    expect(sim.takeEvents().filter((e) => e.kind === 'commandRejected').map((e) => e.data?.['reason'])).toEqual(['unknownBollard', 'mooringLineNotFast', 'unknownMooringLine']);
    // 示範泊位:BREAST(右舷船舯)到 B1(里程 75 → 船前方約 −25 m、右舷 150 m)在纜長內
    sim.apply(mooringCommand('BREAST', 'makeFast', { bollard: 'B1', tension_t: 2 }));
    const ok = sim.takeEvents().find((e) => e.kind === 'mooringCommand')!;
    expect(ok.data).toMatchObject({ lineId: 'BREAST', action: 'makeFast', status: 'fast', bollardId: 'B1' });
    sim.step();
    const line = sim.current.berth!.lines.find((l) => l.id === 'BREAST')!;
    expect(line.status).toBe('fast');
    expect(line.tension_t).toBeCloseTo(2, 0);
    sim.apply(mooringCommand('HEAD', 'makeFast', { bollard: 'B8' }));
    expect(sim.takeEvents().map((e) => e.data?.['reason'])).toEqual(['bollardOutOfReach']);
    const none = new DemoSim({ berth: null, tugs: [] });
    expect(none.current.berth).toBeUndefined();
    expect(none.current.tugs).toBeUndefined();
    none.apply(mooringCommand('HEAD', 'letGo'));
    expect(none.takeEvents().filter((e) => e.kind === 'commandRejected').map((e) => e.data?.['reason'])).toEqual(['noBerth']);
  });
});
