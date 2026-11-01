// 內建 VHF 角色扮演頻道(lib/vhf.ts):協定編解碼(文字框、二進位音訊框)、狀態機(idle/transmitting/receiving/busy、
// 同頻道過濾、雙守聽、佔用、文字/DSC 訊息列)、轉送網址推導、講評統計。
import { describe, expect, it } from 'vitest';
import {
  DSC_CHANNEL,
  VHF_CHANNELS,
  VhfStateMachine,
  decodeAudioFrame,
  defaultVhfUrl,
  encodeAudioFrame,
  hasVhfActivity,
  isVhfChannel,
  parseVhfMessage,
  serializeVhfMessage,
  vhfSummary,
  type VhfEffect,
  type VhfMessage,
} from '../src/lib/vhf.js';
import type { SimEvent } from '../src/types/event.js';

function sends(effects: VhfEffect[]): VhfMessage[] {
  return effects.filter((e): e is Extract<VhfEffect, { type: 'send' }> => e.type === 'send').map((e) => e.message);
}

function kinds(effects: VhfEffect[]): string[] {
  return effects.map((e) => (e.type === 'send' ? `send:${e.message.kind}` : e.type === 'log' ? `log:${e.item.kind}` : e.type));
}

describe('VHF 協定', () => {
  it('頻道清單與 DSC 頻道', () => {
    expect(VHF_CHANNELS).toEqual(['16', '12', '13', '14', '06', '08', '10', '67', '68', '69', '71', '72', '73', '77']);
    expect(isVhfChannel('16')).toBe(true);
    expect(isVhfChannel('70')).toBe(false);
    expect(isVhfChannel(16)).toBe(false);
    expect(DSC_CHANNEL).toBe('70');
  });

  it('文字框解析:只接受 {type:"vhf", kind};已知欄位依型別取出,未知欄位略過', () => {
    expect(parseVhfMessage('{"type":"vhf","kind":"pttStart","id":"s1","station":"bridge","channel":"16","extra":1}')).toEqual({ type: 'vhf', kind: 'pttStart', id: 's1', station: 'bridge', channel: '16' });
    expect(parseVhfMessage({ type: 'vhf', kind: 'dsc', category: 'distress', text: 'MAYDAY', channel: '70' })).toMatchObject({ kind: 'dsc', category: 'distress', text: 'MAYDAY' });
    expect(parseVhfMessage({ type: 'vhf', kind: 'dsc', category: 'panic' })?.category).toBeUndefined();
    const joined = parseVhfMessage({ type: 'vhf', kind: 'joined', id: 's2', channel: '12', watch: ['16', 7], stations: [{ id: 's1', station: 'bridge', channel: '12', watch: [], transmitting: null }, { nope: true }] });
    expect(joined?.watch).toEqual(['16']);
    expect(joined?.stations).toEqual([{ id: 's1', station: 'bridge', channel: '12', watch: [], transmitting: null }]);
    expect(parseVhfMessage({ type: 'event', kind: 'x' })).toBeNull();
    expect(parseVhfMessage({ type: 'vhf' })).toBeNull();
    expect(parseVhfMessage('not json')).toBeNull();
    expect(parseVhfMessage({ type: 'vhf', kind: 'pttEnd', duration_s: Number.NaN })!.duration_s).toBeUndefined();
    expect(JSON.parse(serializeVhfMessage({ type: 'vhf', kind: 'join', station: 'bridge', channel: '16', watch: [] }))).toEqual({ type: 'vhf', kind: 'join', station: 'bridge', channel: '16', watch: [] });
  });

  it('二進位音訊框:[uint32 LE id 長度][id][payload] 來回一致;壞標頭回 null', () => {
    const payload = new Uint8Array([0x1a, 0x45, 0xdf, 0xa3, 1, 2, 3]);
    const frame = encodeAudioFrame('s12', payload);
    expect(Array.from(frame.subarray(0, 4))).toEqual([3, 0, 0, 0]);
    expect(new TextDecoder().decode(frame.subarray(4, 7))).toBe('s12');
    const decoded = decodeAudioFrame(frame.buffer as ArrayBuffer);
    expect(decoded?.id).toBe('s12');
    expect(Array.from(decoded!.payload)).toEqual(Array.from(payload));
    // 中文站名 id 亦可(UTF-8 長度)
    const zh = encodeAudioFrame('教官-1', payload);
    expect(decodeAudioFrame(zh)?.id).toBe('教官-1');
    expect(decodeAudioFrame(new Uint8Array([1, 0]))).toBeNull();
    expect(decodeAudioFrame(new Uint8Array([0, 0, 0, 0, 1]))).toBeNull();
    expect(decodeAudioFrame(new Uint8Array([9, 0, 0, 0, 1, 2]))).toBeNull();
    expect(decodeAudioFrame(encodeAudioFrame('x'.repeat(65), payload))).toBeNull();
  });

  it('轉送網址由核心網址推得(同主機、埠 8767)', () => {
    expect(defaultVhfUrl('ws://192.168.77.11:8765')).toBe('ws://192.168.77.11:8767/');
    expect(defaultVhfUrl('ws://localhost:8765/')).toBe('ws://localhost:8767/');
    expect(defaultVhfUrl('wss://host:9000/x?y=1')).toBe('wss://host:8767/');
    expect(defaultVhfUrl(undefined)).toBe('ws://localhost:8767/');
    expect(defaultVhfUrl('http://x')).toBe('ws://localhost:8767/');
    expect(defaultVhfUrl('garbage')).toBe('ws://localhost:8767/');
  });
});

function machine(station = 'bridge', channel = '16', watch: string[] = []) {
  let now = 1_000_000;
  const m = new VhfStateMachine({ station, channel, watch, now: () => now, busyHold_ms: 1500 });
  const advance = (ms: number) => (now += ms);
  return { m, advance };
}

function join(m: VhfStateMachine, id: string): void {
  m.connected();
  m.handleMessage({ type: 'vhf', kind: 'joined', id, channel: m.channel, watch: [...m.watch], stations: [] });
}

describe('VhfStateMachine', () => {
  it('連線 → join;joined 後取得 id 並記錄系統訊息;未 join 前不可發話', () => {
    const { m } = machine();
    expect(m.pttDown()).toEqual([]);
    const eff = m.connected();
    expect(sends(eff)).toEqual([{ type: 'vhf', kind: 'join', station: 'bridge', channel: '16', watch: [] }]);
    expect(m.joined).toBe(false);
    const j = m.handleMessage({ type: 'vhf', kind: 'joined', id: 's1', channel: '16', watch: [], stations: [{ id: 's1', station: 'bridge', channel: '16', watch: [], transmitting: null }] });
    expect(kinds(j)).toEqual(['log:system']);
    expect(m.joined).toBe(true);
    expect(m.id).toBe('s1');
    expect(m.roster).toHaveLength(1);
    expect(m.log[0]!.text).toContain('已連線');
  });

  it('PTT 按下/放開:pttStart + 擷取 → 停止擷取 + pttEnd + 本站通話記錄(含秒數)', () => {
    const { m, advance } = machine();
    join(m, 's1');
    const down = m.pttDown();
    expect(kinds(down)).toEqual(['send:pttStart', 'startCapture']);
    expect(sends(down)[0]!.channel).toBe('16');
    expect(m.state).toBe('transmitting');
    expect(m.pttDown()).toEqual([]); // 重複按下無效
    advance(2340);
    const up = m.pttUp();
    expect(kinds(up)).toEqual(['stopCapture', 'send:pttEnd', 'log:call']);
    expect(m.state).toBe('idle');
    const call = m.log.at(-1)!;
    expect(call).toMatchObject({ kind: 'call', direction: 'tx', station: 'bridge', channel: '16', duration_s: 2.3 });
    expect(m.pttUp()).toEqual([]);
  });

  it('同頻道才接收:他站 pttStart 於守聽頻道 → receiving + 播放;音訊只接受該發話者;pttEnd → 停止播放並記錄', () => {
    const { m } = machine('bridge', '12');
    join(m, 's1');
    expect(m.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's9', station: 'vts', channel: '16' })).toEqual([]); // 不同頻道
    expect(m.state).toBe('idle');
    const rx = m.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'pilot', channel: '12' });
    expect(rx).toEqual([{ type: 'startPlayback', from: { id: 's2', station: 'pilot', channel: '12' } }]);
    expect(m.state).toBe('receiving');
    expect(m.receivingFrom?.station).toBe('pilot');
    expect(m.acceptsAudio('s2')).toBe(true);
    expect(m.acceptsAudio('s3')).toBe(false);
    // 接收中按 PTT 無效(半雙工)
    expect(m.pttDown()).toEqual([]);
    // 另一站同時開始(伺服器不會發生,但客戶端不切換)
    expect(m.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's3', station: 'tug', channel: '12' })).toEqual([]);
    expect(m.handleMessage({ type: 'vhf', kind: 'pttEnd', id: 's3', duration_s: 1 })).toEqual([]);
    const end = m.handleMessage({ type: 'vhf', kind: 'pttEnd', id: 's2', station: 'pilot', channel: '12', duration_s: 4.2 });
    expect(kinds(end)).toEqual(['stopPlayback', 'log:call']);
    expect(m.state).toBe('idle');
    expect(m.acceptsAudio('s2')).toBe(false);
    expect(m.log.at(-1)).toMatchObject({ kind: 'call', direction: 'rx', station: 'pilot', channel: '12', duration_s: 4.2 });
    // 自己的 pttStart 回音不接收
    expect(m.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's1', station: 'bridge', channel: '12' })).toEqual([]);
  });

  it('發話中收到 busy:停止擷取、狀態 busy(顯示佔用者),期滿回 idle', () => {
    const { m, advance } = machine();
    join(m, 's1');
    m.pttDown();
    const busy = m.handleMessage({ type: 'vhf', kind: 'busy', channel: '16', by: 's7', byStation: 'vts' });
    expect(kinds(busy)).toEqual(['stopCapture', 'log:busy']);
    expect(m.state).toBe('busy');
    expect(m.busyBy).toEqual({ id: 's7', station: 'vts', channel: '16' });
    expect(m.log.at(-1)).toMatchObject({ kind: 'busy', severity: 'warning', text: '頻道 16 佔用中(vts 發話)' });
    expect(m.pttDown()).toEqual([]); // busy 中不可發話
    expect(m.pttUp()).toEqual([]); // 放開不送 pttEnd(未曾佔用頻道)
    expect(m.tick(advance(1000))).toBe(false);
    expect(m.tick(advance(600))).toBe(true);
    expect(m.state).toBe('idle');
    expect(m.busyBy).toBeNull();
    expect(kinds(m.pttDown())).toEqual(['send:pttStart', 'startCapture']);
  });

  it('雙守聽:主頻道與守聽頻道都收;轉頻道/改守聽送 channel 訊息並與主頻道去重', () => {
    const { m } = machine('instructor', '16', ['12']);
    join(m, 'i1');
    expect(m.watch).toEqual(['12']);
    expect(m.listensTo('16')).toBe(true);
    expect(m.listensTo('12')).toBe(true);
    expect(m.listensTo('13')).toBe(false);
    const rx = m.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'bridge', channel: '12' });
    expect(rx[0]).toMatchObject({ type: 'startPlayback' });
    m.handleMessage({ type: 'vhf', kind: 'pttEnd', id: 's2', duration_s: 1 });
    // 改守聽為 13
    const w = m.setWatch('13');
    expect(sends(w)).toEqual([{ type: 'vhf', kind: 'channel', channel: '16', watch: ['13'] }]);
    expect(m.setWatch('13')).toEqual([]); // 不變
    expect(m.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'bridge', channel: '12' })).toEqual([]);
    // 主頻道轉到 13:守聽與主頻道重複時去除
    const c = m.setChannel('13');
    expect(sends(c)).toEqual([{ type: 'vhf', kind: 'channel', channel: '13', watch: [] }]);
    expect(m.watch).toEqual([]);
    expect(m.setWatch('13')).toEqual([]);
    expect(m.setWatch(null)).toEqual([]);
    expect(m.setChannel('99')).toEqual([]);
    expect(m.setChannel('13')).toEqual([]);
    // 伺服器 channel 確認可修正本站頻道
    m.handleMessage({ type: 'vhf', kind: 'channel', id: 'i1', channel: '14', watch: ['16'] });
    expect(m.channel).toBe('14');
    expect(m.watch).toEqual(['16']);
  });

  it('發話中轉頻道:先結束通話再送 channel;接收中轉離頻道停止播放', () => {
    const { m } = machine();
    join(m, 's1');
    m.pttDown();
    const eff = m.setChannel('12');
    expect(kinds(eff)).toEqual(['stopCapture', 'send:pttEnd', 'log:call', 'send:channel']);
    expect(m.channel).toBe('12');
    m.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'pilot', channel: '12' });
    expect(m.state).toBe('receiving');
    const away = m.setChannel('13');
    expect(kinds(away)).toEqual(['stopPlayback', 'send:channel']);
    expect(m.state).toBe('idle');
    // 雙守聽時轉主頻道但守聽仍涵蓋接收頻道 → 繼續播放
    const { m: i } = machine('instructor', '16', ['12']);
    join(i, 'i1');
    i.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's2', station: 'pilot', channel: '12' });
    expect(kinds(i.setChannel('13'))).toEqual(['send:channel']);
    expect(i.state).toBe('receiving');
    // 取消守聽接收中的頻道 → 停止
    expect(kinds(i.setWatch(null))).toEqual(['stopPlayback', 'send:channel']);
    expect(i.state).toBe('idle');
  });

  it('文字與 DSC:送出訊息、收到時依 id 分本站/他站、DSC 嚴重度;空白不送;未 join 不送', () => {
    const { m } = machine();
    expect(m.sendText('x')).toEqual([]);
    join(m, 's1');
    expect(m.sendText('   ')).toEqual([]);
    expect(sends(m.sendText(' Port Control, this is bridge '))).toEqual([{ type: 'vhf', kind: 'text', channel: '16', text: 'Port Control, this is bridge' }]);
    m.handleMessage({ type: 'vhf', kind: 'text', id: 's1', station: 'bridge', channel: '16', text: 'Port Control, this is bridge' });
    m.handleMessage({ type: 'vhf', kind: 'text', id: 's2', station: 'vts', channel: '16', text: 'Bridge, go ahead' });
    expect(m.handleMessage({ type: 'vhf', kind: 'text', id: 's2', station: 'vts', channel: '16' })).toEqual([]);
    expect(m.log.slice(-2).map((l) => [l.direction, l.station, l.text])).toEqual([
      ['tx', 'bridge', 'Port Control, this is bridge'],
      ['rx', 'vts', 'Bridge, go ahead'],
    ]);
    expect(sends(m.sendDsc('distress', ' MAYDAY '))).toEqual([{ type: 'vhf', kind: 'dsc', category: 'distress', text: 'MAYDAY' }]);
    expect(sends(m.sendDsc('urgency'))).toEqual([{ type: 'vhf', kind: 'dsc', category: 'urgency' }]);
    m.handleMessage({ type: 'vhf', kind: 'dsc', id: 's3', station: 'tug', category: 'distress', text: 'fire', channel: '70' });
    m.handleMessage({ type: 'vhf', kind: 'dsc', id: 's1', station: 'bridge', category: 'urgency', channel: '70' });
    m.handleMessage({ type: 'vhf', kind: 'dsc', id: 's4', station: 'vts', category: 'safety', channel: '70' });
    expect(m.log.slice(-3).map((l) => [l.kind, l.direction, l.severity, l.channel, l.category, l.text])).toEqual([
      ['dsc', 'rx', 'alarm', '70', 'distress', 'fire'],
      ['dsc', 'tx', 'warning', '70', 'urgency', undefined],
      ['dsc', 'rx', 'info', '70', 'safety', undefined],
    ]);
    expect(kinds(m.handleMessage({ type: 'vhf', kind: 'error', message: 'bad' }))).toEqual(['log:system']);
    expect(m.handleMessage({ type: 'vhf', kind: 'whatever' })).toEqual([]);
  });

  it('斷線:停止擷取/播放、回 idle、joined 清除;重連後 connected 再 join;roster 的佔用者查詢', () => {
    const { m } = machine();
    join(m, 's1');
    m.pttDown();
    const d = m.disconnected();
    expect(kinds(d)).toEqual(['stopCapture', 'log:system']);
    expect(m.state).toBe('idle');
    expect(m.joined).toBe(false);
    expect(m.pttDown()).toEqual([]);
    expect(sends(m.connected())[0]!.kind).toBe('join');
    m.handleMessage({ type: 'vhf', kind: 'joined', id: 's5', channel: '16', watch: [] });
    m.handleMessage({ type: 'vhf', kind: 'roster', stations: [{ id: 's5', station: 'bridge', channel: '16', watch: [], transmitting: '16' }, { id: 's6', station: 'vts', channel: '16', watch: ['12'], transmitting: '12' }] });
    expect(m.occupantOf('12')?.station).toBe('vts');
    expect(m.occupantOf('16')).toBeUndefined(); // 自己不算
    m.handleMessage({ type: 'vhf', kind: 'pttStart', id: 's6', station: 'vts', channel: '16' });
    expect(kinds(m.disconnected())).toEqual(['stopPlayback', 'log:system']);
  });

  it('訊息列保留上限', () => {
    const m = new VhfStateMachine({ station: 'b', maxLog: 3, now: () => 0 });
    join(m, 's1');
    for (let i = 0; i < 5; i++) m.handleMessage({ type: 'vhf', kind: 'text', id: 's2', station: 'vts', channel: '16', text: `m${i}` });
    expect(m.log.map((l) => l.text)).toEqual(['m2', 'm3', 'm4']);
  });
});

describe('vhfSummary(講評統計)', () => {
  const ev = (kind: string, data: Record<string, unknown>): SimEvent => ({ kind, tick: 0, t: 0, source: 'vhf', visible: true, seq: -1, data });
  it('通話次數、總秒數、文字/DSC、各站分列;其他事件略過', () => {
    const s = vhfSummary([
      ev('vhfCall', { station: 'bridge', channel: '16', duration_s: 3.25 }),
      ev('vhfCall', { station: 'vts', channel: '16', duration_s: 1.5 }),
      ev('vhfCall', { station: 'bridge', channel: '12', duration_s: 'x' }),
      ev('vhfText', { station: 'vts', channel: '16', text: 'hi' }),
      ev('vhfDsc', { station: 'bridge', category: 'distress' }),
      ev('vhfDsc', { station: 'bridge', category: 'urgency' }),
      { kind: 'collision', tick: 1, t: 0.02, source: 'traffic', visible: true, seq: 1 },
    ]);
    expect(s).toEqual({
      calls: 3,
      duration_s: 4.8,
      texts: 1,
      dsc: 2,
      distress: 1,
      byStation: [
        { station: 'bridge', calls: 2, duration_s: 3.3, texts: 0, dsc: 2 },
        { station: 'vts', calls: 1, duration_s: 1.5, texts: 1, dsc: 0 },
      ],
    });
    expect(hasVhfActivity(s)).toBe(true);
    expect(hasVhfActivity(vhfSummary([]))).toBe(false);
    expect(vhfSummary([ev('vhfText', {})]).byStation[0]!.station).toBe('?');
  });
});
