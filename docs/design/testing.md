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
                         Sensors.Radar.Host(X/S;找不到 dll 或埠未開則雷達案例 skip)、simosa-web(tools/launch/StaticWeb 伺服 build/web/instruments;
                         --status-file build/e2e/release-status.json:本輪寫的 simosa-pkg status --json 形式的檔,發行版取 VERSION、另套用過一個
                         比它新的軟體更新包、沒有降版 — 與靠更新包升級的船上筆電相同,教官站頁首是「軟體更新 <版次>」,全部版面案例都在這個狀態下量測;
                         沒有這個檔時 /_status/release.json 回 200 與各欄 null,教官站不顯示、沒有 console error — 02-instructor 有一案把檔暫時移走驗證);
                         第二個 simosa-web 當作「船上筆電」的網頁伺服(--status-file build/e2e/ship-release-status.json、log ship-web.log、不加 --quiet):
                         教官筆電的教官站跨來源向它取船上的發行版狀態(02-instructor 的「教官筆電的教官站顯示船上筆電的版本…」以 ?shipWeb=http://localhost:<埠>
                         指過去;「既有安裝的教官站網址…」另開一個帶 --host-resolver-rules 的 Chromium,把 http://192.168.77.11:8080 對到它;
                         其他案例的頁面與核心都在 127.0.0.1 = 同一台電腦,不會連到它);
                         埠向作業系統取空埠(E2E_PORT_BASE 可固定:base..base+5),寫 build/e2e/env.json 與 process.env
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
| | 操船台 1920×1080(conning 條單列 10 至 13 格;第三十一輪) | 真實核心目前的狀態,加上把 conning 條顯示的狀態換成極端數值(`support/layout.ts` 的 `overrideConning` 以實例屬性蓋過元件的存取子;數值組在 `support/conning-values.ts`):10 / 11 / 12(兩種)/ 13 格各兩組數值、「尚未收到資料」,以及 13 格的「再多一位數與沒有空白的長字串」(UKC 1234.5、航線偏移六位數、目標船代號 13 個字元並超限、航點名稱 22 個字元);每一組以 `support/pages.ts` 的 `expectConningSingleRow` 判定 —— 版面判定通過、排成一列、內容不比方塊高、文字不互相重疊、各方塊的圖形與文字都有顯示(允許的例外只有船速方塊 125 px 以下省略標籤的中文部分)、字距加大 3 % 後 conning 條仍沒有水平裁切;平常的位數下主要數值的字級不低於修改前的九成;有浪時浪向玫瑰看得到、10 至 12 格時船速方塊的「對水 / 對地 / 航跡向」看得到、橫搖角超過警報門檻時是警報色的字且沒有底色。其餘版面案例(1093×614 至 1536×864)見 `docs/design/web-layout.md` 第 5 節 |
| | 操船台 1366×768、1536×864 與 1093×614(conning 條兩列 10 至 13 格;第三十一輪) | 核心載入 E20(有航線,11 格)。1366×768 與 1536×864:真實核心目前的狀態,加上 `overrideConning` 把 conning 條的狀態換成 `support/conning-values.ts` 的 `CONNING_TWO_ROW_*`(平常會出現的位數與狀態)—— 10 格、11 格(航線)、12 格(航線 + 浪)、13 格 × 兩組(各種狀態同時出現:感測器無效、凍結、擱淺、主機故障並 EFAS、艏側推不可用、最長的流場來源標示、99:59:59、西經;倒俥滿舵 70°、−150 並「啟動中 100%」、航向可疑):`support/pages.ts` 的 `expectConningTwoRows`(版面判定通過、排成兩列、內容不比方塊高、沒有被方塊邊界切掉的文字 —— `probeConning` 的 `clipped`、各方塊的圖形與文字都有顯示、文字不重疊、字距加大 3 % 後仍通過);主要數值的字級不低於修改前的九成;「L 偏左」一行、「速限」與「能見度」同一列。審查後補:速限的數值到「速限」的距離 ≤ 8 px、「能見度」到速限的數值 ≥ 18 px(原本數值靠右:離自己的標籤約 180 至 190 px、離「能見度」12 px);擱淺時 UKC 的大字在紅底裡上下置中(行框上緣離紅底上緣 ≥ 2 px、上下的留白相差 ≤ 4.5 px;10 格時多出來的高度 ≥ 8 px)。1093×614 的 13 格兩組數值:同樣的判定,整頁的部分只看 conning 條(`scope: 'conning'`)。修改前在「1366×768 10 格」就失敗(UKC 方塊的內容比方塊高 12 px) |
| `02-instructor` | 情境清單 → 載入 E07 → 狀態幀情境 id 變更、載入鈕回復 | 清單 ≥ 20 個;核心狀態幀 `scenario.id = E07_steering_failure`;載入鈕回「載入 E07…」、清單「已載入」標籤、header 顯示情境 |
| | 無效內嵌情境 → 教官站顯示核心拒絕 | 以 WebSocket 送 `loadScenario args.scenario`(多邊形 2 點、hardFail 引用不存在區域)→ 事件框 `scenarioLoadRejected`(origin inline、issueCount ≥ 2)→ 面板 `.rejected` 列出問題;核心情境不變 |
| | 下舵令 20° → 操船台舵角指示變化 | 舵輪預設鈕「S 20°」→ 狀態幀 `rudderOrder = 20`;`brm-rudder` 舵令「S 20°」、舵角隨舵機速率到「S 20°」;案例結束回正舵 |
| | 凍結(教官站)→ 事件 frozen、tick 停止;恢復 → 事件 resumed、tick 前進 | 執行控制「凍結」→ 事件框 `frozen`(source instructor)、1.5 s 內 tick 不前進、事件流列出;恢復以 WebSocket 送 `resume`(見下列缺陷)→ 事件 `resumed`、`flags.frozen=false`、tick 前進 |
| | 已知缺陷(`test.fail`):凍結後教官站應顯示「已凍結」且「恢復」鈕可用 | 核心凍結時 `Step` 不 `Advance`/`Broadcast`,沒有任何 `flags.frozen=true` 的狀態幀送達 → header 不顯示「已凍結」、「恢復」鈕停用、藥丸轉「資料逾時」。核心修正後此案例會「意外通過」,屆時移除 `test.fail` |
| | E14 繫泊中碰墊的反覆接觸:事件串流合併成一列(第三十輪 ENG-C9) | 前置 `ensureScenario(E07)`(之後收到的 `fenderContact` 都是 E14 的);教官站真實點擊載入 E14、4 倍速跑到 S03 有 4 筆接觸後凍結;測試端 WebSocket(`CoreClient.collectEvents`)數到的每個碰墊事件數 n 與頁面逐一對照:n ≥ 3 時恰好兩列(第一次接觸 + 合併列,`data-merged-count = n − 1`),合併列在第一次接觸之上、時間欄是它建立的時刻(第一次反覆接觸);測試端在合併列建立之後、最後一次併入之前送一筆教官指令,它的事件排在合併列之上(合併列留在原地更新,不跳到最上面),凍結事件在最上面;合併列文字完整(不以 … 截斷、不溢出)、標題「N 筆(合併前 M)」的 M − N = 被合併掉的筆數、完整教官站版面判定通過、關掉「引擎」篩選時隱藏;「講評報告」新分頁的時間軸 S03 同樣兩列、次數相同 |
| | 重播檢視讀超過上限的核心紀錄(第三十一輪) | 測試當場合成(不進版控;跑完刪除):E13 夾具的狀態行循環複製成 25 Hz × 9,000 筆(約 27 MB),事件 / summary / footer 行放回原位。先載入 E14 夾具,再選合成的檔案:頁面內的 MutationObserver 至少看到 2 個遞增的「讀取 <檔名>… N%(共 26 MB)」且有小於 100% 者,各畫格(`requestAnimationFrame`)也至少看到一次小於 100% 的進度(讀取中有讓出主執行緒、畫面有更新),讀取中資訊列仍是 E14;載入後「狀態筆數超過 7,201:畫面每 2 筆取 1 筆(約每 0.08 秒一筆,⏮ ⏭ 逐筆同此間隔);摘要、事件、航跡仍以全部 9,000 筆計算」、資訊列「9000 筆狀態」;時間軸 `00:00 / 06:00`、拖到 180 s;⏭ / ⏮ 的說明「(約 0.08 秒)」、一次走一筆留下的狀態(conning 列顯示的 tick 與時刻逐步核對);**時間軸的方向鍵**(審查後修正:降採樣時走逐筆 —— 聚焦滑桿按 → 兩次、← 一次,每按一次 conning 列顯示的 tick 換一筆留下的狀態;PageDown 照舊由瀏覽器處理);點事件串流的列跳到事件的時刻、點時間軸上的事件標記;拖到底顯示最後一筆(一律保留);講評摘要 `samples = 9000`、滿舵次數 = 全部狀態中的次數(留下來的狀態裡只有另外保留的最後一筆是滿舵)、航跡點 ≥ 355;1366×768、1093×614、1920×1080 的重播檢視版面判定通過;播放 / 暫停;沒有 console error |
| | 完整教官站的「重播檢視」分頁讀同一種紀錄(第三十一輪) | 核心連線中;E01 夾具循環複製成 7,300 筆:降採樣的說明與「7300 筆狀態」、時間軸 `00:00 / 04:51`、拖到 120 s、版面判定通過;切回「即時」再切回「重播檢視」,紀錄與時刻還在。**超不超過上限看最終的歷史**(審查後修正):同樣的 7,300 筆之後加一行 `restore`(回 tick 2000)與 2,000 筆狀態(最終 3,001 筆 < 7,201)再載入 —— 資訊列「3001 筆狀態 · 1 筆輸入」、沒有降採樣的說明、時間軸 `00:00 / 02:00`、倒帶標記「丟棄 6299 筆狀態」、`stateStats` = 全部 3,001 筆 / stride 1、⏭ 的說明是「下一筆」且一次 0.04 秒(修正前:1,501 筆、每 2 筆取 1 筆、說明自相矛盾) |
| | 重播檢視 1920×1080(conning 條單列 11 格)載入 E20 核心紀錄夾具(第三十一輪) | `record-E20_comprehensive_assessment_kaohsiung-core.jsonl`,時間軸 0 % / 50 % / 100 %:`expectConningSingleRow`(同上)、主機轉速 ≥ 50 px、轉速令在轉速的下一行且在方塊內。修改前在 CI 的字型下主機方塊被裁切 3 px |
| | 重播檢視 1920×1080(conning 條單列)載入航線偏移很大的紀錄(第三十一輪) | 由 E02 夾具當場合成(不進版控;跑完刪除):六筆狀態的航線偏移 0 / 386 / 1568(超過限值)/ 左 2345 / 左 999 / 12345 m;每一筆:顯示的數值與左右、`expectConningSingleRow`、偏移的字級 ≥ 25.2 px、數值 / 左右 / 限值 / 位置圓點 / 下方各列都在方塊的內容框裡 |
| | 重播檢視 1920×1080(conning 條單列 12 格)載入 E17b 核心紀錄夾具(第三十一輪審查後) | `record-E17b_typhoon_seakeeping-core.jsonl`(有浪、有能見度;情境庫唯一的 12 格),時間軸 0 % / 50 % / 100 %:`expectConningSingleRow`(12 格)、浪向玫瑰不是 `display: none`、寬高 ≥ 24 px、在方塊的內容框裡、Hs / Tp 的數值沒有疊在圖上;船速方塊寬於 125 px、「STW 對水 / SOG 對地 / COG 航跡向」的中文部分看得到、各列在內容框裡。第一版的修改讓這一格少了玫瑰圖與三個中文標籤,而只判定「不裁切」的案例看不出來 |
| | 完整教官站 1920×945 與 1920×800(瀏覽器沒有全螢幕,conning 條單列只有 170 / 150 px 高;第三十一輪審查後) | 真實核心 + `overrideConning`。1920×945:13 格(航行中平常的值)、12 格(船速兩位數)、13 格(平常的位數裡最寬的)都通過 `expectConningSingleRow`(含字距加大 3 %)。1920×800:13 格、航行中平常的值 —— 主機方塊的內容不比方塊高(≤ 4 px)、主機狀態「運轉中 62%」整行在方塊裡、轉速條由 26 px 縮短(≥ 10 px),其他方塊的內容最多高出 8 px;回到 1080 高時轉速條回到 26 px |
| | 重播檢視載入 E09 核心紀錄夾具、時間軸最後(航線偏移超過限值;第三十一輪審查後) | 1093×614、1366×768、1536×864、1900×1069(兩列)與 1920×1080(單列):conning 條的水平裁切清單是空的(原本兩列時列出 `brm-route div.bar.over scrollWidth 212 > clientWidth 209`)、位置圓點整個在條內並貼著條的一端;1366×768 的重播檢視版面判定通過 |
| | 完整教官站 1366×768 與 1536×864(conning 條兩列 11 與 13 格;另 1536×730 與 1900×700;第三十一輪) | 真實核心 + `overrideConning`。完整教官站給 conning 條的高度是三個頁面裡最矮的(1366×768 時每個方塊 112 px 高)。11 格與 13 格 × 同樣兩組數值:`expectConningTwoRows`;大字(航向、主機轉速、UKC)不低於修改前的九成;主機的車鐘位置與主機狀態的下緣在方塊的內容框裡、轉速條 ≥ 12 px。1536×730 與 1900×700(方塊 107 px 高、字級照視窗寬度;後者是兩列裡最寬而最矮的)10 格與 13 格:同樣的判定(只看 conning 條)、conning 條的高度是下限 220 px。審查後補兩種「方塊寬度剛好在排法門檻上緣」的視窗:1550×872 的 13 格(風方塊約 187 px:玫瑰圖在數值上方、風速兩位數時每一列仍是一行)與 1813×1020 的 10 格(艏側推方塊約 241 px:「令」在「實際」的下一行),都通過 `expectConningTwoRows`(整頁)。修改前在「1366×768 11 格」就失敗(航向 12、主機 8、UKC 20、航線 8 至 9 px) |
| | 教官站按「凍結」後(真實流程;1366×768 與 1536×864,conning 條兩列;第三十一輪) | 核心載入 E20;教官站(`page`)與另一個分頁的駕駛台(`context.newPage()`)都連線後,在教官站按執行控制的「凍結」(真實點擊)並等核心的 `frozen` 事件。兩種視窗 × 兩個頁面:時間方塊的模擬時間列是「HH:MM:SS 凍結」、「凍結」與時間在同一行且整段在方塊的內容框裡,`expectConningTwoRows`(11 格)。按「恢復」後「凍結」消失;`finally` 以 `ensureRunning` 恢復核心並關掉駕駛台分頁。修改前在「教官站 1366×768(凍結後)」失敗(航向 12、主機 8、UKC 5 px) |
| | 重播檢視載入 E06b 核心紀錄夾具(conning 條兩列 11 格;第三十一輪) | `record-E06b_tidal_stream-core.jsonl`(情境庫的夾具裡唯一同時有流場來源、潮高與航線的):標題是「流(潮流)」、UKC 方塊有「潮高」一列。1366×768、1536×864(`expectConningTwoRows`)與 1093×614(只看 conning 條)× 時間軸 0 % / 50 % / 100 %;UKC 方塊下方四列(水深、潮高、Squat、吃水)的下緣都在方塊的內容框裡。審查後補:1366×768 與 1536×864(UKC 方塊是兩欄)每一格的數值到自己的標籤 ≤ 8 px、同一行右邊那一格的標籤離左邊的數值 ≥ 14 px(原本數值靠欄的右緣:「18.0」離「水深」62 px、離「潮高」14 px)。修改前在「1366×768、時間軸 0 %」失敗(航向 12、主機 8、UKC 20 px:「吃水 F/A」整列在方塊之外) |
| | 重播檢視載入 E14 核心紀錄(逐筆的 fenderContact) | 夾具 `record-E14…-core.jsonl`:事件串流 S03 兩列、合併列 ×3(00:16–00:49;時間欄 00:16)、資訊列「n 筆事件(合併前 m)」;時間軸有一段合併列的橫條(寬 > 一半)、點左端跳到 00:16;重播檢視版面判定通過 |
| | 警報列看得到字(第三十一輪;講評摘要與目標船小表) | `?mode=replay` 讀 `record-E08-core.jsonl`(講評摘要有逾時的必要事件與未確認的警報,≥ 3 列)與 `record-E02…-core.jsonl`(跳到結尾,目標船小表有 3 艘 CPA 警報中),逐列讀 `getComputedStyle`:`tr.alarm` 沒有整列的底色、有字的每一格字色不等於列或格子的底色。共用樣式的徽章 `.alarm`(紅底)套到表格列時,列內紅色的數值是紅底紅字 —— 版面判定(裁切 / 重疊 / 遮蓋)查不出這種問題 |
| | 發行版本與降版提醒(第三十輪) | 真實的 `simosa-web`:`/_status/release.json` 只有 `format` / `generatedUtc` / `releaseDowngrade` / `releaseVersion` / `softwareVersion`(狀態檔裡的裝置、金鑰、環境、警告不送出)、`no-store`,`/_status/` 之下其他路徑 404;完整教官站頁首「軟體更新 <VERSION + 1>」(實際執行的那一版,title 寫發行版),`page.route` 回沒套用過更新包的內容時是「發行版 <VERSION>」;**把狀態檔暫時移走**:`/_status/release.json` 回 200 與各欄 `null`,完整與簡化模式頁首都不顯示、沒有 console error;以 `page.route` 回降版內容(夾具取自真實的 `simosa-pkg status`)→ 1366×768 與 1536×864 真實點擊載入 E13 後徽章「發行版 2026.11.1 · 降版提醒」、預設收合、`layoutFailures` 為空;真實點擊展開 → 重點 / 處理 / 完整訊息 / 來源,說明浮貼在頁首下方且整個在視窗內、整頁不捲動;「收合」與 Esc 收起後版面判定仍通過;簡化模式 1366×768 同樣有徽章,展開後點說明裡面不收合、點說明以外的版面收合;駕駛台頁沒有元件、也不請求 `/_status/` |
| | 教官筆電的教官站顯示船上筆電的版本(第三十一輪) | `global-setup` 多啟動一個 `simosa-web` 當作「船上筆電」(不同的埠與狀態檔 `build/e2e/ship-release-status.json`,log `ship-web.log`);頁面在 `http://127.0.0.1:<埠>`、`?shipWeb=http://localhost:<船上埠>` — 兩者在瀏覽器是不同來源,所以是真的跨來源請求、真的 CORS 標頭。真實的 `simosa-web`:Origin 是頁面來源時回 `Access-Control-Allow-Origin` 與 `Vary: Origin`,其他來源沒有;**同一台**(沒有 `shipWeb`、`ws` 在 127.0.0.1):只有同源的一次請求,「船上」的伺服器沒有收到請求;**版本相同**:頁首「船上 <VERSION + 1>」(title 寫本機相同與兩邊的來源)、一次同源 + 一次跨來源的 GET、**伺服器 log 沒有 `OPTIONS`**(只有一行 `GET …(跨來源 http://127.0.0.1:<埠>)`)、完整與簡化模式版面判定通過;**版本不同**(改寫船上的狀態檔;1366×768、1536×864):徽章「船上 <VERSION> · 版本不同」、展開的說明(船上 / 本機 / 哪一台較舊與處理)、收合與展開的版面;**船上降版**(真實 status 的降版輸出;完整與簡化模式 1366×768):徽章「船上 2026.11.1 · 船上降版」、展開的區塊順序是船上 → 本機 → 版本不同 → 來源、船上 status 的完整訊息預設收合(點標題列展開 / 收起)、較舊的船上筆電有降版提醒時「版本不同」的處理指向降版提醒、Esc 與點說明以外收合;**船上沒有發行版狀態**(刪掉船上的狀態檔 → 200、CORS 標頭、各欄 `null`):頁首與只有本機時相同、title 寫「船上筆電沒有發行版狀態」(不是未取得)、4.6 秒內對船上只有一次請求(不重試);以上都沒有 console error。`ship-web.log` 一律以 `expect.poll` 讀(`simosa-web` 先送回應、後寫 log;審查以 strace 把 log 的寫入延後 300 ms 重現過一次性讀取的失敗)。**取不到**三種(另開一頁收集主控台):連不上(沒有監聽的埠)、船上是第三十輪的版本(測試程序內的 HTTP 伺服器回 200、沒有 CORS 標頭)、更舊的版本(404)— 頁首與只有本機時相同(「軟體更新 <版次>」)、title 寫「船上筆電的版本:未取得」、版面判定通過,主控台只容許「對船上那個網址的請求失敗」(`net::ERR_CONNECTION_REFUSED`,或 `blocked by CORS policy` + `net::ERR_FAILED`;瀏覽器記的,網頁端消不掉),其他錯誤照樣不行 |
| | 既有安裝的教官站網址(沒有 `shipWeb` 參數;審查後修正) | 另開一個 Chromium(`--host-resolver-rules`:`192.168.77.11:<核心埠>` → 核心、`192.168.77.11:8080` → 「船上」`simosa-web`;`--no-proxy-server`),網址是 `start-instructor-laptop` 開的形式(`?ws=ws://192.168.77.11:<埠>`,沒有 `shipWeb`):請求送往 `http://192.168.77.11:8080/_status/release.json`、只有 GET(沒有 `OPTIONS`)、伺服器 log 的 Origin 是教官站頁面的來源、頁首「船上 <VERSION + 1>」(完整與簡化模式)、沒有 console error。**1093×614、船上降版**:展開的說明底邊在「凍結」鈕的上緣之上、`INSTRUCTOR_LAYOUT` 的執行控制(凍結 / 恢復 / 重設 / 結束練習)沒有被 `<brm-release-status>` 蓋住、「收合」在說明的可見範圍內;把船上的完整訊息也展開 → 內容在說明裡捲動(`scrollHeight > clientHeight`),同樣不蓋住、「收合」貼在底邊;說明展開著**真實點擊「凍結」**→ 核心送出 `frozen`、頁首「已凍結」、說明收合,再按「恢復」 |
| `03-vhf` | 文字訊息:同頻道互通、不同頻道不互通 | 教官站 CH16 → 操船台(?station=bridge)CH16 收到(RX、CH16);操船台轉 CH12 後雙向都收不到 |
| | PTT 按住 2 s | 假麥克風 → 面板 TX、對方指示燈「RX bridge CH16」;核心事件 `vhfCall{station: bridge, channel: 16, duration_s ≥ 1.5, endReason: pttEnd}`;教官站事件流與 VHF 面板有通話紀錄 |
| `04-recording` | 錄影開始 3 s 停止 | `--media-dir` 出現唯一新 `.webm`(> 1 KiB、無 `.part`)與旁檔 `.json`(file/station/sizeBytes/chunks/durationS ≥ 2/sha256/truncated=false);面板清單列出檔名 |
| `05-radar` | 雷達頁 PPI 畫布非全黑 | 連線 chip ok;`radar-ppi canvas` 非黑像素 > 1%;雷達服務未啟動時 `test.skip`。雷達服務以 `--localhost-only` 啟動時只接受 `Host: localhost`,頁面網址用 `ws://localhost:<埠>` |
| `06-ecdis` | ECDIS 頁載入、顯示情境航線與自船 | 前置 `ensureScenario(E07)`;header chip「E07… · 航線」、HDG 三位數;航線監控面板航段/DTG/XTD;`ecdis-chart canvas` 非黑像素 > 5% |
| `07-report` | 報告頁開啟最近的核心紀錄 | 本輪最新且已寫 footer 的 `.jsonl`(`build/e2e/records` 或 `build/records`)→ 「已載入 … 狀態 n 筆」、無錯誤訊息、無 console 錯誤 |
| | 教官站「講評報告」→ 新分頁載入交接紀錄 | `report.html?from=instructor` 顯示「已載入 教官站紀錄」 |
| | 最近載入的紀錄存 Blob(第三十輪) | 開啟核心紀錄 → IndexedDB 的 `record:last` 是 Blob(大小 = 檔案大小)、沒有整份文字;重新整理後訊息與報告內容相同;舊版存的 `{ name, text }` 讀得進來並改存成 Blob |
| | 大檔讀取中主執行緒有讓出(第三十輪審查 A-rA-2) | 合成約 60 MB 的核心 JSON Lines → 頁面內每個畫格(`requestAnimationFrame`)記下狀態列的讀取進度:至少看到 2 個小於 100% 的百分比且遞增(修正前整個讀取是一個長任務,只畫得出 100%) |
| | 核心仍在寫入的紀錄(第三十輪審查 A-rA-1) | 先開一份紀錄,再開每 20 ms 被附加一行的 `.jsonl`:照樣載入;`record:last` 不可以仍是前一份 — 交易中止(常駐設定檔)→ 清掉並在狀態列寫「未能另存於瀏覽器」,重新整理後沒有紀錄;瀏覽器接受(端對端的無痕視窗:存的是原檔案的參照)→ 重新整理後還原這一份,或讀不到時顯示「無法還原先前載入的紀錄…」並清掉,再重新整理沒有錯誤 |
| `08-scenario-editor` | 開啟範本、改 Hs、驗證通過、YAML 預覽含 waves | 套用範本 E05 → 勾浪、Hs 2.5 → 「結構檢查通過」、`pre.yaml` 含 `waves:`/`significantHeight_m: 2.5`;Hs −1 → 錯誤且載入鈕停用 |
| | 版面(每個範本一案:E17b / E13 / E20 / E08) | 套用範本、展開各區段(兩種互斥組合)後在 1100 / 1366 / 1536 / 1920 / 2560 寬(高 = 寬 × 9/16)量測:面板/欄不水平溢出、面板不重疊、控制項點得到、標籤不逐字換行、輸入框不被擠壓或裁切、頁面無水平捲軸;1100 一欄、1366 / 1536 兩欄、1920 / 2560 三欄、每欄 ≥ 560 px(`probeEditorLayout`;規則見 `scenario-engine.md` 第 6.1 節「版面」) |

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
                           (核心與雷達以 DOTNET_GCgen0size 啟動:--gen0-budget-mb,預設 6;6.2、6.6 節)
                           → 每 --sample-s 取樣 /proc(CPU%、RSS、執行緒)與主控程序 stdout 的 steps/s、掉拍 → stats/host.jsonl
                           → 時間到 stop-all.sh(停止檔 → footer)→ report.mjs → 結束碼
tests/soak/clients.mjs     一個 Node 程序內 N 個客戶端:各一條狀態連線(25 Hz 狀態幀 + 事件框)+ 一條雷達 X 頻連線(SRAD 二進位幀)+ 每 30 s GET index.html;
                           每秒寫幀數、tick 區間、延遲 p50/p95/max、雷達幀數/位元組、斷線/錯誤;結束寫整場延遲直方圖(1 ms 格)
tests/soak/student.mjs     每 10 s 輪流舵令(±10° 以內)/車令(SAH/DSAH/STOP)、每 60 s 輪流注入/清除感測器故障(gyroDrift、radarDegraded、gpsOffset…)、每 5 min snapshot;
                           每個指令以後續狀態幀確認(rudderOrder / telegraph / faults[])在 3 s 內生效,計 commandRejected
tests/soak/report.mjs      讀 meta.json 與 stats/*.jsonl(選配 dotnet-counters 的 counters.csv)→ build/soak/report-<run>.md 與 .json;可匯入(summarise / renderMarkdown)
tests/soak/lib.mjs         參數解析、JSON Lines、毫秒直方圖、延遲估計器
tests/soak/report.test.mjs 判定準則與工具的單元測試:node --test tests/soak/report.test.mjs(24 案例;合成執行目錄在 build/test-tmp/soak/)
tests/soak/fixtures/       真實執行的摘錄(只留 RSS 準則需要的欄位,各數 KB):實驗 B、e01 截到 12 分鐘、e08、r30 修正後 10 / 25 分鐘、修正前 10 分鐘(6.6 節);
                           extract.mjs 從 build/soak/<run>/ 擷取(刷新分組與 report.mjs 相同)
```

只用 Node 22 內建模組(WebSocket、fetch、node:test),無套件相依;不加任何核心後門、不改狀態雜湊(學員指令皆為 `command.schema.json` 既有指令)。

### 6.2 量測方法

- **延遲**:核心以牆鐘推進(`RealtimeRunner`),第 k 幀的理想到達時刻 = 原點 + `state.t`;原點取連線初期(前 50 幀)「到達時刻 − t」的最小值(最快送達的那一幀),之後每幀延遲 = 到達 − t − 原點(≥ 0)。量到的是核心序列化 + WebSocket 傳送 + 客戶端事件迴圈的總延遲,核心落後牆鐘(掉拍)也會反映為延遲上升;凍結幀不計、時間倍率須為 1。因為核心每 20 ms 週期才推進一步,延遲的底值約 0–20 ms 均勻分布(p50 ≈ 10–20 ms 屬正常)。。**注意**:因此量到的是相對於最快一幀的額外延遲(抖動與落後),固定的鏈路延遲與 RealtimeRunner 的固定相位不計;絕對往返以學員腳本的指令生效時間(送出 → 狀態幀反映,容器 p50 26 ms、max 82 ms)補充,2 小時實機請同時看這兩項
- **tick 率**:客戶端首末狀態幀的 tick 差 / 到達時間差(與主控程序每秒輸出的 `steps/s` 互相印證);**掉拍**取自主控程序輸出的累計值(`RealtimeRunner.DroppedSteps`)。
- **CPU%**:`/proc/<pid>/stat` 的 utime+stime 差 / 牆鐘差(Windows:`TotalProcessorTime`),多執行緒可 > 100;**RSS**:`VmRSS`(Windows:`WorkingSet64`)。
- **GC**:`dotnet-counters`(PATH 或 `.cache/dotnet-tools`;開發機與容器以 `dotnet tool install dotnet-counters --tool-path .cache/dotnet-tools` 安裝,船上筆電離線不裝)存在時收**核心**的 System.Runtime 計數器到 `stats/counters.csv`(未安裝 → 略過並在報告註記)。dotnet-counters 10.x 的計數器名稱帶單位與區間(`Gen 0 GC Count (Count / 10 sec)`、`Gen 2 Size (B)`),報告以名稱前綴比對(第三十輪以前以完整名稱比對,gen0/gen1/gen2 次數一直是 null);一次刷新的分組:時間戳的「時:分:秒」(含上午/下午、AM/PM;日期格式隨地區設定而異,不用)都解析得了時依時間排序、與該組第一列相差 ≥ `--sample-s` / 2(或同一名稱再出現)即下一次刷新,第一次刷新的時刻取 `--sample-s`、之後依時間戳的實際間隔;解析不了時退回「同一名稱再出現」分組、第 i 次 = (i + 1) × `--sample-s`。第三十輪審查(S-F3)看到 dotnet-counters 偶爾把第一次刷新拆成兩段(先寫 3 列,接著第二次刷新整批,再寫第一次刷新的其餘各列;`r30-fix-default`),只靠名稱分組時之後每次刷新都晚一個間隔;依時間戳分組後與取樣的 `elapsed_s` 通常差 1 s 以內(soak 腳本在客戶端啟動前一刻開始收)。大小一律換算成 MiB(counters 的「(MB)」是 10^6 B,RSS 是 MiB)。報告列出 gen0 預算、GC 次數(gen0 計數含所有代)與第一次 GC 的時刻、GC 暫停、GC 已提交量、受控堆積(上次 GC 後 gen1+gen2+LOH+POH)、上次 GC 後各代大小、配置總量、例外次數。
- **gen0 預算**:`--gen0-budget-mb <MB>`(PowerShell `-Gen0BudgetMb`;預設 6;0 = 不設、用執行階段預設)以 `DOTNET_GCgen0size`(十六進位位元組,6 → `0x600000`)只傳給核心與雷達,模擬船上內顯筆電的小 L3 快取。理由:.NET 8 工作站 GC 的 gen0 預算約 L3 / 2,雲端大快取機器可達 136–264 MB,10 分鐘內只 GC 一兩次,第一次 GC 前 RSS 依配置速率上升,RSS 判定分辨不出洩漏(6.6 節)。`meta.json` 的 `gc` 欄位記下設定(`gen0BudgetMb`、實際設的環境變數、從外部繼承的 `DOTNET_GC*` / `COMPlus_GC*`),報告開頭與「GC 與 gen0 預算」節列出設定與 counters 量到的實際預算;`meta.json` 另記 `dotnetRuntimes`(機器上的 .NET 執行階段版本;主控程序 `RollForward=Major`,只有較新的執行階段時會用它)。
- **版本**(第三十輪審查 S-F4):`meta.json` 的 `gitRev` 是 HEAD 的提交、`gitDirty` 是工作樹有沒有未提交的修改(含未追蹤檔;`true` 時 `gitRev` 對不到實際執行的程式)、`hostDllSha256` 是實際執行的 `bin/simcore/SimosaBRM.SimCore.Host.dll`(建置輸出複本)的 SHA-256;報告開頭列出三者。有 git 時以 git 取(`git worktree` 的 `.git` 是檔案也可);沒有 git(船上筆電)時讀 `.git`(鬆散 ref、`packed-refs`、worktree 的 `commondir`),`gitDirty` 為 `null`(`launch_git_info` / `Get-LaunchGitInfo`,測試 `tools/launch/tests/` 的 `test_git_info` 與 PowerShell 版)。第三十輪以前的腳本只讀 `.git/HEAD`:`r30-fix-default` 的 `gitRev` 是 `0a47ca1`(修正前的提交),實際執行的是工作樹裡已修正、尚未提交的程式;審查在 `git worktree` 跑的 `r30-v-prefix`、`r30-v-prefix-g0`(`.git` 是檔案)`gitRev` 是 `null`。

### 6.3 判定(結束碼 1 = 不合格;門檻可用參數調整)

| 準則 | 門檻 | 來源 |
|---|---|---|
| 核心 50 Hz 無掉拍 | 掉拍 0;客戶端量到的 tick 率平均 50 ± 0.5(`--core-hz-tol`)、各客戶端 50 ± 1.0 | 第 14 章效能「核心 50 Hz 無掉拍」 |
| 延遲 | 全部客戶端合併的 p95 < 50 ms(`--max-latency-ms`;max 另列) | 「網路延遲 <50 ms」 |
| RSS 不持續成長 | **有 `counters.csv`**,逐項判定:(1) 擬合時段內 GC ≥ 3 次(不足或整段沒有 GC = 整個準則**無法判定**);(2) 第一次 GC 起受控堆積(上次 GC 後 gen1+gen2+LOH+POH)與 (3) 暖機後 GC 已提交量的全段最小平方斜率 ≤ 1 MB/min(`--max-rss-slope-mb-per-min`;擬合時段至少 300 s,`--min-slope-span-s`);(4) **穩定段**「RSS − GC 已提交量」與 (5) 穩定段 RSS 的斜率 ≤ 1 MB/min(穩定段 = max(暖機, 720 s) 起、至少 600 s,`--steady-from-s` / `--min-steady-s`);(6) 暖機後 RSS 增量 ≤ 64 MB(`--max-rss-growth-mb`)。任一項不通過 = **不通過**;沒有不通過、但有資料不足的項目 = **無法判定**(不合格並註明原因);全部通過 = 通過。最短可判定的執行約 22 分鐘(720 + 600 s),soak 腳本預設 `--minutes 25`。**沒有 `counters.csv`**(或缺必要的計數器):暖機後 RSS 斜率 ≤ 1 MB/min 且增量 ≤ 64 MB,報告註明「無法分辨 gen0 預算填充」。暖機 `--warmup-s` 120(短於 10 分鐘的執行取總時長 × 20%;有 counters 時至少延到第一次 GC 之後一個取樣間隔) | 「連續 2 小時」的軟體面近似;規則與理由見 6.6 節 |
| 無斷線與錯誤 | 客戶端斷線 0、JSON/幀格式錯誤 0、網頁 GET 失敗 0 | — |
| 學員指令皆生效 | 未確認 0、`commandRejected` 0、斷線 0 | — |
| 主控程序 | 存活到結束、結束碼 0、紀錄 footer 已寫(`紀錄已關閉`) | 優雅停止(第十六輪) |

RSS 擬合排除**收尾樣本**:`elapsed_s` 超過設定時長(`meta.minutes × 60`)或合成客戶端已結束(`clients.alive = false`)的取樣不入斜率/增量計算(客戶端時間到斷線時核心釋放連線與訂閱常有一筆 +數 MB 的尖峰,不代表持續成長;報告列「排除收尾 N 筆」,起訖/最大值等原始統計與 `hostSamples` 仍含全部樣本);`tools/soak-test.sh` / `.ps1` 在客戶端與學員都已結束後取到的那一筆也不再寫入 `stats/host.jsonl`。

### 6.4 執行

```bash
bash tools/soak-test.sh --clients 3 --skip-build                     # 容器/開發機(預設 25 分鐘,≈ 26 分鐘;需獨佔 CPU,不要與整套測試併行;短於約 22 分鐘時 RSS 準則無法判定,6.3、6.7 節)
bash tools/soak-test.sh --minutes 120 --clients 3 --note "船上筆電"  # 實機 2 小時(第 14 章)
bash tools/soak-test.sh --minutes 120 --clients 3 --gen0-budget-mb 0 --note "船上筆電,執行階段預設的 gen0 預算"   # 實機另跑一次(6.7 節)
node --test tests/soak/report.test.mjs                               # 報告/判定單元測試
node tests/soak/report.mjs --dir build/soak/<run> --md x.md --json x.json --max-latency-ms 80   # 以不同門檻重新判定既有執行(另有 --steady-from-s、--min-steady-s、--min-slope-span-s)
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

> 後記(2026-11-22,第三十輪):第三十輪查明核心有 WebSocket 傳送迴圈的等待者洩漏(6.6 節,已修正),但**本次的數字與該洩漏不符,不能歸因於它**:該洩漏約每條狀態連線 1.3 MB/min,本次 5 條狀態連線(合成客戶端 3、學員 1、雷達服務 1)應約 6–7 MB/min,且若那台的 gen0 預算也約 L3 / 2(雲端大快取機器),第一次 GC 前 RSS 會先上升上百 MB;本次實際只有 98.8 → 138.2 MB(暖機後 8 分鐘 +19.4 MB)、階梯式。**本次的成長在第三十輪未重現,原因未查**。推測(未證實):那次的容器可能是 .NET 9 以上的執行階段(主控程序 `RollForward=Major`,機器上沒有 .NET 8 時會用較新的版本,GC 與通道實作不同);當時 `meta.json` 沒有記錄執行階段版本,現在的 soak 腳本會記 `dotnetRuntimes`。

### 6.6 RSS 成長的根因與判定規則(2026-11-22,第三十輪;`--clients 2 --no-web`,4 vCPU / 16 GB / L3 260 MiB 容器,.NET 8.0.33)

第二十五、二十六輪記下的「核心 97 → 約 360 MB、雷達 89 → 約 333 MB」以受控實驗查明是兩件事疊加(每次在 `git worktree` 建置與執行;`dotnet-counters` 收 System.Runtime、旁路取樣 `/proc/<pid>/smaps`、`dotnet-gcdump` 取存活物件、另寫獨立探針;實驗資料不進版控,摘錄在 `tests/soak/fixtures/`)。本節的 MB 除特別註明外為 MiB;dotnet-counters 顯示的「MB」是 10^6 B。

**1. 核心:WebSocket 傳送迴圈的等待者洩漏(真的洩漏;已證實,限 .NET 8.0.33;已修正)**
- 機制:`WebSocketStateServer.SendLoopAsync` 在回覆 / 事件 / 狀態三個通道都空時,每圈對三個通道各呼叫一次 `WaitToReadAsync(_cts.Token).AsTask()` 再 `Task.WhenAny`;狀態幀 25 Hz,每條連線每秒 25 圈。回覆通道是 `SingleReader` 的無界通道,新的等待者會取代並取消前一個,但被取代的等待者在伺服器整個生命週期的權杖 `_cts` 上的取消註冊不會釋放,連同 `OperationCanceledException`、其堆疊與 `AsTask()` 的 `Task` 一直存活到程序結束(客戶端離線也不釋放);每個約 1 KB,每條連線約 1.3 MB/min,不會持平。事件通道(有界)的等待者則排隊到下一個事件才喚醒。獨立探針(.NET 8.0.33,同一通道連續 50 000 次 `WaitToReadAsync`):SingleReader 無界通道 + 長壽權杖時 49 999 個被取代的等待者都被取消,每個保留約 1.1 KB,寫入資料後仍在,權杖取消後才釋放;同樣的通道不傳權杖時被取代的等待者不保留(約 11 B);多讀者無界通道與有界通道的等待者不會被取代,而是排隊(每個約 0.4 KB),下一筆寫入時釋放。
- 證據:gcdump(每次先做完整 GC)核心存活堆積 31.3 MB(第 4 分鐘)→ 88.4 MB(第 14.5 分鐘),`OperationCanceledException` 24 606 → 86 824 個(每秒多約 100 個 = 4 條狀態連線 × 25 Hz;合成客戶端 2、學員 1、雷達服務 1);counters 的例外計數每 10 s 約 1000 次(修正後 0);受控堆積 4 條連線 5.3 MB/min、`--no-radar`(3 條)4.0 MB/min,與連線數成正比;`DOTNET_GCHeapHardLimit=0x6400000`(100 MB)時第 15 分鐘 `Out of memory.` 中止(結束碼 134)。
- 修正:`ChannelWaitSet<T>`(`src/SimCore.Host/ChannelWaitSet.cs`)把未完成的等待留到下一圈繼續用,每個通道同時最多一個等待者;回覆 > 事件 > 狀態的優先順序與結束條件不變。回歸測試 `ChannelWaitSetTests`(確定性,不量記憶體):通道閒置時連續 200 次喚醒拿到的是同一個未完成的等待、沒有被取消、建立的等待數 = 喚醒次數 + 2;伺服器層以 `SendWaitsStarted` 確認送出迴圈每送一幀只新建 1 個等待(修正前 3 個)。突變驗證:改回每圈新建時 3 項失敗;送出迴圈改回原本的寫法(不經 `ChannelWaitSet`)時伺服器層那項失敗。
- 修正後:e07(預設 gen0 預算,25 分鐘)受控堆積 8.5 → 9.6 MB(0.08 MB/min)、gcdump 存活 9.3 MB(第 12 分鐘)→ 9.8 MB(第 23 分鐘),上述型別不再累積;e08(100 MB 上限)25 分鐘不中止,受控堆積 5.5 → 10.0 MB(0.09 MB/min)、RSS 110 → 121 MB、例外 0。剩下的緩慢上升來源未查(推測是倒帶快照裡隨模擬時間增加的評估紀錄;證據不足)。
- 雷達服務 `RadarWebSocketServer` 是同樣的寫法,但兩個通道(Frames / Json)都是有界通道、Json 持續有寫入,等待者在下一筆寫入時就喚醒:另對雷達收的 counters 顯示受控堆積 25–30 分鐘斜率 0.03–0.07 MB/min(gen2 約 4.6 MB)持平,當時未修改(雷達的 RSS 在第一次 GC 前同樣有 gen0 預算填充,RSS 不是「不是洩漏」的證據)。**後來的 2 小時搜尋證實**:雷達待機或失效時 Frames 沒有影像幀,每則 Json 就在 Frames 多排 1 個等待者,已改用同一份 `ChannelWaitSet`(第 4 點)。`VhfRelayServer`、`RadarReplayClient` 是單一通道的 `while (await WaitToReadAsync(...))`,第三十輪的重現程式確認不累積。
- 連線關閉後的資源(第三十輪審查 C-F1;修正前就存在,與上述洩漏無關,soak 的連線數固定所以不影響 soak 判定):Linux / macOS 的受控 `HttpListener` 在 WebSocket 連線結束後,只 `WebSocket.Dispose()` 不會關閉底層 socket(`AcceptWebSocketAsync` 的 WebSocket 建在不擁有 socket 的 NetworkStream 上),`HttpConnection` / `HttpListenerContext` / `Socket` 一直留在 listener 內部的連線表,伺服端也不關 TCP。審查探針(同一程序內反覆建立再關閉 2000 條連線,一半正常關閉、一半 Abort):每條留下 1 個檔案描述元(fd 318 → 2320)與約 15 KB 受控記憶體(gcdump:HttpConnection、HttpListenerContext 等各約 2200 個)。修正:三個 WebSocket 伺服器(`WebSocketStateServer`、`VhfRelayServer`、`RadarWebSocketServer`)的收尾在 `socket.Dispose()` 之後呼叫 `HttpListenerStarter.ReleaseWebSocketConnection`(`ctx.Response.Abort()`);修正後同一探針 fd 116 → 120、受控堆積每條約 2 B。回歸測試 `WebSocketConnectionReleaseTests`(SimCore.Tests:狀態、VHF;Sensors.Radar.Tests:雷達;確定性,不量 fd 或記憶體):以最小的原始 TCP WebSocket 客戶端完成關閉握手後,伺服端必須在 10 s 內關閉 TCP(讀到 EOF);修正前三項都逾時失敗(突變驗證:拿掉三處呼叫時 3 項失敗)。**Windows(http.sys)是另一套實作,是否有同樣問題、`Abort()` 有無副作用都未實機驗證,所以 Windows 上不呼叫、測試也只在非 Windows 執行**;船上筆電是 Windows,需要時以實機反覆連線再關閉(例如教官筆電重連)觀察控制代碼數。

**2. 核心與雷達:gen0 預算填充(不是洩漏)**
- 兩個主控程序的 `runtimeconfig.json` 沒有 GC 設定(工作站 GC、Concurrent 開為 .NET 8 預設)。工作站 GC 的 gen0 預算約 L3 / 2:本容器 counters 的 `Gen 0 GC Budget` 為 136 MB(10^6 B,= 130 MiB;探針量到兩次 GC 之間配置 129.6 MiB)。第二十六輪 L3 504 MiB 的容器推論約 252 MiB(= 264 MB;依 L3 / 2 推論,當時未量);船上內顯筆電 L3 8–12 MB 推論約 4–6 MB(**未實測**)。
- 第一次 GC 前 RSS 以配置速率上升(核心約 35 MB/min、雷達約 55 MB/min),本容器核心約第 3.3 分鐘、雷達約第 2 分鐘才第一次 GC,之後重用同一批已提交的記憶體;第二、三次 GC 前後 GC 已提交量再上一兩個階梯(核心 134 → 144 MB)。第二十六輪看到的「第 7 分鐘起持平」是兩次 GC 之間的平台,不是沒有洩漏。

| 實驗(核心;未註明者為修正前) | 設定 | RSS MB(soak 取樣):起 → 5 / 10 分 / 末 | 受控堆積 MB(第一次 GC 後 → 末;斜率) | 截取點:合格 / 無法判定 / 不通過 | 說明 |
|---|---|---|---|---|---|
| e01 基準 30 分 | 預設 | 97 → 231 / 285 / 392 | 26.5 → 150.7(5.2 MB/min) | 0 / 13 / 39 | gen0 預算 136 MB;第一次 GC 約 200 s |
| e02 12 分 | `DOTNET_GCgen0size=0x2000000` | 99 → 155 / 196 / 204 | 12.0 → 70.7(5.3) | 0 / 4 / 12 | 預算 33 MB:成長只是提前開始 |
| e03 12 分 | `DOTNET_GCHeapHardLimit=0xC800000`(200 MB) | 96 → 154 / 187 / 200 | 9.6 → 72.2(5.4) | 0 / 3 / 13 | 預算 20 MB |
| e09 12 分 | `GCgen0size=0x2000000`、`--no-radar`(3 條連線) | 95 → 154 / 182 / 190 | 11.2 → 54.0(4.0) | 0 / 4 / 12 | 與連線數成正比 |
| e05 25 分 | `GCgen0size=0x2000000` + 2 次 gcdump | 98 → 177 / 211 / 292 | 12.0 → 141(5.4) | 0 / 4 / 38 | gcdump 存活 31 → 88 MB |
| e06 25 分 | `DOTNET_GCHeapHardLimit=0x6400000`(100 MB) | 96 → 145 / 176 / 201(15 分) | 5.6 → 87.6(5.4) | 0 / 3 / 20 | 第 15 分鐘 `Out of memory.` 中止 |
| 實驗 B 10 分 | `DOTNET_GCgen0size=0xFC00000`(預算 264 MB) | 96 → 286 / 361 | 45.7 → 45.7(只 GC 1 次) | 0 / 12 / 0 | 第一次 GC 約第 7 分鐘;懷疑者:修正前提案的規則把它判合格;現行規則 GC 次數不足 → 無法判定 |
| r30-v-prefix 10 分(審查) | soak 腳本預設 `--gen0-budget-mb 6` | 94 → 149 / 185 / 185 | 9.0 → 65.8(5.7) | 0 / 3 / 9 | **不通過**:受控堆積 5.749、GC 已提交量 6.033 MB/min(RSS 增量 60.7 MB 未超過 64,判出洩漏靠斜率);例外 6 萬次;`gitRev` null(在 `git worktree` 執行) |
| r30-v-prefix-g0 10 分(審查) | `--gen0-budget-mb 0`(預算 129.7 MiB) | 99 → 232 / 282 / 282 | 26.5 → 46.5(只 GC 2 次) | 0 / 12 / 0 | 無法判定:GC 2 次(< 3) |
| e07 修正後 25 分 | 預設 + 2 次 gcdump | 95 → 218 / 232 / 244 | 8.5 → 9.6(0.08) | 5 / 26 / 11 | 預算 136 MB:GC 已提交量的階梯與 gcdump 的 EventPipe 緩衝(每次約 +9 MB)讓已提交量斜率在 17.5 分鐘以前超過門檻;22.5 分鐘起合格 |
| e08 修正後 25 分 | `DOTNET_GCHeapHardLimit=0x6400000` | 93 → 108 / 114 / 121 | 5.5 → 10.0(0.09) | 5 / 37 / 0 | 預算 10 MB;不中止;22.5 分鐘起合格 |
| r30-v-fixed 修正後 10 分(審查) | soak 腳本預設 `--gen0-budget-mb 6` | 91 → 117 / 124 / 124 | 5.4 → 8.7(0.086) | 0 / 12 / 0 | **無法判定**(穩定段不夠長;受控堆積、GC 已提交量 0.03 MB/min、增量 9.4 MB 通過,例外 0)。先前的全段規則判不通過:RSS − 已提交量 1.443 MB/min(審查 S-F1) |
| r30 修正後 12 分(`r30-fix-default`) | 同上 | 93 → 118 / 124 / 124 | 8.4 → 8.9(0.08) | 0 / 16 / 0 | **無法判定**(穩定段不夠長);預算 5.7 MiB、GC 62 次、例外 0、GC 已提交量 22 MB 持平。先前的全段規則判不通過:RSS − 已提交量 1.23 MB/min(200–530 s 階梯式 +10 MB 後持平;最大一階約 423 s +4 MB,與 JIT 方法數增加約 600 個、第 43 個組件載入同時)。`meta.json` 的 `gitRev` 是 `0a47ca1`(修正前的提交),實際執行的是工作樹裡未提交的修正(6.2 節「版本」) |
| r30 修正後 25 分(`r30-fix-default-25`) | 同上(另旁路取樣 smaps) | 91 → 116 / 120 / 123 | 5.4 → 9.7(0.07) | 5 / 37 / 0 | **合格**(六項):GC 147 次、例外 0、GC 已提交量 −0.02、穩定段 RSS − 已提交量 0.361、RSS 0.132 MB/min、增量 11.9 MB;22.5 分鐘起合格,之前無法判定;截到 1050–1080 s(穩定段約 330 s)RSS 斜率 1.19 / 1.08,穩定段因此至少 600 s。雷達(另收 counters)受控堆積 0.03 MB/min、RSS 101 → 103 MB |
| r30-fix-final 修正後 25 分(本輪) | soak 腳本預設(`--clients 2 --no-web`;含 HttpListener 連線釋放的修正) | 93 → 117 / 121 / 119 | 5.4 → 9.7(0.071) | 5 / 37 / 0 | **合格**(六項):掉拍 0、tick 率 50/s、延遲 p50/p95/max 14/18/39.4 ms、GC 147 次(預算 5.72 MiB)、例外 0、GC 已提交量 −0.147、穩定段 RSS − 已提交量 0.022、RSS −0.296 MB/min、增量 2.6 MB;`gitDirty` true(修正未提交),`hostDllSha256` 對應實際執行的程式 |

截取點:把該次執行截到 4 分鐘起每 30 s 的各時間點(截取點 = 取樣只留前 T 秒、counters 只留到最後一筆取樣),各以本節的規則(6.3 節)判定 RSS 準則,列合格 / 無法判定 / 不通過的個數。修正前的執行沒有一個截取點合格;修正後、gen0 預算小(soak 預設 6 MB 或 e08)的執行沒有一個截取點不通過,22.5 分鐘起(穩定段 ≥ 600 s)合格;e07(預設預算 136 MB)見 6.7 節。

GC 設定的注意事項(第三十輪探針與 soak 實測,.NET 8.0.33,x64 Linux):
- `DOTNET_` 前綴的 GC 值一律十六進位(`2000000` 與 `0x2000000` 同為 32 MiB)。
- Linux 上環境變數名稱區分大小寫:`DOTNET_GCgen0MaxBudget`(小寫 g)無效,正確是 `DOTNET_GCGen0MaxBudget`;第二十六輪 `r26-gen0cap32` 核心第 10 分鐘仍 354 MB 就是這個原因(Windows 的環境變數不分大小寫)。
- .NET 8 的 gen0 預算只能以環境變數調:`runtimeconfig.json` 的 `System.GC.Gen0Size`、`GCgen0size`、`GCGen0MaxBudget` 實測皆無效;`DOTNET_gcConcurrent=0`、`DOTNET_GCConserveMemory=5` 不改變預算;Server GC(`DOTNET_gcServer=1`)預算 641 MB;`GCHeapHardLimit` 把預算壓到約上限的 1/10。Windows 上 `DOTNET_GCgen0size` 的效果未在 Windows 實測(報告的「GC 與 gen0 預算」節列出 counters 量到的實際預算,可據以確認)。
- 產品不設定 gen0 預算(不改 `runtimeconfig.json`、啟動腳本不設環境變數):洩漏修正後,大快取機器上剩下的是一次性的預算填充與有上限的已提交量階梯;soak 腳本預設 6 MB 只是為了讓 10–120 分鐘的量測在雲端機器上也分辨得出洩漏。

**3. RSS 判定規則的理由(6.3 節)**
- RSS = GC 已提交量 + 非 GC 記憶體(原生配置、執行緒堆疊、JIT 程式碼…)。洩漏的存活物件反映在受控堆積(gen0 預算大時 RSS 可以很久不動,實驗 B);GC 自己多提交(碎片)反映在 GC 已提交量;原生洩漏反映在 RSS − 已提交量。各自以斜率判定,再加 RSS 總斜率與增量門檻,全部通過才合格。
- 不用「最後三分之一」或「RSS 平 **或** 受控堆積平」:第三十輪的第一版提案(暖機延到第一次 GC 之後、兩者擇一)在實驗 B(修正前、gen0 預算 264 MB、10 分鐘)六項全合格——第一次 GC 在第 7 分鐘,之後受控堆積與 RSS 都還沒機會上升。現行規則要求擬合時段內 GC ≥ 3 次(第一次 GC 之後至少再兩次,受控堆積才有斜率),不足就是「無法判定」(不合格並註明,建議延長或降低 gen0 預算)。
- GC 已提交量的斜率是懷疑者提出的規則(GC 次數、受控堆積、RSS − 已提交量、增量)之外另加的:合成資料「已提交量每分鐘 +4 MB、受控堆積持平」在 10 分鐘內 RSS 增量只有約 40 MB,不加這一項會判合格。
- **非 GC 與 RSS 總斜率只看穩定段**(max(暖機, 720 s) 起、至少 600 s;第三十輪審查 S-F1):前約 12 分鐘「RSS − GC 已提交量」有一次性的階梯(學員腳本 6 種故障每 120 s 輪到一種新的、最後一種約 660 s;約 280 s 一階不是學員指令造成,推測是情境事件;約 430 s 一階與第一次注入 echoSounderFailure、第 43 個組件載入、JIT 方法數 +665 同時),全段斜率在 10–12 分鐘分不出有沒有洩漏:修正後 `r30-v-fixed`(10 分鐘)1.443 MB/min、修正前 `r30-v-prefix` 1.241 MB/min,同一量級;先前的規則因此把修正後、預設設定的 10 分鐘(`r30-v-fixed`)、12 分鐘(`r30-fix-default`)與 25 分鐘那次截到 12 分鐘以前都判「不通過」(階梯的時間點與 echoSounderFailure 等對照是審查以 counters 與學員紀錄比對的結果)。真正分得出洩漏的是受控堆積(5.749 對 0.086)與 GC 已提交量(6.033 對 0.03),這兩項仍從頭判,10 分鐘就判得出。穩定段至少 600 s:720 s 之後仍偶有一次性階梯(`r30-fix-default-25` 約 860–930 s +4 MB),一次 4 MB 的階梯在 W 分鐘的視窗中最多讓斜率 1.5 × 4 / W MB/min(5 分鐘 1.2、10 分鐘 0.6);截到 1050–1080 s(穩定段約 330 s)時 RSS 斜率 1.19、1.08。受控堆積與已提交量的擬合時段也至少 300 s(GC 已提交量在前一兩分鐘有階梯,2 分鐘的執行斜率可達 3 MB/min)。
- **RSS 總斜率**(穩定段;第三十輪審查 S-F2):各分量各自略低於門檻時(合成資料:受控 0.9 + 原生 0.9 MB/min),RSS 合計約 1.8 MB/min,舊規則 30 分鐘仍合格(增量約 50 MB),要約 35 分鐘以上才因增量攔下;加這一項後 30 分鐘就不通過。以真實資料重判不改變修正後執行的判定(見上表)。
- 逐項判定:任一項確定不通過 → 不通過;沒有不通過、但有資料不足的項目(穩定段不夠長、擬合時段不到 300 s)→ 無法判定;GC 次數不足 → 整個準則無法判定。修正前 10 分鐘的執行因受控堆積與已提交量確定不通過 → 「不通過」;修正後 10 分鐘的執行 → 「無法判定」(兩者都不合格,但不再把正確的程式說成不通過)。
- 暖機在有 counters 時延到第一次 GC 之後一個取樣間隔(gen0 預算填充不算成長);受控堆積的斜率從第一次 GC 起算、不受暖機影響,所以延長暖機不會掩蓋洩漏。
- 懷疑洩漏時以 `dotnet-gcdump collect -p <pid>` 相隔 10 分鐘以上取兩次、比較 `dotnet-gcdump report` 的型別數量(gcdump 會在目標程序開 EventPipe 緩衝,RSS 約 +9 MB,之後的非 GC 斜率不可採信)。

**4. 2 小時內無上限成長的搜尋:主控程式、操控台服務、雷達(2026-11-22,第三十輪後段;容器 Linux、.NET 8.0.33)**

搜尋者列出候選並寫探針,驗證者逐項實測(探針與數字不進版控:`build/tmp/sweep30/`,`verify-host.json`、`verify-radar.json`);修正後以同一支探針(或精簡版,`build/tmp/sweep30/host-fix/`、`radar-fix/`)重量。

| 項目(嚴重度) | 機制 | 修正前(探針) | 修正 | 修正後(同一探針) |
|---|---|---|---|---|
| 接受迴圈結束(major,功能性) | 狀態 WebSocket 與 VHF 的接受迴圈在迴圈本體裡 await 健康檢查的回應;`GET /` 後立刻 RST 時寫出丟例外,迴圈結束、沒有 log,之後所有站都連不上,每次重連在受控 HttpListener 內部留下 1 條沒人取走的連線 | 新程序 6 次:WS 6/6、VHF 5/6 第 1 次 RST 就死(16 次嘗試 12 次觸發);死後每秒重連 300 s:成功 0/300、socket 4 → 309、fd 106 → 420、HttpConnection 305 個、受控堆積 5.3 → 10.1 MB | `HttpListenerStarter.AcceptLoopAsync`(四個伺服器共用,雷達一份相同實作):單一請求的處理一律在背景(健康檢查 `RespondTextAsync`、握手失敗 `CloseResponse`,寫出或關閉失敗即 Abort);取得 context 的非關機例外記 log(有次數限制)後繼續,只有權杖取消或 listener 關閉才結束 | 暖機後 WS、VHF 各 20 次 GET + RST 都沒死;新程序 6 次 × 60 次 × 2 個埠 0 次;之後每秒重連 120 s:成功 590/590、socket 4 → 4 |
| 錄影上傳中途 RST(nit) | 讀 body 丟例外 → 500 也寫不出去 → Close / Abort 都沒執行 | 100 次:socket 4 → 104、HttpConnection 100、受控堆積 +8.7 MB,30 s 與約 4 分鐘後都不釋放;GET + RST 100 次 socket +7 | 回應的寫出一律「成功即 Close、失敗即 Abort」,不拋例外;宣告長度沒收滿的片段不寫入 | 100 次:socket 4 → 4、HttpConnection 7(關閉中)、受控堆積 +0.9 MB;GET + RST 100 次 socket +1(探針自己的 keep-alive) |
| 錄影 body 卡住(minor) | 對端消失(不送 FIN / RST)時讀 body 沒有逾時(Linux 受控 HttpListener 沒有 body 逾時),永久持有依 Content-Length 預先配置的緩衝與連線 | 20 條(宣告 1 MB、送 2 KB):3 s 與 150 s 後受控堆積 35.8 MB、socket +20 | 每次讀取 30 s 沒有進度就 Abort(`BodyIdleTimeout`;`WaitAsync`,不留計時器);預先配置最多 4 MB;錯誤回應前的排空也有逾時 | 3 s 後 27.8 MB(預先配置),逾時後(+40 s)7.7 MB、socket 回到 4(伺服端關閉全部 20 條),+130 s 時 HttpConnection 0、5.9 MB(受控 HttpListener 自己的 90 s 請求計時器持有連線物件到那時,有上限);之後同一序號的片段照常寫入 |
| 被放棄的錄影(nit) | 開始後沒有 stop 的錄影(分頁重新整理、當機、stop 送不出去)留到主控程序結束 | 200 次:active 200、fd 108 → 312、FileStream / IncrementalHash 各 200 | 5 分鐘沒有任何片段請求、且沒有片段正在上傳 → **暫停寫檔**(關閉 `.part`,釋放檔案代號與寫入緩衝;序號與 SHA-256 狀態保留),下一個片段接續寫入;暫停中的錄影超過 16 份時最後活動最早者定稿(`stopReason: idleTimeout`,`stoppedUtc` = 最後一次活動),主控程序結束時暫停中者同樣以 `idleTimeout` 定稿;每 30 s 檢查(`docs/design/screen-recording.md` 第 3 節)。第一版在 5 分鐘時直接定稿,審查 REV-MEDIA 指出畫面靜止、沒有音訊時 MediaRecorder 可能數分鐘不吐片段,定稿後下一片 410、教官站停止錄影,因此改為暫停 | 200 次 + 之後才開始的 1 份「畫面靜止」錄影(`host-fix2/probeE3-suspend.mjs`):第 330 s 起 active 201 → 16(皆暫停)、fd 311 → 109(基準 107)、FileStream 201 → 0、IncrementalHash 201 → 16;靜止 345 s 後的第 2 片 200、接續寫入(stop:2 片、98 304 B、`client`)。同一探針跑第一版:第 330 s 起 active 0、fd 109,靜止錄影已被定稿,第 2 片 404(201 份同時定稿,已定稿清單只留 64 筆;只有它一份時為 410),教官站兩者都當成致命錯誤 |
| 操控台送往裝置的佇列(minor) | 無界通道:裝置仍送心跳但不讀(序列埠寫出每行 1 s 逾時)時 LED / DISP 行無上限累積,顯示延遲也無上限 | 300 s:佇列 49 → 1172(3.9 行/s,線性) | `DeviceLineQueue`:同類行(`LED,名稱`、`DISP,欄位`、`BUZZ`)只留最新、位置不變;其他行超過 64 行丟最舊(`src/Contracts/console-serial.md` 第 5 節) | 300 s:佇列 0–2 行(10 / 100 / 200 / 300 s 時 2 / 1 / 0 / 1),被同類新行取代 1171 行;寫出立即完成的對照組佇列 0、不合併 |
| 雷達送出迴圈的等待者(minor;R1、R3) | 每圈對 Frames / Json 各新建 WaitToReadAsync(有界通道 → 排隊);待機、失效或 `/radar/replay` 時 Frames 沒有資料,每則 Json 在 Frames 多排 1 個等待者與其在 `_cts` 上的註冊 | 待機 7200 模擬秒:Frames 等待者 7205、受控堆積 +2078 KiB、`_cts` 節點 7208(恢復發射後 free list 仍 7196);失效 1800 s × 2 客戶端各 1808;重播 1 萬則 9999、+3085 KiB | `src/Sensors.Radar/Net/ChannelWaitSet.cs`(與 SimCore.Host 同一份實作) | 待機 7200 s:等待者 F1 / J1、+5 KiB、`_cts` 節點 4(free 4);失效 1800 s × 2:各 F1、+110 KiB;重播 1 萬則:F1、+89 KiB |
| 雷達主控程式的 ARPA 對真值紀錄(minor;R2) | 每次追蹤清單的每筆比對都存進兩個 List,只在結束摘要讀 | E02 ×4:cpaErrors 6.4 筆/模擬秒、陣列容量 2048 → 32 768(1.3 MB);推估 2 小時 4–28 MB | `ArpaTruthSummary`:累計值與每組(追蹤編號 × 真值目標)的最後一筆;摘要輸出格式與數值不變(單元測試以 5 萬筆比對修正前的 LINQ 寫法,逐位元相同) | E02 ×4、牆鐘 330 s(1320 模擬秒、6174 筆比對):沒有隨筆數成長的陣列(35 組配對,`PairLast[]` 2 KB);受控堆積 7.5 MB(60 s)→ 7.4 MB(300 s)(修正前 7.8 MB(120 s)→ 10.2 MB(720 s));摘要格式相同 |
| 雷達客戶端的分段訊息(nit;R6) | 不結束的分段訊息 MemoryStream 無上限 | 送 64 MiB 未結束:受控堆積 +64.4 MiB,結尾後 +128.4 MiB 直到連線結束 | 單則上限 1 MB(`MaxMessageBytes`,比照 VHF),超過以 MessageTooBig 關閉 | 送 64 MiB:受控堆積 +0.3 MiB |

回歸測試(確定性,不量記憶體):`AcceptLoopResilienceTests`(SimCore.Tests:狀態、VHF、錄影端各 30 次 GET + RST 後仍可連線;錄影端另以反射數 Linux 受控 HttpListener 的連線表 `_connections`,必須回到基準)、`MediaUploadTests`(body 卡住逾時中止、上傳中途 RST 後連線表回到基準、閒置暫停寫檔與暫停後接續寫入〔可手動前進的時鐘;Linux 以 /proc/self/fd 確認已關檔;內容與 SHA-256 不變;片段上傳中不暫停;主控程序結束時以 idleTimeout 定稿〕、暫停中的錄影上限與背景檢查)、`DeviceLineQueueTests`(Console.Serial.Tests:分類、合併與位置、上限、橋接在裝置卡住時佇列 ≤ 12 行且恢復後收到各欄最新值)、`RadarServerBoundsTests`(Sensors.Radar.Tests:等待者集合、送出迴圈在 Frames 閒置時每則 Json 只新建 1 個等待、MessageTooBig、GET + RST、`ArpaTruthSummary` 與修正前寫法相同)。突變驗證:接受迴圈改回在迴圈裡 await、錄影端寫出失敗不 Abort、body 讀取不設逾時時對應的 5 項失敗;雷達送出迴圈改回每圈新建、拿掉訊息上限時 2 項失敗;錄影閒置改回直接定稿(第一版)時 3 項失敗;操控台佇列不合併且無上限時 2 項失敗。雷達的 GET + RST 測試在修正前也通過(雷達原本把寫出包在 try 裡;只是防回歸)。

未修改(記錄):紀錄檔約 0.95 GB / 2 小時(E05,含 25 Hz 狀態行;沒有剩餘空間檢查,同一秒內切換兩次情境時前一份紀錄被 `FileMode.Create` 蓋掉;RSS 不成長)、操控台的 `_buzzed` 只增不減(每次可聽警報約 100 B,一場小於 50 KB)、操控台 log 量(舵令連續 10 Hz 約 625 B/s、裝置不在 160 B/s;磁碟)、錄影片段的 LOH 配置(GC 已提交量一次性 +7 MB 後持平)、倒帶快照每份隨操作次數變大(另案處理)、雷達的核心離線指令佇列 / 重播相異客戶端量程 / `SimStateClient` 斷線留下的已取消等待(量小或下一次操作即清)、狀態 WebSocket 接收迴圈沒有單則訊息上限(loadScenario 可帶內嵌情境,上限待定)。**Windows(http.sys)未驗證**:反覆重連的控制代碼(`ReleaseWebSocketConnection` 在 Windows 不 Abort)、GET + RST 在 http.sys 上是否同樣丟例外(修正同時涵蓋兩者)、http.sys 的 EntityBody 逾時(約 2 分鐘;本修正的 30 s 先到)。

### 6.7 限制

- 合成客戶端在同一個 Node 程序內,不含瀏覽器繪圖與視景 GPU 負載;內顯筆電的幀率、CPU 與延遲需實機以同一工具(`--minutes 120`)重跑,並與視景同時執行(`start-ship-laptop.ps1` 啟動視景後再跑 `soak-test.ps1 -SkipBuild`,或改用 `--port-base` 避開 8765 系列埠)。
- 延遲為牆鐘估計而非封包往返;教官筆電直連的網路延遲需實機(追溯表第 3 節)。
- 情境固定(預設 E05,不切換情境);學員腳本只做溫和操作,不涵蓋倒帶/重播/情境載入的記憶體行為。
- RSS 準則只看核心;dotnet-counters 只附掛核心,雷達服務的 RSS / CPU 列為參考(第三十輪另對雷達收過 counters,見 6.6 節)。
- soak 的連線數固定(不反覆重連),量不到連線建立 / 關閉的資源洩漏;第三十輪審查找到的 HttpListener 連線未釋放(6.6 節第 1 點)由 `WebSocketConnectionReleaseTests` 涵蓋,只在 Linux / macOS;Windows(http.sys)未驗證。異常的連線(GET 後 RST、上傳中途 RST、body 卡住、被放棄的錄影)由 6.6 節第 4 點的回歸測試涵蓋,soak 不做。
- `--gen0-budget-mb 6` 是對船上內顯筆電的**推論**(L3 / 2,未實測)。gen0 預算小 → GC 次數多(12 分鐘 62 次,GC 暫停合計約 0.1 s),50 Hz 與延遲的結果包含這個影響;實機 2 小時另以 `--gen0-budget-mb 0` 跑一次,量執行階段預設下的真實行為(報告「GC 與 gen0 預算」節會列出 counters 量到的實際預算)。
- 大快取機器上以 `--gen0-budget-mb 0` 執行時,約 20 分鐘以內常因 GC 次數不足(無法判定)或 GC 已提交量的階梯而不合格(6.6 節 e07:17.5 分鐘以前的截取點因已提交量斜率不通過,22.5 分鐘起合格),這是量測方法的限制,不代表有洩漏;判讀以受控堆積與 gcdump 為準。
- **短於約 22 分鐘的執行不足以判定 RSS 準則**:「RSS − GC 已提交量」在前約 12 分鐘有一次性的階梯(新的程式路徑第一次執行),之後也偶有數 MB 的一階,所以非 GC 與 RSS 總斜率只看穩定段(720 s 起、至少 600 s;6.3、6.6 節),較短的執行該兩項判「無法判定」(結束碼 1,報告寫明原因)。soak 腳本預設 `--minutes 25`;10 分鐘的執行仍能判出受控堆積與 GC 已提交量的洩漏(修正前 10 分鐘 → 不通過),但判不出原生記憶體的緩慢洩漏。第三十輪(修正前的全段規則)修正後的兩次執行:12 分鐘那次不合格(1.23 MB/min),25 分鐘那次合格(0.51 MB/min)。旁路 smaps(25 分鐘那次,第 2 → 24 分鐘):GC 區 RSS 22.2 → 21.6 MB(與 counters 的已提交量一致)、RssFile(組件與原生程式庫的檔案頁)39.5 → 39.7 MB、RssShmem(.NET 在 Linux 以 memfd「doublemapper」雙重對映的可執行記憶體,JIT 程式碼與 stub)23.5 → 25.0 MB、其他匿名映射(原生配置、載入器堆積等,內容未查)13.9 → 24.7 MB,其中前 8 分鐘 +7.2 MB、之後到第 20 分鐘再 +3.5 MB 後持平。建議容器量測用 `--minutes 25` 以上,實機仍以 2 小時為準;其他匿名映射的來源未查(若 2 小時執行的後半段仍上升,先以 smaps 與 `dotnet-dump` 查原生配置)。
