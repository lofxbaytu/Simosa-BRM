// 核心事件(SimEvent)的解析、文字化與保存(設計文件 docs/design/scenario-engine.md 第 3.2、5.2 節):
//  - WebSocket 推送框 {"type":"event","event":{…}} 與紀錄 event 行的 event 物件都用 parseSimEvent();
//  - 對未知欄位寬容、對缺欄位有預設(source 由 kind 推得、visible 預設 false、seq 預設 -1);
//  - eventLabel() 把事件轉成教官站事件流、重播時間軸與講評分段用的繁體中文一句話。

import type { SimEvent, SimEventKind, SimEventSource, VoiceScriptData } from '../types/event.js';
import { formatHeading } from './format.js';
import { alertEventLabel, alertEventSeverity } from './alerts.js';
import { ANCHOR_SIDE_LABEL, EXTERNAL_COMMAND_EVENT_KINDS, EXTERNAL_REJECT_REASON_LABEL, externalEventLabel, externalEventSeverity } from './external-forces.js';

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function finiteNum(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}

const TRAFFIC_KINDS: ReadonlySet<string> = new Set(['collision', 'cpaAlarm', 'cpaClear', 'targetActivated', 'waypointReached', 'scriptStep', 'colreg', 'soundSignal', 'targetControl']);
const TIMELINE_KINDS: ReadonlySet<string> = new Set([
  'timelineTriggered',
  'timelineArmed',
  'timelineDisarmed',
  'faultInjected',
  'faultCleared',
  'environmentChange',
  'environmentRampDone',
  'voiceScript',
  'message',
  'phaseChanged',
  'frozen',
  'resumed',
  'autopilotOverride',
  'ownShipOverride',
  'timeScaleChanged',
  'exerciseEnded',
]);
const ASSESSMENT_KINDS: ReadonlySet<string> = new Set([
  'routeLegChanged',
  'routeCompleted',
  'checkpointPassed',
  'reportPointPassed',
  'xtdExceeded',
  'xtdRestored',
  'zoneEntered',
  'zoneExited',
  'ukcAlarm',
  'ukcClear',
  'speedLimitExceeded',
  'speedLimitRestored',
  'encounterStarted',
  'encounterFirstAction',
  'encounterClosed',
  'requiredEventMet',
  'requiredEventMissed',
  'hardFail',
  'bandWarn',
  'bandFail',
]);
const INSTRUCTOR_KINDS: ReadonlySet<string> = new Set(['acknowledge', 'timelineManual', 'steeringModeChanged', 'commandRejected', 'scenarioLoadRejected']);

/** 由 kind 推得預設來源(舊 TrafficEvent 紀錄沒有 source 欄位)。 */
export function defaultSourceFor(kind: string): SimEventSource {
  if (TRAFFIC_KINDS.has(kind)) return 'traffic';
  if (TIMELINE_KINDS.has(kind)) return 'timeline';
  if (ASSESSMENT_KINDS.has(kind)) return 'assessment';
  if (INSTRUCTOR_KINDS.has(kind)) return 'instructor';
  // tugCommand / anchorLetGo / anchorCommand / mooringCommand 來源依指令來源(缺 source 的舊紀錄視為教官);其他外力事件為 engine
  if (EXTERNAL_COMMAND_EVENT_KINDS.has(kind)) return 'instructor';
  return 'engine';
}

const SOURCES: readonly SimEventSource[] = ['traffic', 'timeline', 'assessment', 'instructor', 'engine'];

/** 物件是否至少具備 SimEvent 的必要欄位(kind 字串、tick 或 t 數字)。 */
export function isSimEvent(v: unknown): v is SimEvent {
  if (!isRecord(v)) return false;
  if (typeof v['kind'] !== 'string' || !v['kind']) return false;
  return finiteNum(v['tick']) !== undefined || finiteNum(v['t']) !== undefined;
}

/**
 * 解析事件物件(紀錄 event 行的 event、推送框的 event);缺 t 時以 tick × dt 推得,缺 tick 時以 t / dt 推得。
 * 無法解析回傳 null。
 */
export function parseSimEvent(raw: unknown, dt = 0.02): SimEvent | null {
  if (!isSimEvent(raw)) return null;
  const r = raw as unknown as Record<string, unknown>;
  const kind = r['kind'] as string;
  let tick = finiteNum(r['tick']);
  let t = finiteNum(r['t']);
  if (tick === undefined && t !== undefined) tick = Math.round(t / dt);
  if (t === undefined && tick !== undefined) t = tick * dt;
  const source = typeof r['source'] === 'string' && (SOURCES as readonly string[]).includes(r['source']) ? (r['source'] as SimEventSource) : defaultSourceFor(kind);
  const ev: SimEvent = { kind, tick: tick ?? 0, t: t ?? 0, source, visible: r['visible'] === true, seq: finiteNum(r['seq']) ?? -1 };
  if (typeof r['itemId'] === 'string') ev.itemId = r['itemId'];
  if (typeof r['targetId'] === 'string') ev.targetId = r['targetId'];
  if (typeof r['message'] === 'string') ev.message = r['message'];
  if (typeof r['phase'] === 'string') ev.phase = r['phase'];
  const range = finiteNum(r['range_nm']);
  if (range !== undefined) ev.range_nm = range;
  const cpa = finiteNum(r['cpa_nm']);
  if (cpa !== undefined) ev.cpa_nm = cpa;
  const tcpa = finiteNum(r['tcpa_min']);
  if (tcpa !== undefined) ev.tcpa_min = tcpa;
  if (isRecord(r['data'])) ev.data = r['data'];
  return ev;
}

/** WebSocket 推送框 {"type":"event","event":{…}} → SimEvent;非事件框回傳 null。 */
export function eventFromFrame(frame: Record<string, unknown>, dt = 0.02): SimEvent | null {
  if (frame['type'] !== 'event') return null;
  return parseSimEvent(frame['event'], dt);
}

/** 事件嚴重度(事件流著色、重播時間軸標記;hardFail/collision/grounding 為紅)。 */
export type EventSeverity = 'info' | 'warning' | 'alarm';

export function eventSeverity(e: SimEvent): EventSeverity {
  const alertSev = alertEventSeverity(e);
  if (alertSev) return alertSev;
  const extSev = externalEventSeverity(e);
  if (extSev) return extSev;
  switch (e.kind) {
    case 'collision':
    case 'grounding':
    case 'hardFail':
    case 'bandFail':
    case 'requiredEventMissed':
      return 'alarm';
    case 'cpaAlarm':
    case 'faultInjected':
    case 'xtdExceeded':
    case 'zoneEntered':
    case 'ukcAlarm':
    case 'speedLimitExceeded':
    case 'bandWarn':
    case 'frozen':
      return 'warning';
    case 'message': {
      const sev = e.data?.['severity'];
      return sev === 'alarm' ? 'alarm' : sev === 'warning' ? 'warning' : 'info';
    }
    default:
      return 'info';
  }
}

export const SOURCE_LABEL: Readonly<Record<SimEventSource, string>> = {
  traffic: '目標船',
  timeline: '時間表',
  assessment: '評估',
  instructor: '教官',
  engine: '引擎',
};

export const ACK_KIND_LABEL: Readonly<Record<string, string>> = {
  verbal: '口頭辨識',
  notifyEcr: '通知機控室',
  callMaster: '呼叫船長',
  report: '報告(VTS/港口)',
  checklist: '檢查表',
  steeringMode: '切換操舵模式',
  anchorReady: '備錨',
  gyroCheck: '羅經比對異常',
  prediction: '記下預測',
  driftCalculation: '漂流計算',
  anchorDecision: '下錨決策',
  notifyCompany: '通報公司',
  notifySar: '通報搜救',
  lookoutReport: '瞭望報告',
  other: '其他',
};

/** commandRejected 的 reason 顯示文字(D35、sensors-alerts.md 第 2.3、3.3 節;拖船/錨/繫纜 19 種見 physics-tugs-anchors-mooring.md 第 6.2 節)。 */
export const REJECT_REASON_LABEL: Readonly<Record<string, string>> = {
  emergencySteering: '應急操舵中不可自動舵',
  autopilotUnavailable: '自動舵不可用(電羅經或自動舵失效)',
  unknownAlert: '未知的警報 id',
  alertStationRequired: '此警報只能在負責站確認(Category A 或已轉移責任)',
  unknownStation: '未知的工作站',
  alertNotTransferable: '此警報不可轉移到該站',
  ...EXTERNAL_REJECT_REASON_LABEL,
};

/** scenarioLoadRejected 事件的問題項(C# ScenarioIssue:path、message、severity error|warning)。 */
export interface ScenarioLoadIssue {
  path: string;
  message: string;
  severity: 'error' | 'warning';
}

/** 情境載入被拒的可讀內容(教官站情境面板/靠離泊預演面板顯示;由 scenarioLoadRejected 事件或 Python ack ok=false 得到)。 */
export interface ScenarioLoadRejection {
  /** 情境 id(檔名、內嵌物件 id 或 args.id;核心辨認不出時 '') */
  scenarioId: string;
  /** file | inline | command(指令本身不完整)| ack(Python 參考伺服器的 ack 回覆) */
  origin: string;
  /** 核心給的摘要訊息 */
  message: string;
  issues: ScenarioLoadIssue[];
  /** 模擬時間(s) */
  t: number;
}

/** 由 scenarioLoadRejected 事件取出拒絕內容(其他事件回 null;data.issues 缺或格式不對時為空清單,仍保留 message)。 */
export function scenarioLoadRejection(e: SimEvent): ScenarioLoadRejection | null {
  if (e.kind !== 'scenarioLoadRejected') return null;
  const id = ds(e, 'scenarioId') || e.itemId || '';
  const raw = d(e, 'issues');
  const issues: ScenarioLoadIssue[] = [];
  if (Array.isArray(raw)) {
    for (const it of raw) {
      if (!isRecord(it)) continue;
      const message = typeof it['message'] === 'string' ? it['message'] : '';
      if (!message) continue;
      issues.push({ path: typeof it['path'] === 'string' ? it['path'] : '', message, severity: it['severity'] === 'warning' ? 'warning' : 'error' });
    }
  }
  return { scenarioId: id, origin: ds(e, 'origin') || 'command', message: e.message ?? `情境 ${id || '(未知)'} 載入被拒`, issues, t: e.t };
}

/** commandRejected 的指令名稱顯示文字(外力指令中文化;其他保留原字串)。 */
export const COMMAND_NAME_LABEL: Readonly<Record<string, string>> = {
  tugControl: '拖船令',
  anchor: '錨令',
  mooring: '繫纜令',
};

/** 操舵模式顯示名稱(state.steering.mode / steeringMode 指令 value)。 */
export const STEERING_MODE_LABEL: Readonly<Record<string, string>> = {
  fu: 'FU 隨動',
  nfu: 'NFU 非隨動',
  autopilot: '自動舵',
  emergency: '應急操舵',
};

export const ROLE_LABEL: Readonly<Record<string, string>> = {
  vts: 'VTS',
  pilot: '引水人',
  ecr: '機控室',
  tug: '拖船',
  otherShip: '他船',
  master: '船長',
  agent: '代理',
  helmsman: '舵工',
  lookout: '瞭望',
  narrator: '旁白',
  officer: '駕駛員',
  leader: '帶領者',
};

const CHANNEL_LABEL: Readonly<Record<string, string>> = {
  vhf16: 'VHF 16',
  vhf13: 'VHF 13',
  vhfPort: 'VHF 港口頻道',
  intercom: '對講',
  phone: '電話',
  pa: '廣播',
  bridge: '駕駛台',
};

function d(e: SimEvent, key: string): unknown {
  return e.data?.[key];
}

function dn(e: SimEvent, key: string, digits = 1): string {
  const v = d(e, key);
  return typeof v === 'number' && Number.isFinite(v) ? v.toFixed(digits) : '?';
}

function ds(e: SimEvent, key: string): string {
  const v = d(e, key);
  return typeof v === 'string' ? v : v === undefined || v === null ? '' : String(v);
}

/** voiceScript 事件的 data 轉成型別(缺欄位補預設)。 */
export function voiceScriptData(e: SimEvent): VoiceScriptData | null {
  if (e.kind !== 'voiceScript') return null;
  const out: VoiceScriptData = { role: ds(e, 'role') || 'narrator', channel: ds(e, 'channel') || 'bridge' };
  const text = ds(e, 'text');
  if (text) out.text = text;
  const file = ds(e, 'file');
  if (file) out.file = file;
  const lang = ds(e, 'lang');
  if (lang) out.lang = lang;
  const dur = d(e, 'durationEstimate_s');
  if (typeof dur === 'number' && Number.isFinite(dur)) out.durationEstimate_s = dur;
  return out;
}

/** 事件的一句話(繁體中文;既有交通事件也涵蓋)。 */
export function eventLabel(e: SimEvent): string {
  const alertText = alertEventLabel(e);
  if (alertText !== null) return alertText;
  const extText = externalEventLabel(e);
  if (extText !== null) return extText;
  const target = e.targetId ? ` ${e.targetId}` : '';
  const item = e.itemId ? ` ${e.itemId}` : '';
  switch (e.kind as SimEventKind) {
    // ---- 交通
    case 'collision':
      return `碰撞${target}`;
    case 'cpaAlarm':
      return `CPA 警報${target}${e.cpa_nm !== undefined ? ` CPA ${e.cpa_nm.toFixed(2)} nm` : ''}${e.tcpa_min !== undefined ? ` TCPA ${e.tcpa_min.toFixed(1)} min` : ''}`;
    case 'cpaClear':
      return `CPA 警報解除${target}`;
    case 'targetActivated':
      return `目標船啟動${target}`;
    case 'waypointReached':
      return `目標船到達航點${target}`;
    case 'scriptStep':
      return `目標船腳本步驟${target}${e.message ? ` ${e.message}` : ''}`;
    case 'colreg':
      return `COLREG 行動${target}${e.message ? ` ${e.message}` : ''}`;
    case 'soundSignal':
      return `聲號${target}${e.message ? ` ${e.message}` : ''}`;
    case 'targetControl':
      return `目標船控制${target}${e.message ? ` ${e.message}` : ''}`;
    // ---- 時間表
    case 'timelineTriggered': {
      const manual = d(e, 'manual') === true ? '(手動)' : '';
      const forced = d(e, 'forced') === true ? '(強制)' : '';
      return `時間表觸發${item}${manual}${forced}`;
    }
    case 'timelineArmed':
      return `時間表項目啟用${item}`;
    case 'timelineDisarmed':
      return `時間表項目停用${item}`;
    case 'faultInjected':
      return `故障注入 ${ds(e, 'fault') || e.message || ''}${d(e, 'alreadyActive') === true ? '(已存在)' : ''}`.trimEnd();
    case 'faultCleared':
      return `故障清除 ${ds(e, 'fault') || e.message || ''}`.trimEnd();
    case 'environmentChange': {
      const parts: string[] = [];
      const wind = d(e, 'wind');
      if (isRecord(wind)) parts.push(`風 ${typeof wind['trueSpeed'] === 'number' ? wind['trueSpeed'] : '?'} kn/${typeof wind['trueDir'] === 'number' ? formatHeading(wind['trueDir']) : '?'}`);
      const cur = d(e, 'current');
      if (isRecord(cur)) parts.push(`流 ${typeof cur['set'] === 'number' ? formatHeading(cur['set']) : '?'}/${typeof cur['drift'] === 'number' ? cur['drift'] : '?'} kn`);
      if (typeof d(e, 'waterDepth') === 'number') parts.push(`水深 ${dn(e, 'waterDepth')} m`);
      if (typeof d(e, 'visibility_nm') === 'number') parts.push(`能見度 ${dn(e, 'visibility_nm')} nm`);
      // 浪(setEnvironment waves;physics-waves.md「練習中變更」):物件為設定/變更(只列有給的欄位)、null 為關閉
      const waves = d(e, 'waves');
      if (waves === null) parts.push('浪關閉');
      else if (isRecord(waves)) {
        const hs = waves['significantHeight_m'];
        if (typeof hs === 'number' && hs === 0) parts.push('浪關閉');
        else {
          const items: string[] = [];
          if (typeof hs === 'number') items.push(`Hs ${hs} m`);
          if (typeof waves['peakPeriod_s'] === 'number') items.push(`Tp ${waves['peakPeriod_s']} s`);
          if (typeof waves['direction_deg'] === 'number') items.push(`${formatHeading(waves['direction_deg'])}`);
          if (typeof waves['spectrum'] === 'string') items.push(String(waves['spectrum']).toUpperCase());
          parts.push(`浪 ${items.join('/') || '不變'}`);
        }
      }
      const ramp = d(e, 'ramp_s');
      const rampText = typeof ramp === 'number' && ramp > 0 ? `(${ramp} s 漸變)` : '';
      return `環境改變 ${parts.join(' · ') || e.message || ''}${rampText}`.trimEnd();
    }
    case 'environmentRampDone':
      return `環境漸變完成${ds(e, 'channel') ? ` ${ds(e, 'channel')}` : ''}`;
    case 'voiceScript': {
      const v = voiceScriptData(e)!;
      return `語音 ${ROLE_LABEL[v.role] ?? v.role}(${CHANNEL_LABEL[v.channel] ?? v.channel}):${v.text ?? v.file ?? ''}`;
    }
    case 'message':
      return `${ds(e, 'to') === 'all' ? '訊息(全體)' : '訊息'}:${ds(e, 'text') || e.message || ''}`;
    case 'phaseChanged':
      return `階段 ${ds(e, 'from') || '?'} → ${ds(e, 'to') || e.phase || '?'}`;
    case 'frozen':
      return `凍結${ds(e, 'reason') ? `:${ds(e, 'reason')}` : ''}`;
    case 'resumed':
      return '恢復';
    case 'autopilotOverride':
      return `自動舵覆寫${d(e, 'enabled') === false ? '(關閉)' : typeof d(e, 'heading') === 'number' ? ` 航向 ${formatHeading(d(e, 'heading') as number)}` : ''}`;
    case 'ownShipOverride':
      return `自船覆寫${e.message ? ` ${e.message}` : ''}`;
    case 'timeScaleChanged':
      return `時間倍率 ×${ds(e, 'timeScale') || ds(e, 'to') || ds(e, 'value') || e.message || '?'}${d(e, 'frozen') === true ? '(凍結中)' : ''}`;
    case 'exerciseEnded':
      return `練習結束${ds(e, 'reason') ? `:${ds(e, 'reason')}` : ''}`;
    case 'grounding':
      return '擱淺';
    // ---- 評估
    case 'routeLegChanged':
      return `進入航段 ${ds(e, 'legIndex') || ds(e, 'to') || '?'}${ds(e, 'waypoint') ? `(通過 ${ds(e, 'waypoint')})` : ''}`;
    case 'routeCompleted':
      return '航線完成';
    case 'checkpointPassed':
      return `通過檢查點 ${ds(e, 'waypoint')}`;
    case 'reportPointPassed':
      return `通過報告點 ${ds(e, 'waypoint')}${e.message ? ` — ${e.message}` : ''}`;
    case 'xtdExceeded':
      return `XTD 超限 ${dn(e, 'xtd_m', 0)} m(限 ${dn(e, 'limit_m', 0)} m)`;
    case 'xtdRestored':
      return `XTD 回到走廊內 ${dn(e, 'xtd_m', 0)} m`;
    case 'zoneEntered':
      return `進入區域 ${ds(e, 'zoneId')}${ds(e, 'type') ? `(${zoneTypeLabel(ds(e, 'type'))})` : ''}`;
    case 'zoneExited':
      return `離開區域 ${ds(e, 'zoneId')}`;
    case 'ukcAlarm':
      return `UKC 警報 ${dn(e, 'ukc_m', 2)} m(限 ${dn(e, 'limit_m', 2)} m${ds(e, 'zoneId') ? `,區域 ${ds(e, 'zoneId')}` : ''})`;
    case 'ukcClear':
      return `UKC 警報解除 ${dn(e, 'ukc_m', 2)} m`;
    case 'speedLimitExceeded':
      return `超過速限 ${dn(e, 'sog_kn')} kn(限 ${dn(e, 'limit_kn')} kn${ds(e, 'id') ? `,${ds(e, 'id')}` : ''})`;
    case 'speedLimitRestored':
      return `回到速限內 ${dn(e, 'sog_kn')} kn`;
    case 'encounterStarted':
      return `會遇開始${target} 距離 ${dn(e, 'range_nm', 1)} nm CPA ${dn(e, 'cpa_nm', 2)} nm TCPA ${dn(e, 'tcpa_min', 1)} min`;
    case 'encounterFirstAction':
      return `會遇首次行動${target} ${ds(e, 'action')}${typeof d(e, 'headingChange_deg') === 'number' ? ` 改向 ${dn(e, 'headingChange_deg', 0)}°` : ''}`;
    case 'encounterClosed':
      return `會遇結束${target}${ds(e, 'reason') ? `(${ds(e, 'reason')})` : ''}`;
    case 'requiredEventMet':
      return `必要事件達成${item} ${dn(e, 'dt_s', 0)} s(限 ${dn(e, 'within_s', 0)} s${ds(e, 'by') ? `,${ds(e, 'by')}` : ''})`;
    case 'requiredEventMissed':
      return `必要事件逾時${item}(限 ${dn(e, 'within_s', 0)} s)`;
    case 'hardFail':
      return `硬性失敗${item} ${ds(e, 'rule')}${typeof d(e, 'value') === 'number' ? ` ${dn(e, 'value', 2)} / ${dn(e, 'limit', 2)}` : ''}`;
    case 'bandWarn':
      return `指標進入警告帶${item} ${ds(e, 'metric')} ${dn(e, 'value', 1)}`;
    case 'bandFail':
      return `指標進入失敗帶${item} ${ds(e, 'metric')} ${dn(e, 'value', 1)}`;
    // ---- 教官
    case 'acknowledge':
      return `確認:${ACK_KIND_LABEL[ds(e, 'kind')] ?? ds(e, 'kind')}${ds(e, 'role') ? `(${ROLE_LABEL[ds(e, 'role')] ?? ds(e, 'role')})` : ''}${ds(e, 'note') ? ` ${ds(e, 'note')}` : ''}`;
    case 'timelineManual':
      return `手動觸發${item}${d(e, 'ok') === false ? ' — 核心拒絕' : ''}${e.message ? ` ${e.message}` : ''}`;
    case 'steeringModeChanged':
      return `操舵模式 ${STEERING_MODE_LABEL[ds(e, 'from')] ?? ds(e, 'from') ?? '?'} → ${STEERING_MODE_LABEL[ds(e, 'to')] ?? ds(e, 'to') ?? '?'}${ds(e, 'pump') ? `(泵 ${ds(e, 'pump')})` : ''}`;
    case 'scenarioLoadRejected': {
      const n = d(e, 'issueCount');
      return e.message ?? `情境 ${ds(e, 'scenarioId') || e.itemId || '(未知)'} 載入被拒${typeof n === 'number' ? `(${n} 項問題)` : ''}`;
    }
    case 'commandRejected': {
      const cmd = ds(e, 'command');
      const args = d(e, 'args');
      // 外力指令附上對象(拖船 id / 錨側 / 纜 id),教官一眼看出哪一項被拒
      const who = COMMAND_NAME_LABEL[cmd] && isRecord(args) ? (typeof args['id'] === 'string' ? args['id'] : typeof args['side'] === 'string' ? (ANCHOR_SIDE_LABEL[args['side'] as 'port' | 'starboard'] ?? args['side']) : typeof args['lineId'] === 'string' ? args['lineId'] : '') : '';
      const name = COMMAND_NAME_LABEL[cmd] ? `${COMMAND_NAME_LABEL[cmd]}(${cmd})` : cmd;
      return `指令被拒 ${name}${who ? ` ${who}` : ''}${REJECT_REASON_LABEL[ds(e, 'reason')] ? `(${REJECT_REASON_LABEL[ds(e, 'reason')]})` : ds(e, 'reason') ? `(${ds(e, 'reason')})` : ''}`;
    }
    default:
      return `${e.kind}${item}${target}${e.message ? ` ${e.message}` : ''}`;
  }
}

export function zoneTypeLabel(type: string): string {
  switch (type) {
    case 'noGo':
      return '禁區';
    case 'speedLimit':
      return '速限區';
    case 'ukcPolicy':
      return 'UKC 政策區';
    case 'reporting':
      return '報告區';
    default:
      return type;
  }
}

/** 事件保存(教官站事件流只保留最近 N 筆,設計文件第 9 節「大量事件」)。 */
export class EventStore {
  private readonly events: SimEvent[] = [];

  constructor(readonly max = 2000) {}

  push(e: SimEvent): void {
    this.events.push(e);
    if (this.events.length > this.max) this.events.splice(0, this.events.length - this.max);
  }

  all(): readonly SimEvent[] {
    return this.events;
  }

  get length(): number {
    return this.events.length;
  }

  clear(): void {
    this.events.length = 0;
  }

  /** 倒帶(restore):丟棄 t > tMax 的舊分支事件(與 record-replay.pushState 的規則相同,D31);回傳丟棄筆數。 */
  dropAfter(tMax: number): number {
    const before = this.events.length;
    let w = 0;
    for (const e of this.events) if (e.t <= tMax) this.events[w++] = e;
    this.events.length = w;
    return before - w;
  }

  /** 該 kind(可再依 itemId/targetId)最後一次發生的事件。 */
  last(kind: string, filter: { itemId?: string; targetId?: string } = {}): SimEvent | undefined {
    for (let i = this.events.length - 1; i >= 0; i--) {
      const e = this.events[i]!;
      if (e.kind !== kind) continue;
      if (filter.itemId !== undefined && e.itemId !== filter.itemId) continue;
      if (filter.targetId !== undefined && e.targetId !== filter.targetId) continue;
      return e;
    }
    return undefined;
  }

  count(kind: string, filter: { itemId?: string; targetId?: string } = {}): number {
    let n = 0;
    for (const e of this.events) {
      if (e.kind !== kind) continue;
      if (filter.itemId !== undefined && e.itemId !== filter.itemId) continue;
      if (filter.targetId !== undefined && e.targetId !== filter.targetId) continue;
      n++;
    }
    return n;
  }
}
