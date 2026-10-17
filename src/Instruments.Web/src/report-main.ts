// 講評報告頁進入點(report.html;規劃書第 8.2、8.4、9.1、9.2 節):講評報告、行為標記評估表、個人訓練紀錄與課程班次紀錄。
// theme.css 由 report.html 載入;report.css 含 @media print(A4)列印樣式。
import { ensureFavicon } from './lib/favicon.js';
ensureFavicon();
import './report/report.css';
import './report/report-app.js';
