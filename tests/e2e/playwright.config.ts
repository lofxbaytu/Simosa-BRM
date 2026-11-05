// Playwright 端對端測試設定(docs/design/testing.md 第 4 節):
//  - 真實 Chromium(headless)+ 真實 SimCore.Host / Sensors.Radar.Host / simosa-web(global-setup.ts 以 dotnet 啟動,global-teardown.ts 結束);
//  - 所有輸出留在專案內 build/e2e/(test-results、trace、截圖、HTML/JSON 報告、核心紀錄、錄影、各程序 log;CLAUDE.md);
//  - 每案例逾時 60 s、失敗重試 1 次、單一 worker(全部案例共用同一個核心程序,案例依檔名序執行);
//  - 假媒體:--use-fake-device-for-media-stream / --use-fake-ui-for-media-stream(VHF PTT 的麥克風),getDisplayMedia 以 canvas captureStream 取代(specs/05-recording);
//  - Chromium:E2E_CHROMIUM 指定執行檔;未指定時用 Playwright 自己管理的完整版 chromium(channel 'chromium',PLAYWRIGHT_BROWSERS_PATH 可指到
//    /opt/pw-browsers 或 .cache/pw-browsers;tools/run-e2e.sh 會設定)。
import { defineConfig } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../..');
const out = path.join(root, 'build', 'e2e');
const chromiumExe = process.env['E2E_CHROMIUM'];

export default defineConfig({
  testDir: path.join(here, 'specs'),
  outputDir: path.join(out, 'test-results'),
  globalSetup: path.join(here, 'global-setup.ts'),
  globalTeardown: path.join(here, 'global-teardown.ts'),
  timeout: 60_000,
  expect: { timeout: 10_000 },
  retries: 1,
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env['CI'],
  reporter: [
    ['list'],
    ['html', { outputFolder: path.join(out, 'report'), open: 'never' }],
    ['json', { outputFile: path.join(out, 'results.json') }],
  ],
  use: {
    browserName: 'chromium',
    headless: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    viewport: { width: 1600, height: 1000 },
    locale: 'zh-TW',
    timezoneId: 'Asia/Taipei',
    // 麥克風(VHF PTT)預先授權;配合假媒體旗標不會跳出對話框
    permissions: ['microphone'],
    ...(chromiumExe ? {} : { channel: 'chromium' }),
    launchOptions: {
      ...(chromiumExe ? { executablePath: chromiumExe } : {}),
      args: [
        '--use-fake-device-for-media-stream',
        '--use-fake-ui-for-media-stream',
        '--autoplay-policy=no-user-gesture-required',
        '--auto-select-desktop-capture-source=Entire screen',
      ],
    },
  },
  projects: [{ name: 'chromium' }],
});
