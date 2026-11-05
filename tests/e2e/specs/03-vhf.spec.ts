// 內建 VHF 角色扮演頻道(src/Contracts/vhf-protocol.md):操船台 ?station=bridge 與教官站同頻道文字互通、不同頻道不互通;
// PTT 以 Chromium 假媒體(--use-fake-device-for-media-stream / --use-fake-ui-for-media-stream)按住 2 s → 核心事件流 vhfCall、
// 對方 VHF 面板有 RX 通話紀錄(pttStart/pttEnd)。
import type { Locator, Page } from '@playwright/test';
import { bridgeUrl, instructorUrl } from '../support/env.ts';
import { expect, test } from '../support/fixtures.ts';
import { waitConnected } from '../support/pages.ts';

/** 等 VHF 面板加入頻道(連線藥丸「已連線」、主頻道按鈕 active) */
async function waitJoined(vhf: Locator, channel = '16'): Promise<void> {
  await expect(vhf.locator('.status .pill')).toHaveText('已連線', { timeout: 15_000 });
  await expect(vhf.locator('.channels button.active')).toHaveText(channel);
}

async function openStations(page: Page, env: Parameters<typeof bridgeUrl>[0]): Promise<{ bridge: Page; instructor: Page; vhfB: Locator; vhfI: Locator }> {
  const bridge = page;
  await bridge.goto(bridgeUrl(env, { station: 'bridge' }));
  const instructor = await page.context().newPage();
  await instructor.goto(instructorUrl(env));
  await waitConnected(bridge);
  await waitConnected(instructor);
  const vhfB = bridge.locator('brm-vhf');
  const vhfI = instructor.locator('brm-vhf');
  await waitJoined(vhfB);
  await waitJoined(vhfI);
  return { bridge, instructor, vhfB, vhfI };
}

async function sendText(vhf: Locator, text: string): Promise<void> {
  await vhf.locator('.text-row input').fill(text);
  await vhf.locator('.text-row button.send').click();
  // 伺服器回送本站(含 utc):本站 log 出現即表示伺服器已轉送
  await expect(vhf.locator('.log .item[data-kind="text"] .text', { hasText: text })).toBeVisible({ timeout: 5000 });
}

test('文字訊息:同頻道互通、不同頻道不互通', async ({ page, env }) => {
  const { bridge, vhfB, vhfI } = await openStations(page, env);
  const same = `E2E 同頻道 ${Date.now()}`;
  await sendText(vhfI, same);
  const rxB = vhfB.locator('.log .item[data-kind="text"].rx .text', { hasText: same });
  await expect(rxB).toBeVisible({ timeout: 5000 });
  await expect(rxB.locator('..').locator('.ch')).toHaveText('CH16');

  // 操船台轉到 CH12:教官站(CH16、無額外守聽)的文字不應到達
  await vhfB.locator('.channels button', { hasText: /^12$/ }).click();
  await expect(vhfB.locator('.channels button.active')).toHaveText('12');
  const other = `E2E 不同頻道 ${Date.now()}`;
  await sendText(vhfI, other);
  await bridge.waitForTimeout(1500);
  await expect(vhfB.locator('.log .item .text', { hasText: other })).toHaveCount(0);

  // 反向亦然:操船台在 CH12 送出,教官站(CH16)收不到
  const back = `E2E 反向 ${Date.now()}`;
  await sendText(vhfB, back);
  await bridge.waitForTimeout(1500);
  await expect(vhfI.locator('.log .item .text', { hasText: back })).toHaveCount(0);
});

test('PTT 按住 2 s → 核心事件 vhfCall、教官站事件流與 VHF 面板有通話紀錄', async ({ page, env, core }) => {
  const { bridge, instructor, vhfB, vhfI } = await openStations(page, env);
  const ptt = vhfB.locator('button.ptt');
  const box = await ptt.boundingBox();
  expect(box).not.toBeNull();
  const call = core.waitEvent('vhfCall', 20_000);
  await bridge.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);
  await bridge.mouse.down();
  // 假麥克風授權後 pttStart 被接受 → 面板進入 TX
  await expect(ptt).toHaveClass(/transmitting/, { timeout: 10_000 });
  await expect(ptt).toHaveText(/TX CH16/);
  // 對方面板的接收指示燈顯示發話站
  await expect(vhfI.locator('.lamp')).toContainText('RX bridge CH16', { timeout: 5000 });
  await bridge.waitForTimeout(2000);
  await bridge.mouse.up();
  await expect(ptt).not.toHaveClass(/transmitting/, { timeout: 5000 });

  const ev = await call;
  const data = ev['data'] as { station?: string; channel?: string; duration_s?: number; endReason?: string };
  expect(data.station).toBe('bridge');
  expect(data.channel).toBe('16');
  expect(data.endReason).toBe('pttEnd');
  expect(data.duration_s ?? 0).toBeGreaterThanOrEqual(1.5);
  expect(data.duration_s ?? 0).toBeLessThan(6);
  // 教官站:事件流(核心事件框 source vhf)與 VHF 面板的 RX 通話紀錄
  await expect(instructor.locator('brm-event-stream .item[data-kind="vhfCall"]').first()).toBeVisible({ timeout: 10_000 });
  await expect(instructor.locator('brm-event-stream .item[data-kind="vhfCall"] .text').first()).toContainText('bridge');
  await expect(vhfI.locator('.log .item[data-kind="call"].rx').first()).toBeVisible();
});
