// 講評報告頁(report.html):開啟最近的核心紀錄(JSON Lines;本輪 build/e2e/records 或 build/records 中最新且已寫 footer 者)無 console 錯誤;
// 教官站「講評報告」鈕 → 新分頁 report.html?from=instructor 載入交接紀錄。
import fs from 'node:fs';
import path from 'node:path';
import { ROOT, instructorUrl, reportUrl } from '../support/env.ts';
import { ensureRunning, expect, test } from '../support/fixtures.ts';
import { PageErrors, waitConnected } from '../support/pages.ts';

/** 本輪產生、已寫 footer 的最新核心紀錄;沒有則退回 --record 的第一份紀錄 */
function latestClosedRecord(recordsDir: string, startedUtc: string, fallback: string): string {
  const started = Date.parse(startedUtc) || 0;
  const candidates: Array<{ file: string; mtime: number }> = [];
  for (const dir of [recordsDir, path.join(ROOT, 'build', 'records')]) {
    if (!fs.existsSync(dir)) continue;
    for (const f of fs.readdirSync(dir)) {
      if (!f.endsWith('.jsonl')) continue;
      const full = path.join(dir, f);
      const st = fs.statSync(full);
      if (!st.isFile() || st.mtimeMs < started - 1000) continue;
      const text = fs.readFileSync(full, 'utf8');
      const lines = text.trimEnd().split('\n');
      if (!lines[lines.length - 1]?.includes('"kind":"footer"')) continue;
      candidates.push({ file: full, mtime: st.mtimeMs });
    }
  }
  candidates.sort((a, b) => b.mtime - a.mtime);
  return candidates[0]?.file ?? fallback;
}

test('報告頁開啟最近的核心紀錄,無 console 錯誤', async ({ page, env }) => {
  const record = latestClosedRecord(env.recordsDir, env.startedUtc, env.recordPath);
  expect(fs.existsSync(record), `紀錄 ${record}`).toBe(true);
  const errors = new PageErrors(page);
  await page.goto(reportUrl(env));
  const app = page.locator('brm-report-app');
  await expect(app).toContainText('開啟檔案');
  await app.locator('input.file-input').setInputFiles(record);
  await expect(app.locator('.msg')).toContainText('已載入', { timeout: 15_000 });
  await expect(app.locator('.msg')).not.toHaveClass(/error/);
  await expect(app.locator('.msg')).toContainText(/狀態 \d+ 筆/);
  await expect(app).not.toContainText('請以「開啟檔案」載入練習紀錄');
  errors.expectNone();
});

test('教官站「講評報告」→ 新分頁載入交接紀錄', async ({ page, env, core }) => {
  await ensureRunning(core);
  await page.goto(instructorUrl(env));
  await waitConnected(page);
  // 累積幾筆狀態抽樣再交接
  await page.waitForTimeout(2000);
  const [popup] = await Promise.all([page.context().waitForEvent('page'), page.locator('header button.open-report').click()]);
  const errors = new PageErrors(popup);
  await popup.waitForLoadState();
  await expect(popup).toHaveURL(/report\.html\?from=instructor/);
  const app = popup.locator('brm-report-app');
  await expect(app.locator('.msg')).toContainText('已載入 教官站紀錄', { timeout: 15_000 });
  await expect(app.locator('.msg')).not.toHaveClass(/error/);
  errors.expectNone();
  await popup.close();
});
