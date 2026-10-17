// 行為標記錨定描述(規劃書第 8.2 節「評分錨定與評估者校準」第 1 點):五大類 × 三要素,每要素 1、3、5 分的可觀察行為描述,
// 對應的 STCW A-II/1 BRM 五項準則與 A-II/1、A-II/2 領導與團隊合作八項準則,以及 SIRE 2.0 缺失分類。
// 內容放在可版本化的 behavioural-anchors.json(草案,待訓練負責人核准);評估表記下所用的版本(anchorsVersion)。

import anchorsJson from './behavioural-anchors.json';

export type DeficiencyType = 'hardware' | 'procedure' | 'human';
export type HumanFactor = 'familiarity' | 'taskDesign' | 'equipment' | 'stressLanguage' | 'fatigue';

export interface StcwCriterion {
  id: string;
  group: 'brm' | 'leadership';
  text: string;
}

export interface AnchorElement {
  id: string;
  label: string;
  en: string;
  /** 對應的 STCW 準則 id(StcwCriterion.id) */
  stcw: string[];
  /** 1、3、5 分的行為錨定描述 */
  anchors: { '1': string; '3': string; '5': string };
}

export interface AnchorCategory {
  /** 與 brm-markers.ts 的 BrmCategory 相同,BRM 快速標記可直接對應 */
  id: string;
  label: string;
  en: string;
  elements: AnchorElement[];
}

export interface LabelDef<T extends string = string> {
  id: T;
  label: string;
  en: string;
}

export interface BehaviouralAnchors {
  format: 'simosa-brm-behavioural-anchors';
  version: string;
  status: 'draft' | 'approved';
  statusLabel: string;
  approvedBy: string | null;
  approvedDate: string | null;
  basis: string;
  scale: Array<{ score: number; label: string; en: string }>;
  /** 此分數(含)以下必須附時間戳與觀察描述 */
  lowScoreThreshold: number;
  /** 雙評一致率:±tolerance 分內的比例 ≥ target(第 8.2 節校準) */
  agreement: { tolerance: number; target: number };
  stcwCriteria: StcwCriterion[];
  criteriaGroups: Array<{ id: 'brm' | 'leadership'; label: string }>;
  deficiencyTypes: Array<LabelDef<DeficiencyType>>;
  humanFactors: Array<LabelDef<HumanFactor>>;
  categories: AnchorCategory[];
}

export const ANCHORS = anchorsJson as unknown as BehaviouralAnchors;

/** 依類別順序展開的十五項要素(含所屬類別)。 */
export function allElements(a: BehaviouralAnchors = ANCHORS): Array<AnchorElement & { categoryId: string; categoryLabel: string }> {
  return a.categories.flatMap((c) => c.elements.map((e) => ({ ...e, categoryId: c.id, categoryLabel: c.label })));
}

export function findElement(id: string, a: BehaviouralAnchors = ANCHORS): (AnchorElement & { categoryId: string; categoryLabel: string }) | undefined {
  return allElements(a).find((e) => e.id === id);
}

export function criterion(id: string, a: BehaviouralAnchors = ANCHORS): StcwCriterion | undefined {
  return a.stcwCriteria.find((c) => c.id === id);
}

export function scaleLabel(score: number, a: BehaviouralAnchors = ANCHORS): string {
  return a.scale.find((s) => s.score === score)?.label ?? '';
}

export function deficiencyLabel(type: DeficiencyType | null | undefined, factor?: HumanFactor | null, a: BehaviouralAnchors = ANCHORS): string {
  if (!type) return '';
  const t = a.deficiencyTypes.find((d) => d.id === type)?.label ?? type;
  if (type !== 'human' || !factor) return t;
  return `${t}/${a.humanFactors.find((h) => h.id === factor)?.label ?? factor}`;
}

export function isDeficiencyType(v: unknown): v is DeficiencyType {
  return v === 'hardware' || v === 'procedure' || v === 'human';
}

export function isHumanFactor(v: unknown): v is HumanFactor {
  return v === 'familiarity' || v === 'taskDesign' || v === 'equipment' || v === 'stressLanguage' || v === 'fatigue';
}

/** 錨定描述檔的結構檢查(測試與載入時用):回傳問題清單,空陣列表示通過。 */
export function validateAnchors(a: BehaviouralAnchors): string[] {
  const issues: string[] = [];
  if (a.format !== 'simosa-brm-behavioural-anchors') issues.push('format 不符');
  if (!a.version) issues.push('缺 version');
  const ids = new Set(a.stcwCriteria.map((c) => c.id));
  const seen = new Set<string>();
  for (const c of a.categories) {
    if (c.elements.length !== 3) issues.push(`${c.id} 應有 3 項要素`);
    for (const e of c.elements) {
      if (seen.has(e.id)) issues.push(`要素 id 重複:${e.id}`);
      seen.add(e.id);
      for (const k of ['1', '3', '5'] as const) if (!e.anchors?.[k]?.trim()) issues.push(`${e.id} 缺 ${k} 分錨定描述`);
      if (e.stcw.length === 0) issues.push(`${e.id} 未對應任何 STCW 準則`);
      for (const s of e.stcw) if (!ids.has(s)) issues.push(`${e.id} 引用未知準則 ${s}`);
    }
  }
  if (a.categories.length !== 5) issues.push('應有 5 大類');
  return issues;
}
