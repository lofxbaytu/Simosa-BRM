// 警報工作站與責任轉移(MSC.302(87) 第 6、7.3 節;docs/design/sensors-alerts.md 第 3.6 節):
// state.alerts[] 的 station/transferredTo 解析、parseStation(?station=)、canAcknowledgeAt / transferTargets(與核心規則相同)、
// alertAcknowledge 帶 station / alertTransfer 指令、事件文字;<brm-alert-panel> 依站別停用確認、轉移選單;教官站標示。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { SimCommand } from '../src/types/command.js';
import { COMMAND_TYPES } from '../src/types/command.js';
import type { AlertItem } from '../src/types/alerts.js';
import type { SimEvent } from '../src/types/event.js';
import { COMMAND_EVENT } from '../src/components/shared.js';
import {
  alertAcknowledgeCommand,
  alertEventLabel,
  alertEventSeverity,
  alertSilenceCommand,
  alertTransferCommand,
  canAcknowledgeAt,
  parseAlertItems,
  parseStation,
  responsibleStation,
  transferTargets,
} from '../src/lib/alerts.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };

async function mount<T extends HTMLElement = El>(tag: string, props: Record<string, unknown> = {}): Promise<T & El> {
  const el = document.createElement(tag) as T & El;
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

const text = (el: HTMLElement) => el.shadowRoot?.textContent?.replace(/\s+/g, ' ') ?? '';
const q = <T extends Element>(el: HTMLElement, sel: string) => el.shadowRoot!.querySelector(sel) as T;

function captureCommands(): SimCommand[] {
  const cmds: SimCommand[] = [];
  document.body.addEventListener(COMMAND_EVENT, (ev) => cmds.push((ev as CustomEvent<SimCommand>).detail));
  return cmds;
}

function alert(over: Partial<AlertItem>): AlertItem {
  return { id: 'x', number: 1, priority: 'alarm', category: 'B', source: 'gyro', state: 'activeUnacknowledged', text: 'x', raisedAt_s: 0, escalated: false, count: 1, audible: true, ...over };
}

const SHALLOW = alert({ id: 'shallowWater', number: 11, category: 'A', source: 'echoSounder', text: '淺水警報', station: 'bridge' });
const ENGINE = alert({ id: 'mainEngine', number: 9, source: 'engine', text: '主機停車', station: 'bridge' });
const ENGINE_ECR = alert({ ...ENGINE, transferredTo: 'ecr' });
const BNWAS = alert({ id: 'bnwas', number: 14, priority: 'caution', source: 'bnwas', text: 'BNWAS', audible: false });

beforeAll(async () => {
  await import('../src/components/alert-panel.js');
  await import('../src/components/instructor/alert-monitor.js');
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('站別解析與規則(與核心 AlertRecord.CanAcknowledgeAt / AlertCatalogue.TransferTargets 相同)', () => {
  it('parseStation:bridge|portWing|stbdWing|ecr,其餘 bridge', () => {
    expect(parseStation('ecr')).toBe('ecr');
    expect(parseStation(' portWing ')).toBe('portWing');
    expect(parseStation('wheelhouse')).toBe('bridge');
    expect(parseStation(null)).toBe('bridge');
  });

  it('state.alerts[] 的 station/transferredTo:舊核心無欄位 → undefined(= bridge);列舉外記問題', () => {
    const issues: string[] = [];
    const list = parseAlertItems(
      [
        { id: 'mainEngine', number: 9, priority: 'alarm', category: 'B', source: 'engine', state: 'activeUnacknowledged', text: '主機', raisedAt_s: 1, escalated: false, count: 1, audible: true, station: 'bridge', transferredTo: 'ecr' },
        { id: 'gyroFailure', number: 1, priority: 'alarm', category: 'B', source: 'gyro', state: 'activeUnacknowledged', text: '電羅經', raisedAt_s: 2, escalated: false, count: 1, audible: true },
        { id: 'gpsFailure', number: 2, priority: 'alarm', category: 'B', source: 'gnss', state: 'activeUnacknowledged', text: 'GNSS', raisedAt_s: 3, escalated: false, count: 1, audible: true, station: 'engineRoom', transferredTo: null },
      ],
      issues,
    );
    expect(list[0]).toMatchObject({ id: 'mainEngine', station: 'bridge', transferredTo: 'ecr' });
    expect(list[1]!.station).toBeUndefined();
    expect(list[1]!.transferredTo).toBeUndefined();
    expect(responsibleStation(list[1]!)).toBe('bridge');
    expect(responsibleStation(list[0]!)).toBe('ecr');
    expect(issues).toEqual(['alerts[2].station']);
  });

  it('canAcknowledgeAt:Category A 只在負責站;B 任一站;已轉移者只在接收站', () => {
    expect(canAcknowledgeAt(SHALLOW, 'bridge')).toBe(true);
    expect(canAcknowledgeAt(SHALLOW, 'ecr')).toBe(false);
    expect(canAcknowledgeAt(SHALLOW, 'stbdWing')).toBe(false);
    expect(canAcknowledgeAt(ENGINE, 'ecr')).toBe(true);
    expect(canAcknowledgeAt(ENGINE, 'portWing')).toBe(true);
    expect(canAcknowledgeAt(ENGINE_ECR, 'bridge')).toBe(false);
    expect(canAcknowledgeAt(ENGINE_ECR, 'ecr')).toBe(true);
    expect(canAcknowledgeAt(alert({ category: 'A' }), 'bridge')).toBe(true); // 無 station 欄位 = bridge
  });

  it('transferTargets:BNWAS 不可;機艙類來源可含 ecr;其他只在駕駛台三站', () => {
    expect(transferTargets(BNWAS)).toEqual([]);
    expect(transferTargets(ENGINE)).toEqual(['bridge', 'portWing', 'stbdWing', 'ecr']);
    expect(transferTargets(alert({ id: 'steeringGear', source: 'steering' }))).toContain('ecr');
    expect(transferTargets(SHALLOW)).toEqual(['bridge', 'portWing', 'stbdWing']);
    expect(transferTargets(alert({ id: 'gyroFailure', source: 'gyro' }))).not.toContain('ecr');
  });

  it('指令:alertAcknowledge 只在非 bridge 時帶 station(既有格式不變);alertTransfer {id, station};型別在 COMMAND_TYPES 內', () => {
    expect(alertAcknowledgeCommand('shallowWater')).toEqual({ type: 'alertAcknowledge', args: { id: 'shallowWater' } });
    expect(alertAcknowledgeCommand('shallowWater', 'bridge')).toEqual({ type: 'alertAcknowledge', args: { id: 'shallowWater' } });
    expect(alertAcknowledgeCommand('shallowWater', 'ecr')).toEqual({ type: 'alertAcknowledge', args: { id: 'shallowWater', station: 'ecr' } });
    expect(alertAcknowledgeCommand(undefined, 'portWing')).toEqual({ type: 'alertAcknowledge', args: { station: 'portWing' } });
    expect(alertAcknowledgeCommand('all', 'bridge')).toEqual({ type: 'alertAcknowledge' });
    expect(alertTransferCommand(' mainEngine ', 'ecr')).toEqual({ type: 'alertTransfer', args: { id: 'mainEngine', station: 'ecr' } });
    for (const t of ['alertTransfer', 'radarControl']) expect((COMMAND_TYPES as readonly string[]).includes(t)).toBe(true);
    // alertSilence 同樣只在非 bridge 時帶 station(核心只用它決定是否視為駕駛台操作重設 BNWAS;靜音不受站別限制)
    expect(alertSilenceCommand()).toEqual({ type: 'alertSilence' });
    expect(alertSilenceCommand(undefined, 'bridge')).toEqual({ type: 'alertSilence' });
    expect(alertSilenceCommand(undefined, 'ecr')).toEqual({ type: 'alertSilence', args: { station: 'ecr' } });
    expect(alertSilenceCommand('bnwas', 'portWing')).toEqual({ type: 'alertSilence', args: { id: 'bnwas', station: 'portWing' } });
  });

  it('事件文字:alertTransferred 與 radarControl', () => {
    const ev = (kind: string, data: Record<string, unknown>, itemId?: string): SimEvent =>
      ({ kind, tick: 1, t: 1, source: 'engine', visible: true, seq: 1, data, itemId }) as SimEvent;
    expect(alertEventLabel(ev('alertTransferred', { id: 'mainEngine', station: 'ecr', from: 'bridge', transferredTo: 'ecr' }, 'mainEngine'))).toBe('警報責任轉移:主機故障 駕駛台 → 機控室');
    expect(alertEventLabel(ev('alertTransferred', { id: 'mainEngine', station: 'bridge', from: 'ecr', transferredTo: null }, 'mainEngine'))).toBe('警報責任轉移:主機故障 機控室 → 駕駛台(轉回原站)');
    expect(alertEventSeverity(ev('alertTransferred', {}))).toBe('info');
    expect(alertEventLabel(ev('radarControl', { radar: 'x', cmd: 'range', ok: true, value: 3 }, 'x'))).toBe('雷達 X 操作:range 3');
    expect(alertEventLabel(ev('radarControl', { radar: 's', cmd: 'acquire', ok: false, error: '雷達未發射', id: 2 }, 's'))).toBe('雷達 S 操作:acquire 目標 02(失敗:雷達未發射)');
    expect(alertEventSeverity(ev('radarControl', {}))).toBe('info');
  });
});

describe('<brm-alert-panel> 工作站', () => {
  it('預設 bridge:Category A 可確認、顯示站別;ecr 頁面上 Category A 顯示「須在駕駛台確認」,確認指令帶 station', async () => {
    const cmds = captureCommands();
    const bridge = await mount('brm-alert-panel', { alerts: [SHALLOW, ENGINE] });
    expect(text(bridge)).toContain('駕駛台');
    expect(q(bridge, 'button[data-ack="shallowWater"]')).not.toBeNull();
    q<HTMLButtonElement>(bridge, 'button[data-ack="shallowWater"]').click();
    expect(cmds).toEqual([{ type: 'alertAcknowledge', args: { id: 'shallowWater' } }]);
    bridge.remove();

    const ecr = await mount('brm-alert-panel', { alerts: [SHALLOW, ENGINE], station: 'ecr' });
    expect(text(ecr)).toContain('機控室');
    expect(q(ecr, 'button[data-ack="shallowWater"]')).toBeNull();
    expect(q<HTMLElement>(ecr, '[data-lock="shallowWater"]').textContent).toContain('須在駕駛台確認');
    expect(q(ecr, '[data-alert="shallowWater"]').getAttribute('data-station')).toBe('bridge');
    q<HTMLButtonElement>(ecr, 'button[data-ack="mainEngine"]').click();
    q<HTMLButtonElement>(ecr, 'button.ack-all').click();
    q<HTMLButtonElement>(ecr, 'button.silence').click();
    expect(cmds.slice(1)).toEqual([
      { type: 'alertAcknowledge', args: { id: 'mainEngine', station: 'ecr' } },
      { type: 'alertAcknowledge', args: { station: 'ecr' } },
      { type: 'alertSilence', args: { station: 'ecr' } }, // 機控室靜音帶站別:核心不視為駕駛台操作(不重設 BNWAS)
    ]);
  });

  it('已轉移者標示接收站、只能在接收站確認;轉移選單只列核心允許的站並送 alertTransfer', async () => {
    const cmds = captureCommands();
    const bridge = await mount('brm-alert-panel', { alerts: [ENGINE_ECR, SHALLOW, BNWAS] });
    expect(text(bridge)).toContain('→ 機控室');
    expect(q(bridge, 'button[data-ack="mainEngine"]')).toBeNull();
    expect(q<HTMLElement>(bridge, '[data-lock="mainEngine"]').textContent).toContain('須在機控室確認');
    expect(q(bridge, '[data-alert="mainEngine"]').getAttribute('data-station')).toBe('ecr');
    // 轉移選單:mainEngine 已在 ecr → 列駕駛台三站(轉回駕駛台);shallowWater(Category A)只列翼;bnwas 無選單
    const selEngine = q<HTMLSelectElement>(bridge, 'select[data-transfer="mainEngine"]');
    expect([...selEngine.options].map((o) => o.value)).toEqual(['', 'bridge', 'portWing', 'stbdWing']);
    expect(selEngine.options[1]!.textContent).toContain('轉回駕駛台');
    const selShallow = q<HTMLSelectElement>(bridge, 'select[data-transfer="shallowWater"]');
    expect([...selShallow.options].map((o) => o.value)).toEqual(['', 'portWing', 'stbdWing']);
    expect(q(bridge, 'select[data-transfer="bnwas"]')).toBeNull();
    selShallow.value = 'stbdWing';
    selShallow.dispatchEvent(new Event('change'));
    expect(cmds).toEqual([{ type: 'alertTransfer', args: { id: 'shallowWater', station: 'stbdWing' } }]);
    expect(selShallow.value).toBe('');
    bridge.remove();
    const ecr = await mount('brm-alert-panel', { alerts: [ENGINE_ECR], station: 'ecr' });
    expect(q(ecr, 'button[data-ack="mainEngine"]')).not.toBeNull();
    // 已確認者無轉移選單
    const done = await mount('brm-alert-panel', { alerts: [alert({ ...ENGINE, state: 'activeAcknowledged', audible: false })] });
    expect(q(done, 'select.transfer')).toBeNull();
  });

  it('教官站警報監看:Category A 標負責站、已轉移標接收站', async () => {
    const el = await mount('brm-alert-monitor', { alerts: [SHALLOW, ENGINE_ECR], events: [] });
    const t = text(el);
    expect(t).toContain('→ 機控室');
    expect(q<HTMLElement>(el, 'tr[data-alert="shallowWater"] .tag').textContent).toContain('駕駛台');
  });
});
