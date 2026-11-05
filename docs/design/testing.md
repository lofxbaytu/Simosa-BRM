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
