// 情境編輯器的擴充區段(規劃書第 9.1 節;R4;設計文件 docs/design/scenario-engine.md 第 6.1 節):航線、區域、時間表項目逐項編輯、
// 簡報角色/附件、感測器/警報/BNWAS、評估必要事件與設定、拖船/錨/泊位/岸壁。以純函式渲染,透過 EditorHost 讀寫 <brm-scenario-editor> 的草稿,
// 樣式沿用主元件(模板渲染在同一個 shadow root)。草稿模型與各區段的修改函式見 lib/scenario-editor.ts(原樣物件 + 點路徑修改,未改的鍵不動)。

import { html, nothing, type TemplateResult } from 'lit';
import {
  ACTION_ARG_HINTS,
  ACTION_KINDS,
  AFTER_EVENT_SUGGESTIONS,
  ALERT_FIELDS,
  ANCHOR_SIDES,
  BNWAS_FIELDS,
  BNWAS_MODES,
  BODY_POINTS,
  ENCOUNTER_FIELDS,
  LINE_INITIAL_STATES,
  MANOEUVRING_FIELDS,
  PHASES,
  PLAYED_BY_OPTIONS,
  ROLE_SUGGESTIONS,
  SENSOR_FIELDS,
  TUG_INITIAL_MODES,
  TUG_MODES,
  UKC_LIMIT_KINDS,
  WAYPOINT_FIELDS,
  ZONE_TYPE_LABEL,
  addAttachment,
  addBerthLine,
  addRequired,
  addRole,
  addTimelineAction,
  addTug,
  addWaypoint,
  addZone,
  addZoneVertex,
  areaFileChoices,
  areaIds,
  bankLineCount,
  berthLines,
  bodyPointFromText,
  bodyPointText,
  briefingAttachments,
  briefingRoles,
  expectSuggestions,
  idListFromText,
  intFromText,
  moveRequired,
  moveTimelineAction,
  moveTimelineItem,
  moveWaypoint,
  newActionValue,
  numberOrRefFromText,
  numberOrRefText,
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
  positionMode,
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
  tugModes,
  ukcLimitForm,
  zonePolygon,
  type DraftPosition,
  type FaultTrigger,
  type FlatSectionKey,
  type NumberOrRef,
  type PositionMode,
  type RawSection,
  type ScenarioDraft,
  type TriggerKind,
  type UkcLimitKind,
} from '../../lib/scenario-editor.js';
import { ZONE_TYPES } from '../../lib/timeline.js';

/** 主元件提供給各區段的介面:讀最新草稿、寫回(觸發驗證與儲存)、表單錯誤、展開狀態。 */
export interface EditorHost {
  readonly draft: ScenarioDraft;
  setDraft(d: ScenarioDraft): void;
  /** 顯示表單錯誤(不改草稿) */
  fail(message: string): void;
  isOpen(key: string): boolean;
  toggle(key: string): void;
}

export function textOf(ev: Event): string {
  return (ev.target as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value;
}

export function checked(ev: Event): boolean {
  return (ev.target as HTMLInputElement).checked;
}

function numOf(ev: Event): NumberOrRef | undefined {
  return numberOrRefFromText(textOf(ev));
}

function txt(v: unknown): string {
  return v === undefined || v === null ? '' : String(v);
}

function isRecord(v: unknown): v is RawSection {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/** 以 try/catch 包住會丟 Error 的表單處理(YAML 片段、座標點格式),錯誤顯示在輸出區。 */
function guard(h: EditorHost, fn: () => void): void {
  try {
    fn();
  } catch (err) {
    h.fail(err instanceof Error ? err.message : String(err));
  }
}

/** 數值/參數引用輸入框(type text + inputmode decimal,可填 ${params:key})。 */
function numInput(cls: string, value: unknown, onChange: (v: NumberOrRef | undefined) => void, placeholder = ''): TemplateResult {
  return html`<input class=${cls} type="text" inputmode="decimal" placeholder=${placeholder} .value=${numberOrRefText(numberOrRefFromText(txt(value)))} @change=${(e: Event) => onChange(numOf(e))} />`;
}

/** 上/下移與刪除鈕。 */
function moveButtons(prefix: string, canUp: boolean, canDown: boolean, onMove: (delta: number) => void, onRemove: () => void): TemplateResult {
  return html`<span class="pair">
    <button class="small ${prefix}-up" title="上移" ?disabled=${!canUp} @click=${() => onMove(-1)}>↑</button>
    <button class="small ${prefix}-down" title="下移" ?disabled=${!canDown} @click=${() => onMove(1)}>↓</button>
    <button class="small danger ${prefix}-remove" @click=${onRemove}>刪除</button>
  </span>`;
}

// ------------------------------------------------------------------ 位置欄位(自船、目標船、航點、區域頂點共用)

function setPositionKey(current: () => DraftPosition, key: keyof DraftPosition, ev: Event, apply: (p: DraftPosition) => void): void {
  const p = { ...current() };
  const v = numberOrRefFromText(textOf(ev));
  if (v === undefined) delete p[key];
  else p[key] = v;
  apply(p);
}

/** 位置欄位;current 以函式取得,讓連續變更都讀到最新草稿而非渲染時的複本。 */
export function renderPosition(current: () => DraftPosition, cls: string, apply: (p: DraftPosition) => void): TemplateResult {
  const p = current();
  const mode = positionMode(p);
  const switchMode = (ev: Event) => {
    const want = textOf(ev) as PositionMode;
    if (positionMode(current()) === want) return;
    apply(want === 'latlon' ? { lat: 23.9, lon: 119.98 } : { x: 0, y: 0 });
  };
  return html`
    <select class="${cls}-mode" data-value=${mode} @change=${switchMode}>
      <option value="latlon">經緯度</option>
      <option value="enu">ENU x/y (m)</option>
    </select>
    ${mode === 'latlon'
      ? html`<div class="pair">
          <input class="${cls}-lat" type="number" step="0.0001" .value=${numberOrRefText(p.lat)} @change=${(e: Event) => setPositionKey(current, 'lat', e, apply)} /><span>°N</span>
          <input class="${cls}-lon" type="number" step="0.0001" .value=${numberOrRefText(p.lon)} @change=${(e: Event) => setPositionKey(current, 'lon', e, apply)} /><span>°E</span>
        </div>`
      : html`<div class="pair">
          <input class="${cls}-x" type="number" step="1" .value=${numberOrRefText(p.x)} @change=${(e: Event) => setPositionKey(current, 'x', e, apply)} /><span>x</span>
          <input class="${cls}-y" type="number" step="1" .value=${numberOrRefText(p.y)} @change=${(e: Event) => setPositionKey(current, 'y', e, apply)} /><span>y</span>
        </div>`}
  `;
}

function positionOf(o: RawSection | undefined): DraftPosition {
  const p: DraftPosition = {};
  if (!o) return p;
  for (const k of ['lat', 'lon', 'x', 'y'] as const) {
    const v = numberOrRefFromText(txt(o[k]));
    if (v !== undefined) p[k] = v;
  }
  return p;
}

// ------------------------------------------------------------------ 航線 route

export function renderRouteSection(h: EditorHost): TemplateResult {
  const d = h.draft;
  const route = d.route;
  const wps = routeWaypoints(d);
  const kept = route ? Object.keys(route).filter((k) => !['name', 'xtdLimit_m', 'startLeg', 'waypoints'].includes(k)) : [];
  return html`<div class="panel route">
    <div class="panel-title">
      <span>航線 ROUTE</span>
      <label class="pair"><input class="route-enabled" type="checkbox" .checked=${route !== null} @change=${(e: Event) => h.setDraft(setRouteEnabled(h.draft, checked(e)))} /><span>${route ? `${wps.length} 航點` : '無航線'}</span></label>
    </div>
    ${route
      ? html`<div class="form">
            <label>名稱</label>
            <input class="route-name" type="text" .value=${txt(route['name'])} @change=${(e: Event) => h.setDraft(patchRoute(h.draft, 'name', textOf(e)))} />
            <label>走廊半寬 (m)</label>
            ${numInput('route-xtd', route['xtdLimit_m'], (v) => h.setDraft(patchRoute(h.draft, 'xtdLimit_m', v)), '可被航點覆寫')}
            <label>起始航段</label>
            <input class="route-startleg" type="number" step="1" min="0" .value=${txt(route['startLeg'])} @change=${(e: Event) => h.setDraft(patchRoute(h.draft, 'startLeg', intFromText(textOf(e))))} />
            ${kept.length > 0 ? html`<div class="full hint">範本的 ${kept.join('、')} 原樣保留</div>` : nothing}
          </div>
          ${wps.map((w, i) => renderWaypoint(h, w, i, wps.length))}
          <div class="btn-row"><button class="add-waypoint" @click=${() => h.setDraft(addWaypoint(h.draft))}>新增航點</button></div>
          <div class="hint">速限/走廊/到達半徑作用於「到達該航點的那一段」;檢查點通過時產生 checkpointPassed 事件;報告點填說明文字(事件 reportPointPassed)。</div>`
      : html`<div class="hint">勾選建立航線(2 個航點),或套用含航線的範本。</div>`}
  </div>`;
}

function renderWaypoint(h: EditorHost, w: RawSection, i: number, count: number): TemplateResult {
  const kept = Object.keys(w).filter((k) => !['name', 'lat', 'lon', 'x', 'y', 'checkpoint', 'report', ...WAYPOINT_FIELDS.map((f) => f[0])].includes(k));
  return html`<div class="row g4 wp" data-index=${i}>
    <label>#${i + 1} 名稱</label>
    <input class="wp-name" type="text" .value=${txt(w['name'])} @change=${(e: Event) => h.setDraft(patchWaypoint(h.draft, i, 'name', textOf(e)))} />
    <label>位置</label>
    <div class="pair">${renderPosition(() => positionOf(routeWaypoints(h.draft)[i]), 'wp-pos', (p) => h.setDraft(patchWaypointPosition(h.draft, i, p)))}</div>
    ${WAYPOINT_FIELDS.map(
      ([key, label, unit]) => html`<label>${label} (${unit})</label>${numInput(`wp-${key}`, w[key], (v) => h.setDraft(patchWaypoint(h.draft, i, key, v)))}`,
    )}
    <label>報告點說明</label>
    <input class="wp-report" type="text" .value=${txt(w['report'])} @change=${(e: Event) => h.setDraft(patchWaypoint(h.draft, i, 'report', textOf(e)))} />
    <div class="tail">
      <label class="pair"><input class="wp-checkpoint" type="checkbox" .checked=${w['checkpoint'] === true} @change=${(e: Event) => h.setDraft(patchWaypoint(h.draft, i, 'checkpoint', checked(e) ? true : undefined))} /><span>檢查點</span>${kept.length > 0 ? html`<span class="hint">保留:${kept.join('、')}</span>` : nothing}</label>
      ${moveButtons('wp', i > 0, i < count - 1, (delta) => h.setDraft(moveWaypoint(h.draft, i, delta)), () => h.setDraft(removeWaypoint(h.draft, i)))}
    </div>
  </div>`;
}

// ------------------------------------------------------------------ 區域 zones

export function renderZonesSection(h: EditorHost): TemplateResult {
  const zones = h.draft.zones;
  return html`<div class="panel zones">
    <div class="panel-title"><span>區域 ZONES</span><span class="zh">${zones.length} 區;多邊形 ≥ 3 點</span></div>
    ${zones.map((z, i) => renderZone(h, z, i))}
    <div class="btn-row"><button class="add-zone" @click=${() => h.setDraft(addZone(h.draft))}>新增區域</button></div>
  </div>`;
}

function renderZone(h: EditorHost, z: RawSection, i: number): TemplateResult {
  const poly = zonePolygon(z);
  const ukc = ukcLimitForm(z['ukcMin']);
  const type = txt(z['type']);
  const open = h.isOpen(`zone:${i}`);
  const kept = Object.keys(z).filter((k) => !['id', 'name', 'type', 'polygon', 'waterDepth_m', 'ukcMin', 'speedLimit_kn'].includes(k));
  return html`<div class="row g4 zone" data-index=${i}>
    <label>id</label>
    <input class="z-id" type="text" .value=${txt(z['id'])} @change=${(e: Event) => h.setDraft(patchZone(h.draft, i, 'id', textOf(e)))} />
    <label>名稱</label>
    <input class="z-name" type="text" .value=${txt(z['name'])} @change=${(e: Event) => h.setDraft(patchZone(h.draft, i, 'name', textOf(e)))} />
    <label>型別</label>
    <select class="z-type" data-value=${type} @change=${(e: Event) => h.setDraft(patchZone(h.draft, i, 'type', textOf(e)))}>
      ${ZONE_TYPES.map((t) => html`<option value=${t}>${t} ${ZONE_TYPE_LABEL[t] ?? ''}</option>`)}
      ${type && !(ZONE_TYPES as readonly string[]).includes(type) ? html`<option value=${type}>${type}(未知)</option>` : nothing}
    </select>
    <label>速限 (kn)</label>
    ${numInput('z-speed', z['speedLimit_kn'], (v) => h.setDraft(patchZone(h.draft, i, 'speedLimit_kn', v)), 'speedLimit 區')}
    <label>水深覆寫 (m)</label>
    ${numInput('z-depth', z['waterDepth_m'], (v) => h.setDraft(patchZone(h.draft, i, 'waterDepth_m', v)), 'ukcPolicy 區')}
    <label>UKC 下限</label>
    <div class="pair">
      <select class="z-ukc-kind" data-value=${ukc.kind} @change=${(e: Event) => h.setDraft(setZoneUkcMin(h.draft, i, textOf(e) as UkcLimitKind | '', ukcLimitForm(h.draft.zones[i]?.['ukcMin']).value ?? (textOf(e) === 'metres' ? 1 : 0.1)))}>
        <option value="">(無)</option>
        ${UKC_LIMIT_KINDS.map((k) => html`<option value=${k}>${k === 'metres' ? 'm' : '吃水比例'}</option>`)}
      </select>
      ${numInput('z-ukc-value', ukc.value, (v) => h.setDraft(setZoneUkcMin(h.draft, i, ukcLimitForm(h.draft.zones[i]?.['ukcMin']).kind, v)))}
    </div>
    <div class="tail">
      <span class="hint">多邊形 ${poly.length} 點${kept.length > 0 ? ` · 保留:${kept.join('、')}` : ''}</span>
      <span class="pair">
        <button class="small z-vertices" @click=${() => h.toggle(`zone:${i}`)}>${open ? '收合頂點' : '編輯頂點'}</button>
        <button class="small danger z-remove" @click=${() => h.setDraft(removeZone(h.draft, i))}>刪除</button>
      </span>
    </div>
    ${open
      ? html`<div class="full vertices">
          ${poly.map(
            (p, j) => html`<div class="row g2 vertex" data-vertex=${j}>
              <label>#${j + 1}</label>
              <div class="pair">
                ${renderPosition(() => positionOf(zonePolygon(h.draft.zones[i] ?? {})[j]), 'z-v', (pos) => h.setDraft(patchZoneVertex(h.draft, i, j, pos)))}
                <button class="small danger v-remove" @click=${() => h.setDraft(removeZoneVertex(h.draft, i, j))}>✕</button>
              </div>
            </div>`,
          )}
          <div class="btn-row"><button class="small add-vertex" @click=${() => h.setDraft(addZoneVertex(h.draft, i))}>新增頂點</button></div>
        </div>`
      : nothing}
  </div>`;
}

// ------------------------------------------------------------------ 時間表項目逐項編輯

/** 時間表項目的展開編輯器(主元件在每個項目列下方呼叫)。 */
export function renderTimelineItemEditor(h: EditorHost, index: number): TemplateResult {
  const item = h.draft.timeline[index];
  if (!item) return html``;
  const f = timelineItemForm(item);
  const count = h.draft.timeline.length;
  const setTrigger = (kind: TriggerKind, atLeastText: string, conditionText: string) =>
    guard(h, () => {
      let trigger: FaultTrigger;
      if (kind === 'manual') trigger = { kind: 'manual' };
      else if (kind === 'time') {
        const t = Number(atLeastText);
        if (atLeastText.trim() === '' || !Number.isFinite(t) || t < 0) throw new Error('觸發時間需為 ≥ 0 的秒數');
        trigger = { kind: 'time', atLeast: t };
      } else {
        const when = parseValueText(conditionText);
        if (!isRecord(when)) throw new Error('觸發條件 需為 YAML/JSON 物件(例如 { type: ownSpeed, lessThan: 5 })');
        trigger = { kind: 'condition', when };
      }
      h.setDraft(setTimelineTrigger(h.draft, index, trigger));
    });
  return html`<fieldset class="tl-editor" data-index=${index}>
    <legend>編輯項目 ${f.id}</legend>
    <label>id</label>
    <input class="tl-id" type="text" .value=${f.id} @change=${(e: Event) => h.setDraft(patchTimelineItem(h.draft, index, 'id', textOf(e).trim()))} />
    <label>名稱</label>
    <input class="tl-name" type="text" .value=${f.name} @change=${(e: Event) => h.setDraft(patchTimelineItem(h.draft, index, 'name', textOf(e)))} />
    <label>觸發</label>
    <select class="tl-trigger" data-value=${f.trigger} @change=${(e: Event) => setTrigger(textOf(e) as TriggerKind, f.atLeast || '0', f.condition || '{ type: ownSpeed, lessThan: 5 }')}>
      <option value="time">模擬時間 t ≥</option>
      <option value="manual">教官手動觸發</option>
      <option value="condition">自訂條件</option>
    </select>
    ${f.trigger === 'time'
      ? html`<div class="pair"><input class="tl-time" type="number" step="30" min="0" .value=${f.atLeast} @change=${(e: Event) => setTrigger('time', textOf(e), '')} /><span>s</span></div>`
      : f.trigger === 'condition'
        ? html`<input class="tl-condition" type="text" .value=${f.condition} @change=${(e: Event) => setTrigger('condition', '', textOf(e))} />`
        : html`<span class="hint">時間表面板「觸發」鈕</span>`}
    <label>階段</label>
    <select class="tl-phase" data-value=${f.phase} @change=${(e: Event) => h.setDraft(patchTimelineItem(h.draft, index, 'phase', textOf(e)))}>
      <option value="">(不變)</option>
      ${PHASES.map((p) => html`<option value=${p}>${p}</option>`)}
      ${f.phase && !PHASES.includes(f.phase) ? html`<option value=${f.phase}>${f.phase}</option>` : nothing}
    </select>
    <label>可見</label>
    <select class="tl-visibility" data-value=${f.visibility} @change=${(e: Event) => h.setDraft(patchTimelineItem(h.draft, index, 'visibility', textOf(e)))}>
      <option value="">(預設 教官)</option>
      <option value="instructor">instructor 教官</option>
      <option value="all">all 學員可見</option>
    </select>
    <label>條件持續 (s)</label>
    ${numInput('tl-hold', item['holdFor_s'], (v) => h.setDraft(patchTimelineItem(h.draft, index, 'holdFor_s', v)))}
    <label>延遲執行 (s)</label>
    ${numInput('tl-delay', item['delay_s'], (v) => h.setDraft(patchTimelineItem(h.draft, index, 'delay_s', v)))}
    <label>初始啟用</label>
    <select class="tl-armed" data-value=${f.armed} @change=${(e: Event) => h.setDraft(patchTimelineItem(h.draft, index, 'armed', textOf(e)))}>
      <option value="">(預設 啟用)</option>
      <option value="false">false(需 arm 啟用)</option>
      <option value="true">true</option>
    </select>
    <div class="full actions">
      ${f.actions.map((a, j) => renderAction(h, index, j, a, f.actions.length))}
      <div class="btn-row">
        <button class="small add-action" @click=${() => h.setDraft(addTimelineAction(h.draft, index, 'message'))}>新增動作</button>
        ${moveButtons('tl', index > 0, index < count - 1, (delta) => h.setDraft(moveTimelineItem(h.draft, index, delta)), () => h.setDraft({ ...h.draft, timeline: h.draft.timeline.filter((_, k) => k !== index) }))}
      </div>
    </div>
    ${f.kept.length > 0 ? html`<div class="full hint">保留:${f.kept.join('、')}</div>` : nothing}
  </fieldset>`;
}

function renderAction(h: EditorHost, index: number, j: number, a: { kind: string; value: string }, count: number): TemplateResult {
  const hint = (ACTION_ARG_HINTS as Record<string, string>)[a.kind] ?? '';
  const applyValue = (text: string) =>
    guard(h, () => {
      const v = parseValueText(text);
      if (v === undefined) throw new Error(`動作 ${a.kind} 需有內容:${hint}`);
      h.setDraft(setTimelineAction(h.draft, index, j, a.kind, v));
    });
  const changeKind = (kind: string) => h.setDraft(setTimelineAction(h.draft, index, j, kind, newActionValue(kind, h.draft)));
  return html`<div class="row g2 action" data-action=${j}>
    <select class="a-kind" data-value=${a.kind} @change=${(e: Event) => changeKind(textOf(e))}>
      ${ACTION_KINDS.map((k) => html`<option value=${k}>${k}</option>`)}
      ${a.kind && !ACTION_KINDS.includes(a.kind) ? html`<option value=${a.kind}>${a.kind}(未知)</option>` : nothing}
    </select>
    <div class="pair">
      <input class="a-value" type="text" placeholder=${hint} title=${hint} .value=${a.value} @change=${(e: Event) => applyValue(textOf(e))} />
      ${moveButtons('a', j > 0, j < count - 1, (delta) => h.setDraft(moveTimelineAction(h.draft, index, j, delta)), () => h.setDraft(removeTimelineAction(h.draft, index, j)))}
    </div>
    <div class="full hint">${hint}</div>
  </div>`;
}

// ------------------------------------------------------------------ 簡報角色與附件

export function renderBriefingSection(h: EditorHost): TemplateResult {
  const d = h.draft;
  const roles = briefingRoles(d);
  const attachments = briefingAttachments(d);
  const kept = Object.keys(d.briefing).filter((k) => !['roles', 'attachments', 'minParticipants', 'duration_s'].includes(k));
  return html`<div class="panel briefing">
    <div class="panel-title"><span>簡報角色與附件 BRIEFING</span><span class="zh">角色 ${roles.length}、附件 ${attachments.length}</span></div>
    <div class="form">
      <label>最少人數</label>
      <input class="b-min" type="number" step="1" min="1" .value=${txt(d.briefing['minParticipants'])} @change=${(e: Event) => h.setDraft(patchBriefing(h.draft, 'minParticipants', intFromText(textOf(e))))} />
      <label>預估時長 (s)</label>
      ${numInput('b-duration', d.briefing['duration_s'], (v) => h.setDraft(patchBriefing(h.draft, 'duration_s', v)))}
      ${kept.length > 0 ? html`<div class="full hint">範本的 ${kept.join('、')} 原樣保留</div>` : nothing}
    </div>
    <datalist id="role-names">${ROLE_SUGGESTIONS.map((r) => html`<option value=${r}></option>`)}</datalist>
    ${roles.map(
      (r, i) => html`<div class="row g4 role" data-index=${i}>
        <label>角色</label>
        <input class="r-role" type="text" list="role-names" .value=${txt(r['role'])} @change=${(e: Event) => h.setDraft(patchRole(h.draft, i, 'role', textOf(e)))} />
        <label>扮演</label>
        <select class="r-played" data-value=${txt(r['playedBy'])} @change=${(e: Event) => h.setDraft(patchRole(h.draft, i, 'playedBy', textOf(e)))}>
          <option value="">(預設 leader)</option>
          ${PLAYED_BY_OPTIONS.map((p) => html`<option value=${p}>${p}</option>`)}
          ${r['playedBy'] && !PLAYED_BY_OPTIONS.includes(txt(r['playedBy'])) ? html`<option value=${txt(r['playedBy'])}>${txt(r['playedBy'])}</option>` : nothing}
        </select>
        <div class="tail">
          <label class="pair"><input class="r-required" type="checkbox" .checked=${r['required'] === true} @change=${(e: Event) => h.setDraft(patchRole(h.draft, i, 'required', checked(e)))} /><span>必要</span></label>
          <button class="small danger r-remove" @click=${() => h.setDraft(removeRole(h.draft, i))}>刪除</button>
        </div>
      </div>`,
    )}
    <div class="btn-row"><button class="add-role" @click=${() => h.setDraft(addRole(h.draft))}>新增角色</button></div>
    ${attachments.map(
      (a, i) => html`<div class="row g4 attachment" data-index=${i}>
        <label>附件標題</label>
        <input class="at-title" type="text" .value=${txt(a['title'])} @change=${(e: Event) => h.setDraft(patchAttachment(h.draft, i, 'title', textOf(e)))} />
        <label>檔案</label>
        <div class="pair">
          <input class="at-file" type="text" placeholder="docs/passage-plan.pdf" .value=${txt(a['file'])} @change=${(e: Event) => h.setDraft(patchAttachment(h.draft, i, 'file', textOf(e)))} />
          <button class="small danger at-remove" @click=${() => h.setDraft(removeAttachment(h.draft, i))}>刪除</button>
        </div>
      </div>`,
    )}
    <div class="btn-row"><button class="add-attachment" @click=${() => h.setDraft(addAttachment(h.draft))}>新增附件</button></div>
  </div>`;
}

// ------------------------------------------------------------------ 感測器 / 警報 / BNWAS

function renderFlatFields(h: EditorHost, section: FlatSectionKey, prefix: string, fields: ReadonlyArray<readonly [string, string, string]>): TemplateResult[] {
  const o = h.draft[section];
  return fields.map(([key, label, unit]) => html`<label>${label} (${unit})</label>${numInput(`${prefix}-${key}`, o?.[key], (v) => h.setDraft(patchSection(h.draft, section, key, v)), '未設 = 預設/關閉')}`);
}

export function renderSensorsSection(h: EditorHost): TemplateResult {
  const d = h.draft;
  const mode = txt(d.bnwas?.['mode']);
  return html`<div class="panel sensors">
    <div class="panel-title"><span>感測器 / 警報 / BNWAS</span><span class="zh">門檻未設 = 關閉;秒數未設 = 預設</span></div>
    <fieldset>
      <legend>感測器 sensors</legend>
      ${renderFlatFields(h, 'sensors', 's', SENSOR_FIELDS)}
    </fieldset>
    <fieldset>
      <legend>警報門檻 alerts(BAM)</legend>
      ${renderFlatFields(h, 'alerts', 'al', ALERT_FIELDS)}
    </fieldset>
    <fieldset>
      <legend>BNWAS(MSC.128(75))</legend>
      <label>模式</label>
      <select class="bn-mode" data-value=${mode} @change=${(e: Event) => h.setDraft(patchSection(h.draft, 'bnwas', 'mode', textOf(e)))}>
        <option value="">(預設 off)</option>
        ${BNWAS_MODES.map((m) => html`<option value=${m}>${m}${m === 'auto' ? '(自動舵啟用時)' : ''}</option>`)}
        ${mode && !BNWAS_MODES.includes(mode) ? html`<option value=${mode}>${mode}(未知)</option>` : nothing}
      </select>
      ${renderFlatFields(h, 'bnwas', 'bn', BNWAS_FIELDS)}
    </fieldset>
  </div>`;
}

// ------------------------------------------------------------------ 評估:必要事件與設定

export function renderAssessmentExtras(h: EditorHost): TemplateResult {
  const d = h.draft;
  const s = d.assessment.settings;
  const enc = isRecord(s['encounter']) ? s['encounter'] : {};
  const man = isRecord(s['manoeuvring']) ? s['manoeuvring'] : {};
  const ukc = ukcLimitForm(isRecord(s['ukc']) ? s['ukc']['min'] : undefined);
  const required = d.assessment.required;
  return html`
    <div class="panel-title"><span>必要事件 required</span><span class="zh">${required.length} 條;after 計時起點 → expect 期望動作</span></div>
    <datalist id="after-events">${AFTER_EVENT_SUGGESTIONS.map((e) => html`<option value=${e}></option>`)}</datalist>
    <datalist id="expect-specs">${expectSuggestions().map((e) => html`<option value=${e}></option>`)}</datalist>
    <datalist id="timeline-ids">${d.timeline.map((t) => html`<option value=${txt(t['id'])}></option>`)}</datalist>
    ${required.map((r, i) => renderRequired(h, r, i, required.length))}
    <div class="btn-row"><button class="add-required" @click=${() => h.setDraft(addRequired(h.draft))}>新增必要事件</button></div>
    <fieldset class="settings">
      <legend>評估設定(minCpa、會遇、UKC、操縱)</legend>
      <label>CPA 警報 (nm)</label>
      ${numInput('as-minCpa', s['minCpa_nm'], (v) => h.setDraft(patchAssessmentSetting(h.draft, 'minCpa_nm', v)), '預設 0.5')}
      <label>UKC 下限</label>
      <div class="pair">
        <select class="ukc-kind" data-value=${ukc.kind} @change=${(e: Event) => h.setDraft(setAssessmentUkcMin(h.draft, textOf(e) as UkcLimitKind | '', ukcLimitForm(isRecord(h.draft.assessment.settings['ukc']) ? h.draft.assessment.settings['ukc']['min'] : undefined).value ?? (textOf(e) === 'metres' ? 1 : 0.1)))}>
          <option value="">(無)</option>
          ${UKC_LIMIT_KINDS.map((k) => html`<option value=${k}>${k === 'metres' ? 'm' : '吃水比例'}</option>`)}
        </select>
        ${numInput('ukc-value', ukc.value, (v) => h.setDraft(setAssessmentUkcMin(h.draft, ukcLimitForm(isRecord(h.draft.assessment.settings['ukc']) ? h.draft.assessment.settings['ukc']['min'] : undefined).kind, v)))}
      </div>
      ${ENCOUNTER_FIELDS.map(([key, label, unit]) => html`<label>會遇 ${label} (${unit})</label>${numInput(`enc-${key}`, enc[key], (v) => h.setDraft(patchAssessmentSetting(h.draft, `encounter.${key}`, v)))}`)}
      ${MANOEUVRING_FIELDS.map(([key, label, unit]) => html`<label>操縱 ${label} (${unit})</label>${numInput(`man-${key}`, man[key], (v) => h.setDraft(patchAssessmentSetting(h.draft, `manoeuvring.${key}`, v)))}`)}
    </fieldset>
  `;
}

function renderRequired(h: EditorHost, r: RawSection, i: number, count: number): TemplateResult {
  const f = requiredForm(r);
  const set = (path: string, value: unknown) => h.setDraft(patchRequired(h.draft, i, path, value));
  return html`<div class="row g4 required" data-index=${i}>
    <label>id</label>
    <input class="rq-id" type="text" .value=${f.id} @change=${(e: Event) => set('id', textOf(e).trim())} />
    <label>計時起點事件</label>
    <input class="rq-event" type="text" list="after-events" .value=${f.event} @change=${(e: Event) => set('after.event', textOf(e).trim())} />
    <label>項目 / 目標 / 航點</label>
    <div class="pair">
      <input class="rq-item" type="text" list="timeline-ids" placeholder="itemId" .value=${f.itemId} @change=${(e: Event) => set('after.itemId', textOf(e).trim())} />
      <input class="rq-target" type="text" placeholder="targetId" .value=${f.targetId} @change=${(e: Event) => set('after.targetId', textOf(e).trim())} />
      <input class="rq-waypoint" type="text" placeholder="waypoint" .value=${f.waypoint} @change=${(e: Event) => set('after.waypoint', textOf(e).trim())} />
    </div>
    <label>期望</label>
    <input class="rq-expect" type="text" list="expect-specs" title=${`動作名(${['rudderOrder', 'telegraphChange', '…'].join('/')})、{ acknowledge: { kind } }、{ event: { kind } }、{ any: [ … ] }`} .value=${f.expect} @change=${(e: Event) =>
      guard(h, () => {
        const v = parseValueText(textOf(e));
        if (v === undefined) throw new Error('期望(expect)不可空白');
        set('expect', v);
      })} />
    <label>期限 (s)</label>
    ${numInput('rq-within', r['within_s'], (v) => set('within_s', v))}
    <label>扣分 / 逾時每秒</label>
    <div class="pair">
      ${numInput('rq-penalty', r['penalty'], (v) => set('penalty', v))}
      ${numInput('rq-late', r['latePenaltyPer_s'], (v) => set('latePenaltyPer_s', v))}
    </div>
    <div class="tail">
      <span class="hint">${f.keptAfter.length > 0 ? `after 保留:${f.keptAfter.join('、')}` : ''}</span>
      ${moveButtons('rq', i > 0, i < count - 1, (delta) => h.setDraft(moveRequired(h.draft, i, delta)), () => h.setDraft(removeRequired(h.draft, i)))}
    </div>
  </div>`;
}

// ------------------------------------------------------------------ 拖船 / 錨 / 泊位 / 岸壁

export function renderExternalSection(h: EditorHost): TemplateResult {
  const d = h.draft;
  return html`<div class="panel external">
    <div class="panel-title"><span>拖船 / 錨 / 泊位 / 岸壁</span><span class="zh">拖船 ${d.tugs.length} · 錨 ${d.anchors ? '有' : '無'} · 泊位 ${d.berth ? '有' : '無'} · 岸壁 ${d.banks ? '有' : '無'}</span></div>
    <datalist id="body-points">${BODY_POINTS.map((p) => html`<option value=${p}></option>`)}</datalist>
    ${renderTugs(h)}
    ${renderAnchors(h)}
    ${renderBerth(h)}
    ${renderBanks(h)}
  </div>`;
}

function renderTugs(h: EditorHost): TemplateResult {
  const tugs = h.draft.tugs;
  return html`<fieldset class="tugs">
    <legend>拖船 tugs(${tugs.length})</legend>
    <div class="full">
      ${tugs.map((t, i) => renderTug(h, t, i))}
      <div class="btn-row"><button class="small add-tug" @click=${() => h.setDraft(addTug(h.draft))}>新增拖船</button></div>
    </div>
  </fieldset>`;
}

function renderTug(h: EditorHost, t: RawSection, i: number): TemplateResult {
  const modes = tugModes(t);
  const init = isRecord(t['initial']) ? t['initial'] : {};
  const kept = Object.keys(t).filter((k) => !['id', 'name', 'bollardPull_t', 'attach', 'lineLength_m', 'modes', 'initial'].includes(k));
  const setModes = (mode: string, on: boolean) => {
    const next = TUG_MODES.filter((m) => (m === mode ? on : modes.includes(m)));
    h.setDraft(patchTug(h.draft, i, 'modes', next));
  };
  return html`<div class="row g4 tug" data-index=${i}>
    <label>id</label>
    <input class="tg-id" type="text" .value=${txt(t['id'])} @change=${(e: Event) => h.setDraft(patchTug(h.draft, i, 'id', textOf(e).trim()))} />
    <label>名稱</label>
    <input class="tg-name" type="text" .value=${txt(t['name'])} @change=${(e: Event) => h.setDraft(patchTug(h.draft, i, 'name', textOf(e)))} />
    <label>拖力 (t)</label>
    ${numInput('tg-bp', t['bollardPull_t'], (v) => h.setDraft(patchTug(h.draft, i, 'bollardPull_t', v)), '預設 40')}
    <label>連接點</label>
    <input class="tg-attach" type="text" list="body-points" placeholder="fwd | x,y" .value=${bodyPointText(t['attach'])} @change=${(e: Event) => guard(h, () => h.setDraft(patchTug(h.draft, i, 'attach', bodyPointFromText(textOf(e)))))} />
    <label>纜長 (m)</label>
    ${numInput('tg-line', t['lineLength_m'], (v) => h.setDraft(patchTug(h.draft, i, 'lineLength_m', v)), '預設 40')}
    <label>可用模式</label>
    <div class="pair">
      ${TUG_MODES.map((m) => html`<label class="pair"><input class="tg-${m}" type="checkbox" .checked=${modes.includes(m)} @change=${(e: Event) => setModes(m, checked(e))} /><span>${m}</span></label>`)}
    </div>
    <label>初始模式</label>
    <select class="tg-mode" data-value=${txt(init['mode'])} @change=${(e: Event) => h.setDraft(patchTug(h.draft, i, 'initial.mode', textOf(e)))}>
      <option value="">(預設 standby)</option>
      ${TUG_INITIAL_MODES.map((m) => html`<option value=${m}>${m}</option>`)}
    </select>
    <label>方向 (°) / 出力 (%)</label>
    <div class="pair">
      ${numInput('tg-dir', init['direction_deg'], (v) => h.setDraft(patchTug(h.draft, i, 'initial.direction_deg', v)), '90')}
      ${numInput('tg-power', init['power_pct'], (v) => h.setDraft(patchTug(h.draft, i, 'initial.power_pct', v)), '0')}
    </div>
    <div class="tail">
      <span class="hint">${kept.length > 0 ? `保留:${kept.join('、')}` : '連接點:fwd|aft|bow|stern|fwdPort|fwdStbd|midPort|midStbd|aftPort|aftStbd 或 x,y(m)'}</span>
      <button class="small danger tg-remove" @click=${() => h.setDraft(removeTug(h.draft, i))}>刪除</button>
    </div>
  </div>`;
}

function renderAnchors(h: EditorHost): TemplateResult {
  const a = h.draft.anchors;
  const side = (s: string) => (isRecord(a?.[s]) ? (a[s] as RawSection) : {});
  const kept = a ? Object.keys(a).filter((k) => !['port', 'starboard', 'holdingFactor', 'dragSpeed_mps'].includes(k)) : [];
  return html`<fieldset class="anchors">
    <legend>錨 anchors</legend>
    <label class="full pair"><input class="anchors-enabled" type="checkbox" .checked=${a !== null} @change=${(e: Event) => h.setDraft(checked(e) ? patchAnchors(h.draft, 'holdingFactor', 1) : { ...h.draft, anchors: null })} /><span>${a ? '有錨區段(未給的錨 = 收存)' : '無(兩錨收存、預設抓地力)'}</span></label>
    ${a
      ? html`
          ${ANCHOR_SIDES.map(
            (s) => html`<label>${s === 'port' ? '左錨' : '右錨'} 鏈長 m / 節</label>
              <div class="pair">
                ${numInput(`an-${s}-length_m`, side(s)['length_m'], (v) => h.setDraft(patchAnchors(h.draft, `${s}.length_m`, v)), 'm')}
                ${numInput(`an-${s}-length_shackles`, side(s)['length_shackles'], (v) => h.setDraft(patchAnchors(h.draft, `${s}.length_shackles`, v)), '節')}
              </div>
              <label>${s === 'port' ? '左錨' : '右錨'} 方位 (°)</label>
              ${numInput(`an-${s}-bearing_deg`, side(s)['bearing_deg'], (v) => h.setDraft(patchAnchors(h.draft, `${s}.bearing_deg`, v)), '預設初始航向')}`,
          )}
          <label>抓地力倍率</label>
          ${numInput('an-holdingFactor', a['holdingFactor'], (v) => h.setDraft(patchAnchors(h.draft, 'holdingFactor', v)), '預設 1')}
          <label>走錨速率 (m/s)</label>
          ${numInput('an-dragSpeed_mps', a['dragSpeed_mps'], (v) => h.setDraft(patchAnchors(h.draft, 'dragSpeed_mps', v)), '預設 0.5')}
          ${kept.length > 0 ? html`<div class="full hint">保留:${kept.join('、')}</div>` : nothing}
        `
      : nothing}
  </fieldset>`;
}

function renderBerth(h: EditorHost): TemplateResult {
  const b = h.draft.berth;
  const files = areaFileChoices('berths');
  const ids = areaIds('berths', txt(b?.['file']));
  const lines = berthLines(h.draft);
  const kept = b ? Object.keys(b).filter((k) => !['file', 'id', 'lines', 'departedDistance_m'].includes(k)) : [];
  return html`<fieldset class="berth">
    <legend>泊位 berth</legend>
    <datalist id="berth-files">${files.map((f) => html`<option value=${f}></option>`)}</datalist>
    <datalist id="berth-ids">${ids.map((i) => html`<option value=${i}></option>`)}</datalist>
    <label class="full pair"><input class="berth-enabled" type="checkbox" .checked=${b !== null} @change=${(e: Event) => h.setDraft(setBerthEnabled(h.draft, checked(e)))} /><span>${b ? `泊位區段(繫纜 ${lines.length})` : '無泊位'}</span></label>
    ${b
      ? html`
          <label>泊位檔</label>
          <input class="berth-file" type="text" list="berth-files" placeholder="data/areas/mailiao-berth.json" .value=${txt(b['file'])} @change=${(e: Event) => h.setDraft(patchBerth(h.draft, 'file', textOf(e).trim()))} />
          <label>泊位 id</label>
          <input class="berth-id" type="text" list="berth-ids" placeholder="(省略 = 第一個)" .value=${txt(b['id'])} @change=${(e: Event) => h.setDraft(patchBerth(h.draft, 'id', textOf(e).trim()))} />
          <label>已離泊距離 (m)</label>
          ${numInput('berth-departed', b['departedDistance_m'], (v) => h.setDraft(patchBerth(h.draft, 'departedDistance_m', v)), '預設 20')}
          ${kept.length > 0 ? html`<div class="full hint">保留:${kept.join('、')}${isRecord(b['definition']) ? `(definition ${txt(b['definition']['id'])} 內嵌定義)` : ''}</div>` : nothing}
          <div class="full">
            ${lines.map((l, i) => renderBerthLine(h, l, i))}
            <div class="btn-row"><button class="small add-line" @click=${() => h.setDraft(addBerthLine(h.draft))}>新增繫纜</button></div>
          </div>
        `
      : nothing}
  </fieldset>`;
}

function renderBerthLine(h: EditorHost, l: RawSection, i: number): TemplateResult {
  const kept = Object.keys(l).filter((k) => !['id', 'fairlead', 'bollard', 'mbl_t', 'length_m', 'initial'].includes(k));
  return html`<div class="row g4 line" data-index=${i}>
    <label>纜 id</label>
    <input class="ln-id" type="text" .value=${txt(l['id'])} @change=${(e: Event) => h.setDraft(patchBerthLine(h.draft, i, 'id', textOf(e).trim()))} />
    <label>導纜孔</label>
    <input class="ln-fairlead" type="text" list="body-points" .value=${bodyPointText(l['fairlead'])} @change=${(e: Event) => guard(h, () => h.setDraft(patchBerthLine(h.draft, i, 'fairlead', bodyPointFromText(textOf(e)))))} />
    <label>繫船柱</label>
    <input class="ln-bollard" type="text" .value=${txt(l['bollard'])} @change=${(e: Event) => h.setDraft(patchBerthLine(h.draft, i, 'bollard', textOf(e).trim()))} />
    <label>MBL (t) / 長 (m)</label>
    <div class="pair">
      ${numInput('ln-mbl', l['mbl_t'], (v) => h.setDraft(patchBerthLine(h.draft, i, 'mbl_t', v)), '50')}
      ${numInput('ln-length', l['length_m'], (v) => h.setDraft(patchBerthLine(h.draft, i, 'length_m', v)), '150')}
    </div>
    <label>初始</label>
    <select class="ln-initial" data-value=${txt(l['initial'])} @change=${(e: Event) => h.setDraft(patchBerthLine(h.draft, i, 'initial', textOf(e)))}>
      <option value="">(預設 stowed)</option>
      ${LINE_INITIAL_STATES.map((s) => html`<option value=${s}>${s}</option>`)}
    </select>
    <div class="tail">
      <span class="hint">${kept.length > 0 ? `保留:${kept.join('、')}` : ''}</span>
      <button class="small danger ln-remove" @click=${() => h.setDraft(removeBerthLine(h.draft, i))}>刪除</button>
    </div>
  </div>`;
}

function renderBanks(h: EditorHost): TemplateResult {
  const b = h.draft.banks;
  const files = areaFileChoices('banks');
  const ids = Array.isArray(b?.['ids']) ? b['ids'].map(txt) : [];
  const available = areaIds('banks', txt(b?.['file']));
  return html`<fieldset class="banks">
    <legend>岸壁 banks(岸壁效應)</legend>
    <datalist id="bank-files">${files.map((f) => html`<option value=${f}></option>`)}</datalist>
    <label class="full pair"><input class="banks-enabled" type="checkbox" .checked=${b !== null} @change=${(e: Event) => h.setDraft(setBanksEnabled(h.draft, checked(e)))} /><span>${b ? '計算岸壁效應' : '無(不計算、雜湊不變)'}</span></label>
    ${b
      ? html`
          <label>岸壁檔</label>
          <input class="banks-file" type="text" list="bank-files" placeholder="data/areas/mailiao-channel-banks.json" .value=${txt(b['file'])} @change=${(e: Event) => h.setDraft(patchBanks(h.draft, 'file', textOf(e).trim()))} />
          <label>岸壁 ids</label>
          <input class="banks-ids" type="text" placeholder=${available.length > 0 ? `省略 = 全部(${available.join('、')})` : '省略 = 全部'} .value=${ids.join(', ')} @change=${(e: Event) => h.setDraft(patchBanks(h.draft, 'ids', idListFromText(textOf(e))))} />
          <label>倍率</label>
          ${numInput('banks-scale', b['scale'], (v) => h.setDraft(patchBanks(h.draft, 'scale', v)), '預設 1;0 = 只顯示距離')}
          <div class="full hint">${bankLineCount(b) > 0 ? `情境內直接定義的岸壁 lines/definitions ${bankLineCount(b)} 段原樣保留(不在此編輯)` : '岸壁幾何取自岸壁檔;情境內 lines 需手編 YAML'}</div>
        `
      : nothing}
  </fieldset>`;
}
