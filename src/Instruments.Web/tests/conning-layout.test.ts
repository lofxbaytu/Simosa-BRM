// conning 條單列(視窗寬於 1900 px)與兩列(1900 px 以下)的排法(第三十一輪;docs/design/web-layout.md 第 2.5 節)。
// happy-dom 不排版,這裡只釘住「樣式與樣板的結構」:版面本身(各方塊不被水平或垂直裁切、餘裕)由端對端測試以真實 Chromium 量測
// (tests/e2e/specs/01-bridge.spec.ts「操船台 1920×1080(conning 條單列)…」「操船台 1366×768、1536×864 與 1093×614(conning 條兩列)…」、
// 02-instructor.spec.ts 的重播檢視與完整教官站案例)。
//  - brm-conning-bar:單列是 flex,13 個方塊各有「基本寬度 + 比例」,基本寬度的總和 = 13 格在 1901 px 視窗的可用寬度(1817 px);
//    單列設 --brm-conning-single: 1,兩列(≤ 1900 px)改回格線並設 0;八種格數組合的每一格都有寬度規則;
//  - 13 個方塊元件都帶 conningTileStyles(單列專用的規則以 @container style(--brm-conning-single: 1) 包住);
//  - 比平常多一位數的數值加 long 類別(ROT ≥ 100 °/min、UKC ≥ 100 m);
//  - 可以在內部換行的數值裡不拆開的一段以 .nb 包住(模擬時間、航線的距離 / 方位 / 速限 / 能見度、交互作用的 Y / N、橫搖週期 / 角速度),
//    文字內容與原本相同;航線偏移的位置圓點由樣板給 --pos、樣式夾在條內(單列與兩列都是)。
// 審查後補:
//  - 修正不是把內容藏起來:13 個方塊的樣式裡會讓內容看不到的宣告(display: none、visibility: hidden、overflow: hidden、text-overflow)
//    只有第三十一輪之前就有的三種,而且都不在單列的樣式查詢裡;浪向玫瑰在單列一律顯示;
//  - 多兩位數的值:UKC 1000 m 以上(六個字元以上)加 xlong 並給 --ukc-chars,航線偏移六位數以上加 long;
//  - 沒有空白的長字串可以換行:標題的中文部分 overflow-wrap: anywhere,交互作用列的標籤三段各在 .nb 裡;
//  - 航線方塊「下一航點」一列在 .wp 裡(平常 display: contents,單列時跨兩欄);主機的轉速條在單列可以縮短;
//  - 傾斜儀的警報等級是警報色的字、沒有徽章的底色。
// 兩列(檔尾「兩列的排法」):
//  - brm-conning-bar:兩列是同一個 flex 容器折成兩行(order 1 的方塊在第一列,::before 的整列項目換行,order 3 的在第二列),設
//    --brm-conning-single: 0;方塊是 size 容器、高度是條高的一半;第一列六格的比例與原本的格線相同,13 格時 UKC 排在第一列;
//  - 13 個方塊都帶兩列的樣式查詢(@container style(--brm-conning-single: 0));兩列的規則沒有新增會讓內容看不到的宣告;
//  - 各方塊兩列專用的規則(行高固定、大字的字級以方塊高度設上限、圖形可以縮短、排法只看方塊寬度)都包在兩列的樣式查詢裡;
//  - 樣板的文字內容不變(航線方塊的標籤與數值多了 k-brg / k-lim / k-vis 類別,文字相同)。
// @vitest-environment happy-dom
import { beforeAll, describe, expect, it } from 'vitest';
import type { CSSResult, CSSResultGroup } from 'lit';
import { defaultState } from '../src/lib/state-parser.js';
import { getShipConfig } from '../src/lib/ship-config.js';
import type { OwnShipState } from '../src/types/state.js';

type El = HTMLElement & { updateComplete: Promise<unknown> };

async function mount(tag: string, props: Record<string, unknown> = {}): Promise<El> {
  const el = document.createElement(tag) as El;
  Object.assign(el, props);
  document.body.appendChild(el);
  await el.updateComplete;
  return el;
}

/** 元件類別的樣式文字(static styles 攤平) */
function cssOf(tag: string): string {
  const ctor = customElements.get(tag) as unknown as { styles?: CSSResultGroup } | undefined;
  expect(ctor, `${tag} 已註冊`).toBeDefined();
  const flat = (g: CSSResultGroup | undefined): string => (g === undefined ? '' : Array.isArray(g) ? g.map((x) => flat(x as CSSResultGroup)).join('\n') : (g as CSSResult).cssText);
  // 去掉註解(註解裡會提到屬性名稱)
  return flat(ctor!.styles).replace(/\/\*[\s\S]*?\*\//g, '');
}

const text = (el: Element | null): string => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

/** 樣式文字裡的每一條規則:at 是包住它的 at 規則(由外到內;@container、@media),selector 是選擇器,body 是宣告 */
interface CssRule {
  at: string[];
  selector: string;
  body: string;
}
function rulesOf(css: string): CssRule[] {
  const out: CssRule[] = [];
  const at: string[] = [];
  let head = '';
  for (let i = 0; i < css.length; i++) {
    const ch = css[i]!;
    if (ch === '{') {
      const h = head.replace(/\s+/g, ' ').trim();
      head = '';
      if (h.startsWith('@')) {
        at.push(h);
      } else {
        const end = css.indexOf('}', i);
        out.push({ at: [...at], selector: h, body: css.slice(i + 1, end).replace(/\s+/g, ' ').trim() });
        i = end;
      }
    } else if (ch === '}') {
      at.pop();
      head = '';
    } else {
      head += ch;
    }
  }
  return out;
}
const SINGLE = '--brm-conning-single: 1';
const TWO = '--brm-conning-single: 0';
const inSingleRow = (r: CssRule): boolean => r.at.some((a) => a.includes(SINGLE));
const inTwoRows = (r: CssRule): boolean => r.at.some((a) => a.includes(TWO));
/** 某個方塊在兩列樣式查詢裡、選擇器相符的規則(多條時依原始碼順序);cond 有給時樣式查詢還要帶這個尺寸條件(例如 'max-width: 239px') */
const twoRowRules = (tag: string, selector: string, cond?: string): CssRule[] =>
  rulesOf(cssOf(tag)).filter((r) => inTwoRows(r) && r.selector.split(',').map((x) => x.trim()).includes(selector) && (cond === undefined ? r.at.every((a) => !a.includes(' and ')) : r.at.some((a) => a.includes(`(${cond})`))));
const twoRowBody = (tag: string, selector: string, cond?: string): string => twoRowRules(tag, selector, cond).map((r) => r.body).join(' ');
/** 某個方塊在單列樣式查詢裡、選擇器相符的規則的宣告(多條時接在一起) */
const singleRowBody = (tag: string, selector: string): string =>
  rulesOf(cssOf(tag))
    .filter((r) => inSingleRow(r) && r.selector.split(',').map((x) => x.trim()).includes(selector))
    .map((r) => r.body)
    .join(' ');

const TILES = ['brm-heading', 'brm-rot', 'brm-speed', 'brm-rudder', 'brm-engine', 'brm-thruster', 'brm-depth', 'brm-wind', 'brm-current', 'brm-sea', 'brm-inclinometer', 'brm-clock', 'brm-route'];

beforeAll(async () => {
  await import('../src/components/conning-bar.js');
});

describe('brm-conning-bar 單列的格寬', () => {
  /** 樣式裡各方塊的 flex 規則:tag → { grow, basis } */
  function flexRules(): Map<string, { grow: number; basis: number }> {
    // 只看兩列的媒體查詢之前(單列)的部分;兩列另有一組比例(檔尾「兩列的排法」)
    const all = cssOf('brm-conning-bar');
    const css = all.slice(0, all.indexOf('@media (max-width: 1900px)'));
    const out = new Map<string, { grow: number; basis: number }>();
    for (const m of css.matchAll(/(brm-[a-z-]+)\s*\{\s*flex:\s*([\d.]+)\s+1\s+(\d+)px;\s*\}/g)) out.set(m[1]!, { grow: Number(m[2]), basis: Number(m[3]) });
    return out;
  }

  it('13 個方塊各有「基本寬度 + 比例」;基本寬度的總和 = 13 格在 1901 px 視窗的可用寬度', () => {
    const rules = flexRules();
    expect([...rules.keys()].sort()).toEqual([...TILES].sort());
    // 視窗 1901 px:頁面左右內距各 6 px → 列寬 1889 px;13 格有 12 個 6 px 的間距
    const available = 1901 - 12 - 12 * 6;
    expect(available).toBe(1817);
    expect([...rules.values()].reduce((a, r) => a + r.basis, 0)).toBe(available);
    for (const [tag, r] of rules) {
      expect(r.basis, `${tag} 的基本寬度`).toBeGreaterThanOrEqual(110);
      expect(r.grow, `${tag} 的比例`).toBeGreaterThan(0);
    }
  });

  it('單列設 --brm-conning-single: 1(flex);兩列(≤ 1900 px)設 0(同一個 flex 容器折成兩行)', () => {
    const css = cssOf('brm-conning-bar');
    const media = css.indexOf('@media (max-width: 1900px)');
    expect(media).toBeGreaterThan(0);
    const single = css.slice(0, media);
    const twoRows = css.slice(media);
    expect(single).toMatch(/\.bar\s*\{[^}]*display:\s*flex;[^}]*--brm-conning-single:\s*1;/);
    expect(single).not.toContain('--brm-conning-single: 0');
    // 兩列:第三十一輪之前是 6 欄 / 7 欄 × 2 列的格線(兩列共用欄寬);現在兩列各自分配寬度(細節在檔尾「兩列的排法」)
    expect(twoRows).toMatch(/\.bar\s*\{[^}]*flex-wrap:\s*wrap;[^}]*--brm-conning-single:\s*0;/);
    expect(twoRows).not.toContain('--brm-conning-single: 1');
    // 單列不再有逐組合的欄寬樣板(原本八種組合各寫一份,「有浪、有橫搖、沒有航線」那一種對錯了格);兩列也沒有
    expect(css).not.toContain('grid-template-columns');
  });

  it('八種格數組合:每一格都有寬度規則,格序固定(浪、傾斜儀在流之後,時間在其後,航線最後)', async () => {
    const rules = flexRules();
    const cfg = getShipConfig('FSB1');
    const base = defaultState('FSB1');
    const extras = {
      route: { route: { nextWaypoint: 'FW1', xtd_m: 12, xtdLimit_m: 150 } },
      sea: { waves: { significantHeight_m: 2, peakPeriod_s: 7, direction_deg: 90, wavelength_m: 76 } },
      roll: { roll: { angle_deg: 1, rate_degps: 0, naturalPeriod_s: 9.8, staticHeel_deg: 0 } },
    };
    for (const route of [false, true]) {
      for (const sea of [false, true]) {
        for (const roll of [false, true]) {
          const state = { ...base, ...(route ? extras.route : {}), ...(sea ? extras.sea : {}), ...(roll ? extras.roll : {}) } as OwnShipState;
          const el = await mount('brm-conning-bar', { state, config: cfg, issues: [] });
          const tags = [...el.shadowRoot!.querySelectorAll('.bar > *')].map((t) => t.tagName.toLowerCase());
          const expected = ['brm-heading', 'brm-rot', 'brm-speed', 'brm-rudder', 'brm-engine', 'brm-thruster', 'brm-depth', 'brm-wind', 'brm-current', ...(sea ? ['brm-sea'] : []), ...(roll ? ['brm-inclinometer'] : []), 'brm-clock', ...(route ? ['brm-route'] : [])];
          expect(tags, `route=${route} sea=${sea} roll=${roll}`).toEqual(expected);
          for (const tag of tags) expect(rules.has(tag), `${tag} 有寬度規則`).toBe(true);
          el.remove();
        }
      }
    }
  });
});

describe('各方塊單列專用的樣式', () => {
  it('13 個方塊都帶 conningTileStyles(標題與「標籤 + 數值」列放不下時換行),而且只在單列作用', () => {
    for (const tag of TILES) {
      const css = cssOf(tag);
      expect(css, `${tag} 的樣式含單列的樣式查詢`).toContain('@container style(--brm-conning-single: 1)');
      // 共用的片段:標題列與 .row 的換行寫在樣式查詢裡(兩列時不套用,排法與原本相同)
      const block = css.slice(css.indexOf('@container style(--brm-conning-single: 1)'));
      expect(block).toMatch(/\.tile-title\s*\{\s*flex-wrap:\s*wrap;/);
      expect(block).toMatch(/\.row\s*\{\s*flex-wrap:\s*wrap;/);
      // 修正不是把內容藏起來:新增的規則沒有 overflow: hidden / text-overflow
      expect(block).not.toMatch(/overflow:\s*hidden|text-overflow/);
    }
  });

  it('修正不是把內容藏起來:會讓內容看不到的宣告只有原本就有的三種,都不在單列的樣式查詢裡', () => {
    const hiding = /display:\s*none|visibility:\s*hidden|overflow(-x|-y)?:\s*(hidden|clip)|text-overflow|clip-path|opacity:\s*0(\.0+)?\s*(;|$)|font-size:\s*0/;
    const found: string[] = [];
    for (const tag of TILES) {
      for (const r of rulesOf(cssOf(tag))) {
        if (!hiding.test(r.body)) continue;
        expect(inSingleRow(r), `${tag} ${r.at.join(' ')} ${r.selector} { ${r.body} } 不在單列的樣式查詢裡`).toBe(false);
        expect(inTwoRows(r), `${tag} ${r.at.join(' ')} ${r.selector} { ${r.body} } 不在兩列的樣式查詢裡`).toBe(false);
        found.push(`${tag} | ${r.at.join(' ')} | ${r.selector}`);
      }
    }
    const expected = [
      // 方塊的邊界(tileStyles;內容超出方塊時被它裁切 —— 端對端的判定 4 與「內容不比方塊高」就是在防這個)
      ...TILES.map((tag) => `${tag} |  | .tile`),
      // 船速方塊 125 px 以下標籤只留英文縮寫(單列只有 13 格、視窗約 1980 px 以下;兩列的 7 欄)。第三十一輪的第一版另有一條
      // 「單列 150 px 以下」的,11 至 12 格因此少了「對水 / 對地 / 航跡向」,審查後拿掉
      'brm-speed | @container (max-width: 125px) | .label .zh',
      // 浪方塊 165 px 以下不顯示浪向玫瑰:只在兩列生效,單列由下面的規則蓋過
      'brm-sea | @container (max-width: 165px) | .rose',
    ];
    expect(found.sort()).toEqual(expected.sort());
  });

  it('浪向玫瑰在單列一律顯示:單列的樣式查詢(沒有寬度條件)把 .rose 設回 display: block,而且排在 165 px 的規則之後', () => {
    const rules = rulesOf(cssOf('brm-sea'));
    const hide = rules.findIndex((r) => r.selector === '.rose' && /display:\s*none/.test(r.body));
    const show = rules.findIndex((r) => r.selector === '.rose' && /display:\s*block/.test(r.body));
    expect(hide).toBeGreaterThanOrEqual(0);
    expect(show).toBeGreaterThan(hide);
    expect(rules[show]!.at).toEqual([`@container style(${SINGLE})`]);
  });

  it('船速方塊:單列沒有另外省略標籤中文部分的規則(門檻與兩列相同,125 px)', () => {
    const zh = rulesOf(cssOf('brm-speed')).filter((r) => r.selector === '.label .zh');
    expect(zh.map((r) => r.at.join(' '))).toEqual(['@container (max-width: 125px)']);
  });

  it('沒有空白的長字串可以換行:標題的中文部分、交互作用列的標籤、航線各列的數值', () => {
    for (const tag of TILES) {
      const title = singleRowBody(tag, '.tile-title .zh');
      expect(title, `${tag} 的標題`).toMatch(/white-space:\s*normal/);
      expect(title, `${tag} 的標題`).toMatch(/overflow-wrap:\s*anywhere/);
    }
    expect(singleRowBody('brm-sea', '.interaction .label')).toMatch(/white-space:\s*normal/);
    expect(singleRowBody('brm-sea', '.interaction .label .nb')).toMatch(/overflow-wrap:\s*anywhere/);
    expect(singleRowBody('brm-route', '.rows .v')).toMatch(/overflow-wrap:\s*anywhere/);
  });

  it('主機的轉速條在單列可以縮短(方塊不夠高時把高度讓給文字),下限 10 px', () => {
    const bar = singleRowBody('brm-engine', '.bar');
    expect(bar).toMatch(/flex:\s*0 1 26px/);
    expect(bar).toMatch(/min-height:\s*10px/);
    // 兩列不變:原本的規則還在(固定至少 26 px)
    const base = rulesOf(cssOf('brm-engine')).find((r) => r.selector === '.bar' && r.at.length === 0)!;
    expect(base.body).toMatch(/min-height:\s*26px/);
  });

  it('傾斜儀的警報等級:警報色的字,取消共用徽章樣式的底色、內距與圓角(原本紅底紅字,看不到)', async () => {
    const all = rulesOf(cssOf('brm-inclinometer'));
    const alarm = all.filter((r) => r.selector === '.value.alarm' && r.at.length === 0);
    expect(alarm).toHaveLength(1);
    expect(alarm[0]!.body).toMatch(/color:\s*var\(--ob-alarm\)/);
    expect(alarm[0]!.body).toMatch(/background:\s*none/);
    expect(alarm[0]!.body).toMatch(/padding:\s*0/);
    expect(alarm[0]!.body).toMatch(/border-radius:\s*0/);
    // 這條規則排在共用的 .alarm(徽章)之後,同為一個類別以上的選擇器,後者勝出
    expect(all.findIndex((r) => r.selector === '.alarm')).toBeGreaterThanOrEqual(0);
    expect(all.indexOf(alarm[0]!)).toBeGreaterThan(all.findIndex((r) => r.selector === '.alarm'));
    const cases: Array<[number, string]> = [
      [-3.5, ''],
      [-15, 'warn'],
      [24.9, 'warn'],
      [25, 'alarm'],
      [-35.5, 'alarm'],
    ];
    for (const [angle, level] of cases) {
      const el = await mount('brm-inclinometer', { roll: { angle_deg: angle, rate_degps: 0, naturalPeriod_s: 9.6, staticHeel_deg: 0 } });
      const value = el.shadowRoot!.querySelector('.roll-angle')!;
      expect(value.classList.contains('alarm'), `橫搖 ${angle}° 的警報等級`).toBe(level === 'alarm');
      expect(value.classList.contains('warn'), `橫搖 ${angle}° 的警示等級`).toBe(level === 'warn');
      el.remove();
    }
  });
});

describe('比平常多一位數的數值(long)', () => {
  it('ROT:|ROT| ≥ 100 °/min 才加 long', async () => {
    const cases: Array<[number, string, boolean]> = [
      [0, '0.0', false],
      [99.9, '→ 99.9', false],
      [-99.9, '← 99.9', false],
      [100, '→ 100.0', true],
      [-135.5, '← 135.5', true],
    ];
    for (const [rot, shown, long] of cases) {
      const el = await mount('brm-rot', { rot, range: 30 });
      const readout = el.shadowRoot!.querySelector('.readout')!;
      expect(text(readout)).toBe(shown);
      expect(readout.classList.contains('long'), `ROT ${rot}`).toBe(long);
      el.remove();
    }
  });

  it('UKC:100 m 以上(五個字元以上)才加 long;尚未收到資料的「--.-」不加;1000 m 以上(六個字元以上)再加 xlong 並給字元數', async () => {
    // [屬性, 顯示的文字, long, xlong 時的字元數(0 = 沒有 xlong)]
    const cases: Array<[Record<string, unknown>, string, boolean, number]> = [
      [{ ukc: 0.6, waterDepth: 7 }, '0.6', false, 0],
      [{ ukc: 19.24, waterDepth: 25 }, '19.2', false, 0],
      [{ ukc: 99.9, waterDepth: 105 }, '99.9', false, 0],
      [{ ukc: 100.3, waterDepth: 106 }, '100.3', true, 0],
      [{ ukc: 994.3, waterDepth: 1000 }, '994.3', true, 0],
      [{ ukc: 999.94, waterDepth: 1006 }, '999.9', true, 0],
      [{ ukc: 999.96, waterDepth: 1006 }, '1000.0', true, 6],
      [{ ukc: 1234.5, waterDepth: 1240.6 }, '1234.5', true, 6],
      [{ ukc: 10994.3, waterDepth: 11000 }, '10994.3', true, 7],
      [{ ukc: 0, waterDepth: 0, valid: false }, '--.-', false, 0],
    ];
    for (const [props, shown, long, chars] of cases) {
      const el = await mount('brm-depth', props);
      const value = el.shadowRoot!.querySelector('.main .value.big') as HTMLElement;
      expect(text(value)).toBe(shown);
      expect(value.classList.contains('long'), `UKC ${shown}`).toBe(long);
      expect(value.classList.contains('xlong'), `UKC ${shown}`).toBe(chars > 0);
      expect(value.getAttribute('style') ?? '', `UKC ${shown} 的行內樣式`).toBe(chars > 0 ? `--ukc-chars:${chars};` : '');
      el.remove();
    }
    // 測深儀無效時的淡化與刪除線仍在,與字元數並存
    const invalid = await mount('brm-depth', { ukc: 1234.5, waterDepth: 1240.6, sensorInvalid: true });
    expect(invalid.shadowRoot!.querySelector('.main .value.big')!.getAttribute('style')).toBe('--ukc-chars:6;opacity:0.45;text-decoration:line-through');
    const invalidShort = await mount('brm-depth', { ukc: 19.2, waterDepth: 25, sensorInvalid: true });
    expect(invalidShort.shadowRoot!.querySelector('.main .value.big')!.getAttribute('style')).toBe('opacity:0.45;text-decoration:line-through');
    // 樣式:xlong 的字級是「方塊寬度的 130 % ÷ 字元數」(六個字元 21.7 %),排在 long(26.5 %)之後
    const rules = rulesOf(cssOf('brm-depth'));
    const longRule = rules.findIndex((r) => r.selector === '.main .value.big.long');
    const xlongRule = rules.findIndex((r) => r.selector === '.main .value.big.xlong');
    expect(longRule).toBeGreaterThanOrEqual(0);
    expect(xlongRule).toBeGreaterThan(longRule);
    expect(rules[xlongRule]!.body).toMatch(/font-size:\s*clamp\(28px, min\(3\.2vw, calc\(130cqi \/ var\(--ukc-chars, 6\)\)\), 56px\)/);
    expect(inSingleRow(rules[xlongRule]!)).toBe(true);
  });

  it('航線偏移:六位數以上才加 long(字級 17.5 %)', async () => {
    const cases: Array<[number, string, boolean]> = [
      [0.2, '0m', false],
      [-386.4, '386m', false],
      [12345, '12345m', false],
      [99999.4, '99999m', false],
      [99999.6, '100000m', true],
      [-123456, '123456m', true],
    ];
    for (const [xtd, shown, long] of cases) {
      const el = await mount('brm-route', { route: { nextWaypoint: 'W2', xtd_m: xtd, xtdLimit_m: 18520 } });
      const value = el.shadowRoot!.querySelector('.xtd .value')!;
      expect(text(value)).toBe(shown);
      expect(value.classList.contains('long'), `偏移 ${xtd}`).toBe(long);
      el.remove();
    }
    expect(singleRowBody('brm-route', '.xtd .value.mid.long')).toMatch(/17\.5cqi/);
  });
});

describe('不拆開的一段(.nb)與文字內容', () => {
  it('時間:模擬時間列是 .row.sim,時間在 .nb 裡;凍結時「凍結」仍在數值內、文字不變', async () => {
    const frozen = await mount('brm-clock', { simTime: 1235, tick: 61750, lat: 22.5, lon: 120.2, frozen: true });
    const sim = frozen.shadowRoot!.querySelector('.row.sim')!;
    expect(text(sim.querySelector('.label'))).toBe('模擬');
    expect(text(sim.querySelector('.value .nb'))).toBe('00:20:35');
    expect(text(sim.querySelector('.value'))).toBe('00:20:35 凍結');
    expect(sim.querySelector('.value')!.classList.contains('caution')).toBe(true);
    const running = await mount('brm-clock', { simTime: 359999, tick: 1, lat: 22.5, lon: 120.2, frozen: false });
    expect(text(running.shadowRoot!.querySelector('.row.sim .value'))).toBe('99:59:59');
    expect(running.shadowRoot!.querySelector('.row.sim .value .label')).toBeNull();
  });

  it('航線:距離、方位 / 航段、速限、能見度各在 .nb 裡,整列文字不變;「下一航點」一列在 .wp 裡;位置圓點由樣板給 --pos、樣式夾在條內', async () => {
    const el = await mount('brm-route', {
      route: { nextWaypoint: 'ENTRANCE', xtd_m: 1568, xtdLimit_m: 1500, distanceToWaypoint_nm: 123.456, bearingToWaypoint_deg: 15, legCourse_deg: 22, speedLimit_kn: 12.5, complete: false },
      visibility: 0.2,
    });
    const root = el.shadowRoot!;
    expect(text(root.querySelector('.xtd'))).toBe('1568m R 偏右 限 1500 m');
    const values = [...root.querySelectorAll('.rows .v')];
    expect(values.map((v) => text(v))).toEqual(['ENTRANCE 123.46 nm', '015 / 022', '12.5 kn', '0.2 nm']);
    expect(values.map((v) => text(v.querySelector('.nb')))).toEqual(['123.46 nm', '015 / 022', '12.5 kn', '0.2 nm']);
    // 「下一航點」的標籤與數值包在 .wp 裡(平常 display: contents,格線照舊;單列時跨兩欄,名稱與距離可以用整個方塊的寬度)
    const wp = root.querySelector('.rows .wp')!;
    expect([...wp.children].map((c) => c.className)).toEqual(['label', 'v']);
    expect(text(wp)).toBe('下一航點ENTRANCE 123.46 nm');
    // 「方位 / 航段」「速限」「能見度」的標籤與數值各帶 k-brg / k-lim / k-vis 類別(兩列時「速限」與「能見度」排在同一列用;單列的規則不看這些類別)
    expect([...root.querySelectorAll('.rows > *')].map((c) => c.className.replace(/\s+/g, ' ').trim())).toEqual(['wp', 'label k-brg', 'v k-brg', 'label k-lim', 'v k-lim', 'label k-vis', 'v k-vis warning']);
    const rules = rulesOf(cssOf('brm-route'));
    const wpBase = rules.filter((r) => r.selector === '.rows .wp' && r.at.length === 0);
    expect(wpBase.map((r) => r.body)).toEqual(['display: contents;']);
    const wpSingle = singleRowBody('brm-route', '.rows .wp');
    expect(wpSingle).toMatch(/grid-column:\s*1 \/ -1/);
    expect(wpSingle).toMatch(/display:\s*flex/);
    expect(wpSingle).toMatch(/flex-wrap:\s*wrap/);
    // 偏移超過限值:圓點在 100%,樣式把它夾在條內(兩端各留半個圓點 3 px)。這條規則不在單列的樣式查詢裡 —— 兩列也夾
    // (原本兩列時圓點超出條 3 px,重播檢視載入 E09 夾具、時間軸最後被判定為水平裁切)
    const pos = root.querySelector('.bar .pos') as HTMLElement;
    expect(root.querySelector('.bar')!.classList.contains('over')).toBe(true);
    expect(pos.getAttribute('style')).toBe('--pos:100%');
    const left = await mount('brm-route', { route: { nextWaypoint: 'W2', xtd_m: -75, xtdLimit_m: 150 } });
    expect((left.shadowRoot!.querySelector('.bar .pos') as HTMLElement).getAttribute('style')).toBe('--pos:25%');
    const posRules = rules.filter((r) => r.selector === '.bar .pos');
    expect(posRules).toHaveLength(1);
    expect(posRules[0]!.at).toEqual([]);
    expect(posRules[0]!.body).toMatch(/left:\s*clamp\(3px, var\(--pos, 50%\), calc\(100% - 3px\)\)/);
    expect(posRules[0]!.body).not.toContain('!important');
  });

  it('浪方塊的交互作用:側向力與艏搖力矩各在 .nb 裡,文字不變', async () => {
    const el = await mount('brm-sea', { interaction: { active: true, targetId: 'ANCH1', forceSway_t: -123.44, momentYaw_tm: -12345.2, event: true }, heading: 0 });
    const force = el.shadowRoot!.querySelector('.interaction-force')!;
    expect(text(force)).toBe('Y -123.4t N -12345t·m');
    expect([...force.querySelectorAll('.nb')].map((n) => text(n))).toEqual(['Y -123.4t', 'N -12345t·m']);
    const none = await mount('brm-sea', { interaction: { active: false, forceSway_t: 0, momentYaw_tm: 0 }, heading: 0 });
    expect(text(none.shadowRoot!.querySelector('.interaction-force'))).toBe('無目標');
    expect(none.shadowRoot!.querySelector('.interaction-force .nb')).toBeNull();
  });

  it('浪方塊的交互作用標籤:「交互作用」、目標船代號、「超限」三段各在 .nb 裡(比方塊寬時在段與段之間換行),文字不變', async () => {
    const label = (el: El): Element => el.shadowRoot!.querySelector('.interaction .label')!;
    const event = await mount('brm-sea', { interaction: { active: true, targetId: 'YM_WARRANTY', forceSway_t: -123.4, momentYaw_tm: -12345, event: true }, heading: 0 });
    expect(label(event).textContent).toBe('交互作用 YM_WARRANTY 超限');
    expect([...label(event).querySelectorAll('.nb')].map((n) => n.textContent)).toEqual(['交互作用', 'YM_WARRANTY', '超限']);
    const normal = await mount('brm-sea', { interaction: { active: true, targetId: 'T3', forceSway_t: 1, momentYaw_tm: 2 }, heading: 0 });
    expect(label(normal).textContent).toBe('交互作用 T3');
    expect([...label(normal).querySelectorAll('.nb')].map((n) => n.textContent)).toEqual(['交互作用', 'T3']);
    // 沒有目標(或核心沒給代號)時只有「交互作用」;超限但沒有代號時是「交互作用 超限」
    const inactive = await mount('brm-sea', { interaction: { active: false, targetId: 'T3', forceSway_t: 0, momentYaw_tm: 0 }, heading: 0 });
    expect(label(inactive).textContent).toBe('交互作用');
    const noId = await mount('brm-sea', { interaction: { active: true, forceSway_t: 0, momentYaw_tm: 0, event: true }, heading: 0 });
    expect(label(noId).textContent).toBe('交互作用 超限');
  });

  it('傾斜儀:「T / 速率」的週期與角速度各在 .nb 裡,文字不變', async () => {
    const el = await mount('brm-inclinometer', { roll: { angle_deg: -35.5, rate_degps: -10.5, naturalPeriod_s: 12.5, staticHeel_deg: 12.5 } });
    const period = el.shadowRoot!.querySelector('.roll-period')!;
    expect(text(period)).toBe('12.5 s −10.5°/s');
    expect([...period.querySelectorAll('.nb')].map((n) => text(n))).toEqual(['12.5 s', '−10.5°/s']);
    const none = await mount('brm-inclinometer', {});
    expect(text(none.shadowRoot!.querySelector('.roll-period'))).toBe('--');
  });
});

describe('兩列(視窗 ≤ 1900 px)的排法', () => {
  /** brm-conning-bar 兩列媒體查詢裡的規則 */
  const barRules = (): CssRule[] => rulesOf(cssOf('brm-conning-bar')).filter((r) => r.at.some((a) => a.includes('max-width: 1900px')));
  const barBody = (selector: string): string =>
    barRules()
      .filter((r) => r.selector.split(',').map((x) => x.trim()).includes(selector))
      .map((r) => r.body)
      .join(' ');
  /** 「flex: 比例 1 0%」的比例 */
  const grow = (body: string): number => Number(/flex:\s*([\d.]+)\s+1\s+0%/.exec(body)?.[1] ?? Number.NaN);
  const ROW1 = ['brm-heading', 'brm-rot', 'brm-speed', 'brm-rudder', 'brm-engine', 'brm-thruster'];
  const ROW2 = ['brm-depth', 'brm-wind', 'brm-current', 'brm-sea', 'brm-inclinometer', 'brm-clock', 'brm-route'];

  it('brm-conning-bar:同一個 flex 容器折成兩行,::before 的整列項目換行;方塊是 size 容器、高度是條高的一半', () => {
    const bar = barBody('.bar');
    expect(bar).toMatch(/flex-wrap:\s*wrap/);
    expect(bar).toMatch(/align-content:\s*space-between/);
    expect(bar).toMatch(/row-gap:\s*0/);
    expect(bar).toMatch(/--brm-conning-single:\s*0/);
    const br = barBody('.bar::before');
    expect(br).toMatch(/order:\s*2/);
    expect(br).toMatch(/width:\s*100%/);
    expect(br).toMatch(/height:\s*0/);
    const tiles = barBody('.bar > *');
    expect(tiles).toMatch(/container-type:\s*size/);
    expect(tiles).toMatch(/height:\s*calc\(50% - 3px\)/);
    // 單列仍是 inline-size 容器(規則在媒體查詢之外)
    const base = rulesOf(cssOf('brm-conning-bar')).find((r) => r.selector === '.bar > *' && r.at.length === 0)!;
    expect(base.body).toMatch(/container-type:\s*inline-size/);
    // 第一列的方塊 order 1、第二列的 order 3;13 個方塊都分到其中一列
    const orderOf = (tag: string): string => /order:\s*(\d)/.exec(barRules().find((r) => r.selector.split(',').map((x) => x.trim()).includes(tag) && /order:/.test(r.body))?.body ?? '')?.[1] ?? '';
    for (const tag of ROW1) expect(orderOf(tag), `${tag} 在第一列`).toBe('1');
    for (const tag of ROW2) expect(orderOf(tag), `${tag} 在第二列`).toBe('3');
    expect([...ROW1, ...ROW2].sort()).toEqual([...TILES].sort());
  });

  it('第一列六格的比例與原本的格線相同(1.6 : 1.1 : 1.2 : 1.7 : 1.4 : 1.1);第二列每個方塊都有比例,文字最多的浪、傾斜儀、航線最寬', () => {
    const flexOf = (tag: string): number => grow(barRules().find((r) => r.selector === tag && /flex:/.test(r.body))?.body ?? '');
    expect(ROW1.map(flexOf)).toEqual([1.6, 1.1, 1.2, 1.7, 1.4, 1.1]);
    const row2 = Object.fromEntries(ROW2.map((tag) => [tag, flexOf(tag)]));
    for (const tag of ROW2) expect(row2[tag], `${tag} 的比例`).toBeGreaterThan(1);
    for (const wide of ['brm-sea', 'brm-inclinometer', 'brm-route']) for (const narrow of ['brm-depth', 'brm-wind', 'brm-current', 'brm-clock']) expect(row2[wide]!, `${wide} 比 ${narrow} 寬`).toBeGreaterThan(row2[narrow]!);
    // 六個方塊(12 格、或 13 格的第二列)時最窄的方塊在 1093 px 寬的視窗仍有 125 px 以上:列寬 1081 − 5 個間距
    for (const six of [ROW2.filter((t) => t !== 'brm-inclinometer'), ROW2.filter((t) => t !== 'brm-depth')]) {
      const sum = six.reduce((a, t) => a + row2[t]!, 0);
      for (const tag of six) expect(((1081 - 30) * row2[tag]!) / sum, `${tag} 在 1093 px 寬、六個方塊時的寬度`).toBeGreaterThan(125);
    }
  });

  it('13 格(有航線、有浪、有橫搖)時 UKC 排在第一列,第一列七格另給比例;其餘格數 UKC 在第二列', () => {
    const special = barRules().filter((r) => r.selector.startsWith('.bar.with-route.with-sea.with-roll '));
    expect(special.map((r) => r.selector.replace('.bar.with-route.with-sea.with-roll ', '')).sort()).toEqual([...ROW1, 'brm-depth'].sort());
    const depth = special.find((r) => r.selector.endsWith('brm-depth'))!;
    expect(depth.body).toMatch(/order:\s*1/);
    for (const r of special) expect(grow(r.body), `${r.selector} 的比例`).toBeGreaterThan(1);
    // 只有三個類別都有時才套用;沒有只看 .with-roll 的規則(原本有傾斜儀就是 7 欄)
    expect(barRules().some((r) => /^\.bar\.with-roll\b/.test(r.selector))).toBe(false);
  });

  it('13 個方塊都帶兩列的樣式查詢:標題列的行高固定,放不下時中文標題換到下一行;兩列的規則沒有會讓內容看不到的宣告', () => {
    const hiding = /display:\s*none|visibility:\s*hidden|overflow(-x|-y)?:\s*(hidden|clip)|text-overflow|clip-path|opacity:\s*0(\.0+)?\s*(;|$)|font-size:\s*0|!important/;
    for (const tag of TILES) {
      expect(cssOf(tag), `${tag} 的樣式含兩列的樣式查詢`).toContain(`@container style(${TWO})`);
      expect(twoRowBody(tag, '.tile-title'), `${tag} 的標題列`).toMatch(/flex-wrap:\s*wrap/);
      expect(twoRowBody(tag, '.tile-title'), `${tag} 的標題列`).toMatch(/line-height:\s*13px/);
      const zh = twoRowBody(tag, '.tile-title .zh');
      expect(zh, `${tag} 的中文標題`).toMatch(/margin-left:\s*auto/);
      expect(zh, `${tag} 的中文標題`).toMatch(/white-space:\s*normal/);
      expect(zh, `${tag} 的中文標題`).toMatch(/overflow-wrap:\s*anywhere/);
      for (const r of rulesOf(cssOf(tag)).filter(inTwoRows)) {
        expect(hiding.test(r.body), `${tag} ${r.at.join(' ')} ${r.selector} { ${r.body} }`).toBe(false);
        // 兩列的規則不寫在單列的樣式查詢裡(兩組互不影響)
        expect(inSingleRow(r), `${tag} ${r.selector}`).toBe(false);
      }
    }
  });

  it('大字(航向、主機轉速、UKC):行高 0.82,字級以方塊高度設上限(cqb)', () => {
    const heading = twoRowBody('brm-heading', '.hdg .value.big');
    expect(heading).toMatch(/font-size:\s*clamp\(28px, min\(3\.2vw, 33cqi, calc\(119cqb - 86px\)\), 56px\)/);
    expect(heading).toMatch(/line-height:\s*0\.82/);
    const engine = twoRowBody('brm-engine', '.top .value.big');
    expect(engine).toMatch(/font-size:\s*clamp\(28px, min\(3\.2vw, calc\(116cqb - 78px\)\), 56px\)/);
    expect(engine).toMatch(/line-height:\s*0\.82/);
    // 窄於 210 px(主機狀態在車鐘位置的下一行,多一行)時上限低一點
    expect(twoRowBody('brm-engine', '.top .value.big', 'max-width: 209px')).toMatch(/font-size:\s*clamp\(28px, min\(3\.2vw, calc\(116cqb - 85px\)\), 56px\)/);
    // UKC:上限寫在 --ukc-h —— 單欄三行、單欄四行(有潮高一列)、兩欄(215 px 以上)各一個
    const narrow = twoRowBody('brm-depth', '.main .value.big');
    expect(narrow).toMatch(/font-size:\s*clamp\(28px, min\(3\.2vw, var\(--ukc-h\)\), 56px\)/);
    expect(narrow).toMatch(/line-height:\s*0\.82/);
    expect(twoRowBody('brm-depth', '.tile')).toMatch(/--ukc-h:\s*calc\(119cqb - 79px\)/);
    expect(twoRowBody('brm-depth', '.tile:has(.row.tide)')).toMatch(/--ukc-h:\s*calc\(119cqb - 93px\)/);
    expect(twoRowBody('brm-depth', '.tile', 'min-width: 215px')).toMatch(/--ukc-h:\s*calc\(119cqb - 64px\)/);
    expect(twoRowBody('brm-depth', '.tile:has(.row.tide)', 'min-width: 215px')).toMatch(/--ukc-h:\s*calc\(119cqb - 64px\)/);
    // 多一位數的值依方塊寬度再縮(與單列相同的比例),仍受方塊高度的上限
    expect(twoRowBody('brm-depth', '.main .value.big.long')).toMatch(/26\.5cqi, var\(--ukc-h\)/);
    expect(twoRowBody('brm-depth', '.main .value.big.xlong')).toMatch(/calc\(130cqi \/ var\(--ukc-chars, 6\)\), var\(--ukc-h\)/);
    // 單列的規則沒有動
    expect(singleRowBody('brm-heading', '.hdg .value.big')).toMatch(/clamp\(28px, min\(3\.2vw, 38cqi\), 56px\)/);
    expect(singleRowBody('brm-engine', '.top .value.big')).toMatch(/clamp\(28px, min\(3\.2vw, 32\.5cqi\), 56px\)/);
    expect(singleRowBody('brm-depth', '.main .value.big')).toMatch(/clamp\(28px, min\(3\.2vw, 33cqi\), 56px\)/);
  });

  it('圖形吃剩下的高度、可以縮短:航向帶、主機的轉速條、艏側推的推力條', () => {
    const tape = twoRowBody('brm-heading', '.tape');
    expect(tape).toMatch(/flex:\s*1 1 0/);
    expect(tape).toMatch(/min-height:\s*24px/);
    const bar = twoRowBody('brm-engine', '.bar');
    expect(bar).toMatch(/flex:\s*0 1 26px/);
    expect(bar).toMatch(/min-height:\s*12px/);
    expect(twoRowBody('brm-thruster', '.bar')).toMatch(/min-height:\s*14px/);
  });

  it('文字列的行高固定(不隨字型的行高而變):各方塊的標籤與最後一列', () => {
    expect(twoRowBody('brm-heading', '.sub')).toMatch(/line-height:\s*13px/);
    expect(twoRowBody('brm-speed', '.rows .label')).toMatch(/line-height:\s*13px/);
    expect(twoRowBody('brm-engine', '.bottom')).toMatch(/line-height:\s*16px/);
    expect(twoRowBody('brm-thruster', '.readouts')).toMatch(/line-height:\s*16px/);
    expect(twoRowBody('brm-depth', '.rows')).toMatch(/line-height:\s*clamp\(12px, calc\(20cqb - 11px\), 14px\)/);
    expect(twoRowBody('brm-current', '.rows .label')).toMatch(/line-height:\s*13px/);
    expect(twoRowBody('brm-clock', '.rows .label')).toMatch(/line-height:\s*13px/);
    expect(twoRowBody('brm-sea', '.rows')).toMatch(/line-height:\s*13px/);
    expect(twoRowBody('brm-sea', '.interaction')).toMatch(/line-height:\s*13px/);
    expect(twoRowBody('brm-inclinometer', '.readouts')).toMatch(/line-height:\s*13px/);
    expect(twoRowBody('brm-route', '.rows')).toMatch(/line-height:\s*13px/);
  });

  it('船速、時間、浪、傾斜儀:字級以方塊高度設上限(視窗寬而矮時各列不比方塊高)', () => {
    expect(twoRowBody('brm-speed', '.rows .value.mid')).toMatch(/font-size:\s*clamp\(18px, min\(1\.6vw, 21cqb\), 28px\)/);
    expect(twoRowBody('brm-speed', '.rows .value.small')).toMatch(/font-size:\s*clamp\(12px, min\(1\.1vw, 15cqi, 14\.5cqb\), 20px\)/);
    expect(twoRowBody('brm-clock', '.rows .value.small')).toMatch(/font-size:\s*clamp\(14px, min\(1\.1vw, 16cqb\), 20px\)/);
    expect(twoRowBody('brm-clock', '.pos .value.small')).toMatch(/font-size:\s*clamp\(11px, min\(1\.1vw, 10cqi, 16cqb\), 20px\)/);
    expect(twoRowBody('brm-clock', '.rows')).toMatch(/gap:\s*0/);
    // 浪與傾斜儀的數值也是(字級照視窗寬度到上限時,107 px 高的方塊放不下)
    expect(twoRowBody('brm-sea', '.rows .value')).toMatch(/font-size:\s*clamp\(11px, min\(0\.95vw, 8cqi, 13cqb\), 18px\)/);
    expect(twoRowBody('brm-sea', '.interaction .value')).toMatch(/font-size:\s*clamp\(11px, min\(0\.85vw, 7\.5cqi, 12cqb\), 16px\)/);
    expect(twoRowBody('brm-inclinometer', '.readouts .value')).toMatch(/font-size:\s*clamp\(11px, min\(0\.9vw, 9cqi, 13\.5cqb\), 16px\)/);
    expect(twoRowBody('brm-inclinometer', '.readouts .value.main')).toMatch(/font-size:\s*clamp\(12px, min\(1\.1vw, 11cqi, 16cqb\), 20px\)/);
    // 風方塊沒有兩列專用的規則(原本就放得下;只有共用的標題列規則)
    expect(rulesOf(cssOf('brm-wind')).filter((r) => inTwoRows(r) && !['.tile-title', '.tile-title .zh', '.nb'].includes(r.selector))).toEqual([]);
    // 船速方塊省略標籤中文部分的規則仍只有 125 px 的那一條(兩列沒有新增)
    expect(rulesOf(cssOf('brm-speed')).filter((r) => r.selector === '.label .zh').map((r) => r.at.join(' '))).toEqual(['@container (max-width: 125px)']);
  });

  it('排法只看方塊寬度(不隨數值的位數跳動):主機狀態、艏側推、UKC 下方各列、交互作用的數值', () => {
    // 主機:窄於 210 px 時車鐘位置獨占一行(主機狀態在下一行、靠右)
    expect(twoRowBody('brm-engine', '.bottom .teleg', 'max-width: 209px')).toMatch(/flex-basis:\s*100%/);
    expect(twoRowBody('brm-engine', '.bottom > :last-child')).toMatch(/margin-left:\s*auto/);
    // 艏側推:窄於 240 px 時「實際」「令」各一行(與單列同一門檻)
    expect(twoRowBody('brm-thruster', '.readouts', 'max-width: 239px')).toMatch(/flex-direction:\s*column/);
    expect(twoRowBody('brm-thruster', '.readouts > span', 'max-width: 239px')).toMatch(/justify-content:\s*space-between/);
    // UKC:215 px 以上下方各列排兩欄
    const rows = twoRowBody('brm-depth', '.rows', 'min-width: 215px');
    expect(rows).toMatch(/display:\s*grid/);
    expect(rows).toMatch(/grid-template-columns:\s*minmax\(max-content, 1fr\) minmax\(max-content, 1fr\)/);
    // 浪:窄於 290 px 時交互作用的數值固定在標籤的下一行;窄於 236 px 時玫瑰圖佔前兩列的左側
    expect(twoRowBody('brm-sea', '.interaction .value', 'max-width: 289px')).toMatch(/flex-basis:\s*100%/);
    expect(twoRowBody('brm-sea', '.rose', 'max-width: 235px')).toMatch(/grid-row:\s*1 \/ span 2/);
    expect(twoRowBody('brm-sea', '.rows > :nth-child(2)', 'max-width: 235px')).toMatch(/grid-column:\s*2/);
    // 兩列沒有另外不顯示浪向玫瑰的規則(165 px 的門檻是原本就有的;兩列時浪方塊至少約 200 px 寬)
    expect(rulesOf(cssOf('brm-sea')).filter((r) => r.selector === '.rose' && /display:\s*none/.test(r.body)).map((r) => r.at.join(' '))).toEqual(['@container (max-width: 165px)']);
  });

  it('流:185 px 以下玫瑰圖在數值上方(與風方塊同一門檻)', () => {
    const body = twoRowBody('brm-current', '.body', 'max-width: 185px');
    expect(body).toMatch(/flex-direction:\s*column/);
    const rose = twoRowBody('brm-current', '.rose', 'max-width: 185px');
    expect(rose).toMatch(/width:\s*28px/);
    expect(rose).toMatch(/align-self:\s*center/);
    expect(twoRowBody('brm-current', '.rows')).toMatch(/min-width:\s*0/);
  });

  it('傾斜儀:刻度在左、四項數值在右(單欄),「T / 速率」的數值在標籤的下一行', () => {
    const tile = twoRowBody('brm-inclinometer', '.tile');
    expect(tile).toMatch(/display:\s*grid/);
    expect(tile).toMatch(/grid-template-columns:\s*minmax\(0, 1fr\) auto/);
    expect(twoRowBody('brm-inclinometer', '.tile-title')).toMatch(/grid-column:\s*1 \/ -1/);
    expect(twoRowBody('brm-inclinometer', '.scale')).toMatch(/grid-column:\s*1/);
    const readouts = twoRowBody('brm-inclinometer', '.readouts');
    expect(readouts).toMatch(/grid-column:\s*2/);
    expect(readouts).toMatch(/grid-template-columns:\s*auto/);
    expect(twoRowBody('brm-inclinometer', '.readouts > span:nth-child(4)')).toMatch(/flex-direction:\s*column/);
  });

  it('航線:偏移、左右、限值同一行(放不下時限值換行);「速限」與「能見度」同一列;樣板的文字內容不變', async () => {
    expect(twoRowBody('brm-route', '.xtd')).toMatch(/flex-wrap:\s*wrap/);
    expect(twoRowBody('brm-route', '.xtd .side')).toMatch(/white-space:\s*nowrap/);
    expect(twoRowBody('brm-route', '.xtd .label')).toMatch(/margin-left:\s*auto/);
    expect(twoRowBody('brm-route', '.xtd .value.mid')).toMatch(/font-size:\s*clamp\(18px, min\(1\.6vw, 20cqb\), 28px\)/);
    expect(twoRowBody('brm-route', '.rows')).toMatch(/grid-template-columns:\s*auto minmax\(0, 1fr\) auto auto/);
    expect(twoRowBody('brm-route', '.rows .wp')).toMatch(/grid-column:\s*1 \/ -1/);
    expect(twoRowBody('brm-route', '.rows .label.k-brg')).toMatch(/grid-column:\s*1 \/ span 2/);
    expect(twoRowBody('brm-route', '.rows .v.k-brg')).toMatch(/grid-column:\s*3 \/ span 2/);
    // 只有「速限」或只有「能見度」時數值佔到方塊的右緣
    expect(twoRowBody('brm-route', '.rows:not(:has(.k-lim)) .v.k-vis')).toMatch(/grid-column:\s*2 \/ -1/);
    expect(twoRowBody('brm-route', '.rows:not(:has(.k-vis)) .v.k-lim')).toMatch(/grid-column:\s*2 \/ -1/);
    // 單列的規則不看這些類別
    for (const r of rulesOf(cssOf('brm-route')).filter((r) => /k-(brg|lim|vis)/.test(r.selector))) expect(inTwoRows(r), r.selector).toBe(true);
    // 樣板:文字內容與原本相同;各列的標籤與數值成對帶同一個類別
    const el = await mount('brm-route', {
      route: { nextWaypoint: 'APPROACH', xtd_m: -1568, xtdLimit_m: 1500, distanceToWaypoint_nm: 14.76, bearingToWaypoint_deg: 359, legCourse_deg: 359, speedLimit_kn: 12.5, complete: false },
      visibility: 0.2,
    });
    const root = el.shadowRoot!;
    expect(text(root.querySelector('.tile-title'))).toBe('ROUTE→ APPROACH');
    expect(text(root.querySelector('.xtd'))).toBe('1568m L 偏左 限 1500 m');
    expect(text(root.querySelector('.rows'))).toBe('下一航點APPROACH 14.76 nm 方位 / 航段359 / 359 速限12.5 kn 能見度0.2 nm');
    for (const k of ['k-brg', 'k-lim', 'k-vis']) expect([...root.querySelectorAll(`.rows > .${k}`)].map((c) => c.classList[0])).toEqual(['label', 'v']);
    // 沒有航線、只有能見度:只有「能見度」一列
    const visOnly = await mount('brm-route', { visibility: 8 });
    expect(text(visOnly.shadowRoot!.querySelector('.rows'))).toBe('能見度8.0 nm');
    expect([...visOnly.shadowRoot!.querySelectorAll('.rows > *')].map((c) => c.className.replace(/\s+/g, ' ').trim())).toEqual(['label k-vis', 'v k-vis']);
  });

  it('八種格數組合的樣板:方塊的順序與文字不因兩列的排法而變(兩列與單列是同一份樣板,只靠樣式分列)', async () => {
    const cfg = getShipConfig('FSB1');
    const state = {
      ...defaultState('FSB1'),
      route: { nextWaypoint: 'FW1', xtd_m: 12, xtdLimit_m: 150 },
      waves: { significantHeight_m: 2, peakPeriod_s: 7, direction_deg: 90, wavelength_m: 76 },
      roll: { angle_deg: 1, rate_degps: 0, naturalPeriod_s: 9.8, staticHeel_deg: 0 },
    } as OwnShipState;
    const el = await mount('brm-conning-bar', { state, config: cfg, issues: [] });
    const bar = el.shadowRoot!.querySelector('.bar')!;
    expect([...bar.classList].sort()).toEqual(['bar', 'with-roll', 'with-route', 'with-sea']);
    // 樣板裡 UKC 仍排在艏側推之後(DOM 順序不變);13 格時由樣式(order)排到第一列的最後
    expect([...bar.children].map((t) => t.tagName.toLowerCase())).toEqual(TILES.filter((t) => t !== 'brm-clock' && t !== 'brm-route').concat(['brm-clock', 'brm-route']));
    el.remove();
  });
});
