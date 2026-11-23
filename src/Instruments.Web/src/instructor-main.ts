// 教官站進入點:註冊 <brm-instructor-app>。theme.css 由 instructor.html 載入(與儀器頁共用)。
import { ensureFavicon } from './lib/favicon.js';
// 頁首的「發行版 <版本>」與降版提醒:向同源的 simosa-web 取 /_status/release.json;頁面與核心不在同一台電腦時另外跨來源取船上筆電的
//(必須排在 instructor-app 之前;lib/release-status-enable.ts)
import './lib/release-status-enable.js';
ensureFavicon();
import './instructor-app.js';
