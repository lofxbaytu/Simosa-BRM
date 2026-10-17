// 行為標記評估表(規劃書第 8.2 節):錨定描述檔結構、表單驗證(2 分以下必須附時間戳與觀察描述、SIRE 2.0 缺失分類)、
// 時間輸入、BRM 快速標記帶入、雙評一致率(±1 分內比例,目標 ≥ 80%)、JSON 匯出/匯入。
import { describe, expect, it } from 'vitest';
import { ANCHORS, allElements, validateAnchors } from '../src/report/anchors.js';
import { BRM_CATEGORIES, type BrmMarker } from '../src/lib/brm-markers.js';
import {
  attachMarker,
  computeAgreement,
  createMarkerForm,
  elementsForMarker,
  exportForms,
  importForms,
  isFormComplete,
  parseSimTimeInput,
  ratingOf,
  summarizeForm,
  validateMarkerForm,
  type MarkerForm,
} from '../src/report/marker-form.js';

const NOW = new Date('2026-10-17T08:00:00Z');

function filled(scores: number[], opts: { assessor?: string; role?: 'primary' | 'second'; key?: string } = {}): MarkerForm {
  const f = createMarkerForm({ record: { key: opts.key ?? 'sha256:ABC' }, trainee: { id: 'T001', name: '王大明' }, assessor: { name: opts.assessor ?? '陳船長', role: opts.role ?? 'primary' }, now: NOW });
  f.ratings.forEach((r, i) => {
    r.score = scores[i] ?? null;
    if (r.score !== null && r.score <= 2) r.observations.push({ t_s: 100 + i, text: '舵令未複誦' });
  });
  return f;
}

describe('錨定描述檔(behavioural-anchors.json)', () => {
  it('五大類 × 三要素、1/3/5 分錨定、STCW 對應皆存在,標註草案待核准', () => {
    expect(validateAnchors(ANCHORS)).toEqual([]);
    expect(allElements()).toHaveLength(15);
    expect(ANCHORS.status).toBe('draft');
    expect(ANCHORS.statusLabel).toBe('草案,待訓練負責人核准');
    expect(ANCHORS.version).toMatch(/draft/);
    // 類別 id 與 BRM 快速標記相同,快速標記可直接對應
    expect(ANCHORS.categories.map((c) => c.id)).toEqual(BRM_CATEGORIES.map((c) => c.id));
    // 第 8.2 節範例:閉環溝通 1/3/5 分
    const cl = allElements().find((e) => e.id === 'communication.closedLoop')!;
    expect(cl.anchors['1']).toContain('未複誦');
    expect(cl.anchors['5']).toContain('全程複誦');
    // 五項 BRM 準則皆至少被一項要素引用
    for (const c of ANCHORS.stcwCriteria.filter((x) => x.group === 'brm')) expect(allElements().some((e) => e.stcw.includes(c.id))).toBe(true);
    expect(ANCHORS.deficiencyTypes.map((d) => d.label)).toEqual(['硬體', '程序', '人員']);
    expect(ANCHORS.humanFactors.map((d) => d.label)).toEqual(['熟悉度', '任務設計', '設備', '緊張/語言', '疲勞']);
  });

  it('結構錯誤會被檢出', () => {
    const bad = structuredClone(ANCHORS);
    bad.categories[0]!.elements[0]!.anchors['3'] = '';
    bad.categories[1]!.elements[0]!.stcw = ['XX9'];
    const issues = validateAnchors(bad);
    expect(issues.some((i) => i.includes('缺 3 分'))).toBe(true);
    expect(issues.some((i) => i.includes('未知準則 XX9'))).toBe(true);
  });
});

describe('評估表驗證', () => {
  it('空白表:十五項未評分為提醒,評估員已填無錯誤;未完成', () => {
    const f = createMarkerForm({ record: { key: 'k' }, assessor: { name: '陳船長' }, now: NOW });
    expect(f.ratings).toHaveLength(15);
    expect(f.anchorsVersion).toBe(ANCHORS.version);
    const issues = validateMarkerForm(f);
    expect(issues.filter((i) => i.level === 'error')).toEqual([]);
    expect(issues.filter((i) => i.message === '未評分')).toHaveLength(15);
    expect(isFormComplete(f, issues)).toBe(false);
  });

  it('2 分以下必須附時間戳與觀察描述', () => {
    const f = createMarkerForm({ record: { key: 'k' }, assessor: { name: '陳船長' }, now: NOW });
    const r = ratingOf(f, 'communication.closedLoop')!;
    r.score = 2;
    let errs = validateMarkerForm(f).filter((i) => i.level === 'error' && i.elementId === r.elementId);
    expect(errs[0]?.message).toContain('時間戳與觀察描述');
    r.observations.push({ t_s: null, text: '舵令未複誦' });
    errs = validateMarkerForm(f).filter((i) => i.level === 'error' && i.elementId === r.elementId);
    expect(errs[0]?.message).toContain('時間戳');
    r.observations[0] = { t_s: 754, text: '  ' };
    errs = validateMarkerForm(f).filter((i) => i.level === 'error' && i.elementId === r.elementId);
    expect(errs[0]?.message).toContain('觀察描述');
    r.observations[0] = { t_s: 754, text: '舵令「右舵 10」未複誦,無人糾正' };
    const issues = validateMarkerForm(f).filter((i) => i.elementId === r.elementId);
    expect(issues.filter((i) => i.level === 'error')).toEqual([]);
    // 建議標記缺失類型(提醒,不阻擋)
    expect(issues.some((i) => i.level === 'warning' && i.message.includes('SIRE 2.0'))).toBe(true);
    // 3 分不需觀察
    r.observations = [];
    r.score = 3;
    expect(validateMarkerForm(f).filter((i) => i.level === 'error')).toEqual([]);
    // 1 分同樣適用
    r.score = 1;
    expect(validateMarkerForm(f).some((i) => i.level === 'error' && i.elementId === r.elementId)).toBe(true);
  });

  it('分數範圍、時間戳、人員缺失子類、評估員', () => {
    const f = createMarkerForm({ record: { key: 'k' }, now: NOW });
    const r = ratingOf(f, 'teamwork.crossCheck')!;
    r.score = 6;
    r.observations.push({ t_s: -5, text: 'x' });
    r.deficiency = { type: 'human', humanFactor: null };
    const r2 = ratingOf(f, 'decisionMaking.review')!;
    r2.score = 4;
    r2.deficiency = { type: 'procedure', humanFactor: 'fatigue' };
    r2.observations.push({ t_s: 900, text: '超出時長' });
    const issues = validateMarkerForm(f, { duration_s: 300 });
    const msgs = issues.map((i) => `${i.level}:${i.elementId ?? ''}:${i.message}`);
    expect(msgs).toContain('error::請填評估員姓名');
    expect(msgs).toContain('error:teamwork.crossCheck:分數須為 1 至 5 的整數');
    expect(msgs).toContain('error:teamwork.crossCheck:觀察的時間戳無效');
    expect(msgs.some((m) => m.startsWith('error:teamwork.crossCheck:人員缺失須再分'))).toBe(true);
    expect(msgs).toContain('error:decisionMaking.review:人員子類只適用於「人員」缺失');
    expect(msgs).toContain('warning:decisionMaking.review:觀察時間超出練習時長');
    r.score = 5;
    r.observations = [];
    r.deficiency = { type: 'human', humanFactor: 'stressLanguage' };
    r2.deficiency = { type: 'hardware', humanFactor: null };
    r2.observations = [];
    f.assessor.name = '陳船長';
    expect(validateMarkerForm(f).filter((i) => i.level === 'error')).toEqual([]);
  });

  it('全部評分且無錯誤即完成;統計平均、最低、缺失分類', () => {
    const f = filled([4, 4, 3, 5, 4, 3, 2, 4, 4, 3, 4, 4, 5, 3, 1]);
    ratingOf(f, 'communication.closedLoop')!.deficiency = { type: 'human', humanFactor: 'stressLanguage' };
    ratingOf(f, 'decisionMaking.review')!.deficiency = { type: 'procedure', humanFactor: null };
    expect(isFormComplete(f)).toBe(true);
    const s = summarizeForm(f);
    expect(s.rated).toBe(15);
    expect(s.min).toBe(1);
    expect(s.lowCount).toBe(2);
    expect(s.mean).toBeCloseTo(53 / 15, 6);
    expect(s.categories.find((c) => c.categoryId === 'leadership')!.mean).toBeCloseTo(11 / 3, 6);
    expect(s.deficiencies).toEqual({ hardware: 0, procedure: 1, human: 1 });
    expect(s.humanFactors.stressLanguage).toBe(1);
  });
});

describe('時間輸入', () => {
  it('HH:MM:SS、MM:SS、秒數', () => {
    expect(parseSimTimeInput('00:12:34')).toBe(754);
    expect(parseSimTimeInput('12:34')).toBe(754);
    expect(parseSimTimeInput('754')).toBe(754);
    expect(parseSimTimeInput('1:02:03.5')).toBe(3723.5);
    expect(parseSimTimeInput('  ')).toBeNull();
    expect(parseSimTimeInput('12h')).toBeNaN();
  });
});

describe('BRM 快速標記帶入', () => {
  const marker: BrmMarker = { id: 7, category: 'communication', t: 181.4, wall: '2026-10-17T08:03:01Z', note: '舵工未複誦', polarity: 'delta' };

  it('只能帶入同類別的三項要素;不重複帶入;依時間排序', () => {
    expect(elementsForMarker(marker)).toEqual(['communication.closedLoop', 'communication.briefing', 'communication.intentions']);
    const f = createMarkerForm({ record: { key: 'k' }, assessor: { name: 'A' }, now: NOW });
    const r = ratingOf(f, 'communication.closedLoop')!;
    r.observations.push({ t_s: 300, text: '後來補正' });
    expect(attachMarker(f, 'communication.closedLoop', marker)).toBe(true);
    expect(attachMarker(f, 'communication.closedLoop', marker)).toBe(false);
    expect(r.observations[0]).toEqual({ t_s: 181.4, text: '(Δ)舵工未複誦', markerId: 7, polarity: 'delta' });
    // 帶入的標記即可滿足 2 分以下的證據要求
    r.score = 2;
    expect(validateMarkerForm(f).some((i) => i.level === 'error' && i.elementId === r.elementId)).toBe(false);
    expect(attachMarker(f, 'nope', marker)).toBe(false);
    // 無備註的標記以類別名稱描述
    expect(attachMarker(f, 'communication.briefing', { ...marker, id: 8, note: '' })).toBe(true);
    expect(ratingOf(f, 'communication.briefing')!.observations[0]!.text).toBe('(Δ)溝通快速標記');
  });
});

describe('雙評一致率(第 8.2 節校準)', () => {
  it('±1 分內比例;12/15 = 80% 達成目標;列出需協調的差異', () => {
    const a = filled([4, 4, 3, 5, 4, 3, 2, 4, 4, 3, 4, 4, 5, 3, 1]);
    const b = filled([4, 3, 3, 3, 4, 3, 4, 5, 4, 3, 4, 4, 5, 3, 3], { assessor: '林船長', role: 'second' });
    const res = computeAgreement(a, b);
    expect(res.compared).toBe(15);
    expect(res.withinTolerance).toBe(12);
    expect(res.rate).toBeCloseTo(0.8, 9);
    expect(res.meetsTarget).toBe(true);
    expect(res.exact).toBe(10);
    expect(res.differences.map((d) => d.elementId)).toEqual(['situationalAwareness.ownship', 'communication.closedLoop', 'decisionMaking.review']);
    expect(res.differences[0]).toMatchObject({ a: 5, b: 3, diff: 2 });
    expect(res.warnings).toEqual([]);
  });

  it('低於目標、未評分要素不計入、不同紀錄/同一評估員提醒', () => {
    const a = filled([1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
    const b = filled([5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 1, 1], { key: 'sha256:OTHER' });
    const res = computeAgreement(a, b);
    expect(res.compared).toBe(14);
    expect(res.missing).toEqual(['decisionMaking.review']);
    expect(res.rate).toBeCloseTo(2 / 14, 9);
    expect(res.meetsTarget).toBe(false);
    expect(res.warnings).toContain('兩份評估表不是同一份練習紀錄');
    expect(res.warnings).toContain('兩份評估表的評估員相同');
    const empty = computeAgreement(createMarkerForm({ record: { key: '' }, now: NOW }), createMarkerForm({ record: { key: '' }, now: NOW }));
    expect(empty.rate).toBeNull();
    expect(empty.meetsTarget).toBe(false);
  });
});

describe('評估表 JSON 匯出/匯入', () => {
  it('整包往返保持內容;單張也可匯入;非評估表報錯', () => {
    const a = filled([4, 4, 3, 5, 4, 3, 2, 4, 4, 3, 4, 4, 5, 3, 1]);
    ratingOf(a, 'communication.closedLoop')!.deficiency = { type: 'human', humanFactor: 'fatigue' };
    const b = filled([4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4], { assessor: '林船長', role: 'second' });
    const text = exportForms([a, b], NOW);
    const obj = JSON.parse(text) as { format: string; anchorsVersion: string };
    expect(obj.format).toBe('simosa-brm-marker-forms');
    expect(obj.anchorsVersion).toBe(ANCHORS.version);
    const res = importForms(text);
    expect(res.error).toBeUndefined();
    expect(res.forms).toEqual([a, b]);
    expect(importForms(JSON.stringify(a)).forms).toEqual([a]);
    expect(importForms('{"format":"x"}').error).toContain('不是行為標記評估表');
    expect(importForms('not json').error).toBe('JSON 解析失敗');
    // 缺欄位補預設、未知要素保留以便驗證提示
    const partial = importForms(JSON.stringify({ format: 'simosa-brm-marker-form', ratings: [{ elementId: 'leadership.standards', score: 3 }, { elementId: 'old.element', score: 2 }] })).forms[0]!;
    expect(partial.ratings).toHaveLength(16);
    expect(ratingOf(partial, 'leadership.standards')!.score).toBe(3);
    expect(validateMarkerForm(partial).some((i) => i.elementId === 'old.element' && i.level === 'error')).toBe(true);
  });
});
