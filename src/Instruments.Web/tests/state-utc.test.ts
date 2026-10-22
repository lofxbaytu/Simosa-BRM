// state.utc(state.schema.json 選填欄位):ISO 8601 字串 = 情境 startTimeUtc(或載入時的實際時鐘)+ t;有就解析、缺省省略、型別錯誤記入 issues。
import { describe, expect, it } from 'vitest';
import { defaultState, parseState } from '../src/lib/state-parser.js';

function frame(extra: Record<string, unknown> = {}): Record<string, unknown> {
  const base = defaultState('FSB1') as unknown as Record<string, unknown>;
  return { ...base, t: 5.04, tick: 252, ...extra };
}

describe('state.utc', () => {
  it('解析 ISO 8601 字串', () => {
    const r = parseState(frame({ utc: '2026-01-01T02:00:05.040Z' }));
    expect(r).not.toBeNull();
    expect(r!.state.utc).toBe('2026-01-01T02:00:05.040Z');
    expect(r!.issues).not.toContain('utc');
    expect(new Date(r!.state.utc!).getTime()).toBe(Date.UTC(2026, 0, 1, 2, 0, 5, 40));
  });

  it('缺省時省略,且不從前一幀繼承', () => {
    const prev = parseState(frame({ utc: '2026-01-01T02:00:05.040Z' }))!.state;
    const r = parseState(frame(), prev);
    expect(r!.state.utc).toBeUndefined();
    expect(r!.issues).not.toContain('utc');
  });

  it('型別錯誤記入 issues、其他欄位不受影響', () => {
    const r = parseState(frame({ utc: 12345 }));
    expect(r!.state.utc).toBeUndefined();
    expect(r!.issues).toContain('utc');
    expect(r!.state.t).toBe(5.04);
    expect(parseState(frame({ utc: null }))!.issues).not.toContain('utc');
    expect(parseState(frame({ utc: '' }))!.issues).toContain('utc');
  });
});
