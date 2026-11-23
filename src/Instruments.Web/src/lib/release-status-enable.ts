// 啟用教官站頁首的發行版狀態請求(lib/release-status.ts):只由教官站的進入點 instructor-main.ts 匯入,
// 而且排在 instructor-app 之前 — 模組依匯入順序執行,元件第一次連上頁面時 provider 已經換好。
import { browserReleaseStatus, releaseStatusProvider } from './release-status.js';

releaseStatusProvider.load = browserReleaseStatus;
