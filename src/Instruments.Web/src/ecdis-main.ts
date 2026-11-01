// 簡化 ECDIS 頁進入點:註冊 <brm-ecdis-app>。theme.css 由 ecdis.html 載入(與儀器頁、教官站、雷達頁共用)。
// 網址參數:?ws=ws://host:8765(狀態服務)、?demo=1(示範模式)、?theme=day|dusk|night、?ship=FSB1|FSB2(示範船)、?range=3(初始量程 nm)。
import { ensureFavicon } from './lib/favicon.js';
ensureFavicon();
import './components/ecdis/ecdis-app.js';
