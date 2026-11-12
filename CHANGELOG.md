# 變更紀錄(CHANGELOG)

> 版本號 = `VERSION` 檔(`<年>.<月>.<輪次>`,例如 2026.11.24;更新包只接受版本遞增,此方案大於舊的 `0.<日期>.<時分>` 後備與文件示例 2026.11.1,船上曾套用的任何版本都不會把它判為降版;`tools/build-release.*` 無 `--version` 時以此為發行版本,與更新包版本規則相同:1–4 段數字)。每輪一條,只列新增/修正/已知限制的摘要;細節、驗證指令與測試數見 `docs/dev-log/2026-10-08-工作階段交接.md` 各輪與各 `docs/design/*.md`。日期為該輪提交日。

## 2026.11.28 — 2026-11-12(第二十八輪)

- 守則(使用者指示):**所有要儲存在本機的資料放在專案(安裝)資料夾內,即 D: 或 G:;C: 只放應用程式本身;放在隨身碟會嚴重拖慢進度的項目,先取得同意才可放 C:**。位置表、例外開關與實機檢查見 `docs/ops/本機資料位置.md`;`CLAUDE.md` 同步。
- 修正(原本會寫到 C: 的項目):NuGet HTTP / 外掛快取與還原暫存(`nuget.config` 的對應鍵無效,改用環境變數)、TEMP/TMP(→ `build\tmp`)、瀏覽器端資料(草稿、參與者資料、訓練紀錄:以專屬 Edge 設定檔 `build\browser\edge` 開啟;找不到 Edge 時不再自動改用預設瀏覽器)、從網頁下載的檔案(→ `build\downloads`)、匯出版視景的 Godot 使用者資料(引擎 log、著色器快取 → `.cache\godot\appdata`)、npm 快取、Node 編譯快取、Playwright 轉譯快取。
- 修正:不再寫死 `D:\Simosa BRM` — simosa-pkg 的預設根目錄(程式所在的安裝根目錄或專案根目錄 → D: → G:,不落到系統磁碟)、安裝程式預設位置(D: → G:;裝在系統磁碟須確認,預設「否」,靜默安裝 `/ALLOWSYSTEMDRIVE=1`)、`setup-windows.ps1`(專案在系統磁碟時預設拒絕;`-AllowSystemDrive`)。
- 修正:所有 PowerShell 腳本改存 **UTF-8 with BOM + CRLF**(繁體中文 Windows 內建的 PowerShell 5.1 會把無 BOM 的檔案當 Big5 讀,中文變亂碼甚至無法執行;安裝程式的捷徑就是用 5.1);`tools/pscheck.py` 檢查;本機設定檔(`config\packaging.json` 等)開頭有 BOM 時也能讀。
- 新增:`tools/check-local-data.ps1`(只讀取:列出根目錄磁碟與檔案系統、環境變數、C: 上各工具預設位置是否有新資料;發行版也附一份,安裝好的筆電可執行)、`tools/dev-shell.cmd`(雙擊開啟已設定好的 PowerShell 視窗)、`setup-windows.ps1 -SessionOnly / -SystemTemp / -Unregister`、`stop-all.ps1` 會請專屬設定檔的 Edge 視窗關閉(`-KeepBrowser` 保留)。
- 修正(審查):`setup-windows.ps1` 不再改 corepack 的位置(會讓已下載的 pnpm 失效、連線時改抓不相容的新版),pnpm 固定建議 10.28.0 並對 11 以上提出警告;檢查專案磁碟的檔案系統(exFAT / FAT32 上 `pnpm install` 會失敗,開發用磁碟須為 NTFS)與抽取式磁碟;`SIMOSA_BRM_ROOT` 不再寫成使用者層級;各腳本對「已設定但指到專案外」的快取變數改指回專案;3 個把系統暫存當成「專案外路徑」的測試改寫;解除安裝不再刪 `build\webview2`(內含訓練紀錄);`.gitignore` 加入 simosa-pkg 的本機狀態(`state/`、`store/`、`config/device/` 等)。
- CI:Windows 工作在 TEMP/TMP 指到 `build\tmp` 的配置下建置與測試,並以內建的 Windows PowerShell 5.1 實跑 `setup-windows.ps1 -SessionOnly`、`check-local-data.ps1`、`build-release.ps1 -WhatIf`。
- 已知限制:專屬 Edge 設定檔與預先寫入的下載位置、WebView2 外殼的下載位置與「另存為 PDF」對話框預設資料夾、匯出版視景的導向、安裝程式的新對話框、exFAT / FAT32 上的行為都**尚未在 Windows 實機確認**(`docs/ops/本機資料位置.md` 第 7 節);直接雙擊視景執行檔或手動用其他瀏覽器開網頁仍會寫到 C:(文件已註明);安裝程式的預設位置只以磁碟是否存在判斷;`build\tmp` 不自動清理。

## 2026.11.27 — 2026-11-11(第二十七輪)

- 新增(R2):浪一階運動——情境 `ship.seakeeping`(預設關閉)啟用且有浪(Hs > 0)時輸出 `state.waveMotion`(`heave_m` 向上為正、`pitch_deg` 艏仰為正、`heaveRate_mps`、`pitchRate_dps`)。線性、只輸出、不回饋 3-DOF;沿用橫搖的同一波場與相位,各成分以遭遇頻率的 RAO 閉式合成(含長度濾波與 Smith 修正);純函數,快照不需新欄位。未啟用、無浪或 Hs = 0 時狀態、雜湊、快照逐位元不變(既有夾具與黃金檔不動)。C# `SeakeepingModel` 與 Python `seakeeping.py` 逐位元一致(`Golden/seakeeping_table.csv`、`seakeeping_run.{yaml,csv}`)。係數皆為 ASSUMED(無耐航試驗可校正)。
- 新增:情境 `E17b_typhoon_seakeeping`(E17 加港內湧浪與升沉/縱搖;E17 本體不動)與核心紀錄夾具;情境數 38。
- 新增(R3):顯示端——視景(Visual.Shared 內插、相機與船模;合成順序 艏搖 → 縱搖 → 橫搖,駕駛台視角隨船升沉與縱搖、鳥瞰不隨動;Godot HUD 一列)、conning 條 SEA 方塊「縱搖 / 升沉」、教官站自船面板(含速率)。
- 新增:`docs/修改指南.md`(想改什麼 → 改哪裡 → 跑哪個測試;三級分類、契約同步清單、夾具與黃金檔重錄、四個熱點檔導覽)。
- 修正:`setEnvironment` 立即改流後重算 `state.waveMotion`(凍結中下指令或時間表同 tick 改流時與「快照 → 還原」一致);Python 參考實作 `wave_number` 在深水短週期浪(k·h > 約 710)不再溢位(與 C# 的 Infinity 路徑一致)。
- 已知限制:升沉/縱搖無耦合(彼此、與橫搖)、無縱盪/橫盪/艏搖一階運動、深水波數、不影響 UKC/擱淺/繫纜張力;視景海面波與核心浪譜相位無關;Godot 端只驗到編譯與數值比對(容器無 Godot 執行檔);情境編輯器沒有 `ship.seakeeping` 表單(YAML 原樣保留);1920 寬單列版面 SEA 方塊既有數值右側裁切(既有問題,另案)。

## 2026.11.26 — 2026-11-10(第二十六輪)

- 修正:SimCore.Host 在 `--port` 被占用且有 `--record` 時不再留下只有 header+footer 的空紀錄(第一份紀錄改在狀態 WebSocket 啟動成功後才開檔);`--media-dir` 不合法時在開檔前即以結束碼 2 結束,不留 0 位元組紀錄檔。
- 新增(R13):版次清單納入船上筆電環境資訊 `environment`(`os`、`dotnet`、`webview2`、`collectedUtc`;`simosa-pkg status`、直連同步與隨身碟 `status.json` 都寫入 `fleet-versions.json`,`store list` 同列顯示;船端未回報時沿用上次已知值)。
- 新增(R7):情境 `briefing` 的 `planningTimeMin`、`peerAssessmentPrompts`、`debriefQuestions`;教官站 `<brm-briefing-block>` 規劃時間倒數(本地計時,到時記入學員操作紀錄)、同儕評估提示與講評引導問題;講評報告對核心紀錄與教官站紀錄都列出;情境編輯器三個欄位;E20 兩檔與 E22a–d 補內容並重錄六檔紀錄夾具;C# 對三個欄位嚴格檢查(與 schema、Python 一致)。
- 工具鏈:Ubuntu 套件版 .NET SDK(無 WindowsDesktop SDK、C# 12 編譯器)也能建置整個方案——雷達 `RadarService` 改寫一處 async 內的 Span 區域變數;`Shell.WebView2` 在這種 SDK 只建空組件並警告,`dotnet publish` 直接失敗,`build-release.*` 另檢查 `SimosaBRM.Shell.exe` 存在。
- soak 比對:本輪工作樹與第二十四輪(abf0e3a)各 10 分鐘的 RSS 曲線相同(核心 97 → 約 360 MB、雷達 89 → 約 333 MB,之後持平),第二十五輪記下的成長不是該輪引入;成因未定(見交接紀錄)。
- 已知限制:浪一階運動(升沉/縱搖,R2)本輪未做,契約已定於交接紀錄;講評引導問題只補六個情境;重播檢視未列引導問題;soak 的 RSS 準則在大快取雲端機器上 10 分鐘內不會合格。

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
