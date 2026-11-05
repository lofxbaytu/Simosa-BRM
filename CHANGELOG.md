# 變更紀錄(CHANGELOG)

> 版本號 = `VERSION` 檔(`<年>.<月>.<輪次>`,例如 2026.11.24;更新包只接受版本遞增,此方案大於舊的 `0.<日期>.<時分>` 後備與文件示例 2026.11.1,船上曾套用的任何版本都不會把它判為降版;`tools/build-release.*` 無 `--version` 時以此為發行版本,與更新包版本規則相同:1–4 段數字)。每輪一條,只列新增/修正/已知限制的摘要;細節、驗證指令與測試數見 `docs/dev-log/2026-10-08-工作階段交接.md` 各輪與各 `docs/design/*.md`。日期為該輪提交日。

## 2026.11.25 — 2026-11-05(第二十五輪)

- 修正:SimCore.Host 三個 HttpListener 伺服器與雷達服務在埠被占用時不再崩潰——每次嘗試用新的 HttpListener(萬用 → localhost+127.0.0.1),VHF/錄影埠失敗只停用該服務、核心照常執行;狀態埠失敗印訊息、寫完紀錄 footer 後結束碼 2;雷達服務埠失敗結束碼 2;`TryStart/StartError/Started`,DisposeAsync 可重複;PortInUseTests(SimCore 8、Radar 3)。
- 新增:情境編輯器 `ship.mmsi` 欄位(驗證訊息與核心一致;字串數字可讀入;舊草稿由 raw 補回)。
- 修正:soak 報告 RSS 擬合排除客戶端收尾樣本;sh/ps1 收尾不取樣;重播模式狀態推送訊息改於啟動時確認。
- 已知限制:第二十五輪樹上 3 分鐘 soak 的核心與雷達 RSS 約 38 MB/min 線性成長(第二十四輪 10 分鐘為 1.8 MB/min、前 3 分鐘約 9 MB/min),是否為暖機期或本輪引入待 10–120 分鐘實跑比對;--port 被占用且有 --record 時留下只有 header+footer 的空紀錄;Windows http.sys 路徑未實機驗證。

## 2026.11.24 — 2026-11-05(第二十四輪)

- 新增:Gateway.Nmea THS(10 Hz,電羅經失效為模式 V)、自船 `!AIVDO`(類型 1 每 10 s、類型 5 每 6 min;MMSI 取情境 `ship.mmsi` 或 particulars `mmsi`,皆無則不送並記 log)、IEC 61162-1 ed.5 ALF/ALC(ALR 保留);`nmea_check` 補四種句型與一致性檢查;夾具重錄(524 句)。
- 新增:config `ports.nmeaTargets/nmeaBind/nmeaFile/vhfWs/mediaHttp`,啟動腳本傳 `--nmea-target/--nmea-bind/--nmea-file`,埠預檢加 8767/8768。
- 新增:長時間穩定性(soak)測試工具 `tools/soak-test.{sh,ps1}`、`tests/soak/`(合成客戶端、學員腳本、報告與判定;容器 10 分鐘:掉拍 0、tick 50.001/s、延遲 p95 8 ms、RSS 斜率 1.8 MB/min 未達準則)、`VERSION`(方案改為 年.月.輪次)、本檔、README「目前狀態」、`docs/design/testing.md` 第 6 節。
- 已知限制:兩船真實 MMSI 未填(正式情境不送 VDO);網頁情境編輯器存檔會丟 `ship.mmsi`;ALF 製造商碼 SIM 非標準識別碼;Host 在 VHF/錄影埠被占用時仍會崩潰(由啟動腳本預檢避免);soak 延遲量到的是相對最快一幀的額外延遲,固定鏈路延遲以學員指令往返(p50 26 ms)補充;2 小時與內顯機需實機。

## 0.23.0 — 2026-11-05(第二十三輪)

- 新增:USB 序列操控台服務補完(`src/Console.Serial` 主程式/橋接、83 項測試、Teensy 4.0 參考韌體、啟動腳本 `--console`、發行版 app/console)。
- 新增:Playwright 端對端 8 個 spec 14 案例、`tools/run-e2e.sh`、CI 工作「端對端」、`docs/design/testing.md`。
- 修正:核心凍結時 200 ms 心跳幀(教官站「已凍結/恢復」可用)、教官站左欄四列、雷達 `--localhost-only` 接受 127.0.0.1、run-ps-tests 的 -WhatIf 斷言、ConsoleBridge 重開訂閱、ECDIS 本地 CPA 誤判引水船。
- 已知限制:操控台硬體與韌體未實機;e2e 的 CI 工作未在 GitHub 實跑;ECDIS 示範模式仍用本地 CPA 門檻;soak 的 2 小時與內顯機結果需實機。

## 0.22.0 — 2026-11-05(第二十二輪,部分)

- 新增:自建簡化 ECDIS 頁 `ecdis.html`(投影、安全等深線、航線監控、AIS 目標 CPA、本地警報;唯讀)與 `--page ecdis`。
- 新增:NMEA 一致性檢驗 `simosa_brm.nmea_check`、Host `--nmea-file/--nmea-target/--nmea-bind`、OpenCPN 設定範例與實機驗收表。
- 已知限制:序列操控台與端對端只有骨架(第二十三輪補完);本輪未做再現式審查。

## 0.21.0 — 2026-11-01(第二十一輪)

- 新增:內建 VHF 角色扮演頻道(`VhfRelayServer` 8767、`<brm-vhf>`、紀錄 `vhf` 行、講評通訊統計)取代 Mumble。
- 新增:練習錄影管線(`MediaUploadServer` 8768、教官站錄影面板、紀錄包納入 `media/`)。
- 新增:pwsh 專案內工具與 `tools/run-ps-tests.sh`、Inno Setup 安裝腳本與 `build-release -Installer`、航跡圖流場層與情境編輯器海圖點選編輯。
- 修正:授權對話框期間放開 PTT 卡在 transmitting 等 9 項。
- 已知限制:VHF 非真實傳播且不錄音;安裝程式與外殼需 Windows 實機;底圖為示意幾何。

## 0.20.0 — 2026-10-29(第二十輪)

- 新增:解耦 1 自由度橫搖模型(`ship.roll`、`state.roll`、C#/Python 黃金夾具)、橫搖視景/傾斜儀、情境編輯器「環境進階」表單。
- 新增:WebView2 操船台外殼 `src/Shell.WebView2` + `Shell.Core`(kiosk、Runtime 偵測、`start-ship-laptop.ps1 -Shell`)。
- 修正:stop-all.ps1 觸發外殼確認對話框等 4 項。
- 已知限制:橫搖不回饋 3-DOF、係數 ASSUMED;外殼需 Windows 實機。

## 0.19.0 — 2026-10-29(第十九輪)

- 新增:練習中變更浪(`setEnvironment waves`,Hs 漸變)、雷達海況與儀表 SEA 方塊連動、空間變化流場 `current.field`。
- 新增:外力場與目標船體快取、Python 區域水深覆寫、`participants` 字串/物件相容。
- 修正:waves 欄位型別兩邊語意不一致、雷達多客戶端測試 flaky 等 5 項;Windows CI 近零值比對。

## 0.18.0 — 2026-10-29(第十八輪)

- 新增:浪平均二階漂移力 `environment.waves`、單向船間交互作用 `environment.shipInteraction`、時變流場(`current.table`/`tidal`)與 E06b 潮流情境;情境編輯器逐欄編輯擴充(草稿版本 2)。
- 修正:Python 快照缺 `current_overridden` 等 7 項。
- 已知限制:浪只有平均漂移力;交互作用單向且係數為文獻量級估計。

## 0.17.0 — 2026-10-29(第十七輪)

- 新增:潮汐模型 `environment.tide`(潮高表/調和分潮、有效水深、E18b)、教官站情境編輯器 `brm-scenario-editor`、月光模型與夜間環境光。
- 修正:Host 紀錄器交換與 `sessionInfo` 同鎖、每客戶端無界回覆通道(ack 不被丟棄)、記憶體版 IndexedDB 假物件測試升級路徑。

## 0.16.0 — 2026-10-23(第十六輪)

- 新增:核心拒絕無效情境 `scenarioLoadRejected`(狀態不變)、優雅停止(停止檔/訊號/`shutdown` 指令、footer `closedUtc`)、`sessionInfo`(參與者與休息時間確認)。
- 新增:報告站「教官與證書」分頁、`docs/驗收/STCW-A-I-12-與-DNV-對照.md`、課程大綱與評估者指引草案。
- 修正:教官站在核心回覆前清本地練習資料等 8 項;Windows CI 主控台 UTF-8。

## 0.15.0 — 2026-10-23(第十五輪)

- 新增:發行組裝 `tools/build-release.{sh,ps1}`(publish、manifest、簽章更新包)、船上/教官筆電啟動與停止腳本、`docs/ops/安裝與啟動.md`、需求追溯表。
- 新增:靠離泊預演自動組題(內嵌情境 `loadScenario args.scenario`、範本、簡化教官模式表單);雷達重播 `radarControl`、純 AIS 目標符號、`summary.radar`。
- 修正:埠占用仍回報成功、stop-all 收掉同步服務等 11 項;Windows CI WebSocket 關閉握手。

## 0.14.0 — 2026-10-22(第十四輪)

- 新增:雷達操作進紀錄(`radarControl`)、每客戶端獨立量程、AIS 融合;警報站別與責任轉移(`alertTransfer`、Category A);`state.utc`;視景繫纜。
- 修正:ecr 確認重設 BNWAS 等 6 項。

## 0.13.0 — 2026-10-22(第十三輪)

- 新增:3D 視景第一版(`Visual.Shared` + Godot 4.5 `Visual.Godot`:品質分級與自動降級、海面/天空/日夜、程序化船體、港口幾何、五視角、無頭煙霧驗證)。
- 修正:視景 5 項與資料路徑 3 項(pytest 暫存、uv 受管 Python 路徑)。
- 已知限制:無實船模型、Godot 匯出範本未取得、內顯機幀率待實測。

## 0.12.0 — 2026-10-18(第十二輪)

- 新增:E19 火災/ISPS、E20 綜合評估、E22a–d 教室迷你情境(規劃書第 8.1 節情境全部到齊);`injectFault recordOnly`、時間表 `tugControl` 與拖船 `unavailable`。
- 修正:E15 計時起點、絞盤遲滯、E20 組合故障等 9 項。

## 0.11.0 — 2026-10-18(第十一輪)

- 新增:E15/E16/E17/E18/E21 情境與 37 個參數鍵;E06 岸壁效應啟用、E14 再碰回指標、雷達 S 頻故障與雨區;Python 外力參考實作(拖船/錨/碰墊/繫纜/岸壁,與 C# ≤ 1e-13)。
- 修正:E16 漁船關燈、E21 潮窗等 10 項。

## 0.10.0 — 2026-10-18(第十輪)

- 新增:E02/E03/E04/E06 情境與示意區域、岸壁效應模型(選用)、確認種類補齊。
- 修正:E06 橫流壓入航道等 10 項;測試紀錄每次執行自動清理(原累積 9.7 GB);CI 稽核紀錄共用鎖與浮點比對。

## 0.9.0 — 2026-10-17(第九輪)

- 新增:靠泊/錨泊評估 `BerthingTracker` 與 21 個指標、試俥目標入紀錄標頭、`engineStartFailure`、E12–E14 情境、教官站/操船台拖船/錨/繫纜介面、FSB2 壓載係數(識別比值轉移)、全部情境核心夾具。
- 修正:14 項(示範模式泊位原點、繫妥後輕碰計入接近速度等)。

## 0.8.0 — 2026-10-17(第八輪)

- 新增:拖船/錨鏈/碰墊/繫纜物理、`ManoeuvreTracker` 操縱量測、F01–F03/E10/E11 情境、講評報告與個人訓練紀錄(`report.html`)。
- 修正:審查 28 項(物理 6、量測/情境 9、報告 13)。
- 已知限制:FSB2 壓載無 MMG 係數(第九輪補);錨定描述與 STCW 對應為草案。

## 0.7.0 — 2026-10-17(第七輪)

- 修正:第六輪再現式審查 28 項(打包資安 5、打包正確性 9、警報 10、雷達 4);Python 補感測器/警報/BNWAS 指令與 E08/E09 夾具。

## 0.6.0 — 2026-10-17(第六輪)

- 新增:感測器故障層與 BAM/BNWAS(MSC.302(87)、MSC.128(75))、E08 電羅經故障;雷達/ARPA 服務(X/S、雜波、ARPA、試操船)與 PPI 頁;簽章更新包/紀錄包/直連同步 `simosa-pkg`。

## 0.5.0 — 2026-10-17(第五輪)

- 新增:C# 結構檢查對齊 Python、E05 核心夾具、簡化教官模式操舵面板、E09 全船失電情境、港區參數。
- 修正:1366×768 等版面。

## 0.4.0 — 2026-10-17(第四輪)

- 新增:教官站操舵模式 UI、示範模式目標船;Python 參數集與結構檢查對齊(八進位、多邊形自交)。
- 修正:第二輪程式審查 9 項(倒帶後事件分支、事件上限等)。

## 0.3.0 — 2026-10-17(第三輪)

- 修正:`steeringMode` 審查 5 項;E07/E01 網頁夾具以現行核心重錄,跨語言測試嚴格通過。

## 0.2.0 — 2026-10-17(第二輪)

- 新增:Python 參數集引用與跨語言測試、教官站目標船/倒帶標記/必要事件倒數、C# `steeringMode` 指令與 `state.steering`。

## 0.1.0 — 2026-10-17(第一輪:情境引擎)

- 新增:情境引擎(時間表觸發/動作、航線/走廊/檢查點、區域、評估規則與自動指標、公司程序參數集、簡報)、E07 舵機故障情境、紀錄 header v2 與 `summary` 行、Host `--validate`/`--rescore`、教官站事件流與時間表面板。
- 修正:四鏡頭審查 22 項。

## 0.0.x — 2026-10-07 ~ 2026-10-08(起步)

- 規劃書 v0.2–v0.10、專案骨架與契約、Python 參考 MMG 模型與 FSB1 識別(78 通過、7 xfail)、C# 核心與 MMG 移植(與 Python 位元一致)、目標船/AIS/COLREG、網頁儀器與教官站第一版、CI。
