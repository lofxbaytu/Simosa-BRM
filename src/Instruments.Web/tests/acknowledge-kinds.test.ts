// 確認種類(happy-dom):情境必要事件用到的 acknowledge kind 都必須能從教官站送出(不被改成 other),
// 且時間表面板的快速鈕會依情境補上(F01 的 prediction、E03 的 lookoutReport 等)。
// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ACKNOWLEDGE_KINDS } from '../src/types/event.js';
import { ACK_KIND_LABEL } from '../src/lib/sim-event.js';
import { acknowledgeCommand } from '../src/lib/instructor-command.js';
import { parseScenario } from '../src/lib/scenario.js';
import '../src/components/instructor/timeline-panel.js';
import type { TimelinePanel } from '../src/components/instructor/timeline-panel.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const scenariosDir = path.resolve(here, '../../../data/scenarios');
const files = readdirSync(scenariosDir).filter((f) => f.endsWith('.yaml'));

function ackKinds(file: string): string[] {
  const sc = parseScenario(readFileSync(path.join(scenariosDir, file), 'utf8'), file);
  const kinds = new Set<string>();
  for (const r of sc.assessment.required) for (const e of r.expect) if (e.type === 'acknowledge') kinds.add(e.kind);
  return [...kinds];
}

describe('acknowledge 種類', () => {
  it('每個情境用到的種類都在 ACKNOWLEDGE_KINDS、有中文標籤,送出時保持原種類', () => {
    const used = new Set(files.flatMap(ackKinds));
    expect(used.size).toBeGreaterThan(5);
    for (const k of used) {
      expect(ACKNOWLEDGE_KINDS as readonly string[]).toContain(k);
      expect(ACK_KIND_LABEL[k]).toBeTruthy();
      expect(acknowledgeCommand({ kind: k }).args).toMatchObject({ kind: k });
    }
  });

  it('時間表面板快速鈕依情境補上 prediction(F01)與 lookoutReport(E03)', async () => {
    for (const [file, kind] of [['F01_FSB1_full.yaml', 'prediction'], ['E03_restricted_visibility_strait.yaml', 'lookoutReport']] as const) {
      const sc = parseScenario(readFileSync(path.join(scenariosDir, file), 'utf8'), file);
      const el = document.createElement('brm-timeline-panel') as TimelinePanel & { updateComplete: Promise<unknown> };
      Object.assign(el, { timeline: sc.timeline, events: [], assessment: sc.assessment, simTime: 0 });
      document.body.appendChild(el);
      await el.updateComplete;
      expect(el.quickAcks()).toContain(kind);
      expect(el.shadowRoot!.querySelector(`button.ack[data-ack="${kind}"]`)).not.toBeNull();
      el.remove();
    }
  });
});
