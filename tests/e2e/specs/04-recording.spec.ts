// 畫面錄影(docs/design/screen-recording.md):headless Chromium 沒有畫面可分享,以 page.addInitScript 用 canvas.captureStream 取代
// navigator.mediaDevices.getDisplayMedia;教官站「開始錄影」→ 3 s →「停止錄影」→ SimCore.Host 的 --media-dir 出現 <mediaId>.webm 與旁檔 .json。
import fs from 'node:fs';
import path from 'node:path';
import { instructorUrl } from '../support/env.ts';
import { expect, test } from '../support/fixtures.ts';
import { PageErrors, waitConnected } from '../support/pages.ts';

test('錄影:開始 3 s 停止 → media 目錄出現 .webm 與旁檔', async ({ page, env }) => {
  await page.addInitScript(() => {
    // 640×360 動畫畫布(每 100 ms 換色與計數),captureStream(15) 當成被分享的畫面;audio 參數忽略(無音軌)
    const fake = async (): Promise<MediaStream> => {
      const canvas = document.createElement('canvas');
      canvas.width = 640;
      canvas.height = 360;
      const ctx = canvas.getContext('2d')!;
      let frame = 0;
      const paint = () => {
        frame += 1;
        ctx.fillStyle = `hsl(${(frame * 11) % 360} 70% 35%)`;
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.fillStyle = '#fff';
        ctx.font = '36px sans-serif';
        ctx.fillText(`Simosa BRM e2e ${frame}`, 24, 72);
      };
      paint();
      setInterval(paint, 100);
      return canvas.captureStream(15);
    };
    Object.defineProperty(navigator.mediaDevices, 'getDisplayMedia', { configurable: true, writable: true, value: fake });
  });
  const errors = new PageErrors(page);
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  const panel = page.locator('brm-recording-panel').first();
  await expect(panel.locator('button.start')).toBeEnabled();
  await expect(panel.locator('.unsupported')).toHaveCount(0);
  // 畫布串流沒有音軌:關閉「含系統音訊」
  const withAudio = panel.locator('input.with-audio');
  if (await withAudio.isChecked()) await withAudio.uncheck();

  const before = new Set(fs.readdirSync(env.mediaDir));
  await panel.locator('button.start').click();
  await expect(panel.locator('.state-label')).toHaveText('錄影中', { timeout: 15_000 });
  await expect(page.locator('header .rec-badge')).toBeVisible();
  await page.waitForTimeout(3000);
  await panel.locator('button.stop').click();
  await expect(panel.locator('.state-label')).toHaveText('未錄影', { timeout: 20_000 });
  await expect(panel.locator('.error')).toHaveCount(0);
  await expect(panel.locator('.list .item .file').first()).toHaveText(/\.webm$/);

  const created = fs.readdirSync(env.mediaDir).filter((f) => !before.has(f));
  const webm = created.filter((f) => f.endsWith('.webm'));
  expect(webm, `本輪新增的錄影檔(${env.mediaDir}:${created.join(', ')})`).toHaveLength(1);
  expect(created.some((f) => f.endsWith('.webm.part')), '停止後不應留下 .part').toBe(false);
  const webmPath = path.join(env.mediaDir, webm[0]!);
  expect(fs.statSync(webmPath).size).toBeGreaterThan(1024);
  const sidecarPath = webmPath.replace(/\.webm$/, '.json');
  expect(fs.existsSync(sidecarPath), `旁檔 ${sidecarPath}`).toBe(true);
  const sidecar = JSON.parse(fs.readFileSync(sidecarPath, 'utf8')) as Record<string, unknown>;
  expect(sidecar['file']).toBe(webm[0]);
  expect(sidecar['station']).toBe('instructor');
  expect(sidecar['sizeBytes']).toBe(fs.statSync(webmPath).size);
  expect(sidecar['chunks'] as number).toBeGreaterThanOrEqual(1);
  expect(sidecar['durationS'] as number).toBeGreaterThanOrEqual(2);
  expect(sidecar['truncated']).toBe(false);
  expect(typeof sidecar['sha256']).toBe('string');
  await expect(panel.locator('.list .item .file').first()).toHaveText(webm[0]!);
  errors.expectNone();
});
