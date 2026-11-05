// 端對端測試共用 fixture:env(global-setup 寫的埠與路徑)、core(測試端的核心 WebSocket 客戶端,用來交叉驗證狀態幀與送網頁 UI 不會產生的指令)。
// 全部案例共用同一個 SimCore.Host 程序(playwright.config.ts workers = 1),案例之間以 ensureScenario 確保情境前置條件,不依賴執行順序。
import { test as base, expect } from '@playwright/test';
import { e2eEnv, stateWsUrl, type E2eEnv } from './env.ts';
import { CoreClient } from './ws.ts';

export const test = base.extend<{ env: E2eEnv; core: CoreClient }>({
  env: async ({}, use) => {
    await use(e2eEnv());
  },
  core: async ({ env }, use) => {
    const client = await CoreClient.connect(stateWsUrl(env));
    // 核心凍結時不廣播狀態幀(SimulationEngine.Step 在 Frozen 時不 Advance):前一案例凍結後失敗未恢復時,先恢復再交給案例
    await ensureRunning(client);
    await use(client);
    client.close();
  },
});

export { expect };

/** 情境代號 → 檔案路徑(data/scenarios/<id>.yaml;與教官站 loadScenarioCommand 相同的 value/args.path) */
export const scenarioPath = (id: string): string => `data/scenarios/${id}.yaml`;

/** 核心目前不是指定情境時送 loadScenario 並等狀態幀切換(載入後第一筆狀態幀即帶新情境 id) */
export async function ensureScenario(core: CoreClient, id: string): Promise<void> {
  const now = await core.nextState(5000);
  if (now.scenario?.id === id) return;
  core.send({ type: 'loadScenario', value: scenarioPath(id), args: { id, path: scenarioPath(id) } });
  await core.waitState((s) => s.scenario?.id === id, 30_000, `情境 ${id} 的狀態幀`);
}

/**
 * 確保核心在執行中:1.5 s 內有狀態幀且 flags.frozen 為 false 即可;沒有狀態幀(凍結中不廣播)或旗標為 true 時送 resume 並等狀態幀恢復。
 * 前一案例凍結後失敗未恢復時的保護;正常流程不會送出任何指令。
 */
export async function ensureRunning(core: CoreClient): Promise<void> {
  try {
    const now = await core.nextState(1500);
    if (!now.flags?.frozen) return;
  } catch {
    // 無狀態幀:視為凍結中
  }
  console.warn('[e2e] 核心無狀態幀或已凍結,送 resume');
  core.send({ type: 'resume' });
  await core.waitState((s) => s.flags?.frozen === false, 10_000, '恢復後的狀態幀');
}

/** 畫布的非黑像素比例(0–1;2D 畫布 getImageData;WebGL 畫布不適用) */
export async function nonBlackRatio(canvas: import('@playwright/test').Locator): Promise<number> {
  return canvas.evaluate((el) => {
    const c = el as HTMLCanvasElement;
    const ctx = c.getContext('2d');
    if (!ctx || c.width === 0 || c.height === 0) return 0;
    const d = ctx.getImageData(0, 0, c.width, c.height).data;
    let lit = 0;
    const n = c.width * c.height;
    for (let i = 0; i < d.length; i += 4) if (d[i]! + d[i + 1]! + d[i + 2]! > 48) lit++;
    return lit / n;
  });
}
