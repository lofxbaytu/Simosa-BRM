// 端對端測試的全域結束:寫停止檔讓 SimCore.Host 優雅結束(寫完紀錄 footer),逾時才 SIGTERM/SIGKILL;雷達與網頁伺服直接結束。
// 核心在 loadScenario 切換情境時另開的紀錄落在預設的 build/records/(RecordWriter.DefaultPath),本輪產生者搬到 build/e2e/records/,
// 讓全部輸出留在 build/e2e/。
import fs from 'node:fs';
import path from 'node:path';
import { ENV_FILE, ROOT, type E2eEnv } from './support/env.ts';
import { alive, stopPid, waitExit } from './support/processes.ts';

export default async function globalTeardown(): Promise<void> {
  if (!fs.existsSync(ENV_FILE)) return;
  const env = JSON.parse(fs.readFileSync(ENV_FILE, 'utf8')) as E2eEnv;
  const started = Date.parse(env.startedUtc) || 0;

  if (env.pids.host && alive(env.pids.host)) {
    fs.mkdirSync(path.dirname(env.stopFile), { recursive: true });
    fs.writeFileSync(env.stopFile, 'e2e teardown\n');
    const graceful = await waitExit(env.pids.host, 8000);
    if (!graceful) {
      console.warn('[e2e] SimCore.Host 未在 8 s 內因停止檔結束,改以 SIGTERM/SIGKILL');
      await stopPid(env.pids.host, 3000);
    }
  }
  await Promise.all([stopPid(env.pids.radar, 3000), stopPid(env.pids.web, 3000)]);
  fs.rmSync(env.stopFile, { force: true });

  // 切換情境後的紀錄(build/records/<yyyyMMdd-HHmmss>-<scenario>.jsonl)→ build/e2e/records/
  const defaultRecords = path.join(ROOT, 'build', 'records');
  if (fs.existsSync(defaultRecords)) {
    for (const f of fs.readdirSync(defaultRecords)) {
      if (!f.endsWith('.jsonl')) continue;
      const full = path.join(defaultRecords, f);
      let st: fs.Stats;
      try { st = fs.statSync(full); } catch { continue; }
      if (!st.isFile() || st.birthtimeMs < started - 1000 || !/-E07_steering_failure\.jsonl$|-E2E_/.test(f)) continue;
      try {
        fs.renameSync(full, path.join(env.recordsDir, f));
      } catch (err) {
        console.warn(`[e2e] 無法搬移 ${f}:${(err as Error).message}`);
      }
    }
  }
}
