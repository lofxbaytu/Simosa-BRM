// AIS 融合顯示(radar.schema.json trackAis;docs/design/radar-arpa.md 第 3.1 節):tracks[].ais 解析、來源符號(○ 雷達、◬ 融合、○△ 未關聯)、
// ARPA 表 SRC 欄與目標資料面板的 AIS 名稱/位置差、示範模式的 AIS 目標。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import { parseRadarMessage, type RadarTrack } from '../src/radar/protocol.js';
import { targetSource } from '../src/radar/arpa.js';
import { DemoRadarSource } from '../src/radar/demo-radar.js';
import { RadarStore } from '../src/radar/radar-store.js';

const flush = () => new Promise<void>((r) => setTimeout(r, 0));

async function mount<T extends HTMLElement>(tag: string, props: Record<string, unknown> = {}): Promise<T> {
  const el = document.createElement(tag) as T & { updateComplete: Promise<unknown> };
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

const text = (el: Element) => (el.shadowRoot?.textContent ?? '').replace(/\s+/g, ' ');

function track(over: Partial<RadarTrack>): RadarTrack {
  return {
    id: 1, label: '01', state: 'tracking', acquisition: 'manual', age_s: 200, stable: true, manoeuvring: false, dangerous: false, swapRisk: false, misses: 0,
    x: 1000, y: 2000, lat: 22.5, lon: 120.2, range_nm: 1.2, bearing_deg: 26.6, relBearing_deg: 26.6, course_deg: 250, speed_kn: 10, cpa_nm: 0.8, tcpa_min: 5, past: [], ...over,
  };
}

beforeAll(async () => {
  await import('../src/radar/radar-arpa-panels.js');
});

describe('tracks[].ais 解析與來源符號', () => {
  it('解析 ais 物件;缺 mmsi 略過;associated 預設 false', () => {
    const m = parseRadarMessage({
      type: 'tracks', radar: 'x', t: 10, scan: 4, transmitting: true,
      ownShip: { x: 0, y: 0, lat: 22.5, lon: 120.2, heading: 0, cog: 0, sog: 8 }, cpaLimit_nm: 1, tcpaLimit_min: 12,
      targets: [
        { id: 1, x: 1, y: 2, range_nm: 1, bearing_deg: 10, relBearing_deg: 10, ais: { mmsi: 416001001, name: 'ALFA', associated: true, positionOffset_m: 23 } },
        { id: 2, x: 1, y: 2, range_nm: 1, bearing_deg: 10, relBearing_deg: 10, ais: { mmsi: 416001002, name: 'BRAVO', positionOffset_m: 380 } },
        { id: 3, x: 1, y: 2, range_nm: 1, bearing_deg: 10, relBearing_deg: 10, ais: { name: 'NO-MMSI' } },
        { id: 4, x: 1, y: 2, range_nm: 1, bearing_deg: 10, relBearing_deg: 10 },
      ],
    });
    if (m?.type !== 'tracks') throw new Error();
    expect(m.targets[0]!.ais).toEqual({ mmsi: 416001001, name: 'ALFA', associated: true, positionOffset_m: 23 });
    expect(m.targets[1]!.ais).toEqual({ mmsi: 416001002, name: 'BRAVO', associated: false, positionOffset_m: 380 });
    expect(m.targets[2]!.ais).toBeUndefined();
    expect(m.targets[3]!.ais).toBeUndefined();
    expect(targetSource(m.targets[0]!)).toMatchObject({ source: 'fused', symbol: '◬' });
    expect(targetSource(m.targets[1]!)).toMatchObject({ source: 'candidate', symbol: '○△' });
    expect(targetSource(m.targets[1]!).label).toContain('380 m');
    expect(targetSource(m.targets[3]!)).toMatchObject({ source: 'radar', symbol: '○', label: '雷達' });
  });
});

describe('ARPA 表與目標資料的 AIS 顯示', () => {
  function storeWith(targets: RadarTrack[]): RadarStore {
    const store = new RadarStore();
    store.handleMessage({
      type: 'tracks', radar: 'x', t: 10, scan: 4, transmitting: true,
      ownShip: { x: 0, y: 0, lat: 22.5, lon: 120.2, heading: 0, cog: 0, sog: 8 }, cpaLimit_nm: 1, tcpaLimit_min: 12, targets,
    });
    return store;
  }

  it('SRC 欄:融合目標顯示 ◬ 與 AIS 船名;未關聯顯示 ○△;純雷達 ○', async () => {
    const store = storeWith([
      track({ id: 1, ais: { mmsi: 416001001, name: 'ALFA', associated: true, positionOffset_m: 23 } }),
      track({ id: 2, label: '02', ais: { mmsi: 416001002, name: 'BRAVO', associated: false, positionOffset_m: 380 } }),
      track({ id: 3, label: '03' }),
    ]);
    await flush();
    const el = await mount<HTMLElement & { sortKey: string }>('radar-arpa-table', { store });
    el.sortKey = 'id';
    await (el as unknown as { updateComplete: Promise<unknown> }).updateComplete;
    const cells = [...el.shadowRoot!.querySelectorAll('tbody tr td.src')] as HTMLElement[];
    expect(cells.map((c) => c.className)).toEqual(['src fused', 'src candidate', 'src radar']);
    expect(cells[0]!.textContent).toContain('◬');
    expect(cells[0]!.textContent).toContain('ALFA');
    expect(cells[1]!.textContent).toContain('○△');
    expect(cells[1]!.textContent).not.toContain('BRAVO');
    expect(cells[2]!.textContent?.trim()).toBe('○');
    expect(text(el)).toContain('SRC');
  });

  it('目標資料面板:AIS 名稱、MMSI 與位置差;未關聯標警告;純雷達只顯示來源', async () => {
    const store = storeWith([
      track({ id: 1, ais: { mmsi: 416001001, name: 'ALFA', associated: true, positionOffset_m: 23 } }),
      track({ id: 2, label: '02', ais: { mmsi: 416001002, name: 'BRAVO', associated: false, positionOffset_m: 380 } }),
      track({ id: 3, label: '03' }),
    ]);
    store.select(1);
    await flush();
    const el = await mount('radar-target-data', { store });
    expect(text(el)).toContain('ALFA (416001001)');
    expect(text(el)).toContain('23 m');
    expect(text(el)).toContain('◬ 融合');
    store.select(2);
    await flush();
    await (el as unknown as { updateComplete: Promise<unknown> }).updateComplete;
    expect(text(el)).toContain('AIS 未關聯');
    expect(el.shadowRoot!.querySelector('.value.warning')?.textContent).toContain('380 m');
    store.select(3);
    await flush();
    await (el as unknown as { updateComplete: Promise<unknown> }).updateComplete;
    expect(text(el)).toContain('○ 雷達');
    expect(text(el)).not.toContain('AIS');
  });

  it('示範模式:T1 追蹤後為融合目標、T2 為未關聯候選、T3 純雷達', () => {
    const src = new DemoRadarSource({ autoTick: false });
    const store = new RadarStore();
    store.attach(src);
    src.start();
    src.tick(400);
    const byLabel = new Map((store.tracks?.targets ?? []).map((t) => [t.id, t]));
    const sources = [...byLabel.values()].map((t) => targetSource(t).source).sort();
    expect(sources).toContain('fused');
    expect(sources).toContain('candidate');
  });
});
