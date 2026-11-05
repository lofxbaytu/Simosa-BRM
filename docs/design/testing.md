# 測試層級與執行方式

> 規劃書第 14 章驗收測試表、第 6.4 節公差;追溯 `docs/驗收/需求追溯表.md` 第 2 節。本文說明儲存庫內四個自動測試層級、各自怎麼跑、常見失敗與不穩(flaky)案例的處理。所有建置輸出、快取與測試紀錄都留在專案資料夾內(`build/`、`.cache/`;CLAUDE.md)。

## 1. 層級總覽

| 層級 | 驗證什麼 | 工具與位置 | 執行時間量級 |
|---|---|---|---|
| **黃金 / 夾具** | 物理與確定性:MMG 核心對試俥/海報目標值(`data/ships/*/trial_targets.json` 公差)、C# 與 Python 參考實作軌跡差異、相同輸入紀錄 → 相同狀態雜湊;全部情境的核心紀錄夾具跨語言(C# 產生 → Python/網頁讀)一致 | `tests/SimCore.Tests/Golden/*.csv`(`export_reference.py` 產生)、`MmgGoldenTests`、`EngineDeterminismTests`、`HashCompatibilityTests`;`tests/fixtures/`(核心紀錄、NMEA);pytest `test_*_fsb1/fsb2`、`test_determinism`;vitest `core-record` | 秒–分 |
| **單元** | 各模組的純邏輯:情境引擎、感測器/警報、雷達/ARPA、封裝簽章、網頁狀態解析/元件/編輯器、Python 校正 | `dotnet test`(SimCore.Tests、Sensors.Radar.Tests、Shell.Core.Tests、Tools.Packaging.Tests、Visual.Shared.Tests…)、`pnpm test`(vitest,happy-dom)、`uv run pytest` | 分 |
| **子程序** | 以真實程序驗證介面與腳本:Host 實跑情境與 `--replay`/`--validate`、雷達 WebSocket 端對端、NMEA 檢驗工具讀真實 Host 輸出、啟動腳本(bash + PowerShell)、發行建置 | `E05IntegrationTests`、`WebSocketEndToEndTests`、`simosa_brm.nmea_check`;`tools/launch/tests/launch-tests.sh`、`tools/run-ps-tests.sh`(pwsh 在 `.cache/dotnet-tools`)、`tools/build-release.sh --skip-godot`、`tools/visual-headless-check.sh` | 分 |
| **端對端(e2e)** | 真實瀏覽器 + 真實核心:Chromium(headless)載入 Vite 建置輸出,連 `SimCore.Host`、`Sensors.Radar.Host`、`simosa-web`,操作教官站/操船台 UI 並以測試端 WebSocket 客戶端交叉驗證狀態幀與事件框 | `tests/e2e/`(Playwright 1.56.1),一鍵 `bash tools/run-e2e.sh` | 約 1 分(14 案例 ≈ 56 s;含建置 ≈ 76 s) |

層級越低越多、越快,也是確定性與數值的最後防線;端對端只覆蓋「頁面與核心真的接得起來」的關鍵路徑,不重複單元測試的細節。

## 2. 黃金與夾具

- **公差**:以 `data/ships/*/trial_targets.json` 為準(No.1 實測、No.2 海報讀值);超差項目在 pytest 以 `xfail` 標示並列於追溯表第 3 節。
- **確定性**:固定步長 50 Hz RK4、亂數只來自情境種子;`EngineDeterminismTests`/`RecordReplayTests` 以相同輸入紀錄重播比對狀態雜湊。**任何改動不得改變既有情境/夾具/黃金測試的數值**;新功能若動到狀態序列,必須先確認雜湊不變或明確更新夾具並記錄原因。
- **夾具再生**:`src/Tools.Calibration` 的 `export_reference.py`、`export_external_reference.py` 產生黃金 CSV;核心紀錄夾具由 SimCore.Tests 的情境測試寫出(見各測試註解)。

## 3. 單元與子程序

```bash
. /etc/profile.d/dotnet.sh                       # 容器/開發機:dotnet 不在 PATH 時
dotnet build SimosaBRM.sln -c Release
dotnet test tests/SimCore.Tests -c Release --no-build   # 其他:Sensors.Radar.Tests、Shell.Core.Tests、Tools.Packaging.Tests、Visual.Shared.Tests…
cd src/Instruments.Web && pnpm test && pnpm exec tsc --noEmit -p .
cd src/Tools.Calibration && uv run pytest -q
bash tools/launch/tests/launch-tests.sh && bash tools/run-ps-tests.sh
```

Windows 以 `tools/setup-windows.ps1` 準備(NuGet/pnpm/uv/Playwright 快取都在 `.cache\`),指令相同。

## 4. 端對端(Playwright)

### 4.1 架構

```
tests/e2e/
  playwright.config.ts   Chromium headless、每案例 60 s、失敗重試 1 次、單一 worker、trace/截圖只在失敗時保留 → build/e2e/
  global-setup.ts        以 dotnet 啟動 SimCore.Host(E05、--record build/e2e/records/…、--vhf-port、--media-port、--media-dir、--no-udp --no-nmea、--stop-file)、
                         Sensors.Radar.Host(X/S;找不到 dll 或埠未開則雷達案例 skip)、simosa-web(tools/launch/StaticWeb 伺服 build/web/instruments);
                         埠向作業系統取空埠(E2E_PORT_BASE 可固定),寫 build/e2e/env.json 與 process.env
  global-teardown.ts     寫停止檔讓 Host 寫完紀錄 footer 再結束;切換情境後落在 build/records/ 的本輪紀錄搬到 build/e2e/records/
  support/env.ts         埠、路徑與各頁網址(bridgeUrl/instructorUrl/radarUrl/ecdisUrl/reportUrl)
  support/ws.ts          測試端核心 WebSocket 客戶端(Node 22 內建 WebSocket):等狀態幀條件、等事件框、送 SimCommand
  support/fixtures.ts    test.extend:env、core;ensureScenario / ensureRunning(案例前置條件,不依賴執行順序);nonBlackRatio(2D 畫布)
  support/pages.ts       waitConnected(header .pill「已連線」)、PageErrors(pageerror / console.error)、Shadow DOM 定位說明
  specs/01–08            案例(見 4.3)
```

與船上部署相同的伺服方式:靜態網頁用 `simosa-web`(不是 vite dev server),核心用 Release 建置的 `SimCore.Host`;不開 UDP 多播與 NMEA(容器/CI 不需要,也避免與其他測試互擾)。Playwright 的 CSS 定位器預設穿透 open shadow root,選擇器直接寫 Lit 元件內部的 class(例如 `brm-scenario-panel button.load`)。

### 4.2 執行

```bash
bash tools/run-e2e.sh                 # 建置網頁(vite)與 .NET(SimCore.Host、StaticWeb、Sensors.Radar.Host)→ 安裝 tests/e2e 套件 → 測試 → 摘要
bash tools/run-e2e.sh --skip-build    # 已建置過
bash tools/run-e2e.sh -- --grep 雷達  # 只跑符合的案例;其他 playwright test 參數照傳
pnpm --dir tests/e2e report           # 開 HTML 報告(build/e2e/report)
```

- Chromium:`E2E_CHROMIUM` 指定執行檔;否則用 Playwright 管理的 chromium(`PLAYWRIGHT_BROWSERS_PATH`:本機容器 `/opt/pw-browsers`,CI 以 `npx playwright install chromium --with-deps` 裝到 `.cache/pw-browsers`)。**不要**在船上/開發機隨意 `playwright install`(會連網際網路);Windows 以 `tools/setup-windows.ps1` 一次備妥。
- 啟動旗標:`--use-fake-device-for-media-stream`、`--use-fake-ui-for-media-stream`(VHF PTT 的假麥克風)、`permissions: ['microphone']`;錄影案例以 `page.addInitScript` 用 `canvas.captureStream` 取代 `getDisplayMedia`(headless 沒有畫面可分享)。
- 輸出:`build/e2e/`(`test-results/` 失敗截圖與 trace、`report/`、`results.json`、`records/` 核心紀錄、`media/` 錄影與旁檔、`logs/` simcore/radar/web);Playwright 的 TypeScript 轉譯快取以 `PWTEST_CACHE_DIR` 指到 `.cache/playwright-transform`(腳本與 CI 皆設定)。
- CI:`.github/workflows/ci.yml` 工作「端對端(Playwright + 真實核心)」(ubuntu;setup-dotnet 8、pnpm、Chromium;失敗時上傳 `build/e2e/test-results`、`report`、`logs` 為 artifact `e2e-failures`)。

### 4.3 案例

| 檔案 | 案例 | 關鍵斷言 |
|---|---|---|
| `01-bridge` | 操船台載入並在 5 s 內顯示 HDG/SOG | `brm-heading .hdg .value` 三位數、`brm-speed` SOG 數值、header「已連線」、無 pageerror/console.error |
| `02-instructor` | 情境清單 → 載入 E07 → 狀態幀情境 id 變更、載入鈕回復 | 清單 ≥ 20 個;核心狀態幀 `scenario.id = E07_steering_failure`;載入鈕回「載入 E07…」、清單「已載入」標籤、header 顯示情境 |
| | 無效內嵌情境 → 教官站顯示核心拒絕 | 以 WebSocket 送 `loadScenario args.scenario`(多邊形 2 點、hardFail 引用不存在區域)→ 事件框 `scenarioLoadRejected`(origin inline、issueCount ≥ 2)→ 面板 `.rejected` 列出問題;核心情境不變 |
| | 下舵令 20° → 操船台舵角指示變化 | 舵輪預設鈕「S 20°」→ 狀態幀 `rudderOrder = 20`;`brm-rudder` 舵令「S 20°」、舵角隨舵機速率到「S 20°」;案例結束回正舵 |
| | 凍結(教官站)→ 事件 frozen、tick 停止;恢復 → 事件 resumed、tick 前進 | 執行控制「凍結」→ 事件框 `frozen`(source instructor)、1.5 s 內 tick 不前進、事件流列出;恢復以 WebSocket 送 `resume`(見下列缺陷)→ 事件 `resumed`、`flags.frozen=false`、tick 前進 |
| | 已知缺陷(`test.fail`):凍結後教官站應顯示「已凍結」且「恢復」鈕可用 | 核心凍結時 `Step` 不 `Advance`/`Broadcast`,沒有任何 `flags.frozen=true` 的狀態幀送達 → header 不顯示「已凍結」、「恢復」鈕停用、藥丸轉「資料逾時」。核心修正後此案例會「意外通過」,屆時移除 `test.fail` |
| `03-vhf` | 文字訊息:同頻道互通、不同頻道不互通 | 教官站 CH16 → 操船台(?station=bridge)CH16 收到(RX、CH16);操船台轉 CH12 後雙向都收不到 |
| | PTT 按住 2 s | 假麥克風 → 面板 TX、對方指示燈「RX bridge CH16」;核心事件 `vhfCall{station: bridge, channel: 16, duration_s ≥ 1.5, endReason: pttEnd}`;教官站事件流與 VHF 面板有通話紀錄 |
| `04-recording` | 錄影開始 3 s 停止 | `--media-dir` 出現唯一新 `.webm`(> 1 KiB、無 `.part`)與旁檔 `.json`(file/station/sizeBytes/chunks/durationS ≥ 2/sha256/truncated=false);面板清單列出檔名 |
| `05-radar` | 雷達頁 PPI 畫布非全黑 | 連線 chip ok;`radar-ppi canvas` 非黑像素 > 1%;雷達服務未啟動時 `test.skip`。雷達服務以 `--localhost-only` 啟動時只接受 `Host: localhost`,頁面網址用 `ws://localhost:<埠>` |
| `06-ecdis` | ECDIS 頁載入、顯示情境航線與自船 | 前置 `ensureScenario(E07)`;header chip「E07… · 航線」、HDG 三位數;航線監控面板航段/DTG/XTD;`ecdis-chart canvas` 非黑像素 > 5% |
| `07-report` | 報告頁開啟最近的核心紀錄 | 本輪最新且已寫 footer 的 `.jsonl`(`build/e2e/records` 或 `build/records`)→ 「已載入 … 狀態 n 筆」、無錯誤訊息、無 console 錯誤 |
| | 教官站「講評報告」→ 新分頁載入交接紀錄 | `report.html?from=instructor` 顯示「已載入 教官站紀錄」 |
| `08-scenario-editor` | 開啟範本、改 Hs、驗證通過、YAML 預覽含 waves | 套用範本 E05 → 勾浪、Hs 2.5 → 「結構檢查通過」、`pre.yaml` 含 `waves:`/`significantHeight_m: 2.5`;Hs −1 → 錯誤且載入鈕停用 |

容器不可行而略過的項目會以 `test.skip(條件, 原因)` 標示並在報告列為 skipped(目前只有雷達服務未啟動一種)。

### 4.4 常見失敗與處理

| 現象 | 原因 | 處理 |
|---|---|---|
| global-setup 丟「前置條件不足」 | 沒建置網頁或 .NET | `bash tools/run-e2e.sh`(不加 `--skip-build`) |
| `SimCore.Host 未在 45 s 內開啟埠` | 建置過期、情境載入失敗、埠被佔 | 看 `build/e2e/logs/simcore.log`;固定埠時改 `E2E_PORT_BASE` |
| 定位器找不到元素 | Lit 元件標籤名與檔名不同(`brm-heading`、`brm-speed`、`brm-rudder`、`brm-helm`、`brm-run-control`、`radar-ppi`、`ecdis-chart`) | 以 `grep customElement(` 查正確標籤;失敗的 `error-context.md`(頁面快照)可對照 |
| `click` 被 `<div class="panel">` 攔截 | 捲動清單裡的項目被其他區塊遮住 | `scrollIntoViewIfNeeded()` 後 `dispatchEvent('click')`(元件以 `@click` 處理) |
| PTT 案例沒有 `transmitting` | 缺假媒體旗標或麥克風權限 | 保留 config 的 `launchOptions.args` 與 `permissions`;自訂 Chromium 時確認支援 fake device |
| 錄影案例 `unsupported` | `getDisplayMedia` 取代腳本未在頁面腳本前注入 | 必須用 `page.addInitScript`(非 `evaluate`);`MediaRecorder` 需 Chromium 完整版(非 headless shell) |
| 雷達案例 skip | `Sensors.Radar.Host` dll 不存在或 30 s 內未開埠 | 建置 `src/Sensors.Radar.Host`;看 `build/e2e/logs/radar.log` |
| 報告頁案例找不到已寫 footer 的紀錄 | E07 載入案例失敗 → E05 紀錄仍在寫 | 案例退回 `--record` 的第一份紀錄;修好 02 後重跑 |
| 雷達頁「未連線」 | 雷達服務 `--localhost-only` 只接受 `Host: localhost`,頁面用了 127.0.0.1 | `support/env.ts` `radarWsUrl` 用 localhost;正式啟動腳本未加 `--localhost-only` 時兩者皆可 |
| 核心無狀態幀、後續案例全部逾時 | 前一案例把核心凍結後失敗未恢復(凍結中不廣播) | `core` fixture 與 `ensureRunning` 會先送 `resume`;凍結案例的 `finally` 一律恢復 |
| 教官站「載入」鈕點不到 | 1600×1000 視窗下左欄情境面板內容超出格列,鈕被時間表面板蓋住(頁面版面問題) | 案例以 `dispatchEvent('click')` 派發;版面修正後可改回真實點擊 |

**flaky 處理原則**:每案例已允許重試 1 次(`retries: 1`);重試後通過的案例在摘要列為「不穩」,**視為缺陷**而非通過 — 先看 `build/e2e/test-results/<案例>/trace.zip`(`pnpm --dir tests/e2e exec playwright show-trace <trace.zip>`)找時序問題,改用「等條件」(`expect(...).toHaveText`、`core.waitState`)取代固定 `waitForTimeout`;只有外部節奏(舵機速率、PTT 持續時間、錄影 3 s)才允許固定等待。案例之間不得依賴執行順序:需要特定情境時用 `ensureScenario`,凍結過要 `ensureRunning`。CI 若因資源不足逾時,先把 `timeout` 放寬到 90 s 並記錄,不要關掉斷言。

## 5. 新增案例的規則

1. 一個案例只驗證一條使用者路徑,斷言落在「核心真的收到/回了什麼」(狀態幀、事件框、檔案)而不只是 UI 文字。
2. 新頁面或新面板先在 vitest 寫元件測試,端對端只加載入與關鍵互動。
3. 不改核心狀態雜湊:端對端只透過既有指令與頁面操作,不加後門。
4. 需要新的 Host 旗標或埠時同步改 `global-setup.ts`、`tools/run-e2e.sh`、本文與 `README.md`「測試」一節。

## 6. 長時間穩定性(soak)

規劃書第 14 章「效能」列:核心 50 Hz 無掉拍、網路延遲 < 50 ms、連續 2 小時(1 次於實機);追溯表第 2 節效能列。`tools/soak-test.sh`(Windows:`tools\soak-test.ps1`,參數相同)把整套服務和合成負載跑一段時間,每 10 s 取樣,結束後產生報告並判定;容器只能證明**軟體面**(50 Hz、延遲量級、記憶體不洩漏),幀率與內顯機 CPU 仍需實機。

### 6.1 組成

```
tools/soak-test.sh         建置(--skip-build 略過)→ 把 build/bin 的三個輸出複製到 build/soak/<run>/bin/(執行中有人 dotnet build 不會覆寫執行中的 dll)
                           → SimCore.Host(--scenario 預設 E05;--record build/soak/<run>/records/;VHF 與錄影端開啟;--no-udp --no-nmea;--stop-file)
                           → Sensors.Radar.Host(x+s)→ simosa-web(build/web/instruments)→ clients.mjs ×N + student.mjs
                           → 每 --sample-s 取樣 /proc(CPU%、RSS、執行緒)與主控程序 stdout 的 steps/s、掉拍 → stats/host.jsonl
                           → 時間到 stop-all.sh(停止檔 → footer)→ report.mjs → 結束碼
tests/soak/clients.mjs     一個 Node 程序內 N 個客戶端:各一條狀態連線(25 Hz 狀態幀 + 事件框)+ 一條雷達 X 頻連線(SRAD 二進位幀)+ 每 30 s GET index.html;
                           每秒寫幀數、tick 區間、延遲 p50/p95/max、雷達幀數/位元組、斷線/錯誤;結束寫整場延遲直方圖(1 ms 格)
tests/soak/student.mjs     每 10 s 輪流舵令(±10° 以內)/車令(SAH/DSAH/STOP)、每 60 s 輪流注入/清除感測器故障(gyroDrift、radarDegraded、gpsOffset…)、每 5 min snapshot;
                           每個指令以後續狀態幀確認(rudderOrder / telegraph / faults[])在 3 s 內生效,計 commandRejected
tests/soak/report.mjs      讀 meta.json 與 stats/*.jsonl(選配 dotnet-counters 的 counters.csv)→ build/soak/report-<run>.md 與 .json;可匯入(summarise / renderMarkdown)
tests/soak/lib.mjs         參數解析、JSON Lines、毫秒直方圖、延遲估計器
tests/soak/report.test.mjs 判定準則與工具的單元測試:node --test tests/soak/report.test.mjs(11 案例;合成執行目錄在 build/test-tmp/soak/)
```

只用 Node 22 內建模組(WebSocket、fetch、node:test),無套件相依;不加任何核心後門、不改狀態雜湊(學員指令皆為 `command.schema.json` 既有指令)。

### 6.2 量測方法

- **延遲**:核心以牆鐘推進(`RealtimeRunner`),第 k 幀的理想到達時刻 = 原點 + `state.t`;原點取連線初期(前 50 幀)「到達時刻 − t」的最小值(最快送達的那一幀),之後每幀延遲 = 到達 − t − 原點(≥ 0)。量到的是核心序列化 + WebSocket 傳送 + 客戶端事件迴圈的總延遲,核心落後牆鐘(掉拍)也會反映為延遲上升;凍結幀不計、時間倍率須為 1。因為核心每 20 ms 週期才推進一步,延遲的底值約 0–20 ms 均勻分布(p50 ≈ 10–20 ms 屬正常)。。**注意**:因此量到的是相對於最快一幀的額外延遲(抖動與落後),固定的鏈路延遲與 RealtimeRunner 的固定相位不計;絕對往返以學員腳本的指令生效時間(送出 → 狀態幀反映,容器 p50 26 ms、max 82 ms)補充,2 小時實機請同時看這兩項
- **tick 率**:客戶端首末狀態幀的 tick 差 / 到達時間差(與主控程序每秒輸出的 `steps/s` 互相印證);**掉拍**取自主控程序輸出的累計值(`RealtimeRunner.DroppedSteps`)。
- **CPU%**:`/proc/<pid>/stat` 的 utime+stime 差 / 牆鐘差(Windows:`TotalProcessorTime`),多執行緒可 > 100;**RSS**:`VmRSS`(Windows:`WorkingSet64`)。
- **GC**:`dotnet-counters`(PATH 或 `.cache/dotnet-tools`)存在時收 System.Runtime 計數器到 `stats/counters.csv`(容器未安裝 → 略過並在報告註記)。

### 6.3 判定(結束碼 1 = 不合格;門檻可用參數調整)

| 準則 | 門檻 | 來源 |
|---|---|---|
| 核心 50 Hz 無掉拍 | 掉拍 0;客戶端量到的 tick 率平均 50 ± 0.5(`--core-hz-tol`)、各客戶端 50 ± 1.0 | 第 14 章效能「核心 50 Hz 無掉拍」 |
| 延遲 | 全部客戶端合併的 p95 < 50 ms(`--max-latency-ms`;max 另列) | 「網路延遲 <50 ms」 |
| RSS 不持續成長 | 暖機(`--warmup-s` 120,短於 10 分鐘的執行取總時長 × 20%)後最小平方斜率 ≤ 1 MB/min(`--max-rss-slope-mb-per-min`)且增量 ≤ 64 MB(`--max-rss-growth-mb`) | 「連續 2 小時」的軟體面近似 |
| 無斷線與錯誤 | 客戶端斷線 0、JSON/幀格式錯誤 0、網頁 GET 失敗 0 | — |
| 學員指令皆生效 | 未確認 0、`commandRejected` 0、斷線 0 | — |
| 主控程序 | 存活到結束、結束碼 0、紀錄 footer 已寫(`紀錄已關閉`) | 優雅停止(第十六輪) |

RSS 擬合排除**收尾樣本**:`elapsed_s` 超過設定時長(`meta.minutes × 60`)或合成客戶端已結束(`clients.alive = false`)的取樣不入斜率/增量計算(客戶端時間到斷線時核心釋放連線與訂閱常有一筆 +數 MB 的尖峰,不代表持續成長;報告列「排除收尾 N 筆」,起訖/最大值等原始統計與 `hostSamples` 仍含全部樣本);`tools/soak-test.sh` / `.ps1` 在客戶端與學員都已結束後取到的那一筆也不再寫入 `stats/host.jsonl`。

### 6.4 執行

```bash
bash tools/soak-test.sh --minutes 10 --clients 3 --skip-build        # 容器/開發機(≈ 11 分鐘;需獨佔 CPU,不要與整套測試併行)
bash tools/soak-test.sh --minutes 120 --clients 3 --note "船上筆電"  # 實機 2 小時(第 14 章)
node --test tests/soak/report.test.mjs                               # 報告/判定單元測試
node tests/soak/report.mjs --dir build/soak/<run> --md x.md --json x.json --max-latency-ms 80   # 以不同門檻重新判定既有執行
```

輸出:`build/soak/<run>/`(`bin/` 建置輸出複本、`logs/`(simcore/radar/web/clients/student)、`stats/`(host/clients/student.jsonl)、`records/`、`media/`、`meta.json`)與 `build/soak/report-<run>.{md,json}`。Ctrl+C 會提前收尾並仍產生報告(標示中止、結束碼 1)。Windows 版 `soak-test.ps1 -WhatIf` 只檢查參數與工具鏈(`tools/run-ps-tests.sh` 的 `soak` 步驟);實跑在 Windows 以同參數執行。

### 6.5 容器結果(2026-11-05,第二十四輪;`--minutes 10 --clients 3`,4 vCPU Linux 容器)

`bash tools/soak-test.sh --minutes 10 --clients 3 --skip-build`(執行 `20261105-052453`,報告 `build/soak/report-20261105-052453.md`;不進版控):**不合格**(不通過:RSS 不持續成長)。

| 項目 | 結果 |
|---|---|
| 核心步率 / 掉拍 | tick 率 50.001/s(各客戶端 50.001–50.001);主控程序 steps/s 平均 50.03(每秒值 48–52);掉拍 0 |
| 狀態幀延遲(3 客戶端合併,n = 44831) | p50 3 / p95 8 / p99 9 / max 75.3 ms |
| 狀態幀率 | 每秒 p50 25(核心廣播 25 Hz);雷達 X 頻每客戶端 44.2 幀/s、411.4 KB/s;網頁 GET 57 成功 / 0 失敗 |
| 主控程序 CPU % | 平均 12、最大 29(4 vCPU 容器;雷達服務平均 8.9、最大 23.6) |
| 主控程序 RSS | 起 98.8 → 訖 138.2 MB(最大 138.2);暖機 120 s 後增量 19.4 MB、斜率 1.813 MB/min;雷達服務 96.2 → 108 MB |
| 斷線 / 錯誤 | 客戶端斷線 0、錯誤 0;學員動作 69(確認 68、未確認 0、snapshot 1、commandRejected 0),生效時間 p50 26.1 ms、最大 82.1 ms |
| 主控程序 | 存活到結束、結束碼 0、紀錄 footer 已寫;GC 計數器略過(容器無 dotnet-counters) |

RSS 準則不通過的判讀:主控程序 RSS 自 98.8 MB 起呈階梯式上升(111 s、161 s、303 s、424 s 各 +3–5 MB,最後一筆於客戶端收尾時 +4.6 MB),暖機後斜率 1.8 MB/min、後半段 1.8 MB/min、最後 3 分鐘 0.4 MB/min;型態與 .NET GC 堆積擴張一致而非線性洩漏,但 10 分鐘不足以判定是否平穩,**是否洩漏以實機 2 小時(`--minutes 120`)為準**;若 2 小時後半段斜率仍 > 1 MB/min,優先檢查核心內隨模擬時間成長的容器(事件清單、評估追蹤器的歷史、紀錄器緩衝)。其餘五項(50 Hz 無掉拍、延遲 p95 8 ms、無斷線、學員指令全部生效、優雅停止)在容器通過。

### 6.6 限制

- 合成客戶端在同一個 Node 程序內,不含瀏覽器繪圖與視景 GPU 負載;內顯筆電的幀率、CPU 與延遲需實機以同一工具(`--minutes 120`)重跑,並與視景同時執行(`start-ship-laptop.ps1` 啟動視景後再跑 `soak-test.ps1 -SkipBuild`,或改用 `--port-base` 避開 8765 系列埠)。
- 延遲為牆鐘估計而非封包往返;教官筆電直連的網路延遲需實機(追溯表第 3 節)。
- 情境固定(預設 E05,不切換情境);學員腳本只做溫和操作,不涵蓋倒帶/重播/情境載入的記憶體行為。
