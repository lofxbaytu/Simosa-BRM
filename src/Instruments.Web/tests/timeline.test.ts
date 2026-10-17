// 情境 YAML 擴充欄位(設計文件第 1 節):timeline / route / zones / briefing / ${params:…} 的解析與摘要,
// 以設計文件第 8.2 節的 E07 範例(tests/fixtures;參數引用加引號)為準;舊格式(E01 無擴充欄位)仍可解析。
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { parse as parseYaml } from 'yaml';
import {
  actionSummary,
  collectParamRefs,
  conditionSummary,
  formatNumberOrParam,
  geoPointToEnu,
  isParamRef,
  latLonToEnu,
  numberOrParam,
  paramRefKey,
  parseBriefing,
  parseRoute,
  parseTimeline,
  parseTriggerCondition,
  parseZones,
} from '../src/lib/timeline.js';
import { briefingText, extensionSummary, parseScenario } from '../src/lib/scenario.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const e07Text = readFileSync(path.resolve(here, 'fixtures/E07_steering_failure.yaml'), 'utf-8');
const e07 = parseYaml(e07Text) as Record<string, unknown>;
const e01Path = path.resolve(here, '../../../data/scenarios/E01_baseline.yaml');
/** 舊格式情境(無 timeline/route/zones/briefing 物件;E01 的 2026-10-08 版) */
const OLD_FORMAT = `
id: E01_old
name: 舊格式
ship: { id: FSB1, loading: ballast }
origin: { lat: 23.80, lon: 120.05 }
initial: { position: { lat: 23.80, lon: 120.05 }, heading: 0, speed: 7.8, telegraph: HAH }
environment: { wind: { trueSpeed: 15, trueDir: 45 }, current: { set: 200, drift: 0.5 }, waterDepth: 30 }
assessment: { minCpa_nm: 0.5 }
targets:
  - { id: T1, initial: { position: { x: 0, y: 9000 }, heading: 180, speed: 9 } }
`;

describe('參數引用', () => {
  it('isParamRef / paramRefKey / numberOrParam', () => {
    expect(isParamRef('${params:navigation.cpa.coastal_nm}')).toBe(true);
    expect(isParamRef('${params:bad key}')).toBe(false);
    expect(isParamRef(0.5)).toBe(false);
    expect(paramRefKey(' ${params:a.b-c_m} ')).toBe('a.b-c_m');
    expect(numberOrParam(0.5)).toEqual({ value: 0.5 });
    expect(numberOrParam('12')).toEqual({ value: 12 });
    expect(numberOrParam('${params:x.y_s}')).toEqual({ param: 'x.y_s' });
    expect(numberOrParam('abc')).toBeNull();
    expect(numberOrParam(Number.NaN)).toBeNull();
  });

  it('formatNumberOrParam 顯示參數引用而非 NaN', () => {
    expect(formatNumberOrParam({ param: 'navigation.xtd.channel_m' }, 'm')).toBe('參數 navigation.xtd.channel_m');
    expect(formatNumberOrParam({ value: 50 }, 'm')).toBe('50 m');
    expect(formatNumberOrParam({ value: 0.5 }, 'nm')).toBe('0.5 nm');
    expect(formatNumberOrParam(null)).toBe('—');
  });

  it('collectParamRefs 走訪整個情境', () => {
    const refs = collectParamRefs(e07);
    expect(refs.length).toBeGreaterThanOrEqual(14);
    expect(refs).toContainEqual({ path: 'route.xtdLimit_m', key: 'navigation.xtd.channel_m' });
    expect(refs).toContainEqual({ path: 'assessment.required[0].within_s', key: 'emergency.steeringFailure.firstAction_s' });
  });
});

describe('parseTimeline(E07)', () => {
  const issues: string[] = [];
  const items = parseTimeline(e07['timeline'], issues);

  it('7 個項目,無問題;欄位預設正確', () => {
    expect(issues).toEqual([]);
    expect(items.map((i) => i.id)).toEqual(['pilotBoarded', 'vtsCall', 'steeringFail', 'ecrPrompt', 'emergencySteering', 'speedCoach', 'end']);
    const sf = items[2]!;
    expect(sf.phase).toBe('degraded');
    expect(sf.visibility).toBe('instructor');
    expect(sf.repeat).toEqual({ mode: 'once' });
    expect(sf.manual).toBe(false);
    expect(sf.armed).toBe(true);
    expect(sf.holdFor_s).toBe(0);
    expect(sf.actions.map((a) => a.kind)).toEqual(['injectFault', 'voiceScript', 'message']);
    expect(items[5]!.repeat).toEqual({ mode: 'every', every_s: 60, max: 3 });
    const manual = items[4]!;
    expect(manual.manual).toBe(true);
    expect(manual.when).toBeNull();
  });

  it('遞迴條件 all/not + leaf 的摘要', () => {
    expect(conditionSummary(items[2]!.when)).toBe('航段 ≥ 1 且 時間 ≥ 90 s 自 routeLegChanged 航段 1 起');
    expect(conditionSummary(items[3]!.when)).toBe('故障 steeringGear 啟用中 且 曾發生 requiredEventMissed steeringFirstAction');
    expect(conditionSummary(items[6]!.when)).toBe('距 航點 BERTH < 0.15 nm');
    expect(conditionSummary(items[5]!.when)).toBe('曾發生 speedLimitExceeded');
    expect(conditionSummary(null)).toBe('(手動)');
  });

  it('其餘 leaf 型別與 any 組合的摘要', () => {
    const c = parseTriggerCondition({
      any: [
        { type: 'ownHeading', greaterThan: 350, lessThan: 10 },
        { type: 'ownSpeed', metric: 'stw', lessThan: '${params:port.x_kn}' },
        { type: 'ukc', lessThanFractionOfDraft: 0.1 },
        { type: 'xtd', greaterThan: 60, side: 'port' },
        { type: 'zone', id: 'inner', inside: false },
        { type: 'phase', equals: 'emergency' },
        { type: 'telegraph', in: ['STOP', 'DSAS'] },
        { type: 'autopilot', enabled: false },
        { type: 'event', kind: 'cpaAlarm', targetId: 'T1', count: { atLeast: 2 } },
        { type: 'routeLeg', passedWaypoint: 'WP2' },
        { type: 'manual' },
      ],
    })!;
    expect(conditionSummary(c)).toBe(
      '航向 > 350 ° 且 < 10 ° 或 STW < 參數 port.x_kn 或 UKC < 吃水 × 0.1 或 XTD > 60 m(左) 或 不在區域 inner 內 或 階段 = emergency 或 車鐘在 STOP/DSAS 或 自動舵關 或 曾發生 cpaAlarm T1 ≥ 2 次 或 通過航點 WP2 或 手動',
    );
  });

  it('動作摘要;不允許的動作與未知動作記入 issues', () => {
    expect(items[2]!.actions.map(actionSummary)).toEqual(['注入故障 steeringGear', '語音 helmsman:舵不聽使喚!舵令右十,舵角指示不動。', '訊息 已注入舵機故障(舵卡在目前角);期望 60 s 內車令/側推/確認,120 s 內通知機控室(SMS-EMG-004)']);
    expect(items[4]!.actions.map(actionSummary)).toEqual(['清除故障 steeringGear', '語音 ecr:駕駛台,舵機房已切換應急操舵,請以電話下舵令。', '階段 → normal']);
    expect(items[6]!.actions.map(actionSummary)).toEqual(['結束練習:到達泊位接近區']);
    const bad: string[] = [];
    const t = parseTimeline([{ id: 'x', when: { type: 'time', atLeast: 1 }, actions: [{ reset: {} }, { foo: 1 }, { setEnvironment: { wind: { trueSpeed: 20, trueDir: 10 }, ramp_s: 180 } }, { injectFault: { name: 'steeringGear', args: { jamAt_deg: 15 } } }] }], bad);
    expect(bad).toEqual(['timeline[0].actions[0] 不允許的動作 reset', 'timeline[0].actions[1] 未知的動作 foo']);
    expect(t[0]!.actions.map(actionSummary)).toEqual(['reset {}', 'foo 1', '環境 風 20 kn/10 °(180 s 漸變)', '注入故障 steeringGear']);
  });

  it('缺 id、非 manual 缺 when、id 重複與未知條件型別記入 issues', () => {
    const issues: string[] = [];
    parseTimeline([{ name: 'a', actions: [] }, { id: 'b', when: { type: 'warp' }, actions: [] }, { id: 'b', manual: true, actions: [] }, 'nope'], issues);
    expect(issues).toEqual(['timeline[0] 缺 id', 'timeline[0] 非 manual 項目需有 when', 'timeline[1].when 未知的條件型別 warp', 'timeline[3] 不是物件', 'timeline id 重複:b']);
    expect(parseTimeline(undefined)).toEqual([]);
    const notArray: string[] = [];
    expect(parseTimeline({ a: 1 }, notArray)).toEqual([]);
    expect(notArray).toEqual(['timeline 不是陣列']);
  });
});

describe('parseRoute / parseZones(E07)', () => {
  it('航點、走廊半寬(參數引用)、檢查點/報告點/到達半徑', () => {
    const issues: string[] = [];
    const r = parseRoute(e07['route'], issues)!;
    expect(issues).toEqual([]);
    expect(r.name).toBe('麥寮進港航道');
    expect(r.xtdLimit_m).toEqual({ param: 'navigation.xtd.channel_m' });
    expect(r.startLeg).toBe(0);
    expect(r.waypoints.map((w) => w.name)).toEqual(['WP1', 'WP2', 'WP3', 'BERTH']);
    expect(r.waypoints[1]).toMatchObject({ lat: 23.796, lon: 120.122, checkpoint: true, speedLimit_kn: { param: 'port.mailiao.speedLimit_kn' } });
    expect(r.waypoints[2]!.report).toBe('麥寮 VTS 報告點(VHF 13)');
    expect(r.waypoints[3]!.arrivalRadius_m).toBe(120);
  });

  it('區域:型別、多邊形、UKC 政策與水深覆寫、速限', () => {
    const issues: string[] = [];
    const z = parseZones(e07['zones'], issues);
    expect(issues).toEqual([]);
    expect(z.map((x) => [x.id, x.type, x.polygon.length])).toEqual([
      ['channel', 'ukcPolicy', 8],
      ['anchorage_S', 'noGo', 4],
      ['inner', 'speedLimit', 4],
    ]);
    expect(z[0]!.waterDepth_m).toBe(12.5);
    expect(z[0]!.ukcMin).toEqual({ fractionOfDraft: { param: 'navigation.ukc.channel_fraction' } });
    expect(z[2]!.speedLimit_kn).toEqual({ param: 'port.mailiao.innerSpeedLimit_kn' });
  });

  it('航點少於 2、多邊形少於 3 點、未知 type 記入 issues', () => {
    const issues: string[] = [];
    parseRoute({ waypoints: [{ name: 'A', lat: 1, lon: 2 }, { name: 'B' }] }, issues);
    parseZones([{ id: 'z', type: 'magic', polygon: [{ x: 0, y: 0 }, { x: 1, y: 0 }] }], issues);
    expect(issues).toEqual(['route.waypoints[1] 需 lat/lon 或 x/y', 'route 航點少於 2', 'zones[0] 未知的 type magic', 'zones[0] 多邊形少於 3 點']);
    expect(parseRoute(undefined)).toBeNull();
    expect(parseZones(null)).toEqual([]);
  });

  it('經緯度 → ENU 平面近似;x/y 點直接用', () => {
    const o = { lat: 23.78, lon: 120.1 };
    const p = latLonToEnu(o, 23.79, 120.11);
    expect(p.y).toBeCloseTo(1113.2, 0);
    expect(p.x).toBeCloseTo(1113.2 * Math.cos((23.78 * Math.PI) / 180), 0);
    expect(geoPointToEnu({ x: 5, y: 6 }, null)).toEqual({ x: 5, y: 6 });
    expect(geoPointToEnu({ lat: 1, lon: 2 }, null)).toBeNull();
  });
});

describe('parseBriefing', () => {
  it('字串等同 {text};物件含附件、角色、最少人數', () => {
    expect(parseBriefing('  hello ')).toEqual({ text: 'hello', attachments: [], roles: [] });
    const issues: string[] = [];
    const b = parseBriefing(e07['briefing'], issues);
    expect(issues).toEqual([]);
    expect(b.text).toContain('麥寮港進港,滿載');
    expect(b.attachments).toEqual([
      { title: '航程計畫(麥寮進港)', file: 'docs/passage-plan-mailiao.pdf' },
      { title: '緊急程序 — 舵機故障', file: 'docs/SMS-EMG-004.pdf' },
    ]);
    expect(b.roles).toEqual([
      { role: 'pilot', playedBy: 'leader', required: true },
      { role: 'ecr', playedBy: 'voiceScript', required: false },
      { role: 'vts', playedBy: 'voiceScript', required: false },
    ]);
    expect(b.minParticipants).toBe(3);
    const bad: string[] = [];
    parseBriefing({ attachments: [{ title: 'x' }], roles: [{}] }, bad);
    expect(bad).toEqual(['briefing.attachments[0] 缺 file', 'briefing.roles[0] 缺 role']);
    expect(parseBriefing(undefined).text).toBe('');
  });
});

describe('parseScenario 擴充欄位', () => {
  it('E07:時間表/航線/區域/評估/簡報/參數引用統計;參數引用不產生 issues', () => {
    const s = parseScenario(e07Text, 'data/scenarios/E07_steering_failure.yaml');
    expect(s.issues).toEqual([]);
    expect(s.id).toBe('E07_steering_failure');
    expect(s.stats).toEqual({ timelineItems: 7, manualItems: 1, routeWaypoints: 4, zones: 3, assessmentRules: 13, paramRefs: s.paramRefs.length, targets: 2 });
    expect(s.stats.paramRefs).toBeGreaterThanOrEqual(14);
    expect(s.origin).toEqual({ lat: 23.78, lon: 120.1 });
    expect(s.environment.visibility_nm).toBe(6);
    expect(s.briefing).toContain('麥寮港進港');
    expect(briefingText(s)).toBe(s.briefing);
    expect(s.briefingDoc.minParticipants).toBe(3);
    expect(s.assessment.parameterSet).toBe('data/parameters/company-procedures.yaml');
    expect(s.assessment.minCpa_nm).toEqual({ param: 'navigation.cpa.channel_nm' });
    expect(extensionSummary(s)).toBe(`時間表 7 項(手動 1) · 航線 4 航點 · 區域 3 · 評估規則 13 條 · 參數引用 ${s.stats.paramRefs} 個 · 目標船 2`);
  });

  it('舊格式(無擴充欄位)統計為零、無 issues;data/scenarios 的 E01 仍無 issues', () => {
    const s = parseScenario(OLD_FORMAT, 'data/scenarios/E01_old.yaml');
    expect(s.issues).toEqual([]);
    expect(s.timeline).toEqual([]);
    expect(s.route).toBeNull();
    expect(s.zones).toEqual([]);
    expect(s.stats).toEqual({ timelineItems: 0, manualItems: 0, routeWaypoints: 0, zones: 0, assessmentRules: 0, paramRefs: 0, targets: 1 });
    expect(s.assessment.minCpa_nm).toEqual({ value: 0.5 });
    expect(s.briefingDoc).toEqual({ text: '', attachments: [], roles: [] });
    expect(s.origin).toEqual({ lat: 23.8, lon: 120.05 });
    expect(extensionSummary(s)).toBe('目標船 1');
    const e01 = parseScenario(readFileSync(e01Path, 'utf-8'), e01Path);
    expect(e01.issues).toEqual([]);
    expect(e01.id).toBe('E01_baseline');
  });

  it('environment.waterDepth 為參數引用時不算缺值', () => {
    const s = parseScenario('id: P\nship: { id: FSB1 }\ninitial: { position: { x: 0, y: 0 }, heading: 0 }\nenvironment:\n  waterDepth: "${params:port.depth_m}"\n', 'P.yaml');
    expect(s.issues).toEqual([]);
    expect(s.environment.waterDepth).toBeUndefined();
    expect(s.paramRefs).toEqual([{ path: 'environment.waterDepth', key: 'port.depth_m' }]);
  });
});
