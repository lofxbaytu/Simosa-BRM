// 啟用教官站頁首的發行版狀態請求(lib/release-status.ts):只由教官站的進入點 instructor-main.ts 匯入,
// 而且排在 instructor-app 之前 — 模組依匯入順序執行,元件第一次連上頁面時 provider 已經換好。
// load:本機(同源的 /_status/release.json);loadShip:船上筆電(跨來源;頁面與核心不在同一台電腦時才會被呼叫)。
import { browserReleaseStatus, browserShipReleaseStatus, releaseStatusProvider } from './release-status.js';

releaseStatusProvider.load = browserReleaseStatus;
releaseStatusProvider.loadShip = browserShipReleaseStatus;
