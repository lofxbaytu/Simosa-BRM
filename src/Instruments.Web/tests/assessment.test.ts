// 評估規則(情境 assessment)與核心 summary(設計文件第 4.8 節 JSON 範例 tests/fixtures/summary-E07.json)的解析。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { parse as parseYaml } from 'yaml';
import { assessmentFromFrame, isUkcLimitSpec, parseAssessmentRules, parseAssessmentSummary, parseRecordParameterSet, requiredRulesAfterFault } from '../src/lib/assessment.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const e07 = parseYaml(readFileSync(path.resolve(here, 'fixtures/E07_steering_failure.yaml'), 'utf-8')) as Record<string, unknown>;
const summaryLine = JSON.parse(readFileSync(path.resolve(here, 'fixtures/summary-E07.json'), 'utf-8')) as { kind: string; tick: number; summary: Record<string, unknown> };

describe('parseAssessmentRules(E07)', () => {
  const issues: string[] = [];
  const rules = parseAssessmentRules(e07['assessment'], issues);

  it('參數集路徑、minCpa、會遇/UKC/操縱門檻(參數引用保留鍵)', () => {
    expect(issues).toEqual([]);
    expect(rules.parameterSet).toBe('data/parameters/company-procedures.yaml');
    expect(rules.minCpa_nm).toEqual({ param: 'navigation.cpa.channel_nm' });
    expect(rules.encounter).toEqual({ detectionRange_nm: { value: 3 }, substantialCourseChange_deg: { param: 'navigation.encounter.substantialAlteration_deg' } });
    expect(rules.ukc).toEqual({ min: { metres: { param: 'navigation.ukc.minimum_m' } } });
    expect(rules.manoeuvring).toEqual({ rotLimit_degPerMin: { param: 'manoeuvring.rotLimit_degPerMin' } });
  });

  it('硬性失敗、容許帶(2x、below)、必要事件(any/acknowledge/event)、評分', () => {
    expect(rules.hardFail.map((h) => [h.id, h.rule])).toEqual([
      ['grounding', 'grounding'],
      ['collision', 'collision'],
      ['noGo', 'zoneEntered'],
      ['ukcHard', 'ukc'],
    ]);
    expect(rules.hardFail[2]!.zoneId).toBe('anchorage_S');
    expect(isUkcLimitSpec(rules.hardFail[3]!.lessThan)).toBe(true);
    expect(rules.bands.map((b) => b.id)).toEqual(['xtdMax', 'xtdTime', 'ukcTime', 'speed', 'minCpa']);
    expect(rules.bands[0]).toMatchObject({ metric: 'track.maxXtd_m', direction: 'above', warn: { param: 'navigation.xtd.channel_m' }, fail: { multiplier: 2 }, penalty: 10, failPenalty: 25 });
    expect(rules.bands[4]).toMatchObject({ direction: 'below', warn: { param: 'navigation.cpa.channel_nm' }, fail: { param: 'navigation.cpa.channelHardFail_nm' } });
    expect(rules.required.map((r) => r.id)).toEqual(['steeringFirstAction', 'notifyEcr', 'emergencySteering', 'vtsReport']);
    expect(rules.required[0]!.expect).toEqual([
      { type: 'action', name: 'telegraphChange' },
      { type: 'action', name: 'rpmOrder' },
      { type: 'action', name: 'thrusterOrder' },
      { type: 'action', name: 'autopilotOff' },
      { type: 'acknowledge', kind: 'steeringMode' },
    ]);
    expect(rules.required[0]!.within_s).toEqual({ param: 'emergency.steeringFailure.firstAction_s' });
    expect(rules.required[0]!.latePenaltyPer_s).toBe(0.5);
    expect(rules.required[2]!.expect).toEqual([{ type: 'event', kind: 'timelineTriggered', itemId: 'emergencySteering' }]);
    expect(rules.required[3]!.after).toEqual({ event: 'reportPointPassed', waypoint: 'WP3' });
    expect(rules.required[3]!.within_s).toEqual({ value: 120 });
    expect(rules.scoring).toEqual({ start: { value: 100 }, passMark: { param: 'assessment.passMark' } });
    expect(rules.ruleCount).toBe(13);
  });

  it('requiredRulesAfterFault 依 itemId 篩選;舊格式(只有 minCpa_nm)與缺 assessment 皆可', () => {
    expect(requiredRulesAfterFault(rules, 'steeringFail').map((r) => r.id)).toEqual(['steeringFirstAction', 'notifyEcr', 'emergencySteering']);
    expect(requiredRulesAfterFault(rules, 'other')).toEqual([]);
    const old = parseAssessmentRules({ minCpa_nm: 0.5 });
    expect(old.minCpa_nm).toEqual({ value: 0.5 });
    expect(old.ruleCount).toBe(0);
    expect(parseAssessmentRules(undefined).ruleCount).toBe(0);
    const bad: string[] = [];
    parseAssessmentRules({ bands: [{ id: 'x' }], required: [{ id: 'r', expect: { foo: 1 } }] }, bad);
    expect(bad).toEqual(['assessment.bands[0] 缺 metric', 'assessment.required[0] 缺 after', 'assessment.required[0].expect 需為可偵測項名稱、acknowledge 或 event']);
  });
});

describe('parseAssessmentSummary(設計文件 4.8 節範例)', () => {
  const s = parseAssessmentSummary(summaryLine.summary)!;

  it('評分、參數集、階段、總覽', () => {
    expect(summaryLine.kind).toBe('summary');
    expect(s.version).toBe(1);
    expect(s.advisory).toBe(true);
    expect(s.final).toBe(true);
    expect(s.scenarioId).toBe('E07_steering_failure');
    expect(s.parameterSet).toEqual({ id: 'simosa-company-procedures', version: '2026.10.1', sha256: '…' });
    expect(s.phases.map((p) => p.phase)).toEqual(['normal', 'degraded', 'emergency']);
    expect(s.general).toEqual({ duration_s: 3600, freezes: 2, distance_nm: 5.8, avgSog_kn: 5.9, maxSog_kn: 8.4 });
    expect(s.assessment.score).toBe(85);
    expect(s.assessment.passMark).toBe(70);
    expect(s.assessment.passed).toBe(true);
    expect(s.assessment.hardFailed).toBe(false);
  });

  it('避碰/航跡/UKC/操縱/故障應變', () => {
    expect(s.collisionAvoidance.minCpa_nm).toBe(0.62);
    expect(s.collisionAvoidance.targets[0]).toMatchObject({ targetId: 'T1', situation: 'headOn', timeToAction_s: 36, firstAction: 'rudder', courseChange_deg: 32, substantial: true });
    expect(s.track).toMatchObject({ maxXtd_m: 74, maxXtdLeg: 1, timeOutsideCorridor_s: 41, corridorExcursions: 1 });
    expect(s.track!.waypoints).toEqual([{ name: 'WP2', passedAt_s: 418, xtd_m: 6, distance_m: 22 }]);
    expect(s.ukc).toEqual({ min_m: 1.42, minAt_s: 2210, minSquat_m: 0.31, timeBelowLimit_s: 0, excursions: 0, groundings: 0, limit_m: 0.57, limitSource: 'navigation.ukc.channel_fraction' });
    expect(s.manoeuvring.rudderDistribution_s).toEqual({ '0-5': 2900, '5-10': 410, '10-20': 200, '20-35': 45, '>35': 0 });
    expect(s.manoeuvring.telegraphOrders).toHaveLength(2);
    expect(s.manoeuvring.autopilotSwitches).toBe(2);
    expect(s.faultResponses[0]).toMatchObject({ fault: 'steeringGear', itemId: 'steeringFail', injectedAt_s: 420, source: 'timeline', firstAction: { kind: 'telegraph', t_s: 455, dt_s: 35 }, cleared_s: 1300, withinFirstAction: true, withinNotifyEcr: true });
    expect(s.faultResponses[0]!.acknowledgements).toEqual([
      { kind: 'verbal', t_s: 431, dt_s: 11, role: 'officer' },
      { kind: 'notifyEcr', t_s: 502, dt_s: 82, role: 'master' },
    ]);
    expect(s.events).toEqual({ total: 57, byKind: { timelineTriggered: 4, faultInjected: 1, cpaAlarm: 0, xtdExceeded: 1 } });
  });

  it('容許帶、必要事件與參數來源', () => {
    expect(s.assessment.bands[0]).toEqual({ id: 'xtdMax', metric: 'track.maxXtd_m', value: 74, warn: 50, fail: 100, state: 'warn', penalty: 10, paramKey: 'navigation.xtd.channel_m', source: 'SMS-NAV-012', revision: '2', clause: '4.1' });
    expect(s.assessment.required.map((r) => [r.id, r.state])).toEqual([
      ['steeringResponse', 'met'],
      ['notifyEcr', 'met'],
      ['vtsReport', 'missed'],
    ]);
    expect(s.assessment.required[0]).toMatchObject({ dt_s: 35, within_s: 60, by: 'telegraph', paramKey: 'emergency.steeringFailure.firstAction_s', source: 'SMS-EMG-004', revision: '1' });
    expect(s.assessment.parameters).toEqual([{ key: 'navigation.xtd.channel_m', value: 50, unit: 'm', source: 'SMS-NAV-012', revision: '2', clause: '4.1' }]);
    expect(s.raw).toBe(summaryLine.summary);
  });

  it('缺欄位寬容;非物件回傳 null;assessment 框帶 final', () => {
    const min = parseAssessmentSummary({})!;
    expect(min.final).toBe(false);
    expect(min.track).toBeNull();
    expect(min.assessment.hardFails).toEqual([]);
    expect(min.manoeuvring.telegraphOrders).toEqual([]);
    expect(parseAssessmentSummary('x')).toBeNull();
    const framed = assessmentFromFrame({ type: 'assessment', final: false, summary: summaryLine.summary })!;
    expect(framed.final).toBe(false);
    expect(assessmentFromFrame({ type: 'event' })).toBeNull();
  });

  it('header.parameterSet 的 resolved 清單', () => {
    const ps = parseRecordParameterSet({ id: 'p', version: '2026.10.1', path: 'data/parameters/company-procedures.yaml', sha256: 'abc', resolved: [{ path: 'assessment.minCpa_nm', key: 'navigation.cpa.coastal_nm', value: 0.5, unit: 'nm', source: 'DWO-007', revision: '3' }, 'junk'] })!;
    expect(ps.version).toBe('2026.10.1');
    expect(ps.resolved).toEqual([{ path: 'assessment.minCpa_nm', key: 'navigation.cpa.coastal_nm', value: 0.5, unit: 'nm', source: 'DWO-007', revision: '3' }]);
    expect(parseRecordParameterSet(undefined)).toBeNull();
  });
});
