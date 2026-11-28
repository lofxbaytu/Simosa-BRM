// 進入點:註冊 <brm-app>。theme.css 由 index.html 載入(全域 CSS 變數,穿透 shadow DOM)。
import { ensureFavicon } from './lib/favicon.js';
// 檔案拖到頁面上不接受檔案的地方時,不讓瀏覽器把整個頁面換成那個檔案(lib/file-drop-guard.ts)
import { installFileDropGuard } from './lib/file-drop-guard.js';
ensureFavicon();
installFileDropGuard();
import './app.js';
