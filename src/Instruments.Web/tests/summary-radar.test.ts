// summary.radar(規劃書第 8.3 節第 7 類;C# SummaryRadar):解析與講評摘要顯示;舊 summary 無 radar 為 null。
// @vitest-environment happy-dom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { parseAssessmentSummary, parseSummaryRadar } from '../src/lib/assessment.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };

async function mount(tag: string, props: Record<string, unknown>): Promise<El> {
  const el = document.createElement(tag) as El;
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

beforeAll(async () => {
  await import('../src/components/instructor/debrief-summary.js');
});

afterEach(() => {
  document.body.innerHTML = '';
});

describe('summary.radar', () => {
  it('解析 controlCount/acquisitions/rangeChanges 與 byRadar;缺欄位補 0;非物件 null', () => {
    const r = parseSummaryRadar({ controlCount: 5, acquisitions: 2, rangeChanges: 2, byRadar: { s: { controlCount: 3, acquisitions: 1, rangeChanges: 1 }, x: { controlCount: 2, acquisitions: 1, rangeChanges: 1 } } })!;
    expect(r).toEqual({ controlCount: 5, acquisitions: 2, rangeChanges: 2, byRadar: { s: { controlCount: 3, acquisitions: 1, rangeChanges: 1 }, x: { controlCount: 2, acquisitions: 1, rangeChanges: 1 } } });
    expect(parseSummaryRadar({ controlCount: 1, byRadar: { x: { controlCount: 1 }, bad: 3 } })).toEqual({ controlCount: 1, acquisitions: 0, rangeChanges: 0, byRadar: { x: { controlCount: 1, acquisitions: 0, rangeChanges: 0 } } });
    expect(parseSummaryRadar(undefined)).toBeNull();
    expect(parseAssessmentSummary({ version: 1 })!.radar).toBeNull();
    expect(parseAssessmentSummary({ version: 1, radar: { controlCount: 2, acquisitions: 1, rangeChanges: 0, byRadar: {} } })!.radar).toEqual({ controlCount: 2, acquisitions: 1, rangeChanges: 0, byRadar: {} });
  });

  it('講評摘要顯示雷達操作列(依 X/S 分列);無 radar 區塊時不顯示', async () => {
    const coreSummary = parseAssessmentSummary({ version: 1, final: true, radar: { controlCount: 5, acquisitions: 2, rangeChanges: 2, byRadar: { s: { controlCount: 3, acquisitions: 1, rangeChanges: 1 }, x: { controlCount: 2, acquisitions: 1, rangeChanges: 1 } } } });
    const el = await mount('brm-debrief-summary', { coreSummary });
    const t = el.shadowRoot!.textContent!.replace(/\s+/g, ' ');
    expect(t).toContain('雷達操作');
    expect(t).toContain('5 次');
    expect(t).toContain('手動擷取 2 · 量程切換 2');
    expect(t).toContain('S:3 次(擷取 1、量程 1)');
    expect(t).toContain('X:2 次(擷取 1、量程 1)');
    const none = await mount('brm-debrief-summary', { coreSummary: parseAssessmentSummary({ version: 1, final: true }) });
    expect(none.shadowRoot!.textContent).not.toContain('雷達操作');
  });
});
