// 雷達 PPI 頁進入點:註冊 <brm-radar-app>。theme.css 由 radar.html 載入(與儀器頁、教官站共用)。
// 網址參數:?radar=x|s(選台)、?ws=ws://host:8766(服務位址)、?demo=1(示範模式)、?fps=30(重繪上限)、?perf=1(效能顯示)。
import { ensureFavicon } from './lib/favicon.js';
// 檔案拖到頁面上不接受檔案的地方時,不讓瀏覽器把整個頁面換成那個檔案(lib/file-drop-guard.ts)
import { installFileDropGuard } from './lib/file-drop-guard.js';
ensureFavicon();
installFileDropGuard();
import './radar/radar-app.js';
