// 教官站示範模式:由情境摘要產生 DemoSim 選項(船、初始航向/速度/位置、環境、拖船與泊位)。
// 座標系與核心、教官站航跡圖疊圖(overlayFromScenario)相同:本地 ENU 原點 = 情境 origin(無 origin 時 = 初始緯經度,
// 見 parseScenario);自船初始位置取 initial.position 的 x/y,只有 lat/lon 時以 wgs84ToEnu 換算到 origin(核心 Geo 同式)。
// 因此情境泊位(碼頭線為 lat/lon 或 x/y)、航線、區域與示範自船在同一座標系,帶纜/碼頭接觸判斷與畫面一致。
// 情境有拖船/泊位時以情境的定義代替內建示範拖船與泊位(無則用內建,離線也能測面板);纜繩 initial: fast 由 DemoExternal 帶纜。
import type { ShipId } from '../types/state.js';
import type { ScenarioSummary } from '../lib/scenario.js';
import { wgs84ToEnu } from '../lib/external-forces.js';
import type { DemoOptions } from './demo-sim.js';
import type { DemoTugDef } from './demo-external.js';

export function demoOptionsFromScenario(s: ScenarioSummary | null, fallbackShip: ShipId): DemoOptions {
  const o: DemoOptions = { shipId: s?.shipId ?? fallbackShip, initialHeading: s?.initial.heading ?? 45, initialSpeed_kn: s?.initial.speed ?? 8 };
  if (s) {
    const p = s.initial.position;
    const origin = s.origin ?? (p?.lat !== undefined && p.lon !== undefined ? { lat: p.lat, lon: p.lon } : null);
    if (origin) {
      o.originLat = origin.lat;
      o.originLon = origin.lon;
    }
    if (p?.x !== undefined && p.y !== undefined) {
      o.initialX = p.x;
      o.initialY = p.y;
    } else if (p?.lat !== undefined && p.lon !== undefined && origin) {
      const e = wgs84ToEnu(origin, p.lat, p.lon);
      // 初始位置即原點時保持 (0, 0)(避免 −0)
      if (e.x !== 0 || e.y !== 0) {
        o.initialX = e.x;
        o.initialY = e.y;
      }
    }
    if (s.environment.waterDepth !== undefined) o.waterDepth_m = s.environment.waterDepth;
    const w = s.environment.wind;
    if (w && w.trueSpeed !== undefined && w.trueDir !== undefined) o.wind = { trueSpeed: w.trueSpeed, trueDir: w.trueDir };
    const c = s.environment.current;
    if (c && c.set !== undefined && c.drift !== undefined) o.current = { set: c.set, drift: c.drift };
    if (s.tugs && s.tugs.length > 0) o.tugs = s.tugs.map((t): DemoTugDef => ({ id: t.id, ...(t.name ? { name: t.name } : {}), bollardPull_t: t.bollardPull_t ?? 40, attach: t.attach ?? 'fwd', lineLength_m: t.lineLength_m ?? 40, ...(t.modes ? { modes: t.modes } : {}) }));
    if (s.berth?.definition) o.berth = { definition: s.berth.definition, lines: s.berth.lines };
  }
  return o;
}
