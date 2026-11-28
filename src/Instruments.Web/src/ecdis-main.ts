// 簡化 ECDIS 頁進入點:註冊 <brm-ecdis-app>。theme.css 由 ecdis.html 載入(與儀器頁、教官站、雷達頁共用)。
// 網址參數:?ws=ws://host:8765(狀態服務)、?demo=1(示範模式)、?theme=day|dusk|night、?ship=FSB1|FSB2(示範船)、?range=3(初始量程 nm)。
import { ensureFavicon } from './lib/favicon.js';
// 檔案拖到頁面上不接受檔案的地方時,不讓瀏覽器把整個頁面換成那個檔案(lib/file-drop-guard.ts)
import { installFileDropGuard } from './lib/file-drop-guard.js';
ensureFavicon();
installFileDropGuard();
import './components/ecdis/ecdis-app.js';
