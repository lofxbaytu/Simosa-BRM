/// <reference types="vitest/config" />
// Vite 設定:建置輸出與快取一律放在專案根目錄的 build\ 與 .cache\(CLAUDE.md 規定)。
import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(here, '../..');

export default defineConfig({
  // 相對路徑的資產,方便 WebView2 以 file:// 或任意子路徑載入(規劃書第 5.2 節 Instruments.Shell)
  base: './',
  cacheDir: path.join(projectRoot, '.cache/vite/instruments-web'),
  resolve: {
    alias: {
      // 船舶資料直接引用 data\ships\<ID>\particulars.json,與核心共用同一份(CLAUDE.md)
      '@data': path.join(projectRoot, 'data'),
    },
  },
  server: {
    port: 5173,
    strictPort: false,
    // 允許開發伺服器讀取專案根目錄下的 data\(預設只允許套件目錄)
    fs: { allow: [projectRoot] },
  },
  build: {
    outDir: path.join(projectRoot, 'build/web/instruments'),
    emptyOutDir: true,
    sourcemap: true,
    target: 'es2022',
    rollupOptions: {
      // 進入點:index.html(駕駛台儀器)與 instructor.html(教官站 / 簡化教官模式 / 重播檢視),共用程式庫與主題
      input: {
        index: path.join(here, 'index.html'),
        instructor: path.join(here, 'instructor.html'),
        // 雷達 PPI 顯示頁(Sensors.Radar WebSocket;RADAR.md)
        radar: path.join(here, 'radar.html'),
        // 講評報告、行為標記評估表、個人訓練紀錄與課程班次紀錄(列印 / 另存 PDF;README「講評報告頁」)
        report: path.join(here, 'report.html'),
      },
    },
  },
  test: {
    include: ['tests/**/*.test.ts'],
    environment: 'node',
  },
});
