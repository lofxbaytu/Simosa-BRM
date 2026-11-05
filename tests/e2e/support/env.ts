// 端對端測試的環境描述(global-setup.ts 寫入 build/e2e/env.json 與 process.env;測試以 e2eEnv() 讀取)。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
/** 專案根目錄(tests/e2e/support/../../..) */
export const ROOT = path.resolve(here, '../../..');
/** 全部輸出的根目錄 build/e2e/ */
export const OUT = path.join(ROOT, 'build', 'e2e');
export const ENV_FILE = path.join(OUT, 'env.json');

export interface E2eEnv {
  /** 狀態 WebSocket 埠(SimCore.Host --port) */
  wsPort: number;
  /** 內建 VHF 轉送埠(--vhf-port) */
  vhfPort: number;
  /** 錄影上傳端埠(--media-port) */
  mediaPort: number;
  /** 雷達服務埠(Sensors.Radar.Host --port);0 = 未啟動 */
  radarPort: number;
  /** 靜態網頁埠(simosa-web --port) */
  webPort: number;
  /** 核心紀錄目錄 build/e2e/records */
  recordsDir: string;
  /** 第一份紀錄(E05)的路徑(--record) */
  recordPath: string;
  /** 錄影目錄 build/e2e/media */
  mediaDir: string;
  /** 各程序 log 目錄 build/e2e/logs */
  logsDir: string;
  /** SimCore.Host 停止檔(--stop-file;teardown 寫入以優雅結束並寫完紀錄 footer) */
  stopFile: string;
  /** 啟動時刻(ISO;teardown 以此找本輪在 build/records 產生的紀錄) */
  startedUtc: string;
  pids: { host: number; radar: number; web: number };
}

export function e2eEnv(): E2eEnv {
  const p = process.env;
  if (p['E2E_WS_PORT']) {
    return {
      wsPort: Number(p['E2E_WS_PORT']),
      vhfPort: Number(p['E2E_VHF_PORT']),
      mediaPort: Number(p['E2E_MEDIA_PORT']),
      radarPort: Number(p['E2E_RADAR_PORT'] ?? 0),
      webPort: Number(p['E2E_WEB_PORT']),
      recordsDir: p['E2E_RECORDS_DIR'] ?? path.join(OUT, 'records'),
      recordPath: p['E2E_RECORD_PATH'] ?? '',
      mediaDir: p['E2E_MEDIA_DIR'] ?? path.join(OUT, 'media'),
      logsDir: p['E2E_LOGS_DIR'] ?? path.join(OUT, 'logs'),
      stopFile: p['E2E_STOP_FILE'] ?? '',
      startedUtc: p['E2E_STARTED_UTC'] ?? '',
      pids: { host: 0, radar: 0, web: 0 },
    };
  }
  if (!fs.existsSync(ENV_FILE)) throw new Error(`找不到 ${ENV_FILE}:global-setup 未執行(請以 playwright test 或 tools/run-e2e.sh 執行)`);
  return JSON.parse(fs.readFileSync(ENV_FILE, 'utf8')) as E2eEnv;
}

/** 把環境寫進 process.env(global-setup 與 worker 共用) */
export function exportEnv(e: E2eEnv): void {
  process.env['E2E_WS_PORT'] = String(e.wsPort);
  process.env['E2E_VHF_PORT'] = String(e.vhfPort);
  process.env['E2E_MEDIA_PORT'] = String(e.mediaPort);
  process.env['E2E_RADAR_PORT'] = String(e.radarPort);
  process.env['E2E_WEB_PORT'] = String(e.webPort);
  process.env['E2E_RECORDS_DIR'] = e.recordsDir;
  process.env['E2E_RECORD_PATH'] = e.recordPath;
  process.env['E2E_MEDIA_DIR'] = e.mediaDir;
  process.env['E2E_LOGS_DIR'] = e.logsDir;
  process.env['E2E_STOP_FILE'] = e.stopFile;
  process.env['E2E_STARTED_UTC'] = e.startedUtc;
}

// ------------------------------------------------------------------ 網址
const host = '127.0.0.1';
export const stateWsUrl = (e: E2eEnv): string => `ws://${host}:${e.wsPort}`;
export const vhfWsUrl = (e: E2eEnv): string => `ws://${host}:${e.vhfPort}/`;
export const mediaHttpUrl = (e: E2eEnv): string => `http://${host}:${e.mediaPort}`;
export const radarWsUrl = (e: E2eEnv): string => `ws://${host}:${e.radarPort}`;
export const webBase = (e: E2eEnv): string => `http://${host}:${e.webPort}`;

function withQuery(base: string, page: string, q: Record<string, string | undefined>): string {
  const u = new URL(`${base}/${page}`);
  for (const [k, v] of Object.entries(q)) if (v !== undefined) u.searchParams.set(k, v);
  return u.toString();
}

/** 操船台 index.html(?ws、?vhf、?station) */
export function bridgeUrl(e: E2eEnv, opts: { station?: string; vhf?: boolean } = {}): string {
  return withQuery(webBase(e), 'index.html', { ws: stateWsUrl(e), vhf: opts.vhf === false ? '0' : vhfWsUrl(e), station: opts.station ?? 'bridge' });
}

/** 教官站 instructor.html(完整模式預設;?mode=simple|replay) */
export function instructorUrl(e: E2eEnv, opts: { mode?: 'simple' | 'replay'; station?: string; vhf?: boolean } = {}): string {
  return withQuery(webBase(e), 'instructor.html', {
    ws: stateWsUrl(e),
    vhf: opts.vhf === false ? '0' : vhfWsUrl(e),
    media: mediaHttpUrl(e),
    ...(opts.mode ? { mode: opts.mode } : {}),
    ...(opts.station ? { station: opts.station } : {}),
  });
}

/** 雷達 PPI 頁 radar.html(?ws 給基底 ws://host:port,頁面補 /radar/<radar>) */
export function radarUrl(e: E2eEnv, radar: 'x' | 's' = 'x'): string {
  return withQuery(webBase(e), 'radar.html', { ws: radarWsUrl(e), radar });
}

/** 講評報告頁 report.html */
export function reportUrl(e: E2eEnv, tab?: string): string {
  return withQuery(webBase(e), 'report.html', tab ? { tab } : {});
}
