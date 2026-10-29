// 情境編輯器擴充區段(lib/scenario-editor.ts 第十八輪):航線、區域、時間表逐項編輯、簡報角色/附件、sensors/alerts/bnwas、
// 必要事件與評估設定、拖船/錨/泊位/岸壁。每份 data/scenarios/*.yaml 經每個區段「不改值」的表單寫回後 toYaml → parseYaml 與原檔深相等;
// 每個區段「新增一項 → YAML 結構正確」與驗證問題;點路徑工具;版本 1 草稿升級。
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse as parseYaml } from 'yaml';
import { describe, expect, it } from 'vitest';
import {
  ALERT_FIELDS,
  BNWAS_FIELDS,
  DRAFT_STORAGE_KEY,
  ENCOUNTER_FIELDS,
  MANOEUVRING_FIELDS,
  SENSOR_FIELDS,
  WAYPOINT_FIELDS,
  addAttachment,
  addBerthLine,
  addFaultItem,
  addRequired,
  addRole,
  addTarget,
  addTimelineAction,
  addTimelineItem,
  addTug,
  addWaypoint,
  addZone,
  addZoneVertex,
  areaFileChoices,
  areaIds,
  berthLines,
  blankDraft,
  bodyPointFromText,
  bodyPointText,
  briefingAttachments,
  briefingRoles,
  flowYaml,
  fromYaml,
  getPath,
  idListFromText,
  loadDraft,
  moveAt,
  moveRequired,
  moveTimelineAction,
  moveTimelineItem,
  moveWaypoint,
  newActionValue,
  newTarget,
  numberOrRefFromText,
  parseValueText,
  patchAnchors,
  patchAssessmentSetting,
  patchAttachment,
  patchBanks,
  patchBerth,
  patchBerthLine,
  patchBriefing,
  patchRequired,
  patchRole,
  patchRoute,
  patchSection,
  patchTimelineItem,
  patchTug,
  patchWaypoint,
  patchWaypointPosition,
  patchZone,
  patchZoneVertex,
  removeAttachment,
  removeBerthLine,
  removeRequired,
  removeRole,
  removeTimelineAction,
  removeTug,
  removeWaypoint,
  removeZone,
  removeZoneVertex,
  requiredForm,
  routeWaypoints,
  setAssessmentUkcMin,
  setBanksEnabled,
  setBerthEnabled,
  setRouteEnabled,
  setTimelineAction,
  setTimelineTrigger,
  setZoneUkcMin,
  timelineItemForm,
  timelineItemTrigger,
  toScenarioObject,
  toYaml,
  ukcLimitForm,
  uniqueId,
  validateDraft,
  withPath,
  zonePolygon,
  type DraftPosition,
  type DraftStorage,
  type RawSection,
  type ScenarioDraft,
} from '../src/lib/scenario-editor.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const scenarioDir = path.resolve(here, '../../../data/scenarios');
const files = readdirSync(scenarioDir)
  .filter((f) => /\.ya?ml$/i.test(f))
  .sort();

function read(file: string): string {
  return readFileSync(path.join(scenarioDir, file), 'utf-8');
}

function isRecord(v: unknown): v is RawSection {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function positionOf(o: RawSection): DraftPosition {
  const p: DraftPosition = {};
  for (const k of ['lat', 'lon', 'x', 'y'] as const) {
    const v = numberOrRefFromText(o[k] === undefined ? '' : String(o[k]));
    if (v !== undefined) p[k] = v;
  }
  return p;
}

/** 表單值寫回:鍵存在時以同值 patch(模擬使用者在欄位上按 Enter 而未改值)。 */
function same(o: RawSection, key: string): unknown {
  return o[key];
}

/**
 * 對每個擴充區段做「不改值」的表單寫回:以各區段的表單讀取函式取得顯示值,再用修改函式寫回同值。
 * 若任一讀/寫路徑有損失(型別、鍵、順序以外的差異),round-trip 深相等就會失敗。
 */
function touchAll(d0: ScenarioDraft): ScenarioDraft {
  let d = d0;
  // 航線
  const route = d.route;
  if (route) {
    for (const k of ['name', 'xtdLimit_m', 'startLeg']) if (route[k] !== undefined) d = patchRoute(d, k, same(route, k));
    routeWaypoints(d).forEach((w, i) => {
      d = patchWaypoint(d, i, 'name', same(w, 'name'));
      d = patchWaypointPosition(d, i, positionOf(w));
      for (const [k] of WAYPOINT_FIELDS) if (w[k] !== undefined) d = patchWaypoint(d, i, k, same(w, k));
      if (w['report'] !== undefined) d = patchWaypoint(d, i, 'report', same(w, 'report'));
      d = patchWaypoint(d, i, 'checkpoint', w['checkpoint'] === true ? true : undefined);
    });
  }
  // 區域
  d.zones.forEach((z, i) => {
    for (const k of ['id', 'name', 'type', 'speedLimit_kn', 'waterDepth_m']) if (z[k] !== undefined) d = patchZone(d, i, k, same(z, k));
    const ukc = ukcLimitForm(z['ukcMin']);
    d = setZoneUkcMin(d, i, ukc.kind, ukc.value);
    zonePolygon(z).forEach((p, j) => {
      d = patchZoneVertex(d, i, j, positionOf(p));
    });
  });
  // 時間表逐項
  d.timeline.forEach((it, i) => {
    const f = timelineItemForm(it);
    d = patchTimelineItem(d, i, 'id', f.id);
    d = patchTimelineItem(d, i, 'name', f.name);
    d = patchTimelineItem(d, i, 'phase', f.phase);
    d = patchTimelineItem(d, i, 'visibility', f.visibility);
    d = patchTimelineItem(d, i, 'armed', f.armed);
    d = patchTimelineItem(d, i, 'holdFor_s', numberOrRefFromText(f.holdFor_s));
    d = patchTimelineItem(d, i, 'delay_s', numberOrRefFromText(f.delay_s));
    d = setTimelineTrigger(d, i, timelineItemTrigger(it));
    f.actions.forEach((a, j) => {
      d = setTimelineAction(d, i, j, a.kind, parseValueText(a.value));
    });
  });
  // 簡報
  for (const k of ['minParticipants', 'duration_s']) if (d.briefing[k] !== undefined) d = patchBriefing(d, k, same(d.briefing, k));
  briefingRoles(d).forEach((r, i) => {
    d = patchRole(d, i, 'role', same(r, 'role'));
    if (r['playedBy'] !== undefined) d = patchRole(d, i, 'playedBy', same(r, 'playedBy'));
    d = patchRole(d, i, 'required', r['required'] === true);
  });
  briefingAttachments(d).forEach((a, i) => {
    for (const k of ['title', 'file']) if (a[k] !== undefined) d = patchAttachment(d, i, k, same(a, k));
  });
  // sensors / alerts / bnwas
  for (const [section, fields] of [
    ['sensors', SENSOR_FIELDS],
    ['alerts', ALERT_FIELDS],
    ['bnwas', BNWAS_FIELDS],
  ] as const) {
    const o = d[section];
    if (!o) continue;
    for (const [k] of fields) if (o[k] !== undefined) d = patchSection(d, section, k, same(o, k));
    if (section === 'bnwas' && o['mode'] !== undefined) d = patchSection(d, 'bnwas', 'mode', same(o, 'mode'));
  }
  // 必要事件與設定
  d.assessment.required.forEach((r, i) => {
    const f = requiredForm(r);
    d = patchRequired(d, i, 'id', f.id);
    d = patchRequired(d, i, 'after.event', f.event);
    d = patchRequired(d, i, 'after.itemId', f.itemId);
    d = patchRequired(d, i, 'after.targetId', f.targetId);
    d = patchRequired(d, i, 'after.waypoint', f.waypoint);
    d = patchRequired(d, i, 'expect', parseValueText(f.expect));
    for (const k of ['within_s', 'penalty', 'latePenaltyPer_s'] as const) d = patchRequired(d, i, k, numberOrRefFromText(f[k]));
  });
  const s = d.assessment.settings;
  if (s['minCpa_nm'] !== undefined) d = patchAssessmentSetting(d, 'minCpa_nm', same(s, 'minCpa_nm'));
  for (const [k] of ENCOUNTER_FIELDS) if (getPath(s, `encounter.${k}`) !== undefined) d = patchAssessmentSetting(d, `encounter.${k}`, getPath(s, `encounter.${k}`));
  for (const [k] of MANOEUVRING_FIELDS) if (getPath(s, `manoeuvring.${k}`) !== undefined) d = patchAssessmentSetting(d, `manoeuvring.${k}`, getPath(s, `manoeuvring.${k}`));
  const ukc = ukcLimitForm(getPath(s, 'ukc.min'));
  d = setAssessmentUkcMin(d, ukc.kind, ukc.value);
  // 拖船
  d.tugs.forEach((t, i) => {
    for (const k of ['id', 'name', 'bollardPull_t', 'lineLength_m', 'modes']) if (t[k] !== undefined) d = patchTug(d, i, k, same(t, k));
    if (t['attach'] !== undefined) d = patchTug(d, i, 'attach', bodyPointFromText(bodyPointText(t['attach'])));
    for (const k of ['mode', 'direction_deg', 'power_pct']) if (getPath(t, `initial.${k}`) !== undefined) d = patchTug(d, i, `initial.${k}`, getPath(t, `initial.${k}`));
  });
  // 錨
  const anchors = d.anchors;
  if (anchors) {
    for (const k of ['holdingFactor', 'dragSpeed_mps']) if (anchors[k] !== undefined) d = patchAnchors(d, k, same(anchors, k));
    for (const side of ['port', 'starboard']) for (const k of ['length_m', 'length_shackles', 'bearing_deg']) if (getPath(anchors, `${side}.${k}`) !== undefined) d = patchAnchors(d, `${side}.${k}`, getPath(anchors, `${side}.${k}`));
  }
  // 泊位
  const berth = d.berth;
  if (berth) {
    for (const k of ['file', 'id', 'departedDistance_m']) if (berth[k] !== undefined) d = patchBerth(d, k, same(berth, k));
    berthLines(d).forEach((l, i) => {
      for (const k of ['id', 'bollard', 'mbl_t', 'length_m', 'initial']) if (l[k] !== undefined) d = patchBerthLine(d, i, k, same(l, k));
      if (l['fairlead'] !== undefined) d = patchBerthLine(d, i, 'fairlead', bodyPointFromText(bodyPointText(l['fairlead'])));
    });
  }
  // 岸壁
  const banks = d.banks;
  if (banks) for (const k of ['file', 'ids', 'scale']) if (banks[k] !== undefined) d = patchBanks(d, k, same(banks, k));
  return d;
}

class MemoryStorage implements DraftStorage {
  readonly map = new Map<string, string>();
  getItem(k: string): string | null {
    return this.map.get(k) ?? null;
  }
  setItem(k: string, v: string): void {
    this.map.set(k, v);
  }
  removeItem(k: string): void {
    this.map.delete(k);
  }
}

describe('擴充區段 round-trip(data/scenarios 全部情境;每個區段不改值寫回)', () => {
  it('情境庫涵蓋每個擴充區段(測試本身才有意義)', () => {
    const docs = files.map((f) => parseYaml(read(f)) as RawSection);
    for (const key of ['route', 'zones', 'sensors', 'alerts', 'bnwas', 'tugs', 'anchors', 'berth', 'banks']) expect(docs.some((x) => x[key] !== undefined), key).toBe(true);
    expect(docs.some((x) => isRecord(x['briefing']) && Array.isArray(x['briefing']['roles']))).toBe(true);
    expect(docs.some((x) => isRecord(x['assessment']) && Array.isArray(x['assessment']['required']))).toBe(true);
    expect(docs.some((x) => isRecord(x['assessment']) && isRecord(x['assessment']['ukc']))).toBe(true);
    expect(docs.some((x) => isRecord(x['assessment']) && isRecord(x['assessment']['encounter']))).toBe(true);
  });

  for (const file of files) {
    it(`${file}:每個區段寫回同值後 toYaml → parseYaml 與原檔深相等;草稿可序列化`, () => {
      const text = read(file);
      const d = touchAll(fromYaml(text, file.replace(/\.ya?ml$/i, '')));
      const yaml = toYaml(d);
      expect(parseYaml(yaml)).toEqual(parseYaml(text));
      const again = JSON.parse(JSON.stringify(d)) as ScenarioDraft;
      expect(toYaml(again)).toBe(yaml);
      // 草稿的區段是情境物件的複本(非 raw 本身)
      expect(d.zones).toEqual(parseYaml(text).zones ?? []);
      expect(validateDraft(d).issues).toEqual([]);
    });
  }

  it('E13:拖船、泊位繫纜、速限區讀入草稿區段;E08:sensors/alerts/bnwas;E18:ukc 設定與 ukcPolicy 區', () => {
    const e13 = fromYaml(read('E13_tug_berthing_mailiao.yaml'), 'E13');
    expect(e13.tugs.map((t) => t['id'])).toEqual(['TG1', 'TG2']);
    expect(e13.berth?.['file']).toBe('data/areas/mailiao-berth.json');
    expect(berthLines(e13).map((l) => l['id'])).toEqual(['HEAD', 'FWDSPRING', 'AFTSPRING', 'STERN']);
    expect(e13.zones[0]).toMatchObject({ id: 'harbour', type: 'speedLimit' });
    expect(e13.route).toBeNull();
    const e08 = fromYaml(read('E08_gyro_failure.yaml'), 'E08');
    expect(e08.sensors).toEqual({ magneticCompassError_deg: 1.0 });
    expect(e08.alerts?.['offHeading_deg']).toBe(10);
    expect(e08.bnwas?.['mode']).toBe('on');
    expect(e08.bnwas?.['dormant_s']).toBe('${params:bridge.bnwas.dormantPeriod_s}');
    expect(routeWaypoints(e08).map((w) => w['name'])).toEqual(['WP1', 'WP2', 'WP3']);
    expect(briefingRoles(e08)).toHaveLength(3);
    expect(briefingAttachments(e08)).toHaveLength(2);
    expect(e08.briefing['minParticipants']).toBe(2);
    expect(e08.briefing['text']).toBeUndefined();
    const e18 = fromYaml(read('E18_ukc_squat_anping.yaml'), 'E18');
    expect(ukcLimitForm(getPath(e18.assessment.settings, 'ukc.min'))).toEqual({ kind: 'fractionOfDraft', value: '${params:navigation.ukc.channel_fraction}' });
    expect(ukcLimitForm(e18.zones[0]!['ukcMin'])).toEqual({ kind: 'fractionOfDraft', value: '${params:navigation.ukc.channel_fraction}' });
    expect(e18.assessment.required).toHaveLength(4);
    expect(requiredForm(e18.assessment.required[3]!)).toMatchObject({ id: 'ukcAlarmResponse', event: 'alertRaised', itemId: 'shallowWater', expect: '[ telegraphChange, rpmOrder ]'.replace('[ ', '{ any: [ ').replace(' ]', ' ] }') });
  });
});

describe('航線 route', () => {
  it('建立航線 → 2 個航點(第一個為初始位置)、新增/上下移/刪除/欄位 → YAML 結構正確', () => {
    let d = setRouteEnabled(blankDraft(), true);
    expect(routeWaypoints(d).map((w) => w['name'])).toEqual(['WP1', 'WP2']);
    expect(routeWaypoints(d)[0]).toEqual({ name: 'WP1', lat: 23.9, lon: 119.98 });
    expect(routeWaypoints(d)[1]).toEqual({ name: 'WP2', lat: 23.92, lon: 119.98 });
    d = addWaypoint(d);
    d = patchRoute(d, 'name', '北上');
    d = patchRoute(d, 'xtdLimit_m', 300);
    d = patchWaypoint(d, 2, 'checkpoint', true);
    d = patchWaypoint(d, 2, 'speedLimit_kn', '${params:port.speed_kn}');
    d = patchWaypoint(d, 2, 'report', 'VTS 報告');
    d = patchWaypointPosition(d, 2, { lat: 24.0, lon: 120.0 });
    d = moveWaypoint(d, 2, -1);
    d = removeWaypoint(d, 0);
    const o = toScenarioObject(d);
    expect(o['route']).toEqual({
      name: '北上',
      xtdLimit_m: 300,
      waypoints: [
        { name: 'WP3', lat: 24.0, lon: 120.0, checkpoint: true, speedLimit_kn: '${params:port.speed_kn}', report: 'VTS 報告' },
        { name: 'WP2', lat: 23.92, lon: 119.98 },
      ],
    });
    const v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.summary.stats.routeWaypoints).toBe(2);
    expect(v.summary.route?.waypoints[0]).toMatchObject({ checkpoint: true, report: 'VTS 報告' });
    expect(v.yaml).toContain('speedLimit_kn: "${params:port.speed_kn}"');
    // 移除航線
    expect(toScenarioObject(setRouteEnabled(d, false))['route']).toBeUndefined();
    // 取消勾選/再勾選不重建已存在的航線
    expect(setRouteEnabled(d, true)).toBe(d);
  });

  it('驗證:航點名稱重複/空白、位置不全、少於 2 點、x/y 無原點、startLeg 超出', () => {
    let d = setRouteEnabled(blankDraft(), true);
    d = patchWaypoint(d, 1, 'name', 'WP1');
    d = patchWaypointPosition(d, 1, { lat: 24 });
    d = patchRoute(d, 'startLeg', 3);
    let v = validateDraft(d);
    expect(v.issues).toContain('route.waypoints[1] name 重複:WP1');
    expect(v.issues.some((i) => i.startsWith('route.waypoints[1] WP1 需完整'))).toBe(true);
    expect(v.issues).toContain('route.startLeg 需為 0–0 的整數');
    d = removeWaypoint(d, 1);
    d = patchWaypoint(d, 0, 'name', '');
    v = validateDraft(d);
    expect(v.issues).toContain('route.waypoints[0] 缺 name');
    expect(v.issues).toContain('route 航點少於 2');
    // ENU 航點:初始位置為經緯度時以其為原點可用;初始也用 x/y 且無 origin 時報錯
    let e = setRouteEnabled(blankDraft(), true);
    e = patchWaypointPosition(e, 1, { x: 0, y: 1000 });
    expect(validateDraft(e).issues).toEqual([]);
    e = { ...e, initial: { ...e.initial, position: { x: 0, y: 0 } } };
    expect(validateDraft(e).issues).toContain('route.waypoints[1] 用 x/y 時情境需有 origin 或經緯度初始位置');
  });
});

describe('區域 zones', () => {
  it('新增區域(四邊形)→ 改型別/屬性/UKC 下限、頂點新增/編輯/刪除 → YAML 結構正確', () => {
    let d = addZone(blankDraft());
    expect(d.zones[0]).toMatchObject({ id: 'zone1', type: 'noGo' });
    expect(zonePolygon(d.zones[0]!)).toHaveLength(4);
    d = patchZone(d, 0, 'id', 'shoal');
    d = patchZone(d, 0, 'name', '淺灘');
    d = patchZone(d, 0, 'type', 'ukcPolicy');
    d = patchZone(d, 0, 'waterDepth_m', 5);
    d = setZoneUkcMin(d, 0, 'metres', 1.5);
    d = addZoneVertex(d, 0);
    d = patchZoneVertex(d, 0, 4, { lat: 23.95, lon: 119.95 });
    d = removeZoneVertex(d, 0, 0);
    d = addZone(d);
    d = patchZone(d, 1, 'type', 'speedLimit');
    d = patchZone(d, 1, 'speedLimit_kn', 6);
    const o = toScenarioObject(d);
    expect(o['zones']).toEqual([
      {
        id: 'shoal',
        name: '淺灘',
        type: 'ukcPolicy',
        polygon: [
          { lat: 23.91, lon: 119.99 },
          { lat: 23.89, lon: 119.99 },
          { lat: 23.89, lon: 119.97 },
          { lat: 23.95, lon: 119.95 },
        ],
        waterDepth_m: 5,
        ukcMin: { metres: 1.5 },
      },
      expect.objectContaining({ id: 'zone2', type: 'speedLimit', speedLimit_kn: 6 }),
    ]);
    // UKC 下限換成吃水比例、再清除
    expect(toScenarioObject(setZoneUkcMin(d, 0, 'fractionOfDraft', 0.1))['zones']).toEqual([expect.objectContaining({ ukcMin: { fractionOfDraft: 0.1 } }), expect.anything()]);
    expect((toScenarioObject(setZoneUkcMin(d, 0, '', undefined))['zones'] as RawSection[])[0]).not.toHaveProperty('ukcMin');
    const v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.summary.stats.zones).toBe(2);
    // 可當 zoneEntered 硬性失敗的目標
    expect(validateDraft({ ...d, assessment: { ...d.assessment, hardFail: [{ id: 'z', rule: 'zoneEntered', zoneId: 'shoal' }] } }).issues).toEqual([]);
    expect(toScenarioObject(removeZone(d, 1))['zones']).toHaveLength(1);
  });

  it('驗證:多邊形少於 3 點、頂點不全、id 重複/缺、未知型別、ukcPolicy/speedLimit 未給屬性(警告)', () => {
    let d = addZone(addZone(blankDraft()));
    d = removeZoneVertex(d, 0, 0);
    d = removeZoneVertex(d, 0, 0);
    d = patchZoneVertex(d, 1, 0, { lat: 23 });
    d = patchZone(d, 1, 'id', 'zone1');
    d = patchZone(d, 1, 'type', 'weird');
    d = patchZone(d, 0, 'type', 'speedLimit');
    const v = validateDraft(d);
    expect(v.issues).toContain('zones[0] 多邊形少於 3 點');
    expect(v.issues).toContain('zones[1].polygon[0] 需完整的 lat/lon 或 x/y 數值');
    expect(v.issues).toContain('zones[1] id 重複:zone1');
    expect(v.issues).toContain('zones[1] 未知的 type weird');
    expect(v.warnings).toContain('zones[0] zone1:speedLimit 區未給 speedLimit_kn');
    const e = patchZone(addZone(blankDraft()), 0, 'id', '');
    expect(validateDraft(e).issues).toContain('zones[0] 缺 id');
  });
});

describe('時間表項目逐項編輯', () => {
  function withItems(): ScenarioDraft {
    let d = blankDraft();
    d = addFaultItem(d, { id: 'steer', fault: 'steeringGear', trigger: { kind: 'time', atLeast: 300 }, phase: 'degraded' });
    d = addTimelineItem(d);
    return d;
  }

  it('表單讀取(觸發種類、動作流式 YAML、保留鍵)與寫回:id/名稱/階段/可見/啟用/觸發/持續/延遲/動作', () => {
    let d = withItems();
    expect(d.timeline[1]).toEqual({ id: 'item2', when: { type: 'time', atLeast: 60 }, actions: [{ message: { to: 'instructor', text: '', severity: 'info' } }] });
    const f0 = timelineItemForm(d.timeline[0]!);
    expect(f0).toMatchObject({ id: 'steer', trigger: 'time', atLeast: '300', phase: 'degraded', visibility: '', armed: '', kept: [] });
    expect(f0.actions).toEqual([{ kind: 'injectFault', value: 'steeringGear' }]);
    d = patchTimelineItem(d, 1, 'id', 'vtsCall');
    d = patchTimelineItem(d, 1, 'name', 'VTS 呼叫');
    d = patchTimelineItem(d, 1, 'phase', 'emergency');
    d = patchTimelineItem(d, 1, 'visibility', 'all');
    d = patchTimelineItem(d, 1, 'armed', 'false');
    d = patchTimelineItem(d, 1, 'holdFor_s', 5);
    d = patchTimelineItem(d, 1, 'delay_s', '${params:x.delay_s}');
    d = setTimelineTrigger(d, 1, { kind: 'condition', when: { type: 'ownDistance', target: 'X1', lessThan: 2 } });
    d = setTimelineAction(d, 1, 0, 'voiceScript', { role: 'vts', channel: 'vhfPort', text: '請報告', lang: 'zh-TW' });
    d = addTimelineAction(d, 1, 'arm');
    d = setTimelineAction(d, 1, 1, 'arm', 'steer');
    d = addTimelineAction(d, 1, 'message');
    d = moveTimelineAction(d, 1, 2, -1);
    d = addTimelineAction(d, 1, 'endExercise');
    d = removeTimelineAction(d, 1, 3);
    d = addTarget(d, { ...newTarget(d), id: 'X1' });
    const item = toScenarioObject(d)['timeline'] as RawSection[];
    expect(item[1]).toEqual({
      id: 'vtsCall',
      name: 'VTS 呼叫',
      phase: 'emergency',
      visibility: 'all',
      armed: false,
      holdFor_s: 5,
      delay_s: '${params:x.delay_s}',
      when: { type: 'ownDistance', target: 'X1', lessThan: 2 },
      actions: [{ voiceScript: { role: 'vts', channel: 'vhfPort', text: '請報告', lang: 'zh-TW' } }, { message: { to: 'instructor', text: '', severity: 'info' } }, { arm: 'steer' }],
    });
    const f1 = timelineItemForm(d.timeline[1]!);
    expect(f1).toMatchObject({ trigger: 'condition', condition: '{ type: ownDistance, target: X1, lessThan: 2 }', armed: 'false', visibility: 'all', holdFor_s: '5', delay_s: '${params:x.delay_s}' });
    expect(f1.actions[0]).toEqual({ kind: 'voiceScript', value: '{ role: vts, channel: vhfPort, text: 請報告, lang: zh-TW }' });
    expect(parseValueText(f1.actions[0]!.value)).toEqual({ role: 'vts', channel: 'vhfPort', text: '請報告', lang: 'zh-TW' });
    // 改回手動與時間觸發
    expect(setTimelineTrigger(d, 1, { kind: 'manual' }).timeline[1]).toMatchObject({ manual: true });
    expect(setTimelineTrigger(d, 1, { kind: 'manual' }).timeline[1]).not.toHaveProperty('when');
    expect(setTimelineTrigger(d, 1, { kind: 'time', atLeast: 90 }).timeline[1]).toMatchObject({ when: { type: 'time', atLeast: 90 } });
    expect(timelineItemTrigger({ when: { type: 'time', atLeast: 10, since: { event: 'x' } } })).toMatchObject({ kind: 'condition' });
    // 上下移
    expect(moveTimelineItem(d, 1, -1).timeline.map((t) => t['id'])).toEqual(['vtsCall', 'steer']);
    expect(moveTimelineItem(d, 1, 1).timeline.map((t) => t['id'])).toEqual(['steer', 'vtsCall']);
    expect(validateDraft(d).issues).toEqual([]);
    expect(validateDraft(d).warnings).toContain('時間表 vtsCall:message 沒有 text');
  });

  it('驗證:arm/disarm 引用不存在的項目、tugControl 引用不存在的拖船、injectFault 缺名稱、id 重複;newActionValue 每種動作可通過 parseScenario', () => {
    let d = withItems();
    d = addTimelineAction(d, 1, 'arm');
    d = setTimelineAction(d, 1, 1, 'arm', 'nope');
    d = addTimelineAction(d, 1, 'tugControl');
    d = addTimelineAction(d, 1, 'injectFault');
    d = setTimelineAction(d, 1, 3, 'injectFault', { args: {} });
    d = patchTimelineItem(d, 1, 'id', 'steer');
    const v = validateDraft(d);
    expect(v.issues).toContain('時間表 steer:arm 引用不存在的項目 nope');
    expect(v.issues).toContain('時間表 steer:tugControl 引用不存在的拖船 (空)');
    expect(v.issues).toContain('時間表 steer:injectFault 缺故障名稱');
    expect(v.issues).toContain('timeline id 重複:steer');
    let all = withItems();
    for (const kind of ['injectFault', 'clearFault', 'setEnvironment', 'voiceScript', 'message', 'phase', 'freeze', 'autopilot', 'ownShip', 'timeScale', 'arm', 'disarm', 'endExercise']) all = addTimelineAction(all, 1, kind, newActionValue(kind, all));
    expect(validateDraft(all).issues).toEqual([]);
    expect(newActionValue('tugControl', addTug(all))).toEqual({ id: 'TG1', mode: 'standby' });
    expect(newActionValue('targetControl', addTarget(all))).toEqual({ targetId: 'T1', speed: 0 });
  });
});

describe('簡報角色與附件、sensors/alerts/bnwas', () => {
  it('字串簡報加角色 → 物件 {text, roles};附件、最少人數、時長;刪到空時回字串', () => {
    let d = blankDraft();
    d = { ...d, general: { ...d.general, briefing: '簡報' } };
    d = addRole(d);
    d = patchRole(d, 0, 'role', 'pilot');
    d = patchRole(d, 0, 'playedBy', 'voiceScript');
    d = patchRole(d, 0, 'required', true);
    d = addRole(d, { role: 'master' });
    d = addAttachment(d);
    d = patchAttachment(d, 0, 'title', '航程計畫');
    d = patchAttachment(d, 0, 'file', 'docs/plan.pdf');
    d = patchBriefing(d, 'minParticipants', 3);
    d = patchBriefing(d, 'duration_s', 3600);
    expect(toScenarioObject(d)['briefing']).toEqual({ text: '簡報', roles: [{ role: 'pilot', playedBy: 'voiceScript', required: true }, { role: 'master' }], attachments: [{ title: '航程計畫', file: 'docs/plan.pdf' }], minParticipants: 3, duration_s: 3600 });
    expect(validateDraft(d).issues).toEqual([]);
    expect(validateDraft(d).summary.briefingDoc.roles).toHaveLength(2);
    d = removeRole(d, 1);
    d = patchRole(d, 0, 'required', false);
    expect((toScenarioObject(d)['briefing'] as RawSection)['roles']).toEqual([{ role: 'pilot', playedBy: 'voiceScript' }]);
    d = removeRole(d, 0);
    d = removeAttachment(d, 0);
    d = patchBriefing(d, 'minParticipants', undefined);
    d = patchBriefing(d, 'duration_s', undefined);
    // 清空後 roles/attachments 保留空陣列(曾有鍵);無文字時整段仍為物件
    expect(toScenarioObject(d)['briefing']).toEqual({ text: '簡報', roles: [], attachments: [] });
    const plain = blankDraft();
    expect(toScenarioObject({ ...plain, general: { ...plain.general, briefing: '只有文字' } })['briefing']).toBe('只有文字');
  });

  it('驗證:最少人數非正整數、playedBy 非建議值(警告)、附件缺 file、角色缺 role', () => {
    let d = blankDraft();
    d = patchBriefing(d, 'minParticipants', 0);
    d = addRole(d, { role: '', playedBy: 'robot' });
    d = addAttachment(d, { title: 'x' });
    const v = validateDraft(d);
    expect(v.issues).toContain('briefing.minParticipants 需為 ≥ 1 的整數');
    expect(v.issues).toContain('briefing.roles[0] 缺 role');
    expect(v.issues).toContain('briefing.attachments[0] 缺 file');
    expect(v.warnings).toContain('briefing.roles[0] playedBy robot 不在 leader/voiceScript/instructor/trainee');
  });

  it('sensors/alerts/bnwas:設值 → 區段輸出;清空 → 區段移除;驗證負值、mode 列舉、休眠期範圍(警告)', () => {
    let d = blankDraft();
    expect(toScenarioObject(d)['alerts']).toBeUndefined();
    d = patchSection(d, 'sensors', 'magneticCompassError_deg', -1.5);
    d = patchSection(d, 'alerts', 'offHeading_deg', 10);
    d = patchSection(d, 'alerts', 'shallowWater_m', '${params:navigation.ukc.alarm_m}');
    d = patchSection(d, 'bnwas', 'mode', 'auto');
    d = patchSection(d, 'bnwas', 'dormant_s', 600);
    const o = toScenarioObject(d);
    expect(o['sensors']).toEqual({ magneticCompassError_deg: -1.5 });
    expect(o['alerts']).toEqual({ offHeading_deg: 10, shallowWater_m: '${params:navigation.ukc.alarm_m}' });
    expect(o['bnwas']).toEqual({ mode: 'auto', dormant_s: 600 });
    expect(toYaml(d)).toContain('mode: auto');
    expect(validateDraft(d).issues).toEqual([]);
    d = patchSection(d, 'alerts', 'offHeading_deg', undefined);
    d = patchSection(d, 'alerts', 'shallowWater_m', undefined);
    expect(d.alerts).toBeNull();
    expect(toScenarioObject(d)['alerts']).toBeUndefined();
    d = patchSection(d, 'bnwas', 'mode', 'on');
    expect(toYaml(d)).toContain('mode: "on"');
    d = patchSection(d, 'bnwas', 'dormant_s', 100);
    d = patchSection(d, 'bnwas', 'stage2_s', -1);
    d = patchSection(d, 'alerts', 'escalate_s', -5);
    let v = validateDraft(d);
    expect(v.issues).toContain('bnwas.stage2_s 需 ≥ 0');
    expect(v.issues).toContain('alerts.escalate_s 需 ≥ 0');
    expect(v.warnings).toContain('bnwas.dormant_s 100 s 不在 MSC.128(75) 的 180–720 s');
    v = validateDraft(patchSection(d, 'bnwas', 'mode', 'maybe'));
    expect(v.issues).toContain('bnwas.mode 需為 off / auto / on');
  });
});

describe('評估:必要事件與設定', () => {
  it('新增必要事件(以第一個時間表項目為計時起點)→ 改欄位/expect/上下移/刪除 → YAML 結構;設定 minCpa/encounter/ukc/manoeuvring', () => {
    let d = addFaultItem(blankDraft(), { id: 'steer', fault: 'steeringGear', trigger: { kind: 'time', atLeast: 300 } });
    d = addRequired(d);
    expect(d.assessment.required[0]).toEqual({ id: 'required1', after: { event: 'timelineTriggered', itemId: 'steer' }, expect: { acknowledge: { kind: 'verbal' } }, within_s: 120, penalty: 10 });
    d = patchRequired(d, 0, 'id', 'notifyEcr');
    d = patchRequired(d, 0, 'after.event', 'faultInjected');
    d = patchRequired(d, 0, 'expect', parseValueText('{ any: [ telegraphChange, { acknowledge: { kind: notifyEcr } } ] }'));
    d = patchRequired(d, 0, 'within_s', '${params:bridge.notifyEcr_s}');
    d = patchRequired(d, 0, 'latePenaltyPer_s', 0.05);
    d = addRequired(d);
    d = patchRequired(d, 1, 'after.itemId', '');
    d = patchRequired(d, 1, 'after.event', 'reportPointPassed');
    d = patchRequired(d, 1, 'after.waypoint', 'WP2');
    d = setRouteEnabled(d, true);
    d = moveRequired(d, 1, -1);
    d = patchAssessmentSetting(d, 'minCpa_nm', 0.8);
    d = patchAssessmentSetting(d, 'encounter.detectionRange_nm', 5);
    d = patchAssessmentSetting(d, 'manoeuvring.rotLimit_degPerMin', '${params:manoeuvring.rotLimit_degPerMin}');
    d = setAssessmentUkcMin(d, 'fractionOfDraft', 0.1);
    const a = toScenarioObject(d)['assessment'] as RawSection;
    expect(a['required']).toEqual([
      { id: 'required2', after: { event: 'reportPointPassed', waypoint: 'WP2' }, expect: { acknowledge: { kind: 'verbal' } }, within_s: 120, penalty: 10 },
      { id: 'notifyEcr', after: { event: 'faultInjected', itemId: 'steer' }, expect: { any: ['telegraphChange', { acknowledge: { kind: 'notifyEcr' } }] }, within_s: '${params:bridge.notifyEcr_s}', penalty: 10, latePenaltyPer_s: 0.05 },
    ]);
    expect(a['minCpa_nm']).toBe(0.8);
    expect(a['encounter']).toEqual({ detectionRange_nm: 5 });
    expect(a['manoeuvring']).toEqual({ rotLimit_degPerMin: '${params:manoeuvring.rotLimit_degPerMin}' });
    expect(a['ukc']).toEqual({ min: { fractionOfDraft: 0.1 } });
    expect(a['parameterSet']).toBe('data/parameters/company-procedures.yaml');
    const v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.summary.stats.assessmentRules).toBe(4);
    expect(v.summary.assessment.required.map((r) => r.expect.length)).toEqual([1, 2]);
    expect(v.yaml).toContain('within_s: "${params:bridge.notifyEcr_s}"');
    // 清除設定 → 鍵移除;刪除必要事件
    d = setAssessmentUkcMin(d, '', undefined);
    d = patchAssessmentSetting(d, 'encounter.detectionRange_nm', undefined);
    const a2 = toScenarioObject(removeRequired(removeRequired(d, 1), 0))['assessment'] as RawSection;
    expect(a2['ukc']).toBeUndefined();
    expect(a2['encounter']).toBeUndefined();
    // 範本原本沒有 required 鍵 → 清空後鍵移除(與 targets/timeline 相同)
    expect(a2['required']).toBeUndefined();
  });

  it('驗證:缺 id / id 重複 / 缺 within_s / after 引用不存在的項目、目標船、航點 / expect 無效 / minCpa ≤ 0', () => {
    let d = addFaultItem(blankDraft(), { id: 'steer', fault: 'steeringGear', trigger: { kind: 'time', atLeast: 300 } });
    d = addRequired(d);
    d = addRequired(d);
    d = patchRequired(d, 1, 'id', 'required1');
    d = patchRequired(d, 1, 'after.itemId', 'ghost');
    d = patchRequired(d, 1, 'after.targetId', 'X9');
    d = patchRequired(d, 1, 'after.waypoint', 'WP9');
    d = patchRequired(d, 1, 'within_s', undefined);
    d = patchRequired(d, 1, 'expect', { nothing: true });
    d = addRequired(d);
    d = patchRequired(d, 2, 'id', '');
    d = patchAssessmentSetting(d, 'minCpa_nm', 0);
    const v = validateDraft(d);
    expect(v.issues).toContain('assessment.required[1] id 重複:required1');
    expect(v.issues).toContain('assessment.required[1] 缺 within_s');
    expect(v.issues).toContain('assessment.required[1] after.itemId 引用不存在的時間表項目 ghost');
    expect(v.issues).toContain('assessment.required[1] after.targetId 引用不存在的目標船 X9');
    expect(v.issues).toContain('assessment.required[1] after.waypoint 引用不存在的航點 WP9');
    expect(v.issues).toContain('assessment.required[1].expect 需為可偵測項名稱、acknowledge 或 event');
    expect(v.issues).toContain('assessment.required[2] 缺 id');
    expect(v.issues).toContain('assessment.minCpa_nm 需 > 0');
    // alertRaised 的 itemId 是警報 id,不檢查時間表
    const ok = patchRequired(patchRequired(addRequired(blankDraft()), 0, 'after.event', 'alertRaised'), 0, 'after.itemId', 'shallowWater');
    expect(validateDraft(ok).issues).toEqual([]);
  });
});

describe('拖船 / 錨 / 泊位 / 岸壁', () => {
  it('拖船:新增/欄位/連接點 x,y/模式/刪除 → YAML;tugControl 動作可引用', () => {
    let d = addTug(blankDraft());
    expect(d.tugs[0]).toEqual({ id: 'TG1', name: '', bollardPull_t: 40, attach: 'fwdStbd', lineLength_m: 40, modes: ['push', 'pull'], initial: { mode: 'standby', direction_deg: 90, power_pct: 0 } });
    d = patchTug(d, 0, 'name', '麥寮1號');
    d = patchTug(d, 0, 'bollardPull_t', 45);
    d = patchTug(d, 0, 'modes', ['push']);
    d = patchTug(d, 0, 'initial.mode', 'push');
    d = patchTug(d, 0, 'initial.power_pct', 50);
    d = addTug(d);
    d = patchTug(d, 1, 'attach', bodyPointFromText('-40, 8'));
    d = patchTug(d, 1, 'name', undefined);
    d = addTimelineItem(d);
    d = setTimelineAction(d, 0, 0, 'tugControl', { id: 'TG2', mode: 'push', power_pct: 40 });
    const o = toScenarioObject(d);
    expect(o['tugs']).toEqual([
      { id: 'TG1', name: '麥寮1號', bollardPull_t: 45, attach: 'fwdStbd', lineLength_m: 40, modes: ['push'], initial: { mode: 'push', direction_deg: 90, power_pct: 50 } },
      { id: 'TG2', bollardPull_t: 40, attach: { x: -40, y: 8 }, lineLength_m: 40, modes: ['push', 'pull'], initial: { mode: 'standby', direction_deg: 90, power_pct: 0 } },
    ]);
    const v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.summary.tugs?.map((t) => t.id)).toEqual(['TG1', 'TG2']);
    expect(bodyPointText({ x: -40, y: 8 })).toBe('-40,8');
    expect(() => bodyPointFromText('middle')).toThrow('船體座標點');
    expect(toScenarioObject(removeTug(removeTug(d, 1), 0))['tugs']).toBeUndefined();
  });

  it('拖船驗證:id 缺/重複、拖力 ≤ 0、連接點無效、modes 空、initial.mode 不在 modes、出力範圍', () => {
    let d = addTug(addTug(addTug(blankDraft())));
    d = patchTug(d, 1, 'id', 'TG1');
    d = patchTug(d, 1, 'bollardPull_t', 0);
    d = patchTug(d, 1, 'attach', 'middle');
    d = patchTug(d, 1, 'modes', ['pull']);
    d = patchTug(d, 1, 'initial.mode', 'push');
    d = patchTug(d, 1, 'initial.power_pct', 120);
    d = patchTug(d, 2, 'id', '');
    d = patchTug(d, 2, 'modes', []);
    const v = validateDraft(d);
    expect(v.issues).toContain('tugs[1] id 重複:TG1');
    expect(v.issues).toContain('tugs[1] bollardPull_t 需 > 0');
    expect(v.issues.some((i) => i.startsWith('tugs[1] attach 需為'))).toBe(true);
    expect(v.issues).toContain('tugs[1] initial.mode push 不在 modes 內');
    expect(v.issues).toContain('tugs[1] initial.power_pct 需在 0–100');
    expect(v.issues).toContain('tugs[2] 缺 id');
    expect(v.issues).toContain('tugs[2] modes 不可為空');
  });

  it('錨:設定左/右錨與倍率 → 區段;清空 → 移除;驗證方位與倍率', () => {
    let d = patchAnchors(blankDraft(), 'port.length_shackles', 5);
    d = patchAnchors(d, 'port.bearing_deg', 45);
    d = patchAnchors(d, 'holdingFactor', 0.9);
    expect(toScenarioObject(d)['anchors']).toEqual({ port: { length_shackles: 5, bearing_deg: 45 }, holdingFactor: 0.9 });
    expect(validateDraft(d).issues).toEqual([]);
    expect(validateDraft(d).summary.anchorsInitial).toBe(true);
    d = patchAnchors(d, 'port.bearing_deg', 400);
    d = patchAnchors(d, 'holdingFactor', 0);
    d = patchAnchors(d, 'starboard.length_m', -1);
    const v = validateDraft(d);
    expect(v.issues).toContain('anchors.port.bearing_deg 需在 0–360');
    expect(v.issues).toContain('anchors.holdingFactor 需 > 0');
    expect(v.issues).toContain('anchors.starboard.length_m 需 ≥ 0');
    d = patchAnchors(d, 'port.bearing_deg', undefined);
    d = patchAnchors(d, 'port.length_shackles', undefined);
    d = patchAnchors(d, 'holdingFactor', undefined);
    d = patchAnchors(d, 'starboard.length_m', undefined);
    expect(d.anchors).toBeNull();
    expect(toScenarioObject(d)['anchors']).toBeUndefined();
  });

  it('泊位:建立(第一個泊位檔與泊位)→ 繫纜新增/欄位/刪除 → YAML;泊位檔與繫船柱清單;驗證', () => {
    expect(areaFileChoices('berths')).toContain('data/areas/mailiao-berth.json');
    expect(areaIds('berths', 'data/areas/mailiao-berth.json')).toEqual(['MLO-OIL-3']);
    expect(areaIds('berths', 'nope.json')).toEqual([]);
    let d = setBerthEnabled(blankDraft(), true);
    const first = areaFileChoices('berths')[0]!;
    expect(d.berth).toEqual({ file: first, id: areaIds('berths', first)[0], lines: [] });
    d = patchBerth(d, 'file', 'data/areas/mailiao-berth.json');
    d = patchBerth(d, 'id', 'MLO-OIL-3');
    d = patchBerth(d, 'departedDistance_m', 25);
    d = addBerthLine(d);
    d = addBerthLine(d);
    d = patchBerthLine(d, 0, 'id', 'HEAD');
    d = patchBerthLine(d, 0, 'bollard', 'B7');
    d = patchBerthLine(d, 0, 'initial', 'fast');
    d = patchBerthLine(d, 1, 'fairlead', bodyPointFromText('-60,-8'));
    d = patchBerthLine(d, 1, 'mbl_t', 60);
    expect(toScenarioObject(d)['berth']).toEqual({
      file: 'data/areas/mailiao-berth.json',
      id: 'MLO-OIL-3',
      lines: [
        { id: 'HEAD', fairlead: 'fwdPort', bollard: 'B7', mbl_t: 50, length_m: 150, initial: 'fast' },
        { id: 'LINE2', fairlead: { x: -60, y: -8 }, bollard: '', mbl_t: 60, length_m: 150, initial: 'stowed' },
      ],
      departedDistance_m: 25,
    });
    let v = validateDraft(d);
    expect(v.issues).toEqual([]);
    expect(v.summary.berth?.definition?.['id']).toBe('MLO-OIL-3');
    expect(v.summary.berth?.lines.map((l) => l.id)).toEqual(['HEAD', 'LINE2']);
    // 驗證:fast 無 bollard、id 重複、導纜孔缺、繫船柱不在泊位(警告)、file 缺、departedDistance ≤ 0、找不到泊位檔(警告)
    d = patchBerthLine(d, 0, 'bollard', '');
    d = patchBerthLine(d, 1, 'id', 'HEAD');
    d = patchBerthLine(d, 1, 'fairlead', undefined);
    d = patchBerthLine(d, 1, 'bollard', 'B99');
    d = patchBerth(d, 'departedDistance_m', 0);
    v = validateDraft(d);
    expect(v.issues).toContain('berth.lines[0] HEAD initial fast 需指定 bollard');
    expect(v.issues).toContain('berth.lines[1] id 重複:HEAD');
    expect(v.issues).toContain('berth.lines[1] HEAD 需 fairlead(fwd|aft|bow|stern|fwdPort|fwdStbd|midPort|midStbd|aftPort|aftStbd 或 {x, y})');
    expect(v.issues).toContain('berth.departedDistance_m 需 > 0');
    expect(v.warnings).toContain('berth.lines[1] HEAD:泊位定義沒有繫船柱 B99');
    v = validateDraft(patchBerth(d, 'file', ''));
    expect(v.issues).toContain('berth 需 file(泊位檔)或 definition');
    v = validateDraft(patchBerth(d, 'file', 'data/areas/unknown-berth.json'));
    expect(v.warnings.some((w) => w.startsWith('berth:教官站建置時匯入的 data/areas 找不到 data/areas/unknown-berth.json'))).toBe(true);
    d = removeBerthLine(removeBerthLine(d, 1), 0);
    expect(berthLines(d)).toEqual([]);
    expect(toScenarioObject(setBerthEnabled(d, false))['berth']).toBeUndefined();
  });

  it('岸壁:建立(第一個岸壁檔)→ ids/倍率 → YAML;lines 原樣保留並計數;驗證', () => {
    expect(areaFileChoices('banks')).toContain('data/areas/mailiao-channel-banks.json');
    let d = setBanksEnabled(blankDraft(), true);
    expect(d.banks).toEqual({ file: areaFileChoices('banks')[0] });
    d = patchBanks(d, 'file', 'data/areas/mailiao-channel-banks.json');
    d = patchBanks(d, 'ids', idListFromText('MLC-N, MLC-S'));
    d = patchBanks(d, 'scale', 0.5);
    expect(toScenarioObject(d)['banks']).toEqual({ file: 'data/areas/mailiao-channel-banks.json', ids: ['MLC-N', 'MLC-S'], scale: 0.5 });
    expect(areaIds('banks', 'data/areas/mailiao-channel-banks.json')).toContain('MLC-N');
    expect(validateDraft(d).issues).toEqual([]);
    d = patchBanks(d, 'ids', []);
    expect(toScenarioObject(d)['banks']).toEqual({ file: 'data/areas/mailiao-channel-banks.json', scale: 0.5 });
    let v = validateDraft(patchBanks(patchBanks(d, 'file', ''), 'scale', -1));
    expect(v.issues).toContain('banks 需 file(岸壁檔)或 lines');
    expect(v.issues).toContain('banks.scale 需 ≥ 0');
    v = validateDraft(patchBanks(d, 'file', 'data/areas/none.json'));
    expect(v.warnings.some((w) => w.startsWith('banks:教官站建置時匯入的 data/areas 找不到'))).toBe(true);
    const e06 = fromYaml(read('E06_taichung_channel_ebb.yaml'), 'E06');
    expect(e06.banks).toEqual({ file: 'data/areas/taichung-harbour-entrance.json' });
    expect(toScenarioObject(setBanksEnabled(d, false))['banks']).toBeUndefined();
  });
});

describe('工具函式與草稿版本', () => {
  it('withPath / getPath / moveAt / uniqueId / flowYaml / parseValueText / idListFromText', () => {
    const o = withPath({ a: { b: 1, c: 2 } }, 'a.b', undefined);
    expect(o).toEqual({ a: { c: 2 } });
    expect(withPath(o, 'a.c', '')).toEqual({});
    expect(withPath(null, 'x.y.z', 3)).toEqual({ x: { y: { z: 3 } } });
    expect(getPath({ x: { y: { z: 3 } } }, 'x.y.z')).toBe(3);
    expect(getPath({ x: 1 }, 'x.y')).toBeUndefined();
    expect(moveAt([1, 2, 3], 0, 1)).toEqual([2, 1, 3]);
    expect(moveAt([1, 2, 3], 2, 1)).toEqual([1, 2, 3]);
    expect(moveAt([1, 2, 3], 0, -1)).toEqual([1, 2, 3]);
    expect(uniqueId('WP', ['WP1', 'WP2'], 1)).toBe('WP3');
    expect(uniqueId('T', ['T2'], 2)).toBe('T3');
    expect(flowYaml({ to: 'instructor', text: '注意: 航向', n: [1, 2] })).toBe('{ to: instructor, text: "注意: 航向", n: [ 1, 2 ] }');
    expect(flowYaml('steeringGear')).toBe('steeringGear');
    expect(flowYaml(undefined)).toBe('');
    expect(parseValueText('')).toBeUndefined();
    expect(parseValueText('{ a: 1 }')).toEqual({ a: 1 });
    expect(parseValueText('all')).toBe('all');
    expect(() => parseValueText('{ a: [')).toThrow();
    expect(idListFromText(' A, B、C  D ')).toEqual(['A', 'B', 'C', 'D']);
  });

  it('版本 1 草稿(第十七輪)升級:已編輯欄位沿用、新區段由 raw 重建;版本不合或缺區段回 null', () => {
    const text = read('E13_tug_berthing_mailiao.yaml');
    const v2 = fromYaml(text, 'E13');
    const v1 = { ...v2, version: 1, general: { ...v2.general, name: '舊草稿' }, assessment: { hardFail: v2.assessment.hardFail, bands: v2.assessment.bands, start: 100, passMark: 70 } } as unknown as RawSection;
    delete v1['route'];
    delete v1['zones'];
    delete v1['tugs'];
    delete v1['berth'];
    delete v1['briefing'];
    const storage = new MemoryStorage();
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(v1));
    const back = loadDraft(storage)!;
    expect(back.version).toBe(2);
    expect(back.general.name).toBe('舊草稿');
    expect(back.tugs.map((t) => t['id'])).toEqual(['TG1', 'TG2']);
    expect(back.zones).toHaveLength(1);
    expect(back.assessment.required).toHaveLength(v2.assessment.required.length);
    expect(back.assessment.passMark).toBe(70);
    expect(parseYaml(toYaml(back))).toEqual({ ...parseYaml(text), name: '舊草稿', assessment: { ...(parseYaml(text) as RawSection)['assessment'] as RawSection, scoring: { start: 100, passMark: 70 } } });
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify({ ...v2, version: 3 }));
    expect(loadDraft(storage)).toBeNull();
    storage.setItem(DRAFT_STORAGE_KEY, JSON.stringify({ ...v2, zones: undefined }));
    expect(loadDraft(storage)).toBeNull();
  });
});
