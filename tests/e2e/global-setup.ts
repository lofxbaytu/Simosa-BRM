// 端對端測試的全域啟動:以 dotnet 啟動真實的 SimCore.Host(E05、--record、--no-udp --no-nmea、隨機埠)、Sensors.Radar.Host(X/S 頻)與
// simosa-web(提供 build/web/instruments 的 Vite 建置輸出),埠與路徑寫進 process.env 與 build/e2e/env.json 供各 worker 讀取。
// 需先:pnpm build(src/Instruments.Web)與 dotnet build SimosaBRM.sln -c Release(tools/run-e2e.sh 一鍵完成)。
// 埠:E2E_PORT_BASE 給定時依序使用 base..base+4(ws、radar、web、vhf、media),否則向作業系統取空埠。
import fs from 'node:fs';
import path from 'node:path';
import { ENV_FILE, OUT, ROOT, exportEnv, type E2eEnv } from './support/env.ts';
import { dotnetExe, findDll, freePort, startBg, waitForPort } from './support/processes.ts';

export default async function globalSetup(): Promise<void> {
  const t0 = Date.now();
  const webDir = [path.join(ROOT, 'build', 'web', 'instruments'), path.join(ROOT, 'app', 'web')].find((d) => fs.existsSync(path.join(d, 'index.html')));
  const hostDll = findDll('SimCore.Host', 'SimosaBRM.SimCore.Host.dll', 'simcore');
  const radarDll = findDll('Sensors.Radar.Host', 'SimosaBRM.Sensors.Radar.Host.dll', 'radar');
  const webDll = findDll('StaticWeb', 'simosa-web.dll', 'web-server');
  const missing: string[] = [];
  if (!webDir) missing.push('網頁建置輸出 build/web/instruments/index.html(cd src/Instruments.Web && pnpm build)');
  if (!hostDll) missing.push('build/bin/SimCore.Host/Release/net8.0/SimosaBRM.SimCore.Host.dll(dotnet build SimosaBRM.sln -c Release)');
  if (!webDll) missing.push('build/bin/StaticWeb/Release/net8.0/simosa-web.dll(dotnet build SimosaBRM.sln -c Release)');
  if (missing.length > 0) throw new Error(`端對端測試前置條件不足(或直接執行 bash tools/run-e2e.sh):\n  - ${missing.join('\n  - ')}`);

  const recordsDir = path.join(OUT, 'records');
  const mediaDir = path.join(OUT, 'media');
  const logsDir = path.join(OUT, 'logs');
  // 本輪輸出從乾淨目錄開始(紀錄/錄影檔以「本輪產生」為斷言依據);報告與 trace 由 Playwright 自行覆寫
  for (const d of [recordsDir, mediaDir]) {
    fs.rmSync(d, { recursive: true, force: true });
    fs.mkdirSync(d, { recursive: true });
  }
  fs.mkdirSync(logsDir, { recursive: true });
  for (const f of ['simcore.log', 'radar.log', 'web.log']) fs.rmSync(path.join(logsDir, f), { force: true });

  const base = process.env['E2E_PORT_BASE'] ? Number(process.env['E2E_PORT_BASE']) : 0;
  const [wsPort, radarPort, webPort, vhfPort, mediaPort] = base
    ? [base, base + 1, base + 2, base + 3, base + 4]
    : [await freePort(), await freePort(), await freePort(), await freePort(), await freePort()];
  const stopFile = path.join(OUT, 'simcore.stop');
  fs.rmSync(stopFile, { force: true });
  const recordPath = path.join(recordsDir, 'e2e-E05_pilot_traffic.jsonl');
  const dotnet = dotnetExe();

  // 1) SimCore.Host:E05、紀錄、VHF、錄影;不開 UDP 多播與 NMEA(容器/CI 不需要,也避免與其他測試互擾)
  const host = startBg('SimCore.Host', path.join(logsDir, 'simcore.log'), dotnet, [
    hostDll!,
    '--scenario', 'data/scenarios/E05_pilot_traffic.yaml',
    '--record', recordPath,
    '--port', String(wsPort!),
    '--vhf-port', String(vhfPort!),
    '--media-port', String(mediaPort!),
    '--media-dir', path.relative(ROOT, mediaDir),
    '--no-udp', '--no-nmea',
    '--stop-file', path.relative(ROOT, stopFile),
  ]);
  await waitForPort(host, wsPort!, 45_000);

  // 2) 雷達服務(選配:找不到 dll 時略過,radarPort = 0,雷達案例自行 skip)
  let radar: ReturnType<typeof startBg> | null = null;
  let radarPortUsed = 0;
  if (radarDll) {
    radar = startBg('Sensors.Radar.Host', path.join(logsDir, 'radar.log'), dotnet, [
      radarDll,
      '--sim', `ws://127.0.0.1:${wsPort}/`,
      '--port', String(radarPort!),
      '--localhost-only',
      '--radars', 'x,s',
      '--areas', 'data/areas',
      '--no-nmea',
    ]);
    try {
      await waitForPort(radar, radarPort!, 30_000);
      radarPortUsed = radarPort!;
    } catch (err) {
      console.warn(`[e2e] 雷達服務未啟動,雷達案例將略過:${(err as Error).message}`);
    }
  }

  // 3) 靜態網頁(simosa-web;與船上/教官筆電相同的伺服方式)
  const web = startBg('simosa-web', path.join(logsDir, 'web.log'), dotnet, [webDll!, '--root', webDir!, '--port', String(webPort!), '--bind', '127.0.0.1', '--quiet']);
  await waitForPort(web, webPort!, 20_000);

  const env: E2eEnv = {
    wsPort: wsPort!,
    vhfPort: vhfPort!,
    mediaPort: mediaPort!,
    radarPort: radarPortUsed,
    webPort: webPort!,
    recordsDir,
    recordPath,
    mediaDir,
    logsDir,
    stopFile,
    startedUtc: new Date(t0).toISOString(),
    pids: { host: host.child.pid ?? 0, radar: radar?.child.pid ?? 0, web: web.child.pid ?? 0 },
  };
  fs.writeFileSync(ENV_FILE, JSON.stringify(env, null, 2));
  exportEnv(env);
  console.log(
    `[e2e] 核心 ws://127.0.0.1:${wsPort}(VHF ${vhfPort}、錄影 ${mediaPort})、雷達 ${radarPortUsed || '略過'}、網頁 http://127.0.0.1:${webPort}/(${path.relative(ROOT, webDir!)});` +
      `啟動 ${((Date.now() - t0) / 1000).toFixed(1)} s;輸出 ${path.relative(ROOT, OUT)}/`,
  );
}
