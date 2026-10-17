// 雷達 PPI 頁進入點:註冊 <brm-radar-app>。theme.css 由 radar.html 載入(與儀器頁、教官站共用)。
// 網址參數:?radar=x|s(選台)、?ws=ws://host:8766(服務位址)、?demo=1(示範模式)、?fps=30(重繪上限)、?perf=1(效能顯示)。
import { ensureFavicon } from './lib/favicon.js';
ensureFavicon();
import './radar/radar-app.js';
