# 練習錄影管線(教官站畫面錄影 → 核心 → 紀錄包)

> 需求:R6「聲音/影像擷取」、R13「紀錄包與錄影同步與備份、錄影上限」(規劃書第 2.7 節;第 14 章「船上部署」列);決策 D1(完全離線)。
> 狀態:**已實作(軟體)**;實機需確認畫質/幀率、磁碟用量與 Windows 畫面擷取授權(`docs/驗收/需求追溯表.md` R6/R13)。
> 取代原規劃的 OBS 外部錄影:不需安裝第三方軟體,錄影檔直接落在核心的紀錄目錄並隨紀錄包簽章回傳。語音對講(Mumble)不在本文件範圍。

## 1. 範圍與原則

- 錄的是**教官站瀏覽器的畫面**(整個螢幕、視窗或分頁,由瀏覽器的分享對話框決定),可一併擷取**系統音訊**(分享對話框勾「分享系統音訊」;Windows + Edge/Chrome 分享整個螢幕時可用)。學員站(操船台/雷達)不錄;要錄操船台畫面時在船上筆電以同一套件開一個教官站分頁錄即可(站別 `instructor-simple`)。
- 完全離線:只用教官站 → 核心主機的 HTTP(直連網段或本機),不需任何外部服務。
- **不改核心紀錄檔、不進狀態雜湊**:錄影與紀錄的關聯寫在錄影旁檔(紀錄檔名 + `header.createdUtc`),由 Tools.Packaging 在打包時關聯;既有紀錄、夾具、黃金測試不受影響。
- 片段**即時上傳**(每 2 s 一片),教官筆電不留檔:筆電遺失/拔線時船上已有上限內的完整片段;核心結束時自動定稿尚未 stop 的錄影。
- 上限:核心每段錄影 `--media-max-mb`(預設 2048 MB);紀錄包納入單段錄影 `mediaMaxMb`(預設 2048 MB);磁碟剩餘 < 256 MB 時拒絕開始。

## 2. 架構

```
教官站 instructor.html                      船上核心 SimCore.Host(同一程序,獨立 HttpListener)
┌──────────────────────────────┐            ┌───────────────────────────────────────────────┐
│ brm-recording-panel          │            │ MediaUploadServer  http://0.0.0.0:8768/media/  │
│  開始/停止、狀態、MB、設定     │            │  POST start  → <mediaId>.webm.part 建立        │
│ lib/screen-recording.ts      │  HTTP      │  POST chunk/<id> (X-Seq) → 追加寫入、SHA-256    │
│  getDisplayMedia+MediaRecorder│ ────────▶ │  POST stop/<id>  → 改名 .webm、寫 <id>.json     │
│  上傳佇列(重試、緩衝上限)     │            │  只接受本機/直連私網(同 shutdown 指令)          │
└──────────────────────────────┘            └───────────────────────────────────────────────┘
                                                   build/records/media/<mediaId>.webm + .json
                                                              │
                                              simosa-pkg records pack(Tools.Packaging)
                                                   .sbrec: records/media/*.webm + *.json
                                                   manifest.media[]、exercises[].media[]
```

教官站取得上傳端網址:`instructor.html?media=http://<核心 IP>:8768` 明示,否則由 `?ws=` 的主機推得(`ws://192.168.77.11:8765` → `http://192.168.77.11:8768`)。

## 3. 核心端 `MediaUploadServer`(`src/SimCore.Host/MediaUploadServer.cs`)

| 選項 | 預設 | 說明 |
|---|---|---|
| `--media-port` | 8768 | HttpListener 前綴 `http://*:8768/`;Windows 無 URL ACL 時退回 `localhost`(同 8765/8766;`netsh http add urlacl url=http://*:8768/ user=Everyone`) |
| `--no-media` | — | 不啟動 |
| `--media-dir` | `build/records/media` | 必須在專案資料夾內(CLAUDE.md) |
| `--media-max-mb` | 2048 | 每段錄影上傳上限;超過回 413 並停止接收 |

端點(回應一律帶 CORS 標頭;`OPTIONS` 回 204,因教官站網頁由 8080 靜態伺服、跨埠 fetch 需 preflight):

| 端點 | 請求 | 回應 |
|---|---|---|
| `POST /media/start` | JSON `{exerciseId, recordPath?, station, mime}` | `200 {mediaId, file, maxBytes, record}`;磁碟不足 `507`;來源不允許 `403` |
| `POST /media/chunk/<mediaId>` | 二進位 body,標頭 `X-Seq`(0 起連續) | `200 {seq, bytes}`;重送已收序號 `200 {duplicate:true}`(教官站未收到回應而重試時不重複寫);跳號 `409 {expectedSeq}`;累計超過上限 `413 {truncated:true}`(之後任何片段都 413);已停止 `410`;未知 `404`;單片 > 64 MB `413` |
| `POST /media/stop/<mediaId>` | 可選 JSON `{durationS?, reason?}` | `200` 旁檔內容(重複 stop 回同一份);未知 `404` |
| `GET /media/status` | — | 進行中的錄影(mediaId、bytes、chunks、startedUtc、record) |

- `mediaId` = `<yyyyMMdd-HHmmss>-<station>-<exerciseId 安全化>-<6 hex>`;檔名 `<mediaId>.webm`,上傳中為 `.webm.part`。
- 來源過濾與 `shutdown` 指令相同(`HostCommandRouter.IsAllowedShutdownClient`:loopback、10/8、172.16/12、192.168/16、169.254/16、IPv6 link-local/ULA)。
- 開始時關聯紀錄:教官站給 `recordPath`(須在專案根目錄內)優先,否則取主控程序**正在寫**的紀錄(`HostCommandRouter.Recorder`,已關閉者不算);讀該檔第一行 header 的 `createdUtc`、`scenario.id`(`RecordWriter` 以 `FileShare.Read` 開檔,可同時讀)。
- 主控程序結束(Ctrl+C、停止檔、shutdown 指令、`--duration`)時先停錄影上傳端:進行中的錄影定稿為 `.webm` + 旁檔(`stopReason: hostShutdown`),再關閉紀錄寫 footer。

旁檔 `<mediaId>.json`(`src/SimCore/Recording/MediaSidecar.cs`;SimCore.Host 寫、Tools.Packaging 讀同一型別):

```json
{
  "format": "simosa-media/1", "mediaId": "20261017-020005-instructor-E05_pilot_traffic@2026-10-17T02_00_00Z-9f8e7d",
  "file": "20261017-020005-instructor-….webm", "exerciseId": "E05_pilot_traffic@2026-10-17T02:00:00.000Z",
  "station": "instructor", "mime": "video/webm;codecs=vp9,opus",
  "startedUtc": "2026-10-17T02:00:05Z", "stoppedUtc": "2026-10-17T03:00:07Z",
  "durationS": 3601.2, "durationEstimated": false, "sizeBytes": 734003200, "chunks": 1801,
  "sha256": "…", "truncated": false, "maxBytes": 2147483648, "stopReason": "client",
  "record": { "file": "20261017-015959-E05_pilot_traffic.jsonl", "path": "build/records/20261017-015959-E05_pilot_traffic.jsonl",
              "createdUtc": "2026-10-17T01:59:59.123Z", "scenarioId": "E05_pilot_traffic" }
}
```

`durationS` 以教官站 MediaRecorder 量到的長度為準(stop 時帶上),未提供時取牆鐘並標 `durationEstimated`。`sha256` 為錄影檔內容的 SHA-256(紀錄包 manifest 的 `files[]` 雜湊與之相同)。

## 4. 紀錄包(`src/Tools.Packaging/Records/ShipRecordManager.cs`、`RecordInspector.cs`;`packaging-sync.md` 第 3.3 節)

- `records pack` 的候選檔案新增 `media/*.json`(`media-meta`)與**已有旁檔**的 `media/*.webm`(`media`);`.part` 與沒有旁檔的 `.webm` 視為錄影進行中不打包(旁檔寫入失敗者 2 分鐘後仍打包並在摘要標 `error`)。
- 單段錄影超過 `packaging.json` `mediaMaxMb`(預設 2048;`records pack --media-max-mb <MB>` 覆寫)時**只放旁檔**,`manifest.media[]` 與 `exercises[].media[]` 標 `omitted: true` 與 `omitReason`;錄影留在船上,`records list` / `status` 與同步回報警告(`MediaOverLimit`),可放寬上限後再 `pack` 補送(摘要仍自船上旁檔讀出),或以外接碟人工搬移。
- `manifest.exercises[].media[]`:旁檔 `record.file` 等於練習檔名,否則 `record.createdUtc` 與練習 `startUtc` 相同(容差 1 s);對應不到的只列在 `manifest.media[]`。欄位 `{mediaId, file, sidecar, station, mime, startUtc, endUtc, durationS, durationEstimated, sizeBytes, sha256, truncated, omitted, omitReason, record, recordCreatedUtc, scenarioId, error}`。
- 教官端 `store import` 解開到 `records/<船>/<年>/<packageId>/media/`;雜湊核對與其他檔案相同。`simosa-pkg inspect <檔.sbrec>` 於 manifest 後列出「練習 n 個、錄影 n 段(包內/只有旁檔)」與每段錄影的開始、長度、大小、站別、對應紀錄、截斷/未納入原因。
- 已備份回報:錄影檔與其旁檔一起刪除;錄影檔仍在船上(超過上限未納入)時**保留旁檔**(`CleanupResult.retainedFiles`),待錄影檔打包回傳後再一併刪除。

## 5. 教官站(`src/Instruments.Web/src/lib/screen-recording.ts`、`components/instructor/recording-panel.ts`)

- 控制器 `ScreenRecordingController`(依賴以 `ScreenRecordingDeps` 注入:`fetch`、`getDisplayMedia`、`createRecorder`、`isTypeSupported`、`now`、`delay`;vitest 以假物件測試):
  - 狀態機 `idle → starting → recording → stopping → idle`,失敗 `error`(可再 start)。
  - `start`:`getDisplayMedia({video:{frameRate:15}, audio})` → `POST start` → `MediaRecorder.start(2000 ms)`;MIME 依序挑 `vp9,opus` / `vp8,opus` / `video/webm`。
  - 上傳佇列:同一時間只傳一片、`X-Seq` 連續;網路錯誤/5xx 指數退避重試(0.5 s × 2ⁿ,最長 10 s,最多 8 次);`409/404/410` 致命;`413` → 標 `truncated`、停止錄影並送 stop(`reason: limit`);尚未上傳的片段(離線緩衝)超過 64 MB → 停止錄影並標錯誤(不丟片段,避免 webm 損壞)。
  - `stop`:`MediaRecorder.stop` → 等最後一片與 `onstop`(上限 5 s)→ 佇列送完 → `POST stop {durationS, reason}` → 以核心旁檔做 `RecordingSummary {mediaId, file, station, startedUtc, stoppedUtc, durationS, uploadedBytes, chunks, truncated, error?, record?, sha256?}`。
  - 使用者按瀏覽器的「停止分享」:track `ended` → 視同 stop(`reason: shareEnded`)。
- 面板 `<brm-recording-panel>`:開始/停止、狀態燈、已錄時間、已上傳 MB、緩衝 MB、重試次數、錯誤/截斷訊息、設定「開始練習時自動開始(結束時停止)」「含系統音訊」(localStorage `simosa-brm:instructor:recording`)、本工作階段的錄影清單;瀏覽器不支援(非 Edge/Chrome、非 `http://127.0.0.1`/https 安全來源)時停用並提示。完整教官站放在左欄執行控制下方;簡化模式簡報頁與練習中(精簡)都有;標題列錄影中顯示「● 錄影中」。
- 自動開始:完整教官站於載入情境成功(`finishLoad`)、簡化模式於「開始練習」開始;`exerciseEnded` 事件或簡化模式「結束練習」停止。
- 教官站紀錄(`SessionRecord` version 4)新增 `recordings[]`;重播解析 `header.recordings`;講評報告第一節「畫面錄影」表列出檔名、開始、長度、大小、狀態(`report-views.ts`);核心 JSON Lines 紀錄沒有此資訊(錄影在紀錄包 `records/media/` 與旁檔)。

## 6. 磁碟與頻寬估計

VP9 1080p 15 fps 螢幕內容約 1.5–3 Mb/s → 每小時 0.7–1.3 GB;`--media-max-mb 2048` 約 1.5–3 小時。船上筆電每週 10 小時練習約 10 GB;紀錄包回傳後經已備份回報刪除。直連 1 Gb/s 下 2 s 一片(≈ 0.5 MB)可即時上傳;教官筆電離線緩衝 64 MB ≈ 3–4 分鐘斷線。

## 7. 安全與限制

- **使用者手勢**:Chromium/Edge/WebView2 要求 `getDisplayMedia` 在使用者手勢(transient activation,約 5 s 內)中呼叫。簡化模式的「開始練習」按鈕符合;完整模式「開始練習時自動開始」由核心 ack 觸發,若 ack 晚於點擊「載入」約 5 s 會以「取得畫面失敗」結束,此時請手動按「開始錄影」(或以企業原則 `ScreenCaptureWithoutGestureAllowedForOrigins` 放寬);實機驗收列含此項。

- 來源只限本機/私有網段;無驗證(與 WebSocket 指令相同的信任模型:實體直連、網路線)。
- 錄影內容含學員畫面與語音(個資):與紀錄相同處理(紀錄包簽章、岸上加密外接碟備份、船上收到回報才刪除;`packaging-sync.md` 第 8 節)。
- 已知限制:不錄學員站語音(VHF 對講另案);錄影與核心紀錄的時間對齊只到秒(旁檔 `startedUtc` 與紀錄 `createdUtc`);瀏覽器分享對話框需人工選擇畫面/勾選系統音訊(Windows 無法由網頁自動授權);Firefox 不支援系統音訊;WebView2 外殼內的教官站同 Edge。

## 8. 測試

- `tests/SimCore.Tests/MediaUploadTests.cs`:HttpClient 三片段 → 檔案內容與旁檔(含紀錄關聯)正確;序號重送/跳號;413 上限與 truncated;拒絕外部來源(403);CORS preflight 與 status;主控程序結束定稿;選項解析與 `--media-dir` 根目錄檢查。
- `tests/Tools.Packaging.Tests/RecordPackageTests.cs`:錄影 + 旁檔打包、`exercises[].media` 關聯(檔名/createdUtc)、匯入解開、`inspect` 輸出、回報後刪除;超過上限只放旁檔並標 omitted、旁檔保留、放寬上限補打包、第二次回報一併刪除。
- vitest:`screen-recording`(狀態機、序號、重試、413、緩衝上限、致命錯誤、開始失敗、停止分享、工具)、`recording-panel`、`report-recordings`(紀錄 v4 → 重播 → 報告;教官站整合:面板/徽章/紀錄 JSON、簡化模式自動開始/停止)。
