// 子程序工具(global-setup / global-teardown 共用):找 dotnet 與建置好的 dll、取空埠、背景啟動並把輸出寫到 build/e2e/logs/、等待埠開啟、結束程序。
import { spawn, type ChildProcess } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { ROOT } from './env.ts';

/** dotnet 執行檔:DOTNET_ROOT/dotnet(/etc/profile.d/dotnet.sh)→ PATH 的 dotnet */
export function dotnetExe(): string {
  const root = process.env['DOTNET_ROOT'];
  if (root) {
    for (const c of [path.join(root, 'dotnet'), path.join(root, 'dotnet.exe')]) if (fs.existsSync(c)) return c;
  }
  if (fs.existsSync('/opt/dotnet/lib/dotnet/dotnet')) return '/opt/dotnet/lib/dotnet/dotnet';
  return 'dotnet';
}

/** 建置輸出 build/bin/<專案>/Release|Debug/net8.0/<dll>(與 tools/launch/launch-common.sh find_dll 相同順序;發行版 app/<元件>/ 亦可) */
export function findDll(project: string, dll: string, appComponent?: string): string | null {
  const candidates = [
    ...(appComponent ? [path.join(ROOT, 'app', appComponent, dll)] : []),
    path.join(ROOT, 'build', 'bin', project, 'Release', 'net8.0', dll),
    path.join(ROOT, 'build', 'bin', project, 'Debug', 'net8.0', dll),
  ];
  return candidates.find((c) => fs.existsSync(c)) ?? null;
}

/** 向作業系統要一個目前空閒的 TCP 埠(127.0.0.1) */
export function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.unref();
    srv.on('error', reject);
    srv.listen(0, '127.0.0.1', () => {
      const addr = srv.address();
      const port = typeof addr === 'object' && addr ? addr.port : 0;
      srv.close(() => resolve(port));
    });
  });
}

/** 埠是否可連線 */
export function portOpen(port: number, host = '127.0.0.1', timeoutMs = 800): Promise<boolean> {
  return new Promise((resolve) => {
    const s = net.connect({ port, host });
    const done = (ok: boolean) => {
      s.removeAllListeners();
      s.destroy();
      resolve(ok);
    };
    s.setTimeout(timeoutMs, () => done(false));
    s.once('connect', () => done(true));
    s.once('error', () => done(false));
  });
}

export interface Spawned {
  name: string;
  child: ChildProcess;
  logPath: string;
  exited: Promise<number | null>;
  exitCode: number | null | undefined;
}

/** 背景啟動(cwd = 專案根目錄),stdout/stderr 追加到 log;與啟動腳本 start_bg 相同的第一行註解 */
export function startBg(name: string, logPath: string, exe: string, args: string[], extraEnv: Record<string, string> = {}): Spawned {
  fs.mkdirSync(path.dirname(logPath), { recursive: true });
  fs.appendFileSync(logPath, `# ${new Date().toISOString()} ${[exe, ...args].join(' ')}\n`);
  const fd = fs.openSync(logPath, 'a');
  const child = spawn(exe, args, {
    cwd: ROOT,
    stdio: ['ignore', fd, fd],
    env: {
      ...process.env,
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      DOTNET_NOLOGO: '1',
      DOTNET_CLI_HOME: process.env['DOTNET_CLI_HOME'] ?? path.join(ROOT, '.cache', 'dotnet-home'),
      NUGET_PACKAGES: process.env['NUGET_PACKAGES'] ?? path.join(ROOT, '.cache', 'nuget', 'packages'),
      SIMOSA_BRM_ROOT: ROOT,
      ...extraEnv,
    },
  });
  const spawned: Spawned = { name, child, logPath, exitCode: undefined, exited: Promise.resolve(null) };
  spawned.exited = new Promise((resolve) => {
    child.once('exit', (code) => {
      spawned.exitCode = code;
      try { fs.closeSync(fd); } catch { /* 已關 */ }
      resolve(code);
    });
    child.once('error', () => {
      spawned.exitCode = -1;
      resolve(-1);
    });
  });
  return spawned;
}

/** 等待埠開啟;程序先結束則丟出錯誤(附 log 最後幾行) */
export async function waitForPort(p: Spawned, port: number, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (p.exitCode !== undefined) throw new Error(`${p.name} 已結束(exit ${p.exitCode}),見 ${p.logPath}:\n${tail(p.logPath)}`);
    if (await portOpen(port)) return;
    await sleep(200);
  }
  throw new Error(`${p.name} 未在 ${timeoutMs / 1000} s 內開啟埠 ${port},見 ${p.logPath}:\n${tail(p.logPath)}`);
}

export function tail(file: string, lines = 8): string {
  try {
    const all = fs.readFileSync(file, 'utf8').split('\n');
    return all.slice(-lines).join('\n');
  } catch {
    return '(無 log)';
  }
}

export const sleep = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));

/** 結束程序:先 SIGTERM(Windows 直接 kill),逾時再 SIGKILL;程序不存在時無動作 */
export async function stopPid(pid: number, graceMs = 5000): Promise<void> {
  if (!pid || !alive(pid)) return;
  try { process.kill(pid, 'SIGTERM'); } catch { return; }
  const deadline = Date.now() + graceMs;
  while (Date.now() < deadline) {
    if (!alive(pid)) return;
    await sleep(100);
  }
  try { process.kill(pid, 'SIGKILL'); } catch { /* 已結束 */ }
}

export function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

/** 等待 pid 結束(最多 timeoutMs);回傳是否已結束 */
export async function waitExit(pid: number, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (!alive(pid)) return true;
    await sleep(100);
  }
  return !alive(pid);
}
