// BRM 快速標記(規劃書第 9.1 節「時間戳筆記/書籤與 BRM 快速標記鈕」、第 8.2 節五大類):
// 教官或帶領者按鈕記錄時間戳、類別與一句備註,供講評;不經核心,匯出為 JSON/CSV。

export type BrmCategory = 'leadership' | 'situationalAwareness' | 'communication' | 'teamwork' | 'decisionMaking';

export interface BrmCategoryDef {
  id: BrmCategory;
  label: string;
  en: string;
  /** 第 8.2 節三要素(提示用) */
  elements: string;
  /** 鍵盤快速鍵(數字鍵 1–5) */
  key: string;
}

export const BRM_CATEGORIES: readonly BrmCategoryDef[] = [
  { id: 'leadership', label: '領導', en: 'Leadership', elements: '設定標準與優先順序;果斷與權威;工作負荷管理與授權', key: '1' },
  { id: 'situationalAwareness', label: '狀況覺知', en: 'Situational awareness', elements: '監控船舶狀態與位置;監控環境與交通;預判與前瞻', key: '2' },
  { id: 'communication', label: '溝通', en: 'Communication', elements: '閉環溝通/複誦;清楚的簡報;分享意圖與計畫', key: '3' },
  { id: 'teamwork', label: '團隊合作', en: 'Teamwork', elements: '交叉核對與質疑;支援他人;衝突解決', key: '4' },
  { id: 'decisionMaking', label: '決策', en: 'Decision making', elements: '定義問題;產生與比較選項及風險;檢討結果', key: '5' },
];

export function brmCategory(id: BrmCategory): BrmCategoryDef {
  const def = BRM_CATEGORIES.find((c) => c.id === id);
  if (!def) throw new RangeError(`未知的 BRM 類別:${id}`);
  return def;
}

export function isBrmCategory(v: unknown): v is BrmCategory {
  return typeof v === 'string' && BRM_CATEGORIES.some((c) => c.id === v);
}

export interface BrmMarker {
  id: number;
  category: BrmCategory;
  /** 模擬時間(秒) */
  t: number;
  /** 核心步數(若已知) */
  tick?: number;
  /** 本地時鐘 ISO */
  wall: string;
  note: string;
  /** 正向(做得好)或待改進;未指定為觀察 */
  polarity?: 'plus' | 'delta';
  scenarioId?: string;
}

export interface BrmMarkerInput {
  category: BrmCategory;
  t: number;
  tick?: number;
  note?: string;
  polarity?: 'plus' | 'delta';
  scenarioId?: string;
  wall?: Date;
}

export class BrmMarkerStore {
  private readonly markers: BrmMarker[] = [];
  private nextId = 1;

  all(): readonly BrmMarker[] {
    return this.markers;
  }

  get length(): number {
    return this.markers.length;
  }

  add(input: BrmMarkerInput): BrmMarker {
    if (!isBrmCategory(input.category)) throw new RangeError(`未知的 BRM 類別:${String(input.category)}`);
    const m: BrmMarker = {
      id: this.nextId++,
      category: input.category,
      t: input.t,
      wall: (input.wall ?? new Date()).toISOString(),
      note: (input.note ?? '').trim(),
    };
    if (input.tick !== undefined) m.tick = input.tick;
    if (input.polarity) m.polarity = input.polarity;
    if (input.scenarioId) m.scenarioId = input.scenarioId;
    this.markers.push(m);
    return m;
  }

  updateNote(id: number, note: string): boolean {
    const m = this.markers.find((x) => x.id === id);
    if (!m) return false;
    m.note = note.trim();
    return true;
  }

  remove(id: number): boolean {
    const i = this.markers.findIndex((x) => x.id === id);
    if (i < 0) return false;
    this.markers.splice(i, 1);
    return true;
  }

  clear(): void {
    this.markers.length = 0;
  }

  /** 從 JSON(toJson 的輸出或其 markers 陣列)載回;無效項目略過,回傳載入數。 */
  load(data: unknown): number {
    const arr = Array.isArray(data) ? data : typeof data === 'object' && data !== null && Array.isArray((data as { markers?: unknown }).markers) ? (data as { markers: unknown[] }).markers : [];
    let n = 0;
    for (const raw of arr) {
      if (typeof raw !== 'object' || raw === null) continue;
      const r = raw as Record<string, unknown>;
      if (!isBrmCategory(r['category']) || typeof r['t'] !== 'number') continue;
      const input: BrmMarkerInput = { category: r['category'], t: r['t'] };
      if (typeof r['note'] === 'string') input.note = r['note'];
      if (typeof r['tick'] === 'number') input.tick = r['tick'];
      if (r['polarity'] === 'plus' || r['polarity'] === 'delta') input.polarity = r['polarity'];
      if (typeof r['scenarioId'] === 'string') input.scenarioId = r['scenarioId'];
      if (typeof r['wall'] === 'string' && !Number.isNaN(Date.parse(r['wall']))) input.wall = new Date(r['wall']);
      this.add(input);
      n++;
    }
    return n;
  }

  toJson(meta: Record<string, unknown> = {}): string {
    return JSON.stringify(
      {
        format: 'simosa-brm-markers',
        version: 1,
        exportedUtc: new Date().toISOString(),
        categories: BRM_CATEGORIES.map((c) => ({ id: c.id, label: c.label, en: c.en })),
        ...meta,
        markers: this.markers,
      },
      null,
      2,
    );
  }

  /** CSV(UTF-8 BOM 讓 Excel 直接開;逗號、引號、換行依 RFC 4180 跳脫)。 */
  toCsv(): string {
    const header = ['id', 'simTime_s', 'simTime', 'tick', 'wallClock', 'category', 'categoryLabel', 'polarity', 'scenarioId', 'note'];
    const rows = this.markers.map((m) => [
      String(m.id),
      m.t.toFixed(2),
      formatSimTimeShort(m.t),
      m.tick === undefined ? '' : String(m.tick),
      m.wall,
      m.category,
      brmCategory(m.category).label,
      m.polarity ?? '',
      m.scenarioId ?? '',
      m.note,
    ]);
    return '﻿' + [header, ...rows].map((r) => r.map(csvEscape).join(',')).join('\r\n') + '\r\n';
  }
}

export function csvEscape(v: string): string {
  return /[",\r\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v;
}

/** 秒 → "HH:MM:SS"(與 format.formatSimTime 相同,但避免循環相依於此另寫)。 */
export function formatSimTimeShort(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return [h, m, s].map((n) => n.toString().padStart(2, '0')).join(':');
}
