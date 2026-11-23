// 端對端測試的全域啟動:以 dotnet 啟動真實的 SimCore.Host(E05、--record、--no-udp --no-nmea、隨機埠)、Sensors.Radar.Host(X/S 頻)與
// simosa-web(提供 build/web/instruments 的 Vite 建置輸出;--status-file 指到本輪寫的發行版狀態檔,與安裝版相同:教官站頁首有「發行版 <版本>」),
// 埠與路徑寫進 process.env 與 build/e2e/env.json 供各 worker 讀取。
// 需先:pnpm build(src/Instruments.Web)與 dotnet build SimosaBRM.sln -c Release(tools/run-e2e.sh 一鍵完成)。
// 另啟動第二個 simosa-web 當作「船上筆電」的網頁伺服(不同的埠與狀態檔;第三十一輪:教官筆電的教官站跨來源向船上取發行版狀態)。
// 埠:E2E_PORT_BASE 給定時依序使用 base..base+5(ws、radar、web、vhf、media、船上網頁),否則向作業系統取空埠。
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
  for (const f of ['simcore.log', 'radar.log', 'web.log', 'ship-web.log']) fs.rmSync(path.join(logsDir, f), { force: true });

  const base = process.env['E2E_PORT_BASE'] ? Number(process.env['E2E_PORT_BASE']) : 0;
  const [wsPort, radarPort, webPort, vhfPort, mediaPort, shipWebPort] = base
    ? [base, base + 1, base + 2, base + 3, base + 4, base + 5]
    : [await freePort(), await freePort(), await freePort(), await freePort(), await freePort(), await freePort()];
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

  // 3) 靜態網頁(simosa-web;與船上/教官筆電相同的伺服方式)。發行版狀態檔:安裝版由啟動腳本在啟動網頁伺服前以 simosa-pkg status --json 寫出
  //   (build/run/release-status.json);這裡寫一份同樣形式的檔(發行版取 VERSION、另套用過一個比它新的軟體更新包、沒有降版;
  //   另帶網頁用不到的欄位,simosa-web 不得送出),讓全部的版面案例都在頁首有「軟體更新 <版次>」的狀態下量測
  //   (船上靠更新包升級,這是常態;它比沒套用過更新包時的「發行版 <版本>」寬一個字)。沒有這個檔時 simosa-web 回各欄 null(教官站不顯示;02-instructor 有一個案例)
  const statusFile = path.join(OUT, 'release-status.json');
  const releaseVersion = fs.readFileSync(path.join(ROOT, 'VERSION'), 'utf8').trim();
  // 軟體更新包版次:VERSION 的最後一段加 1(一定比發行版新)
  const softwareVersion = releaseVersion.replace(/\d+$/, (n) => String(Number(n) + 1));
  fs.writeFileSync(
    statusFile,
    JSON.stringify(
      {
        type: 'statusResult',
        shipId: 'FSB1',
        deviceId: 'E2E-SECRET-DEVICE',
        components: { software: { version: softwareVersion, packageId: 'E2E-SECRET-PACKAGE', summary: 'E2E-SECRET-SUMMARY' } },
        trustedKeys: [{ keyId: 'e2e-secret-key', name: 'E2E 金鑰', role: 'primary', holder: 'E2E 保管人' }],
        environment: { os: 'E2E-SECRET-OS', dotnet: '.NET 8', collectedUtc: new Date(t0).toISOString() },
        warnings: ['E2E-SECRET-WARNING'],
        releaseVersion,
      },
      null,
      2,
    ),
  );
  const web = startBg('simosa-web', path.join(logsDir, 'web.log'), dotnet, [webDll!, '--root', webDir!, '--port', String(webPort!), '--bind', '127.0.0.1', '--status-file', statusFile, '--quiet']);
  await waitForPort(web, webPort!, 20_000);

  // 3b) 第二個 simosa-web:當作「船上筆電」的網頁伺服(教官筆電的教官站跨來源向它取 /_status/release.json;02-instructor 的「教官筆電顯示船上筆電的版本…」)。
  //   狀態檔一開始與上面那一份相同(兩台同一版);案例自己改寫、結束時還原。不加 --quiet:每個請求在 ship-web.log 留一行
  //   (案例據此確認瀏覽器沒有送 preflight、伺服器看到的 Origin 是教官站頁面的來源)。其他案例不會連到它(頁面與核心同在 127.0.0.1,不發跨來源請求)
  const shipStatusFile = path.join(OUT, 'ship-release-status.json');
  fs.copyFileSync(statusFile, shipStatusFile);
  const shipWeb = startBg('simosa-web(船上)', path.join(logsDir, 'ship-web.log'), dotnet, [webDll!, '--root', webDir!, '--port', String(shipWebPort!), '--bind', '127.0.0.1', '--status-file', shipStatusFile]);
  await waitForPort(shipWeb, shipWebPort!, 20_000);

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
    statusFile,
    releaseVersion,
    softwareVersion,
    shipWebPort: shipWebPort!,
    shipStatusFile,
    startedUtc: new Date(t0).toISOString(),
    pids: { host: host.child.pid ?? 0, radar: radar?.child.pid ?? 0, web: web.child.pid ?? 0, shipWeb: shipWeb.child.pid ?? 0 },
  };
  fs.writeFileSync(ENV_FILE, JSON.stringify(env, null, 2));
  exportEnv(env);
  console.log(
    `[e2e] 核心 ws://127.0.0.1:${wsPort}(VHF ${vhfPort}、錄影 ${mediaPort})、雷達 ${radarPortUsed || '略過'}、網頁 http://127.0.0.1:${webPort}/(${path.relative(ROOT, webDir!)})、「船上」網頁 ${shipWebPort};` +
      `啟動 ${((Date.now() - t0) / 1000).toFixed(1)} s;輸出 ${path.relative(ROOT, OUT)}/`,
  );
}
