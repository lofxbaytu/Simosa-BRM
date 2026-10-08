// 教官站指令建構:loadScenario / setEnvironment / injectFault / clearFault / timeScale / snapshot / restore / 自船覆寫。
import { describe, expect, it } from 'vitest';
import { serializeCommand, timeScaleCommand } from '../src/lib/command.js';
import {
  DEFAULT_FAULTS,
  TIME_SCALES,
  clearFaultCommand,
  faultCatalogue,
  injectFaultCommand,
  loadScenarioCommand,
  mergeFaultCatalogue,
  ownShipOverrideCommand,
  parseFaultNamesCs,
  restoreCommand,
  setEnvironmentCommand,
  snapshotCommand,
} from '../src/lib/instructor-command.js';

describe('教官站指令', () => {
  it('loadScenario:value 為路徑(C# 核心)、args 含 id 與 path', () => {
    const cmd = loadScenarioCommand('E01_baseline', 'data/scenarios/E01_baseline.yaml');
    expect(cmd).toEqual({ type: 'loadScenario', value: 'data/scenarios/E01_baseline.yaml', args: { id: 'E01_baseline', path: 'data/scenarios/E01_baseline.yaml' } });
    expect(serializeCommand(cmd)).toBe('{"type":"loadScenario","value":"data/scenarios/E01_baseline.yaml","args":{"id":"E01_baseline","path":"data/scenarios/E01_baseline.yaml"}}');
  });

  it('setEnvironment:只放有給的欄位並正規化', () => {
    expect(setEnvironmentCommand({ wind: { trueSpeed: 20, trueDir: 400 } })).toEqual({ type: 'setEnvironment', args: { wind: { trueSpeed: 20, trueDir: 40 } } });
    expect(setEnvironmentCommand({ current: { set: -95, drift: -1 }, waterDepth: 12.34 })).toEqual({
      type: 'setEnvironment',
      args: { current: { set: 265, drift: 0 }, waterDepth: 12.3 },
    });
    expect(setEnvironmentCommand({ wind: { gustiness: 1.7 }, visibility_nm: 0.25 })).toEqual({ type: 'setEnvironment', args: { wind: { gustiness: 1 }, visibility_nm: 0.3 } });
    expect(setEnvironmentCommand({ waterDepth: 0 })).toBeNull();
    expect(setEnvironmentCommand({})).toBeNull();
    // 與 command.schema.json 的範例一致
    const ex = setEnvironmentCommand({ wind: { trueSpeed: 20, trueDir: 40 }, current: { set: 265, drift: 2.5 }, waterDepth: 12 });
    expect(serializeCommand(ex!)).toBe('{"type":"setEnvironment","args":{"wind":{"trueSpeed":20,"trueDir":40},"current":{"set":265,"drift":2.5},"waterDepth":12}}');
  });

  it('injectFault / clearFault', () => {
    expect(injectFaultCommand('steeringGear')).toEqual({ type: 'injectFault', value: 'steeringGear' });
    expect(() => injectFaultCommand('  ')).toThrow(RangeError);
    expect(clearFaultCommand('mainEngine')).toEqual({ type: 'clearFault', value: 'mainEngine' });
    expect(clearFaultCommand()).toEqual({ type: 'clearFault' });
    expect(serializeCommand(clearFaultCommand())).toBe('{"type":"clearFault"}');
  });

  it('timeScale 選項皆在 0–10 內', () => {
    expect(TIME_SCALES).toEqual([0.5, 1, 2, 5, 10]);
    for (const s of TIME_SCALES) expect(timeScaleCommand(s)).toEqual({ type: 'timeScale', value: s });
  });

  it('snapshot / restore:C# 讀 args.tick、Python 讀 value 名稱', () => {
    expect(snapshotCommand(12000.7, 240.014)).toEqual({ type: 'snapshot', value: 'snap-12000', args: { tick: 12000, t: 240.014 } });
    expect(restoreCommand(12000)).toEqual({ type: 'restore', value: 'snap-12000', args: { tick: 12000 } });
    expect(serializeCommand(restoreCommand(12000))).toBe('{"type":"restore","value":"snap-12000","args":{"tick":12000}}');
  });

  it('自船覆寫以 reset 附 args 送出,只放有給的欄位', () => {
    expect(ownShipOverrideCommand({ x: 100.26, y: -50, heading: 365, speed: 6.55, loading: 'full', tugs: 2.9 })).toEqual({
      type: 'reset',
      args: { x: 100.3, y: -50, heading: 5, speed: 6.6, loading: 'full', tugs: 2 },
    });
    expect(ownShipOverrideCommand({ lat: 23.8, lon: 120.05 })).toEqual({ type: 'reset', args: { lat: 23.8, lon: 120.05 } });
    expect(ownShipOverrideCommand({})).toEqual({ type: 'reset', args: {} });
  });
});

describe('故障目錄', () => {
  const cs = `
namespace SimosaBRM.SimCore.Engine;
public static class FaultNames
{
    /// <summary>舵機故障:舵角停在目前位置</summary>
    public const string SteeringGear = "steeringGear";
    /// <summary>主機故障</summary>
    public const string MainEngine = "mainEngine";
    public const string Blackout = "blackout";
    public const string NewOne = "hydraulicPump";
}`;
  it('parseFaultNamesCs 取出名稱與註解', () => {
    expect(parseFaultNamesCs(cs)).toEqual({ steeringGear: '舵機故障:舵角停在目前位置', mainEngine: '主機故障', blackout: '', hydraulicPump: '' });
    expect(parseFaultNamesCs('nothing here')).toEqual({});
  });
  it('mergeFaultCatalogue:核心有的標 modelled,核心新增的名稱補到清單尾', () => {
    const merged = mergeFaultCatalogue(parseFaultNamesCs(cs));
    const byName = Object.fromEntries(merged.map((f) => [f.name, f]));
    expect(byName['steeringGear']!.modelled).toBe(true);
    expect(byName['mainEngine']!.modelled).toBe(true);
    expect(byName['blackout']!.modelled).toBe(true);
    expect(byName['bowThruster']!.modelled).toBe(false); // 測試用的 cs 片段沒有列出
    expect(byName['gyroDrift']!.modelled).toBe(false);
    expect(byName['hydraulicPump']).toEqual({ name: 'hydraulicPump', label: 'hydraulicPump', modelled: true });
    expect(merged.length).toBe(DEFAULT_FAULTS.length + 1);
  });
  it('faultCatalogue 於建置環境讀到 SimCore FaultNames.cs 時以其為準', () => {
    const { faults, source } = faultCatalogue();
    expect(faults.map((f) => f.name)).toEqual(expect.arrayContaining(['steeringGear', 'mainEngine', 'bowThruster', 'gyroDrift', 'gpsJump', 'radarFailure', 'blackout']));
    if (source === 'simcore') {
      for (const n of ['steeringGear', 'mainEngine', 'bowThruster']) expect(faults.find((f) => f.name === n)!.modelled).toBe(true);
    }
  });
});
