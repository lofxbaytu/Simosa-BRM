// 講評報告頁(report.html):開啟最近的核心紀錄(JSON Lines;本輪 build/e2e/records 或 build/records 中最新且已寫 footer 者)無 console 錯誤;
// 教官站「講評報告」鈕 → 新分頁 report.html?from=instructor 載入交接紀錄;
// 最近載入的紀錄在 IndexedDB 存 Blob(不存整份文字),重新整理後由 Blob 還原;舊版存的整份文字仍讀得進來並改存成 Blob;
// 大檔讀取中主執行緒有讓出(狀態列畫得出小於 100% 的進度);核心仍在寫入的紀錄另存失敗時不會留下前一份(重新整理不還原成別的練習)。
import fs from 'node:fs';
import path from 'node:path';
import { OUT, ROOT, instructorUrl, reportUrl } from '../support/env.ts';
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

/** IndexedDB 內「最近載入的紀錄」(record:last)的型別 */
const LAST_RECORD = `new Promise((resolve, reject) => {
  const open = indexedDB.open('simosa-brm-report');
  open.onerror = () => reject(open.error);
  open.onsuccess = () => {
    const get = open.result.transaction('kv').objectStore('kv').get('record:last');
    get.onerror = () => reject(get.error);
    get.onsuccess = () => {
      const v = get.result;
      resolve(v ? { name: v.name, hasText: typeof v.text === 'string', isBlob: v.blob instanceof Blob, size: v.blob instanceof Blob ? v.blob.size : -1 } : null);
    };
  };
})`;

test('最近載入的紀錄存 Blob:重新整理後還原;舊版存的整份文字仍可讀並改存 Blob', async ({ page, env }) => {
  // 核心可能仍在寫入這份紀錄(檔案持續變大):複製一份固定的內容來比對大小與重新整理前後的報告。
  // 放在 build/e2e/tmp(路徑只有 ASCII;測試輸出目錄以案例名稱命名,含中文的路徑在沒有設定語系的環境下選不進檔案輸入)
  const record = path.join(OUT, 'tmp', 'report-last-record.jsonl');
  fs.mkdirSync(path.dirname(record), { recursive: true });
  fs.copyFileSync(latestClosedRecord(env.recordsDir, env.startedUtc, env.recordPath), record);
  const bytes = fs.statSync(record).size;
  const name = path.basename(record);
  const errors = new PageErrors(page);
  await page.goto(reportUrl(env));
  const app = page.locator('brm-report-app');
  await app.locator('input.file-input').setInputFiles(record);
  await expect(app.locator('.msg')).toContainText(`已載入 ${name}`, { timeout: 15_000 });
  const loaded = (await app.locator('.msg').textContent()) ?? '';
  const sheet = await app.locator('.sheet[data-doc="report"]').innerText();
  // IndexedDB:Blob(大小 = 檔案大小),沒有整份文字
  await expect.poll(() => page.evaluate(LAST_RECORD)).toEqual({ name, hasText: false, isBlob: true, size: bytes });

  await page.reload();
  await expect(app.locator('.msg')).toContainText(`已載入 ${name}`, { timeout: 15_000 });
  await expect(app.locator('.msg')).not.toHaveClass(/error/);
  expect(await app.locator('.msg').textContent()).toBe(loaded);
  expect(await app.locator('.sheet[data-doc="report"]').innerText()).toBe(sheet);

  // 舊版(第三十輪以前)的內容:{ name, text }
  const text = fs.readFileSync(record, 'utf8');
  await page.evaluate(
    ([t]) =>
      new Promise<void>((resolve, reject) => {
        const open = indexedDB.open('simosa-brm-report');
        open.onerror = () => reject(open.error);
        open.onsuccess = () => {
          const tx = open.result.transaction('kv', 'readwrite');
          tx.objectStore('kv').put({ name: 'legacy.jsonl', text: t }, 'record:last');
          tx.oncomplete = () => resolve();
          tx.onerror = () => reject(tx.error);
        };
      }),
    [text],
  );
  await page.reload();
  await expect(app.locator('.msg')).toContainText('已載入 legacy.jsonl', { timeout: 15_000 });
  expect(await app.locator('.sheet[data-doc="report"]').innerText()).toBe(sheet);
  await expect.poll(() => page.evaluate(LAST_RECORD)).toEqual({ name: 'legacy.jsonl', hasText: false, isBlob: true, size: bytes });
  errors.expectNone();
});

test('大檔讀取中主執行緒有讓出:狀態列畫得出小於 100% 的進度,計時器照常跑(不是整段停住到讀完)', async ({ page, env }) => {
  // 第三十輪審查 A-rA-2:檔案已在快取時 Blob.stream() 的資料一直備妥,await reader.read() 只經過微任務,整個讀取成為一個長任務
  // (337 MB 的核心紀錄頁面停住 2.2 s,進度只畫得出「100%」)。讀取迴圈每 50 ms 的工作明確讓出一次(lib/record-stream.ts)。
  // 合成一份約 60 MB 的核心 JSON Lines(E02 夾具的狀態行循環複製、改寫 t / tick):解析約 0.4 s 以上,應讓出數次。
  test.setTimeout(120_000);
  const lines = fs.readFileSync(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/record-E02_coastal_traffic_strait-core.jsonl'), 'utf8').split('\n').filter((l) => l.trim());
  const header = lines.find((l) => l.startsWith('{"kind":"header"'))!;
  const states = lines.filter((l) => l.startsWith('{"kind":"state"')).map((l) => JSON.parse(l) as { kind: string; state: { t: number; tick: number } });
  expect(states.length).toBeGreaterThan(0);
  const record = path.join(OUT, 'tmp', 'report-big-record.jsonl');
  fs.mkdirSync(path.dirname(record), { recursive: true });
  const fd = fs.openSync(record, 'w');
  const TARGET_BYTES = 60 * 1024 * 1024;
  let bytes = fs.writeSync(fd, `${header}\n`);
  let count = 0;
  for (let k = 1; bytes < TARGET_BYTES; ) {
    const batch: string[] = [];
    for (let i = 0; i < 500; i++, k++) {
      const s = states[k % states.length]!;
      s.state.tick = 2 * k;
      s.state.t = 0.04 * k;
      batch.push(JSON.stringify(s));
    }
    count += batch.length;
    bytes += fs.writeSync(fd, `${batch.join('\n')}\n`);
  }
  fs.closeSync(fd);
  const name = path.basename(record);
  const errors = new PageErrors(page);
  await page.goto(reportUrl(env));
  const app = page.locator('brm-report-app');
  await expect(app).toContainText('開啟檔案');
  // 頁面內:每個畫格(requestAnimationFrame)記下狀態列的讀取進度,50 ms 的計時器記錄最長的停頓
  await page.evaluate(() => {
    const w = window as unknown as { __seen: number[]; __gaps: number[]; __stop: () => void };
    w.__seen = [];
    w.__gaps = [];
    let last = performance.now();
    const iv = setInterval(() => {
      const now = performance.now();
      w.__gaps.push(now - last);
      last = now;
    }, 50);
    let raf = 0;
    const frame = (): void => {
      const m = /… (\d+)%/.exec(document.querySelector('brm-report-app .msg')?.textContent ?? '');
      if (m && w.__seen[w.__seen.length - 1] !== Number(m[1])) w.__seen.push(Number(m[1]));
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
    w.__stop = () => {
      clearInterval(iv);
      cancelAnimationFrame(raf);
    };
  });
  await app.locator('input.file-input').setInputFiles(record);
  await expect(app.locator('.msg')).toContainText(`已載入 ${name}`, { timeout: 60_000 });
  await expect(app.locator('.msg')).toContainText(`狀態 ${count} 筆`);
  const { seen, maxGap, ticks } = await page.evaluate(() => {
    const w = window as unknown as { __seen: number[]; __gaps: number[]; __stop: () => void };
    w.__stop();
    return { seen: w.__seen, maxGap: Math.round(Math.max(0, ...w.__gaps)), ticks: w.__gaps.length };
  });
  const partial = seen.filter((p) => p < 100);
  expect(partial.length, `讀取中各畫格看到的進度 [${seen.join(', ')}](計時器 ${ticks} 次,最長停頓 ${maxGap} ms)`).toBeGreaterThanOrEqual(2);
  expect([...seen].sort((a, b) => a - b), '進度遞增').toEqual(seen);
  errors.expectNone();
  fs.rmSync(record, { force: true });
});

test('核心仍在寫入的紀錄(選取後內容持續變動):照樣載入;另存失敗時清掉先前記下的那一份並在狀態列說明,重新整理不會還原成別的練習', async ({ page, env }) => {
  // 第三十輪審查 A-rA-1:把仍在變動的檔案寫進 IndexedDB 時,請求的 onsuccess 先觸發、交易之後才中止(Failed to write blobs),
  // 修正前 record:last 留著前一份紀錄,重新整理後畫面是另一份練習而沒有任何訊息。瀏覽器是否拒絕取決於寫入當下檔案有沒有變動
  // (這裡每 20 ms 附加一行,實測都會拒絕);兩種結果都必須一致:記下的是這一份(沒有說明),或什麼都沒記下(有說明)—— 不可以是前一份。
  test.setTimeout(120_000);
  const dir = path.join(OUT, 'tmp');
  fs.mkdirSync(dir, { recursive: true });
  const earlier = path.join(dir, 'report-earlier-record.jsonl');
  fs.copyFileSync(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/record-E07-core.jsonl'), earlier);
  // 進行中的紀錄:E05 夾具去掉 summary / footer,之後每 20 ms 附加一行狀態
  const src = fs.readFileSync(path.join(ROOT, 'src/Instruments.Web/tests/fixtures/record-E05-core.jsonl'), 'utf8').split('\n').filter((l) => l.trim());
  const body = src.filter((l) => !l.startsWith('{"kind":"summary"') && !l.startsWith('{"kind":"footer"'));
  const lastState = JSON.parse(body.filter((l) => l.startsWith('{"kind":"state"')).at(-1)!) as { state: { t: number; tick: number } };
  const live = path.join(dir, 'report-live-record.jsonl');
  fs.writeFileSync(live, `${body.join('\n')}\n`);
  let k = 0;
  const timer = setInterval(() => {
    k++;
    const s = structuredClone(lastState);
    s.state.tick += 2 * k;
    s.state.t += 0.04 * k;
    fs.appendFileSync(live, `${JSON.stringify(s)}\n`);
  }, 20);
  const errors = new PageErrors(page);
  try {
    await page.goto(reportUrl(env));
    const app = page.locator('brm-report-app');
    await app.locator('input.file-input').setInputFiles(earlier);
    await expect(app.locator('.msg')).toContainText('已載入 report-earlier-record.jsonl', { timeout: 15_000 });
    await expect.poll(() => page.evaluate(LAST_RECORD)).toMatchObject({ name: 'report-earlier-record.jsonl', isBlob: true });

    await app.locator('input.file-input').setInputFiles(live);
    await expect(app.locator('.msg')).toContainText('已載入 report-live-record.jsonl', { timeout: 30_000 });
    await expect(app.locator('.msg')).not.toHaveClass(/error/);
    await expect(app.locator('.msg')).toContainText('E05');
    // 另存的結果(成功或交易中止)要等交易結束:記下的不再是前一份為止
    await expect.poll(async () => ((await page.evaluate(LAST_RECORD)) as { name?: string } | null)?.name ?? null, { timeout: 15_000, message: '最近載入的紀錄不應仍是前一份' }).not.toBe('report-earlier-record.jsonl');
    const last = (await page.evaluate(LAST_RECORD)) as { name?: string } | null;
    const note = '這份紀錄未能另存於瀏覽器';
    if (last === null) await expect(app.locator('.msg')).toContainText(note);
    else {
      expect(last.name).toBe('report-live-record.jsonl');
      await expect(app.locator('.msg')).not.toContainText(note);
    }
    test.info().annotations.push({ type: '另存結果', description: last === null ? '交易中止:已清掉先前的紀錄並顯示說明' : '瀏覽器接受了這份檔案' });

    await page.reload();
    await expect(app).toContainText('開啟檔案');
    await expect.poll(() => page.evaluate(`document.querySelector('brm-report-app')?.ready === true`)).toBe(true);
    const restored = await page.evaluate(`document.querySelector('brm-report-app').recordName`);
    const after = ((await app.locator('.msg').textContent()) ?? '').trim();
    expect(after, '重新整理後的訊息不應提到前一份紀錄').not.toContain('report-earlier-record.jsonl');
    if (last === null) {
      // 常駐設定檔(船上的 WebView2 / Edge):交易中止,什麼都沒記下 → 沒有紀錄可還原,也沒有錯誤
      expect(restored, '另存失敗:重新整理後沒有紀錄可還原(不是前一份)').toBe('');
      await expect(app.locator('.msg')).not.toHaveClass(/error/);
    } else if (restored === '') {
      // 無痕視窗(IndexedDB 只在記憶體,存的是原檔案的參照):檔案又變動了,那一份讀不到 → 中文說明並清掉,下次重新整理不再報錯
      expect(after).toContain('無法還原先前載入的紀錄「report-live-record.jsonl」');
      expect(after).toContain('請重新開啟檔案');
      await expect.poll(() => page.evaluate(LAST_RECORD)).toBeNull();
      await page.reload();
      await expect.poll(() => page.evaluate(`document.querySelector('brm-report-app')?.ready === true`)).toBe(true);
      await expect(app.locator('.msg')).toHaveCount(0);
    } else expect(restored).toBe('report-live-record.jsonl');
    test.info().annotations.push({ type: '重新整理', description: restored === '' ? `沒有還原(${after || '無訊息'})` : `還原 ${restored}` });
    errors.expectNone();
  } finally {
    clearInterval(timer);
  }
  fs.rmSync(live, { force: true });
  fs.rmSync(earlier, { force: true });
});
