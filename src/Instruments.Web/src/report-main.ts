// 講評報告頁進入點(report.html;規劃書第 8.2、8.4、9.1、9.2 節):講評報告、行為標記評估表、個人訓練紀錄與課程班次紀錄。
// theme.css 由 report.html 載入;report.css 含 @media print(A4)列印樣式。
import { ensureFavicon } from './lib/favicon.js';
// 檔案拖到頁面上不接受檔案的地方時,不讓瀏覽器把整個頁面換成那個檔案(lib/file-drop-guard.ts)
import { installFileDropGuard } from './lib/file-drop-guard.js';
ensureFavicon();
installFileDropGuard();
import './report/report.css';
import './report/report-app.js';
