// 主題(日/黃昏/夜)的讀取與套用:儀器頁與教官站共用同一個 localStorage 鍵,切換後兩頁一致。

export type Theme = 'day' | 'dusk' | 'night';

export const THEMES: ReadonlyArray<{ id: Theme; label: string }> = [
  { id: 'day', label: '日' },
  { id: 'dusk', label: '黃昏' },
  { id: 'night', label: '夜' },
];

export const THEME_KEY = 'simosa-brm.instruments.theme';

export function isTheme(v: unknown): v is Theme {
  return v === 'day' || v === 'dusk' || v === 'night';
}

/** 讀取上次選擇的主題;無法讀取(私密視窗等)時預設黃昏。 */
export function readTheme(): Theme {
  try {
    const v = localStorage.getItem(THEME_KEY);
    if (isTheme(v)) return v;
  } catch {
    /* 忽略 */
  }
  return 'dusk';
}

/** 套用到 <html data-theme> 並記住選擇。 */
export function applyTheme(theme: Theme, persist = true): void {
  document.documentElement.dataset['theme'] = theme;
  if (!persist) return;
  try {
    localStorage.setItem(THEME_KEY, theme);
  } catch {
    /* 忽略 */
  }
}
