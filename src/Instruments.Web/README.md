# Instruments.Web — 駕駛台儀器、操船台與教官站(第一版)

中塑海運 FS Bitumen No.1(FSB1)/ No.2(FSB2)BRM 操船模擬器的網頁前端,兩個進入點共用程式庫與主題:

| 頁面 | 用途 |
|---|---|
| `index.html` | 駕駛台儀器與操船台(規劃書第 7 章第一版 conning 顯示與操船台);船上筆電以瀏覽器全螢幕顯示,之後包進 WebView2 殼(`src/Instruments.Shell`) |
| `instructor.html` | 教官站(第 9.1 節;訪船船長直連時用)、船上簡化教官模式(`?mode=simple`)與重播檢視(`?mode=replay`,第 9.2 節講評站雛型),見下方「教官站」一節 |
| `report.html` | 講評報告、行為標記評估表、個人訓練紀錄與課程班次紀錄(第 8.2、8.4、9.1、9.2 節;列印 / 另存 PDF、JSON/CSV 匯出),見下方「講評報告頁」一節 |

技術:Vite + TypeScript(strict)+ Lit 3;pnpm;Node.js 22。**不使用** `@oicl/openbridge-webcomponents`(授權待核,規劃書附錄 C),所有元件與 CSS 自寫,配色參考 OpenBridge 設計指南的日/黃昏/夜三種主題;紅色只用於警報。字型只用系統字型(船上離線)。

## 畫面

| 區域 | 內容 |
|---|---|
| 上排 conning 條 | HDG 大數字 + 航向帶(含自動舵設定航向與 COG 標記)、ROT(±30°/min 弧形)、STW/SOG/COG/漂角、舵角指示器(實際舵角指針 + 舵令標記,量程 ±70°(FSB1 Schilling 舵)或 ±35°(FSB2))、主機轉速(倒車負)與車鐘位置、艏側推、UKC/水深/squat/吃水(UKC ≤ 1.0 m 或擱淺為紅色警報)、真風/相對風、流向流速、本地時鐘/模擬時間/緯經度 |
| 中間 | 鳥瞰航跡圖(本地 ENU、北上):自船外形依 LPP/B 比例、航跡線、航向向量(6 分鐘)、COG/SOG 向量(虛線)、格線與比例尺;滾輪或 +/− 縮放、拖曳平移、「置中」回到跟隨;凍結/擱淺/碰撞/等待資料的提示 |
| 下排操船台 | 舵輪(拖曳或鍵盤 ←/→,Shift 為 5°,`0` 回正;FU 隨動)、NFU 左右按鈕(按住以舵機速率轉舵,放開即停)、舵角快速鍵、車鐘(EFAS…NAVF 11 段,點選送 `telegraph` 指令)、側推桿(−1 至 1,拖曳或 ↑/↓)、自動舵(開/關、設定航向 ±1/±10、ROT 限制、採用現在航向)、教官用凍結/恢復/重設(第一版暫放同頁,重設需按兩次確認) |
| 標題列 | 船名、連線狀態(已連線/連線中/未連線/示範模式/資料逾時)、警報(擱淺、碰撞、故障、資料可疑)、資料來源切換、主題切換、全螢幕、「訓練模擬器,非航行設備」標示(規劃書第 5.5 節) |

畫面以 1920×1080 設計,1366×768 亦可用(conning 條自動折成兩列)。

## 執行

所有套件、快取與建置輸出都留在專案資料夾內(CLAUDE.md):pnpm 套件庫在 `<專案>\.cache\pnpm\`(本目錄 `.npmrc`)、Vite 快取在 `<專案>\.cache\vite\`、建置輸出在 `<專案>\build\web\instruments\`。

```powershell
cd "D:\Simosa BRM\src\Instruments.Web"
pnpm install          # 第一次(或 pnpm install --frozen-lockfile,與 CI 相同)
pnpm dev              # 開發伺服器 http://localhost:5173/,存檔即熱更新
pnpm build            # 型別檢查 + 打包到 ..\..\build\web\instruments\
pnpm preview          # 以 http://localhost:4173/ 預覽建置結果
pnpm test             # vitest 單元與元件測試(一次)
pnpm test:watch       # 監看模式
pnpm typecheck        # 只做 tsc --noEmit
```

網址參數(兩頁共用;教官站另有 `mode` 與 `scenario`,見下):

| 參數 | 說明 | 例 |
|---|---|---|
| `ws` | 模擬核心 WebSocket 位址,預設 `ws://localhost:8765` | `?ws=ws://192.168.10.2:8765` |
| `demo=1` | 啟動即進入示範模式(不連線) | `?demo=1` |
| `ship` | 示範模式的船(`FSB1` / `FSB2`;即時模式由狀態訊息的 `shipId` 決定) | `?demo=1&ship=FSB2` |
| `theme` | `day` / `dusk` / `night`(未指定時沿用上次選擇,預設 dusk;兩頁共用同一個 localStorage 鍵) | `?theme=night` |

例:`http://localhost:5173/?demo=1&ship=FSB2&theme=day`、`http://localhost:5173/instructor.html?ws=ws://192.168.10.2:8765`。

## 與模擬核心連線

儀器是 WebSocket **客戶端**,連到 `ws://localhost:8765`(可用 `?ws=` 改);協定為每則訊息一個 JSON:

- **收**:自船狀態,格式依 [`src/Contracts/state.schema.json`](../Contracts/state.schema.json)(25 Hz;角度為度、航向 0–360、舵角右正左負、ROT 右轉正、轉速倒車負)。也接受包在 `{ "type": "state", "state": {...} }` 或 `{ "data": {...} }` 內的信封;缺欄位或型別錯誤時以前一筆值補上並在標題列標示「資料可疑 n」(規劃書第 7.2 節的「可疑」標記)。
- **送**:指令,格式依 [`src/Contracts/command.schema.json`](../Contracts/command.schema.json)。本儀器會送出:

| 指令 | 來源 | 例 |
|---|---|---|
| `rudder` | 舵輪、快速鍵、NFU(`args.mode` 為 `FU` 或 `NFU`,核心可忽略) | `{"type":"rudder","value":-20,"args":{"mode":"FU"}}` |
| `telegraph` | 車鐘 | `{"type":"telegraph","value":"HAH"}` |
| `thruster` | 側推桿(−1 至 1) | `{"type":"thruster","value":0.5}` |
| `autopilot` | 自動舵面板 | `{"type":"autopilot","args":{"enabled":true,"heading":275,"rotLimit":15}}` |
| `freeze` / `resume` / `reset` | 教官按鈕 | `{"type":"freeze"}` |
| `alertAcknowledge` | 中央警報面板「確認」(帶 id)/「全部確認」(不帶) | `{"type":"alertAcknowledge","args":{"id":"gyroFailure"}}` |
| `alertSilence` | 中央警報面板「靜音 30s」(全部) | `{"type":"alertSilence"}` |
| `bnwasReset` | BNWAS 指示「重設」 | `{"type":"bnwasReset"}` |

- **收(事件框)**:C# 核心的情境引擎(`docs/design/scenario-engine.md` 第 5.2 節)另推送 `{"type":"event","event":{…SimEvent}}`;儀器頁只顯示 `visible: true` 的 `message` / `voiceScript` 事件(橫幅,20 s 後消失,點擊可關),其餘忽略。狀態新增的選填欄位 `visibility_nm`、`scenario`、`route` 由 `state-parser` 解析;有 `route` 或 `visibility_nm` 時 conning 條多一格「ROUTE」方塊(XTD 右正 → R 偏右、走廊半寬條、下一航點/距離/方位、航段航向、速限、能見度;第 7.2 節 MSC.252 最低資訊)。

NFU 的實作:按住時每 100 ms 以舵機速率(由 `particulars.json` 的舵機時間推得)遞增舵令送出,放開時送出當時實際舵角作為舵令;因此任何支援 `rudder` 指令的核心都能用,不需額外協定。

斷線時標題列顯示紅色「未連線」並自動重連(1 → 1.7 → … → 5 秒退避);連線中超過 2 秒沒有狀態則顯示「資料逾時」。未連線期間下達的指令會暫存(每種類型保留最後一筆)並在連上後補送。

### Python 參考模擬器

```powershell
cd "D:\Simosa BRM\src\Tools.Calibration"
uv run simosa-brm serve --ship FSB1 --port 8765     # 參考實作的 WebSocket 伺服器(參數依該套件 README)
```

然後開儀器(`pnpm dev` 或 `pnpm preview`),預設即連 `ws://localhost:8765`。

教官站送出的指令(`src/lib/instructor-command.ts`;各欄位同時相容 C# 核心與 Python 參考伺服器,核心未支援的欄位仍照送並在畫面標示):

| 指令 | 格式 | C# SimCore.Host | Python 參考伺服器(2026-10 實測) |
|---|---|---|---|
| `loadScenario` | `{"type":"loadScenario","value":"data/scenarios/E01_baseline.yaml","args":{"id":"E01_baseline","path":"…"}}`(C# 讀 `value` 或 `args.path`) | 支援 | 回 `ok=false`「情境載入尚未實作」 |
| `timeScale` | `{"type":"timeScale","value":5}`(0.5 / 1 / 2 / 5 / 10) | 支援(≤0 視為凍結,上限依 Host 設定) | 支援(實測 ×5 = 5.02 倍) |
| `freeze` / `resume` / `reset` | 同儀器頁 | 支援 | 支援 |
| `snapshot` | `{"type":"snapshot","value":"snap-<tick>","args":{"tick":…,"t":…}}`(C# 只看 type,Python 以 `value` 為名稱) | 支援 | 支援 |
| `restore` | `{"type":"restore","value":"snap-<tick>","args":{"tick":…}}`(C# 回到該 tick 之前最近的快照,Python 依名稱) | 支援(記憶體內最近 60 份) | 支援(名稱須存在,否則 `KeyError`) |
| `setEnvironment` | `{"type":"setEnvironment","args":{"wind":{"trueSpeed","trueDir","gustiness"},"current":{"set","drift"},"waterDepth","visibility_nm"}}`,只放有改的欄位 | 風、陣風、流、水深;能見度未實作 | 風、流、水深;陣風與能見度照收但無作用 |
| `injectFault` / `clearFault` | `{"type":"injectFault","value":"steeringGear"}`;清除逐項送 `clearFault`(不帶名稱的 `clearFault` 只有 C# 會清全部) | `steeringGear`、`mainEngine`、`bowThruster` 會改變行為(`SimCore/Engine/FaultNames.cs`,建置時讀入),其他名稱只記錄與廣播 | 全部只記錄並在 `faults` 廣播(`detail`「僅記錄,故障行為尚未實作」) |
| 自船覆寫 | `command.schema.json` 無專用指令,以 `{"type":"reset","args":{"x","y","heading","speed","loading","tugs"}}` 送出 | 忽略 `args`,回到情境初始狀態 | 依 `x`/`y`/`heading`/`speed` 重設;`loading`、`tugs` 忽略 |
| `setEnvironment` 漸變 | `args.ramp_s`(秒;環境面板「漸變」欄位,0 = 立即)與 `visibility_nm` | 線性/最短弧漸變,`environmentChange` / `environmentRampDone` 事件(設計文件第 3.5 節) | 照收無作用 |
| `triggerTimeline` | `{"type":"triggerTimeline","args":{"id":"emergencySteering"}}`;非 manual 項目需按住「強制」0.8 s → `args.force: true` | 手動觸發時間表項目;拒絕時事件 `timelineManual {ok:false}` | 不支援(面板標「核心不支援,僅顯示」並停用按鈕) |
| `acknowledge` | `{"type":"acknowledge","args":{"kind":"notifyEcr","role":"master","note":"…","itemId":"steeringFail"}}`(`kind`: verbal / notifyEcr / callMaster / report / checklist / steeringMode / anchorReady / other;`itemId` 選填,不給時核心對應最近注入的故障) | 事件 `acknowledge`,餵給必要事件與故障應變 | 不支援 |
| `endExercise` | `{"type":"endExercise","args":{"reason":"…"}}`(執行控制「結束練習」按兩次;簡化模式「結束練習」) | 凍結、定稿 summary 並推送 `{"type":"assessment","final":true,…}` | 不支援(教官站同時送 `freeze`) |
| `getAssessment` | `{"type":"getAssessment"}`(結束後 1 s 內未收到推送時的後備查詢) | 主控程序直接回 `{"type":"assessment","final":false,"summary":{…}}`,不進引擎佇列 | 不支援 |

Python 參考伺服器對每筆指令回 `{"type":"ack","command":…,"ok":…,"detail":…,"tick":…}`,教官站的「教官指令」面板會顯示;C# 核心目前不回應,只顯示「已送出」。

### C# SimCore(之後)

`SimCore.Host` 的 WebSocket 閘道(規劃書第 5.2 節 Instruments 模組)以相同的 state/command JSON 廣播與接收;儀器端不需改動,只要指到該閘道位址(`?ws=`)。船上直連部署時以固定私有 IP(第 5.4 節)。

### 示範模式

沒有核心時可在標題列切到「示範模式」(或 `?demo=1`):內建簡單運動學(`src/demo/demo-sim.ts`)—— 一階 Nomoto 航向響應(K、T 依瞬時速度換算)、速度一階滯後並隨舵角打折、舵機速率限制、轉速一階滯後、側推隨速度衰減、均勻流、相對風、squat/UKC —— 係數只與 FSB1 試俥概略對齊,**不是**第 6 章的 MMG 船模,僅供儀器獨立預覽與操作練習。

為了離線測試教官站的情境引擎面板,示範模式也產生**假的**情境引擎資料(與核心無關):狀態含 `scenario`(階段、參數集版次 `2026.10.1`、已觸發/待觸發數)、`route`(沿初始航向的假航線,下一航點 2 nm,走廊 50 m)與 `visibility_nm`;固定時刻的假時間表事件(30 s 教官訊息、60 s 機控室語音、120 s 學員可見訊息);`injectFault` / `clearFault` / `acknowledge` / `triggerTimeline` / `freeze` / `resume` / `setEnvironment` / `endExercise` 各產生對應事件(首次注入故障時階段轉 `degraded`);`getAssessment` 與 `endExercise` 回假的 summary(通知機控室是否在 120 s 內確認、擱淺為硬性失敗)。

示範模式也模擬**操舵模式**與**假目標船**(離線預覽教官站的操舵面板與航跡圖/CPA 小表):狀態含 `steering`(初始 `fu` / 泵 `both`),`steeringMode` 指令切 `fu` / `nfu` / `autopilot` / `emergency` 與泵;NFU 推桿 `direction: port|starboard` 把舵令推向滿舵、`stop`(或不明方向)舵停在目前舵角,進出 NFU 時舵令同步到目前舵角(D35);`autopilot` 模式與 `autopilot {enabled: true}` 同義、人工舵令切回 `fu`;應急操舵下自動舵(`steeringMode autopilot` 或 `autopilot {enabled: true}`)被拒並發 `commandRejected {command, reason: emergencySteering}`,舵機速率減半;模式/泵變更發 `steeringModeChanged {from, to, pump}`。兩艘直線等速的假目標船:`DEMO-01` 交叉船在右艏 1.5 nm、朝自船艏前駛來(CPA < 0.5 nm,t = 0 即發 `cpaAlarm`,通過後 `cpaClear`;AIS 開)與 `DEMO-02` 錨泊船在左正橫 1.2 nm(AIS 關);`range_nm` / `bearing_deg` / `cpa_nm` / `tcpa_min` 由相對運動每步計算,警報每秒評估。

## 教官站(`instructor.html`)

規劃書第 9.1 節教官站功能的第一版子集,與儀器頁共用 WebSocket 客戶端、狀態解析、conning 條、鳥瞰航跡圖與主題。`?mode=` 選版面:

| `mode` | 版面 | 用途 |
|---|---|---|
| (未指定)`full` | 教官站 | 訪船船長以網路線直連船上筆電時使用(第 5.5 節) |
| `simple` | 簡化教官模式 | 船長在船上筆電單機帶領練習(第 9.1 節 R13、第 8.0 節) |
| `replay` | 重播檢視 | 講評站雛型(第 9.2 節),離線載入紀錄檔,不需核心 |

另可加 `?scenario=E01_baseline` 預先選定情境(只選定、不載入)。

### 教官站(full)

- **標題列**:船名、連線狀態(與儀器頁相同的已連線/連線中/未連線/示範模式/資料逾時)、凍結/擱淺/碰撞/故障/資料可疑標示、「即時 / 重播檢視」切換、資料來源(即時連線/示範模式)、「下載紀錄 JSON」、主題、全螢幕。
- **自船摘要**:與儀器頁相同的 conning 條(`<brm-conning-bar>`)。
- **情境**:建置時以 `import.meta.glob` 匯入 `data/scenarios/*.yaml`(`yaml` 套件解析,`src/lib/scenario.ts`),列出 id、標題、船、裝載、環境摘要、初始狀態、種子與路徑,以及擴充欄位統計「時間表 n 項(手動 m)· 航線 n 航點 · 區域 n · 評估規則 n 條 · 參數引用 n 個 · 目標船 n」(`src/lib/timeline.ts`、`src/lib/assessment.ts` 解析設計文件第 1 節的 `timeline` / `route` / `zones` / `briefing` / `assessment`);`briefing` 物件版顯示角色(扮演者、必要)、最少人數與附件連結(`data/scenarios/<id>/<file>`);`${params:key}` 參數引用在教官站**不解析**,顯示為「參數 key」(核心載入時解析)。按「載入」送 `loadScenario`。示範模式下改以情境的船、初始位置(情境 origin 座標系)/航向/速度、風、流、水深、拖船與泊位重建示範運動學。
- **時間表**(`src/components/instructor/timeline-panel.ts`):列出情境 `timeline[]` 的 id、名稱、階段、重複規則、條件摘要(遞迴 all/any/not + 14 種 leaf)、動作摘要與狀態(待觸發 / 待手動 / 已觸發 n 次 @時刻 / 已停用 / 核心拒絕;由 `timelineTriggered` / `timelineArmed` / `timelineDisarmed` / `timelineManual` 事件推得,計數優先用 `state.scenario.timelineFired/Pending`,標題列顯示階段);`manual` 項目有「觸發」鈕(`triggerTimeline`),其他項目需**按住「強制」0.8 秒**(`force: true`);**確認**快速鈕列(口頭辨識、通知機控室、呼叫船長、報告、切換操舵模式、備錨、檢查表、其他)含角色(駕駛員/船長/引水人/舵工/瞭望/帶領者)、對應項目(已觸發且含故障注入的項目;預設自動對應)與備註,送 `acknowledge`;**必要事件倒數**:`assessment.required[]` 的 `after` 事件(例如 `faultInjected steeringFail`、`reportPointPassed WP3`)發生後依 `within_s` 顯示「剩 n s」/「逾時 n s」,`requiredEventMet` / `requiredEventMissed` 事件更新為達成/逾時。`within_s` 為參數引用(教官站不解析)時改由事件 data 取期限:規則 id 的事件(含核心「開始計時」事件)或 `after` 事件本身的 `data.deadline_t` 優先,否則 `data.within_s` + 該事件 `t`(`requiredEventMet` / `Missed` 的 `within_s` 視為自 `after` 事件起算),都沒有才只顯示「計時中」;簡化教官模式的講評摘要同樣顯示剩餘/逾時秒數(核心 summary 的 pending 列,或無 summary 時的「必要事件(即時倒數)」表)。核心不廣播 `state.scenario` 也無事件(Python 參考伺服器)時標「核心不支援,僅顯示」並停用按鈕。
- **執行控制**:凍結/恢復/重設(重設按兩次確認)、**結束練習**(按兩次確認,送 `endExercise`;`state.scenario.ended` 後停用)、時間倍率 ×0.5 / 1 / 2 / 5 / 10、快照(列出最近 8 份的模擬時間與 tick)與還原。
- **環境**:真風速/向、陣風、流向/流速、水深、能見度,分組「套用」或「全部套用」(只送有改的欄位);**漸變 s** 欄位附在每次套用上(`ramp_s`,0 = 立即);「帶入目前值」把核心現況(含能見度)填回表單。只有 C# 核心支援的欄位(陣風、能見度、漸變)標示「僅 C# 核心」但仍送出。
- **故障**:目錄按鈕(舵機失效、主機失效、側推失效、電羅經漂移、GPS 跳點、雷達失效、全船失電,名稱優先取自 `src/SimCore/Engine/FaultNames.cs`,讀不到時用內建預設;核心不改變行為者標「僅記錄」)、自訂名稱、現行故障清單(由狀態 `faults` 欄位)逐項或全部清除。
- **自船覆寫**:位置 x/y、航向、速度、裝載、拖船數,按兩次確認後以 `reset` 附 `args` 送出(支援情形見上表)。
- **版面**:中欄航跡圖下方的環境 / 故障 / 自船覆寫 / 操舵模式四個面板,寬度 ≤ 1440px(1366×768 筆電)時改為兩列兩欄,列高依面板內容、放不下時整列捲動;左欄(情境 / 時間表 / 執行控制)在同寬度下依內容高度排列並整欄捲動;conning 條單列 11 格時風 / 流 / 時間格加寬。以 Playwright 在 1366×768 與 1920×1080 檢查過無水平捲動、面板不重疊、文字不截斷、console 無錯誤(教官站進入點 `instructor-main.ts` 內嵌 favicon,避免瀏覽器抓 `/favicon.ico` 的 404)。
- **操舵模式**(`src/components/instructor/steering-panel.ts`、`src/lib/steering.ts`;規劃書第 7.2 節,設計文件 D32/D35):顯示 `state.steering`(模式、泵、NFU 推舵中;C# SimCore 產生,Python 參考伺服器不輸出時標「核心未回報」。`state-parser.ts` 不認得此欄位,由 `ws-client.ts` 以原始框補上);按鈕 **FU / NFU / 自動舵 / 應急操舵** 送 `steeringMode`(`value` 為模式);NFU 的 **左 / 右推桿按住**送 `args.direction: port|starboard`、**放開**(含指標離開)送 `stop`,「NFU 停」送 `stop`;舵機泵選擇(No.1 / No.2 / 1 + 2)附在每筆指令的 `args.pump`,已在某模式時改選泵即送同模式 + 新泵;自動舵可帶設定航向與 ROT 限制(`args.heading` / `rotLimit`,只在 `autopilot` 時送)。應急操舵中自動舵鈕停用並提示需先切回 FU;核心仍拒絕時(`commandRejected` 事件,C# 不回 ack 框)把最近一筆同類型的教官指令標為「核心拒絕:應急操舵中不可自動舵」。`compact` 屬性為簡化教官模式用的精簡版(見下)。
- **鳥瞰航跡圖**:與儀器頁相同的 `<brm-track-plot>`,疊上情境的航線(航點、檢查點/報告點空心圓、到達半徑、各航段走廊半寬虛線;`route.xtdLimit_m` 為參數引用時無走廊)與區域多邊形(禁區紅、速限橙、UKC 政策藍、報告灰;`src/lib/plot-overlay.ts`,經緯度以情境 `origin` 平面近似換算),以及事件標記(warning 橙、alarm 紅三角,位置取事件時刻的狀態抽樣)。**目標船**(`state.targets[]`,`src/types/target.ts`、`src/lib/targets.ts`):三角形依航向、船名/ID 標籤、COG/SOG 虛線短向量;AIS 關閉者空心虛線並標「(AIS 關)」;處於 `cpaAlarm` 的目標紅色(由事件流 `cpaAlarm` / `cpaClear` 推得);完整模式左上角列 **CPA/TCPA 小表**(目標、距離、方位、CPA、TCPA;警報者在前,其餘依距離,最多 8 列)。簡化教官模式只畫目標船,不列小表。
- **事件**(`src/components/instructor/event-stream.ts`):核心推送的 `{"type":"event"}` 框(`src/lib/sim-event.ts` 的 `parseSimEvent` / `eventLabel`;舊 `TrafficEvent` 欄位不變,`source` 缺時由 kind 推得)依時間列出,最新在上,只保留最近 2,000 筆;左緣顏色依來源(時間表藍、評估綠、目標船黃、教官灰、引擎),`hardFail` / `collision` / `grounding` / `bandFail` / `requiredEventMissed` 紅字,`cpaAlarm` / `faultInjected` / `xtdExceeded` / `zoneEntered` / `ukcAlarm` / `speedLimitExceeded` 橙字,`visible: true` 標「學員可見」;來源篩選鈕;語音事件有「播放」鈕;點擊事件 → 切到重播檢視並跳至該時刻(需已載入紀錄)。
- **語音腳本**(`src/lib/voice-script.ts`):收到 `voiceScript` 事件時,有 `file` 用 `<audio>` 播情境包音檔(`data/scenarios/<id>/<file>`,建置時以 `import.meta.glob` 打包 `*.ogg|mp3|wav|m4a|opus`;找不到或載入失敗退回 TTS),無檔用 `speechSynthesis`(語言依 `lang`,預設 zh-TW);完整教官站按「播放」,簡化教官模式自動播放;每次播放記入學員操作紀錄(kind `voiceScript`,含音檔/TTS/被瀏覽器阻擋/僅顯示),並寫入下載的紀錄。
- **學員操作紀錄**:教官站收不到學員站的指令(指令直接進核心),因此由連續狀態推導(`src/lib/operation-log.ts`):舵令(1 秒內的連續變更合併)、車鐘、轉速令、側推、凍結/恢復、故障注入/清除、擱淺/碰撞、時間倒退、**操舵模式/泵**(`state.steering` 變化,NFU 推桿推舵/放開亦記;核心的 `steeringModeChanged` 事件經 `recordSteering()` 記入,同一模式與泵在 1 s 內已記過時不重複),附模擬時間與本地時鐘。
- **教官指令**:本站送出的每筆指令與核心回應(Python 參考伺服器的 ack;C# 不回應)。
- **參數集版次**:標題列顯示 `state.scenario.parameterSetVersion`(例如「參數集 2026.10.1」)與情境階段(`degraded` 橙、`emergency` 紅)、硬性失敗、練習已結束;以下情形標「參數集不一致」(滑鼠停留看原因):核心載入的情境 id 與所選不同、情境檔有參數引用但核心未回報版次、核心 summary 的版次與狀態不同。
- **BRM 快速標記**:第 8.2 節五大類(領導、狀況覺知、溝通、團隊合作、決策)各一鈕,可先填一句備註與 ＋(做得好)/ Δ(待改進)再按類別;鍵盤 1–5 亦可(焦點不在輸入欄時)。標記含模擬時間、tick、本地時鐘、情境 id,不經核心;「匯出 JSON / CSV」下載(`src/lib/brm-markers.ts`;CSV 含 UTF-8 BOM 供 Excel)。
- **下載紀錄 JSON**:本站自己的練習紀錄(`src/lib/session-record.ts`,`format: simosa-brm-instructor-record`,版本 2):`createdUtc`(匯出時刻)、`startedUtc`(練習開始時刻:載入情境/重設/簡化模式「開始練習」後第一筆抽樣時記下,同一練習多次匯出不變;舊紀錄無此欄)、狀態每 1 s 抽樣、學員操作、BRM 標記、指令與回應、本地講評摘要、核心事件(`events`)與核心 summary 原始物件(`coreSummary`,無則 null);可在重播檢視開啟(版本 1 的舊紀錄仍可讀)。這不是核心的 JSON Lines 紀錄(那份在核心主機的 `build/records/`),也尚未簽章(第 5.5 節紀錄包待做)。

### 簡化教官模式(`?mode=simple`)

船長在船上筆電單機用的精簡流程,同一頁、大字:

1. **選擇練習**:分頁「情境清單」(按「載入」→ 送 `loadScenario` 後立即 `freeze`)或「**靠離泊預演**」(規劃書第 8.0、9.1 節 R13;`<brm-berthing-rehearsal-panel>`、`src/lib/berthing-rehearsal.ts`):輸入船/裝載、泊位(`data/areas/*.json` 的泊位清單,顯示碼頭線走向與水側)、靠泊/離泊、靠泊舷、預定時刻 UTC、潮高與基底水深、潮流、預報風、能見度、拖船數(0–2,拖力預設 40 t、連接點在船體水側艏/艉)、引水人在船、選填風變化,按「組題並載入」由 TypeScript 範本函式組出情境物件(靠泊:泊位外約 0.8 nm、艏向與碼頭線平行;離泊:繫妥於泊位、4 纜 fast;時間表:簡報語音、拖船就位、潮窗提示、風變化;評估引用參數集 `berthing.*`/`navigation.ukc.*`;必要事件 MPX 檢查表、拖船令確認;自動簡報文字),以 `parseScenario` 自我驗證後以 `loadScenario {args: {id, scenario}}`(內嵌情境物件,兩核心皆支援)送核心並 `freeze`(初始 UKC 以與核心相同的最大吃水 max(艏、艉、平均)計算:≤ 0 時列為錯誤、不可載入,低於公司下限 1.0 m 時以警告色提示);「預覽」/「下載 YAML」可檢視或保存產生的情境(簡報頁也有「下載情境 YAML」)。
2. **簡報**:大字顯示情境 YAML 的 `briefing`(字串或 `briefing.text`),下方列出角色/扮演者/最少人數與附件標題;標題顯示核心回報的參數集版次。沒有簡報時顯示說明 + 船/裝載 + 初始 + 環境摘要。按「開始練習」送 `resume`,並清空操作紀錄、標記與講評累計。
3. **執行中**:大字狀態列(模擬時間、HDG、ROT、STW/SOG、舵、車鐘、UKC、階段)、最近一則語音/訊息橫幅(語音事件**自動播放**,播放中顯示 ▶)、鳥瞰航跡圖(疊航線/區域)、大字 BRM 標記鈕(含備註與 ＋/Δ)、**精簡版操舵面板**(`<brm-steering-panel compact>`:只有目前模式、FU / NFU / 應急操舵三鈕與 NFU 左右推桿,指令與完整教官站相同,不含自動舵、泵與航向/ROT 表單)、精簡版時間表面板(只有必要事件倒數與確認鈕)、學員操作紀錄(側欄依內容高度排列,1366×768 放不下時側欄捲動);「凍結/恢復」、「結束練習」(按兩次確認,送 `endExercise` + `freeze`,並查詢 `getAssessment`)。
4. **結束**:講評摘要 —— 有核心 summary 時優先顯示核心評分(分數/及格線/通過或硬性失敗、定稿與否、參數集版次)、硬性失敗、必要事件(達成/逾時/逾時後達成/不適用、時間差與時限、扣分、引用的程序文件與版次/條款)、容許帶、故障應變(首個動作、各確認與時間差)、總覽(含會遇、航跡、UKC 門檻來源、舵角分箱、車令段落、階段)與引用的程序參數;沒有(Python 參考伺服器、離線)時退回本地推導(`src/lib/debrief.ts`:練習時間、航程、平均/最大速度、超過港區速限的時間、最小 UKC 與時刻、UKC 低於門檻的時間、最大舵角與時刻、滿舵次數、舵令次數、最大 ROT 與時刻、ROT 超限時間、車鐘變更次數、擱淺/碰撞;超限以警告色標示);兩者皆有時可「並列顯示」本地推導供交叉核對。另有標記清單與匯出、「下載紀錄 JSON」、「新練習」。

本地推導的閾值為常數(港區速限 8 kn、UKC 警報 1.0 m、ROT 上限 30°/min、滿舵 35°;`DEFAULT_THRESHOLDS`);核心評分的門檻由情境 `assessment` 與公司程序參數集提供(第 9.1 節、第 8.3 節),summary 標示「自動評分僅輔助,最終由評估員判定」。靠離泊預演自動組題的固定範例在 `data/scenarios/templates/examples/`(vitest `berthing-rehearsal.test.ts` 同步,C# `BerthingRehearsalFixtureTests` 驗證/重播)。

### 重播檢視(`?mode=replay`,或教官站標題列「重播檢視」)

以檔案選擇器載入核心的 JSON Lines 紀錄(`src/SimCore/README.md` 第 4 節:`header` / `input` / `state` / `snapshot` / `event` / `summary` / `footer` 各行)或教官站下載的 JSON 紀錄(`src/lib/record-replay.ts` 自動判斷;未知的行種類只計數不列為錯誤,舊紀錄無 `event` / `summary` 行亦可):

- 時間軸拖曳、播放/暫停(×0.25 / 1 / 4 / 16)、上一筆/下一筆;任一時刻顯示 conning 條與鳥瞰航跡圖(航跡畫到該時刻為止,疊 `header.scenario` 的航線走廊與區域,事件標記畫到該時刻;**目標船**取該時刻那筆 `state.targets[]`,`cpaAlarm` 紅色由 t ≤ 該時刻的事件推得,CPA/TCPA 小表在非 compact 時顯示)。
- 時間軸上方的**事件標記**列:顏色依來源(時間表藍、評估綠、目標船黃、教官灰),warning 橙、`hardFail` / `collision` / `grounding` 紅,階段分界細線,BRM 標記黑;**倒帶標記**(`ReplayData.rewinds[]`,restore/reset 後 tick 倒退處)為橙色雙線,滑鼠停留顯示倒帶至的時刻與 tick(由 `t / header.dt` 推得)、來源時刻與丟棄的舊分支狀態/事件筆數,點擊跳至倒帶點;滑鼠停留看時刻與說明,點擊跳至。
- 輸入事件清單(tick × header.dt 換算時間;點選跳至)、事件流(同教官站;點選跳至)、BRM 標記(教官站紀錄才有;SimCore 紀錄不含)、講評摘要(**核心 summary 優先**,取紀錄最後一行 `summary`;顯示評分與是否通過、硬性失敗、必要事件、容許帶、故障應變、參數來源;無則本地推導,兩者皆有可並列)、header 資訊(情境、船、動力學模型、雜湊、**參數集 id/版次與已解析引用數**、評分)、略過的壞行數與未知行種類。
- 不需核心連線;航跡圖無海圖,儀器時序曲線、語音影像同步(第 9.2 節)待後續版本。

### 真實連線測試結果(Python 參考伺服器,2026-10)

以 `uv run simosa-brm serve --ship FSB1 --loading full --port 8765` 測試,結果見上方指令表:`setEnvironment`(風/流/水深)、`injectFault`/`clearFault`(只記錄)、`timeScale`、`snapshot`/`restore`、`freeze`/`resume`、`reset` 附 `args`(位置/航向/速度)皆有作用;`loadScenario` 回 `ok=false`「情境載入尚未實作」;`gustiness`、`visibility_nm`、`loading`、`tugs` 照收無作用。教官站畫面會把 ack 的 `ok`/`detail` 顯示在「教官指令」面板。

## 講評報告頁(`report.html`)

規劃書第 8.2 節(行為標記)、第 8.4 節(講評結構)、第 9.1 節「多站與管理」(個人訓練紀錄、課程班次紀錄)、第 9.2 節(講評報告匯出)與第 14 章評估列的第一版。四個分頁共用一份練習紀錄與本機工作資料;左邊編輯、右邊是 A4 列印預覽。**不連網、不經核心**:資料存在瀏覽器 IndexedDB(資料庫 `simosa-brm-report`;不可用時退回記憶體並在狀態列標示,請以 JSON 匯出)與匯出檔。

**開啟紀錄**:「開啟檔案…」可一次選多個檔案,依內容自動判斷 —— 核心 JSON Lines 紀錄(`build/records/*.jsonl`)或教官站「下載紀錄 JSON」(`record-replay.parseReplay`)、BRM 快速標記 JSON(`simosa-brm-markers`)、評估表 JSON、學員名冊 JSON、課程班次 JSON。教官站(完整/重播檢視)標題列與簡化教官模式結束畫面的「**講評報告**」按鈕會把目前的教官站紀錄寫入 IndexedDB 再開 `report.html?from=instructor`(新分頁讀取後刪除交接資料)。重新整理後還原最近的紀錄與工作資料。`?tab=report|form|individual|session` 選起始分頁;報告是紙本文件,預設日間主題(`?theme=` 可覆寫,不影響其他頁記住的主題)。

| 分頁 | 內容 |
|---|---|
| 講評報告 | 頁首「中塑海運內部訓練」+ 課程名稱與版本 + 報告編號;**一、練習資訊**(練習編號/情境、船、裝載、日期、情境時刻、時長、種子、動力學模型、紀錄來源、狀態雜湊/紀錄 SHA-256、倒帶次數、教官、參與者與角色);**二、自動量測與評分**(核心 summary:分數/及格線/通過或硬性失敗、硬性失敗、必要事件、容許帶、故障應變與警報確認時間、警報/BNWAS、會遇、航跡、UKC、操縱;無 summary 時本地推導);**三、操縱量測與試俥目標比較**(summary 有 `manoeuvres[]` 時,F01–F03:量測/目標(範圍目標顯示上下限)/偏差/公差/判定(公差內、超出公差、條件不可比)/資料品質(實測、海報讀值、估計值));**事件時間軸**(核心事件 + BRM 快速標記,依時間;最多 400 列);**行為標記評估**(每位受評者主要評估員的表、類別平均、缺失分類統計、雙評一致率,以及 STCW 準則 → 要素對應表);**講評紀錄**(第 8.4 節五段 + 教官總評;空白段落印書寫格線);**參數集版次與來源文件**(參數集 id/版次/路徑/SHA-256、來源程序文件編號與版次/條款、引用參數);簽名欄。「加入課程班次紀錄」把本練習寫進班次(出席 = 參與者中有學員 ID 者;完成 = 核心 summary 已定稿 `final` 或該學員主要評估員的評估表完整且合規;未完成的評估表在按鈕上標示份數,加入後不計入行為標記分數、標「評估表未完成」) |
| 行為標記評估表 | 每位受評者一份(或「團隊」);十五要素各顯示 **1/3/5 分行為錨定描述**與對應 STCW 準則(滑鼠停留看條文),1–5 分按鈕(再按一次取消);觀察(時刻 `HH:MM:SS` / `MM:SS` / 秒 + 描述)、SIRE 2.0 缺失(硬體/程序/人員,人員再分熟悉度、任務設計、設備、緊張/語言、疲勞)、評語;「新增觀察」的時刻預設空白,須填實際觀察時刻或由 BRM 快速標記帶入;須先載入練習紀錄才能新增評估表;沒有紀錄鍵的評估表(舊版未載入紀錄時建立、或匯入的表缺 `record.key`)不列入任何報告或班次,載入紀錄後評估表分頁提示「綁定到目前紀錄」。**表單驗證**即時顯示:≤ 2 分缺時間戳或觀察描述、人員缺失缺子類、分數不是 1–5 整數、評估員未填為錯誤;未評分、≤ 2 分未標缺失類型、觀察時間超出練習時長為提醒。各類別上方列出該類 BRM 快速標記,選要素按「帶入」成為觀察證據(同一標記不重複帶入)。雙評:新增或匯入同一受評者的「第二評估員」表即顯示一致率(±1 分內比例,目標 ≥ 80%;兩表都評完十五項才判定「達成/未達成」,否則顯示「未完成(缺 n 項)」)、完全一致數、需協調的要素與提醒(不同紀錄、同一評估員、錨定版本不同)。列印版每要素含錨定描述 |
| 個人訓練紀錄 | 課程班次欄位(班次編號、課程名稱/版本、第 8.0 節訓練形式、船、日期、教官、評估員、設備配置、課後評估)、學員名冊(ID、姓名、職級、法定證書到期日;匯出/匯入名冊 JSON)、練習與出席(日期、時數、各學員出席/完成勾選)。列印**每位學員一份**(選「全部」時每人分頁):紀錄編號 `<班次編號>-<學員 ID>`、課程名稱與版本、日期與時數(出席/本期)、出席與完成之練習編號、教官與評估員、評估結果摘要(自動評分(輔助,團隊層級)、行為標記平均/最低(評估表未完成時印「評估表未完成」)、**評估員判定**(通過/未通過)與**備註**、報告編號;判定與備註在本分頁「評估員判定與備註」表輸入,R9),並固定載明「**中塑海運公司內部訓練紀錄,非依《船員訓練檢覈及申請核發證書辦法》核發之專業訓練證書**」(第 9.1 節原文;`INTERNAL_TRAINING_STATEMENT`) |
| 課程班次紀錄 | 開課日期與時數、參訓學員與職級、授課教官、使用之情境與設備配置、出席矩陣(每列一位學員、每欄一個練習,完成/出席/—;每張表最多 10 個練習,不隨學員數加寬 A4)、課後評估;匯出 **CSV**(UTF-8 BOM)與 **JSON**,可匯入 JSON;匯出前檢查(缺班次編號、課程版本、無學員、重複 ID、練習無出席或時數) |

**列印 / 另存 PDF**:按「列印 / 另存 PDF」→ 瀏覽器列印對話框選「另存為 PDF」即離線產生 PDF(檔名預設為「講評報告-<報告編號>」等)。`@media print` 只印目前分頁的 `.sheet`(工具列與編輯區不印),A4、邊界 16/13 mm;`@page` 邊界方塊每頁印頁首「中塑海運內部訓練」與課程版本、頁尾分頁名稱與「頁碼 / 總頁數」(Chromium 131 以上;WebView2 相同);表頭每頁重複、表列不跨頁切斷。以 Playwright(Chromium,`page.emulateMedia({media:'print'})`)截圖並以 `page.pdf` 輸出檢查:E07 講評報告 5 頁、評估表 3 頁、3 位學員的個人訓練紀錄各 1 頁、班次紀錄 1 頁,1366×768 無水平捲動,console 無錯誤。

**錨定描述**(`src/report/behavioural-anchors.json`,版本 `0.1.0-draft`,`status: draft`,「**草案,待訓練負責人核准**」):五大類 × 三要素(類別 id 與 BRM 快速標記相同)、每要素 1/3/5 分描述與 STCW 對應(`BRM1`–`BRM5` = A-II/1 BRM 五項評量準則,`LT1`–`LT8` = A-II/1、A-II/2 領導與團隊合作八項準則;條文為規劃書第 2.1 節中文整理,原文待核對)、SIRE 2.0 缺失分類、`lowScoreThreshold: 2`、`agreement: {tolerance: 1, target: 0.8}`。核准後改 `version`(例如 `1.0.0`)、`status: approved`、`approvedBy` / `approvedDate`;評估表記下所用版本(`anchorsVersion`),雙評時版本不同會提醒。第 8.2 節要求錨定描述或評估表改版時重做校準。

**檔案格式**(皆為 JSON;`src/report/*.ts`):

| format | 內容 | 例 |
|---|---|---|
| `simosa-brm-marker-form`(單張)/ `simosa-brm-marker-forms`(整包 `{forms: [...]}`) | 評估表 | `{"format":"simosa-brm-marker-form","version":1,"anchorsVersion":"0.1.0-draft","formId":"F…","record":{"key":"sha256:502CC5…","scenarioId":"E07_steering_failure","shipId":"FSB1"},"trainee":{"id":"T001","name":"王大明","rank":"船長","role":"master"},"assessor":{"name":"陳船長","role":"primary"},"assessedUtc":"…","ratings":[{"elementId":"communication.closedLoop","score":2,"observations":[{"t_s":181,"text":"右舵 10 未複誦","markerId":5,"polarity":"delta"}],"deficiency":{"type":"human","humanFactor":"stressLanguage"},"comment":""},…],"overallComment":""}` |
| `simosa-brm-roster` | 學員名冊 | `{"format":"simosa-brm-roster","version":1,"trainees":[{"id":"T001","name":"王大明","rank":"船長","certificateExpiry":"2029-03-31"}],"staff":[{"name":"陳船長","role":"instructor"},{"name":"林船長","role":"assessor"}]}` |
| `simosa-brm-course-session` | 課程班次紀錄(匯出時另附 `exportedUtc`、`statement`、`totalHours`) | `{"format":"simosa-brm-course-session","version":1,"sessionId":"BRM-FSB1-2026-01","courseName":"中塑海運 BRM 內部訓練課程","courseVersion":"1.0","trainingForm":"full","shipId":"FSB1","startDate":"2026-10-17","endDate":"2026-10-17","instructors":["陳船長"],"assessors":["林船長"],"equipment":"…","trainees":[…],"exercises":[{"exerciseNo":"E07","scenarioId":"E07_steering_failure","date":"2026-10-17","hours":1,"recordKey":"sha256:502CC5…","reportNo":"RPT-20261017-E07-502CC5","instructor":"陳船長","assessors":["林船長"],"equipment":"…","attended":["T001","T002"],"completed":["T001","T002"],"results":{"T001":{"score":85,"passed":true,"hardFailed":false,"markerMean":3.53,"markerMin":3,"formId":"F…","verdict":"pass","remark":"…"},"T002":{"score":85,"passed":true,"hardFailed":false,"formId":"F…","formIncomplete":true}}}],"evaluation":"問卷平均 4.5/5"}` |

班次 CSV 欄位(一列 = 一位學員 × 一個練習;學員無練習時一列,練習欄空白):`sessionId, courseName, courseVersion, trainingForm, shipId, startDate, endDate, sessionHours, instructors, assessors, traineeId, traineeName, rank, exerciseNo, scenarioId, exerciseDate, exerciseHours, exerciseInstructor, exerciseAssessors, equipment, attended, completed, score, passed, hardFailed, markerMean, markerMin, markerFormComplete, verdict, remark, reportNo, individualRecordNo, evaluation`(`attended`/`completed`/`passed`/`hardFailed` 為 Y/N;`markerFormComplete` 有主要評估員評估表時 Y/N;`verdict` 為 `pass`/`fail`(評估員判定,空白 = 未判定);多值以「; 」分隔)。文字欄位以 `=`、`+`、`-`、`@`、Tab、CR 開頭時前加單引號 `'`(防 Excel 公式注入;BRM 標記 CSV 的備註同);匯入班次 JSON 時單行欄位的控制字元換成空白,頁首 `@page` 字串另做 CSS 跳脫。

**識別與預設值**:紀錄鍵 `recordKey` = 核心紀錄 `sha256:<footer.sha256>`,教官站紀錄 `<情境 id>@<startedUtc>`(舊紀錄無 `startedUtc` 時 `<情境 id>@<createdUtc>`;評估表、報告欄位、班次練習依此對應,同一練習再按「講評報告」或另外下載的紀錄都對得上);報告編號預設 `RPT-<本地日期>-<練習編號>-<6 碼>`(核心紀錄取 SHA-256 前 6 碼,教官站紀錄取紀錄鍵 FNV-1a 雜湊前 6 碼;日期與班次練習日期同為本地日期,依 `startedUtc ?? createdUtc`);練習編號由情境 id 前綴取得(`E07_steering_failure` → `E07`);練習預設時數 = 執行時間 + 0.75 h(簡報 + 講評,第 8.1 節標準流程)進位到 0.25 h,可在出席表修改。自動評分為團隊層級並僅作輔助(第 8.3 節);個人判定由評估員填「通過/未通過」與備註(R9);未通過者的補訓與複查流程未定(D20),個人紀錄不顯示「補訓中…」等狀態。

**限制**:不含錄音錄影與語音轉文字;報告與紀錄未簽章(第 5.5 節紀錄包待做);教官資格紀錄、證書效期雙軌追蹤與訓練排程、xAPI/SCORM 未實作;IndexedDB 只在該瀏覽器,換機請匯出 JSON。

## 警報管理(BAM)與 BNWAS(`docs/design/sensors-alerts.md`)

C# SimCore 的感測器故障層、警報管理(MSC.302(87))與 BNWAS(MSC.128(75))在網頁端的對應;Python 參考伺服器不輸出這些欄位,兩頁會標「核心未回報」並停用相關按鈕。

**狀態解析**(`src/types/alerts.ts`、`src/lib/alerts.ts` 的 `applyAlertBlocks`,由 `state-parser` 呼叫;重播檢視也解析):

| 欄位 | 網頁端型別 | 說明 |
|---|---|---|
| `alerts[]` | `AlertItem[]`(`state.alerts`,無警報時 undefined) | `id`、`number`(ALR 編號)、`priority`(emergencyAlarm/alarm/warning/caution)、`category`、`source`、`state`(activeUnacknowledged/activeSilenced/activeAcknowledged/rectifiedUnacknowledged)、`text`、`raisedAt_s`、`acknowledgedAt_s?`、`silencedUntil_s?`、`escalated`、`count`、`audible`;解析後依 `sortAlerts`(優先序 → 成立時間 → 編號,與核心相同)排序 |
| `bnwas` | `BnwasStatus` | `mode`(off/auto/on)、`active`、`stage`(0–3)、`remaining_s?`、`dormant_s` |
| `truth` | `TruthState` | 真值 heading/pos/cog/sog/stw/depthBelowKeel、`headingError_deg`(指示 − 真值,右正)、`positionError_m`;**只在教官站顯示** |
| `sensors` | `SensorStatus` | `headingValid`/`positionValid`/`depthValid`/`stwValid`、`magneticHeading` |
| `autopilot` | `AutopilotStatus` | `enabled`、`setHeading`、`rotLimit_degPerMin`、`available` |

舊狀態沒有這些欄位時為 undefined、不記「資料可疑」;型別錯誤才記入 issues。頂層 heading/pos/cog/sog/stw/depthBelowKeel 一律是**指示值**。

**操船台(`index.html`)**:

- 中間列右側新增警報欄:上方 `<brm-bnwas>`(模式、休眠期、階段、距下一階段倒數、休眠進度條、「重設」送 `bnwasReset`;第 1 階段黃、**第 2 階段以上整格紅色閃爍**;學員端沒有模式開關,實船受鑰匙/密碼保護),下方 `<brm-alert-panel>` 中央警報面板。
- 警報清單依優先序與時間排序;圖示依 IEC 62288 / MSC.302 的概念自繪(`src/components/alert-icon.ts`,非型式認可圖形):**alarm 紅色三角形、warning 黃橙圓形、caution 黃色方形**,緊急警報加外框;**未確認閃爍、caution 不閃**;靜音中加「喇叭 + 斜線」並顯示剩餘秒數;已解除未確認加打勾;已確認不閃。每列「確認」送 `alertAcknowledge {id}`(caution 不需確認、無按鈕),標題列「靜音 30s」送 `alertSilence`、「全部確認」送 `alertAcknowledge`。
- **聲響**(`src/lib/alert-sound.ts`):依 `audible` 警報的最高優先序以 Web Audio 發短嗶聲(緊急警報連續、alarm 三短聲每 2 s、warning 兩短聲每 6 s、caution 無聲);核心靜音後 `audible` 變 false 即停。瀏覽器要求互動後才可發聲,第一次點擊/按鍵才建立 AudioContext。面板的「聲響 開/關」是本頁設定(預設開,存 localStorage `simosa-brm.instruments.alertSound`),不送核心。
- 標題列顯示「警報未確認 n」與「BNWAS 第 n 階段」(第 2 階段以上)。
- conning 條依 `sensors.*Valid` 標「無效」並淡化保持的最後值:HDG(電羅經)、STW / SOG / COG(計程儀 / GNSS)、UKC(測深儀,無效時不觸發 UKC 警報)、LAT/LON(GNSS 無效)。
- 自動舵面板:`autopilot.available = false` 時「自動舵 不可用」並停用開啟;核心脫開自動舵(`autopilotTripped`,state.autopilot.enabled 由 true 變 false)時同步關閉本頁面板。

**教官站(`instructor.html`)** 中欄下方新增兩個面板(寬度 > 1440 px 三欄、≤ 1440 px 兩欄,放不下時捲動):

- `<brm-alert-monitor>` 警報 / BNWAS:現行警報(唯讀狀態 + 「代為確認」送 `alertAcknowledge {id}`、「全部確認」、「靜音 30 s」)、BNWAS 狀態與各階段次數(由事件流 `bnwasStage`/`bnwasReset` 計數)、**模式切換**「關 / 自動 / 開」送 `bnwasMode`(可填休眠期 180–720 s,空白不變更)、重設;**警報歷史**由事件流 `alertRaised/Silenced/Acknowledged/Rectified/Escalated` 推得(最新在上,含確認耗時,> 30 s 橙色、未確認紅色)。簡化教官模式側欄放 `compact` 版(現行警報 + BNWAS 列)。
- `<brm-sensor-truth>` 真值 vs 指示值:航向(電羅經)、磁羅經、位置(GNSS,ENU 公尺)、COG、SOG、STW(計程儀)、龍骨下水深的指示/真值/差距;該感測器有相關故障、旗標無效,或差距超過門檻(航向 1°、位置 30 m、速度 0.3 kn、水深 0.3 m)時整列標紅;另列核心自動舵狀態。
- 故障面板改以 `src/Contracts/fault-catalogue.json` 為目錄(建置時匯入;標籤、練習、警報優先序、參數):核心會改變行為的故障為按鈕(附「警報 / 警告」標記),只記錄者收在「其他故障(僅記錄)」選單;「參數注入」可填 gyroDrift `rate_degPerMin`、gpsJump/gpsOffset `distance_m`/`bearing_deg`/`ramp_s`、logFailure `bias_kn`/`factor`,送 `{"type":"injectFault","args":{"name":"gyroDrift","args":{"rate_degPerMin":-1}}}`(不填參數時仍送 `value` 字串)。
- 時間表面板的確認快速鈕多一個「羅經比對異常」(`acknowledge {kind: gyroCheck}`,E08)。
- 事件流:警報/BNWAS/自動舵脫開事件有中文說明(`alertEventLabel`),alarm 成立/升級與 BNWAS 第 2 階段以上紅、warning 成立與第 1 階段/自動舵脫開橙;`commandRejected` 的 `autopilotUnavailable`、`unknownAlert` 顯示原因,並標到對應的教官指令。
- 講評摘要(核心 summary 優先):新增「警報與 BNWAS」段 —— 成立/確認/未確認/升級/靜音次數、平均與最長確認時間、BNWAS 第 1/2/3 階段次數與重設次數、每筆警報紀錄(確認耗時、升級、解除時刻);故障應變列加「警報確認 +n s」(`faultResponses[].alertAckDt_s`)。

**示範模式**(`src/demo/demo-alerts.ts`,**假的**,只供離線預覽,不是核心實作):`DemoSource` 預設開啟(`DemoSim` 預設關,以維持既有測試的事件序列)。
- 示範假警報:4–150 s「航向監視:電羅經與磁羅經差異(示範假警報)」warning(未確認 300 s 會升級為 alarm)。
- BNWAS:模式開、休眠 180 s,開始時休眠期已過 170 s → 約 10 s 第 1 階段、25 s 第 2 階段(警報升級為 alarm)、115 s 第 3 階段,第 3 階段 30 s 後由示範**自動重設**(`bnwasReset {by: demoAutoReset}`)形成循環;舵令/車令/側推/自動舵/操舵模式/警報確認或靜音皆重設 BNWAS(與核心相同)。
- 故障類警報(依警報目錄 001–014)與 MSC.302 狀態機:確認、靜音 30 s 後重響、已解除未確認、確認後移除;BNWAS 警報重設即解除。
- 感測器故障改變指示值並輸出 `truth`/`sensors`/`autopilot`:gyroDrift(自動舵跟隨指示航向、真航向偏離)、gyroFreeze、gyroFailure(自動舵脫開、不可再啟用,`commandRejected {reason: autopilotUnavailable}`)、gpsJump/gpsOffset/gpsFailure、echoSounderFailure、logFailure;未知警報 id 回 `commandRejected {reason: unknownAlert}`。

**與規範/核心的差異**(同設計文件第 9 節):責任轉移(responsibility transferred)與 Category A 只能在相關工作站確認未實作(任一站可確認);BNWAS 第 3 階段仍為 alarm;圖示與聲響只是 IEC 62288 / MSC.302 的概念示意,非型式認可的圖形與聲音特性。

畫面以 Playwright(Chromium,1366×768 與 1920×1080,示範模式)檢查過:無水平捲動、console 無錯誤。

## 拖船、錨與繫纜(`docs/design/physics-tugs-anchors-mooring.md`)

C# SimCore 物理第 2 階段(拖船 T 項、錨與錨鏈 A 項、碰墊與繫纜;規劃書第 6.2 節、第 8.1 節 E12–E14、第 8.3 節第 4 類)在網頁端的對應;Python 參考伺服器不輸出這些欄位、這三個指令回 ok=false。

**狀態解析**(`src/types/external.ts`、`src/lib/external-forces.ts` 的 `applyExternalForceBlocks`,由 `state-parser` 呼叫;重播檢視也解析):`state.tugs[]`(模式、狀態、實際出力 t、實際/命令方向(相對船艏)、出力令、效能、繫柱拖力、斷纜、示意位置)、`state.anchors[]`(依左、右排序;鏈長 m/節、最大鏈長、錨鏈孔張力/水平張力、躺底長、抓地力、剎車、鏈端、錨位)、`state.berth`(碼頭硬接觸、受壓碰墊、各纜狀態/張力/MBL/繫船柱/未受力纜長)。**舊狀態沒有欄位時為 undefined、不記「資料可疑」、面板不顯示**;型別錯誤才記入 issues。

**指令**(對外度/噸/公尺;`command.schema.json` `$defs`):

| 指令 | 來源 | 例 |
|---|---|---|
| `tugControl` | 教官站拖船面板(模式鈕附上方向/出力草稿;轉盤放開、方向欄 change、出力滑桿 change 時單獨送該欄位) | `{"type":"tugControl","args":{"id":"TG1","mode":"push","direction_deg":270,"power_pct":50}}` |
| `anchor` | 錨面板(目標鏈長以節送 `targetLength_shackles`;空白 = letGo/veer 到鏈端、heave 收妥) | `{"type":"anchor","args":{"side":"starboard","action":"letGo","targetLength_shackles":4}}` |
| `mooring` | 繫纜面板(依動作只放相關欄位;slack 預設 2 m) | `{"type":"mooring","args":{"lineId":"HEAD","action":"makeFast","bollard":"B6"}}` |
| `injectFault tugLineBreak` | 拖船面板「斷纜故障」鈕 | `{"type":"injectFault","args":{"name":"tugLineBreak","args":{"id":"TG2"}}}` |

`direction_deg` 是**作用於自船的力方向**,相對船艏順時針(000 向前、090 向右舷、180 向後、270 向左舷)。

**教官站(`instructor.html`)**:

- `<brm-tug-panel>`:每艘拖船一張卡 —— 狀態(出力中綠、帶纜/換位中黃、拖纜斷裂紅底)、實際出力 / 繫柱拖力、方向(實際 → 命令)、出力令、效能(< 60 % 標示);推頂/拖拉/待命/解纜四鈕(情境 `modes` 不含者停用;斷纜時「拖拉」提示會被拒)、力方向轉盤(船形朝上;點選/拖曳設定、方向鍵 ±5°、Enter 送出;綠線 = 實際、虛線 = 命令、藍點 = 草稿;只有放開 pointerup 才送出,系統取消 pointercancel / lostpointercapture 只還原草稿)、方向欄、出力滑桿(5 % 一格)、斷纜提示與「斷纜故障」鈕。草稿只在編輯中與「已送出、核心尚未回報」時蓋過 state:state 的命令值與送出值一致、或約 2 s(50 次狀態更新)仍未一致(例如被拒)即丟棄,拖船 id 集合改變或教官站重設/載入情境/倒帶/切換來源(`epoch` 遞增)時清空,之後模式鈕附上的方向與出力一律取 state 的命令值。
- `<brm-anchor-panel>`:左/右錨卡 —— 狀態(抓住綠、送出/起錨/離底黃、走錨紅框紅底)、鏈長「n 節(m)/ 最大節數」、張力(水平)、躺底長、抓地力、剎車;水平張力 / 抓地力比例條(≥ 80 % 黃、走錨紅);目標(節)輸入與 拋錨/鬆鏈/起錨/剎車/放鏈 五鈕(明顯會被核心拒絕者停用並附原因:收存時鬆鏈/起錨/剎車、著底(含起錨中 heaving,即使鏈已上下直立、躺底長 0)或送出中再拋、鏈端時鬆鏈/放鏈)。
- `<brm-mooring-panel>`:泊位名稱、碼頭硬接觸警示、受壓碰墊(壓縮 % 相對額定壓縮:< 60 % 綠、60–100 % 黃、過載紅;反力 kN)、各纜張力條(相對 MBL:< 50 % 綠、50–80 % 黃、≥ 80 % 或斷裂紅;**假設**:以 MBL 50 % 為一般工作上限)、繫船柱選單(泊位幾何)+ 帶纜、目標張力 + 絞纜、放出長度 + 鬆纜、解纜。
- 版面:拖船(情境有拖船時)與繫纜(有泊位時)面板排在中欄下方最前面;錨面板在錨使用中、情境初始已錨泊或有泊位/拖船時排前面,否則排在最後(核心有錨資料時一律輸出 anchors,不擠下警報監看)。
- 簡化教官模式執行畫面:情境有拖船時側欄多一個**精簡拖船令**面板(`compact`:每艘一列,推頂/拖拉/待命/解纜、向前/向右舷/向後/向左舷、25/50/75/100 %;預設鈕以 state 的命令值標示選取;供引水人/船長下拖船令的情境)。
- 航跡圖(兩頁共用 `<brm-track-plot>`):碼頭線(硬接觸時紅)、碰墊(碼頭面向水域突出的矩形,受壓時依壓縮比例著色、標 `F06 21%`)、繫船柱、纜繩(導纜孔 → 繫船柱,依張力/MBL 著色、絞/鬆中虛線、標張力)、拖船示意位置(◆,拉時畫拖纜)與作用於連接點的力向量(長度 ∝ 出力)、錨位(⚓)與錨鏈(錨鏈孔 → 錨;抓住綠、走錨紅)。泊位幾何由情境 `berth`(`file` + `id` 以建置時匯入的 `data/areas/*.json` 解析;重播時用紀錄 header 內嵌的 `berth.definition`)以 **WGS-84 曲率半徑**換算(與核心 `LocalTangentPlane` 相同);船體具名點(fwd/aftPort…)與錨鏈孔估計式同核心,錨鏈孔優先用 particulars `anchors.hawsePipe`。
- 事件流與教官指令紀錄:新事件 `tugCommand`、`tugLineParted`、`anchorLetGo`/`anchorCommand`/`anchorBroughtUp`/`anchorDragging`/`anchorHolding`/`anchorAweigh`/`anchorHoused`/`anchorChainFullyOut`/`anchorBrakeSlipping`/`anchorWindlassStalled`、`fenderContact`、`quayContact`、`mooringCommand`、`mooringLineParted` 有中文說明;斷纜、走錨、碼頭硬接觸、碰墊過載為 alarm(紅),剎車滑出/絞盤停轉/鏈端與接觸法向速度 > 0.15 m/s 的碰墊接觸為 warning(**假設**門檻,待公司程序參數集);`commandRejected` 的 19 種外力拒絕原因(`externalForcesUnsupported`、`unknownTug` … `mooringLineParted`)以中文顯示並附對象(拖船 id / 左右錨 / 纜 id)。

**操船台(`index.html`)**:航跡圖左上角浮貼 `<brm-external-readout>`(不佔中央警報面板高度):錨(兩錨收存時一行;使用中顯示狀態、鏈長、張力,走錨紅)、拖船(狀態、出力 t、方向與口語「向左舷」等,斷纜紅);**接近泊位時**(船舯距碼頭面 < 500 m;沒有泊位幾何時以 `state.berth` 存在且 SOG < 4 kn 判斷)顯示艏/艉橫向速度(m/s,右/左)與距碼頭、艏/艉朝碼頭的法向接近速度(> 0.15 m/s 黃)。**state 沒有艏/艉速度欄位,由 `v + r·(±LPP/2)` 推算,畫面註明**:橫向速度為對水(船體座標);碼頭接近速度為**對地**(船體速度轉 ENU 後加 `state.current` 海流,與核心 `BodyFrame.PointGroundVelocity`、碰墊接觸速度 `normalSpeed_mps` 與 `summary.berthing` 同基準)。核心日後提供欄位時改用之。泊位幾何依 `state.scenario.id` 從建置時匯入的情境清單取得(只在 state 有外力欄位時才動態載入,儀器頁一般情況不載 YAML 解析器)。

**示範模式**(`src/demo/demo-external.ts`,**假的**,只供離線測試,不影響示範運動學):兩艘假拖船 TG1(fwd)/TG2(aft)40 t —— 出力一階上升(τ 30 s)、方向 3 °/s 走最短弧、速度效能(推 3/5 kn、拉 4/7 kn)、帶纜 20 s / 推↔拉換位 15 s(示範縮短)、`tugLineBreak` 斷纜與清除;左右錨依 particulars(節數、鏈重、錨鏈孔)—— 拋錨 2 m/s、鬆鏈 0.5 m/s、起錨依起錨速率、著底/離底/收妥、以「水平距離超過鬆弛長度」的線性彈簧近似張力、超過抓地力走錨;自船初始位置右舷 150 m 的示意泊位與三條纜(HEAD/BREAST/STERN;帶纜預張力、T = MBL·(ε/0.18)^1.5、絞/鬆 15 m/min、超過 MBL 斷纜)。拒絕原因與事件 kind/data 與核心同名。教官站載入有 `tugs`/`berth` 的情境時,示範改用情境的拖船與泊位定義(`src/demo/demo-scenario.ts`):本地 ENU 原點 = 情境 `origin`(無則初始緯經度),自船自 `initial.position` 的 x/y(只有 lat/lon 時以 WGS-84 換算到 origin)起算,與航跡圖疊圖同一座標系;纜繩 `initial: fast` 在開始與重設時即帶纜(與核心相同,不發事件;超出纜長則維持收存)。示範目標船、假航線與內建示意泊位皆相對自船初始位置。

畫面以 Playwright(Chromium,1366×768 與 1920×1080,示範模式)檢查過教官站完整/簡化模式與操船台:無水平捲動、console 無錯誤。

## 程式結構

```
src/Instruments.Web/
├─ index.html                 儀器頁入口(載入 theme.css 與 main.ts)
├─ instructor.html            教官站入口(載入 theme.css 與 instructor-main.ts)
├─ report.html                講評報告頁入口(載入 theme.css 與 report-main.ts)
├─ vite.config.ts             進入點 index / instructor / radar / report;輸出到 build\web\instruments、快取到 .cache\vite、@data 別名指到 data\
├─ tsconfig.json              strict、Lit 裝飾器設定
├─ .npmrc                     pnpm 套件庫指到專案 .cache\pnpm
├─ src/
│  ├─ main.ts, app.ts         <brm-app>:儀器頁版面、資料來源、主題、指令路由
│  ├─ instructor-main.ts, instructor-app.ts
│  │                          <brm-instructor-app>:教官站 / 簡化教官模式 / 重播檢視的版面與流程
│  ├─ report-main.ts, report/ <brm-report-app>(report-app.ts,light DOM)與 report.css(A4 列印);behavioural-anchors.json + anchors.ts(錨定描述)、
│  │                          marker-form.ts(評估表、驗證、帶入標記、一致率)、debrief-report.ts(報告模型、manoeuvres[]、參數集來源)、
│  │                          training-record.ts(名冊、班次、個人訓練紀錄、CSV)、report-views.ts(列印樣板)、report-store.ts(IndexedDB、教官站交接)
│  ├─ styles/theme.css        日/黃昏/夜 CSS 變數(穿透 shadow DOM)
│  ├─ types/                  state.ts(含 scenario/route/visibility_nm/steering/targets/truth/sensors/autopilot/alerts/bnwas)、alerts.ts(警報/BNWAS/真值型別)、
│  │                          target.ts(TargetState、AIS 報告)、command.ts、event.ts(SimEvent、kind 列舉、
│  │                          AcknowledgeKind;對應 Contracts 的 JSON Schema 與設計文件第 3.2 節)
│  ├─ lib/                    angles(正規化、角差)、format(度/分、航向、時間)、state-parser(缺欄位防呆;含 targets[])、
│  │                          targets(cpaAlarm 目標集合、CPA/TCPA 小表列)、steering(steeringMode 指令、由原始框取 state.steering)、
│  │                          command(指令建構與序列化)、ship-config(由 particulars.json 取量程/車鐘表)、
│  │                          ship-geometry(船形、船體→ENU)、track-history、theme(主題讀寫)、download(下載)、
│  │                          scenario(情境 YAML 解析與摘要)、timeline(timeline/route/zones/briefing 與 ${params:…} 解析、
│  │                          條件/動作摘要、經緯度→ENU)、assessment(情境 assessment 規則與核心 summary 解析)、
│  │                          sim-event(事件解析、文字化、嚴重度、EventStore)、voice-script(語音腳本播放)、
│  │                          plot-overlay(航線/區域/事件疊圖)、instructor-command(教官指令與故障目錄)、
│  │                          operation-log(學員操作推導)、debrief(本地講評摘要)、brm-markers(BRM 標記與匯出)、
│  │                          session-record(教官站紀錄 v2)、record-replay(JSON Lines / 教官站紀錄解析,含 event/summary)、
│  │                          alerts(警報/BNWAS/真值解析、排序、圖示狀態、指令、歷史、事件文字)、alert-sound(Web Audio 嗶聲)、
│  │                          external-forces(拖船/錨/泊位解析、標籤與拒絕原因、指令、事件文字、泊位幾何、艏艉橫向速度)
│  ├─ net/                    sim-source.ts(來源介面,含非狀態訊息)、ws-client.ts(WebSocket + 重連 + ack/事件框)
│  ├─ demo/                   demo-sim.ts(示範運動學;假事件、情境/航線狀態、假 summary、操舵模式、假目標船)、demo-source.ts(25 Hz 廣播 + 事件框)、
│  │                          demo-alerts.ts(示範感測器故障、警報狀態機、BNWAS 循環)、demo-external.ts(示範拖船、錨、泊位與繫纜)
│  └─ components/             brm-conning-bar 及其各儀器(含 route-readout)、brm-track-plot(含疊圖、目標船)、brm-helm、
│     │                       brm-telegraph、brm-thruster-lever、brm-autopilot、brm-instructor、brm-alert-panel(中央警報面板)、
│     │                       brm-bnwas(BNWAS 指示)、alert-icon(警報圖示)、brm-external-readout(操船台錨/拖船/靠泊讀數)
│     └─ instructor/          scenario-panel、timeline-panel、event-stream、run-control-panel、environment-panel、
│                             fault-panel、ownship-panel、steering-panel、operation-log-view、command-log-view、brm-marker-bar、
│                             debrief-summary(核心/本地)、replay-view、alert-monitor(警報/BNWAS 監看)、sensor-truth-panel(真值 vs 指示值)、
│                             tug-panel(拖船,含精簡版)、anchor-panel(錨)、mooring-panel(泊位碰墊與繫纜)
└─ tests/                     vitest:angles、format、state-parser、command、ship-config、demo-sim、
                              ws-client(假 WebSocket)、components(happy-dom 渲染與指令事件)、
                              scenario、timeline(含設計文件 E07 範例)、assessment(含設計文件 summary 範例)、
                              sim-event、voice-script、session-record、instructor-command、operation-log、debrief、
                              brm-markers、record-replay、timeline-panel(時間表面板與事件流、必要事件倒數三種來源)、
                              targets(targets[] 解析含舊狀態、cpaAlarm 集合、小表)、steering(指令建構、state.steering 取出、WsClient 補欄位、事件文字)、
                              instructor-components(教官站元件事件、操舵模式面板、講評摘要、重播檢視、航線方塊)、
                              instructor-app(重播檢視常駐、示範模式的操舵模式指令/操作紀錄/commandRejected)、
                              core-record(C# 主控程序實際紀錄的契約一致性:header v2/parameterSet/event/summary/state.scenario+route,
                              以及 header.scenario 與 data/scenarios YAML 經 parseScenario 結構相同)、
                              alerts(警報/BNWAS/真值解析含 E08 範例、排序與圖示狀態、指令、BNWAS 倒數、歷史、summary.alerts、假 AudioContext 聲響、
                              fault-catalogue 同步、示範警報與 BNWAS 循環)、alert-components(中央警報面板、BNWAS 指示、conning 無效標示、
                              教官站警報監看/真值面板/講評/故障面板、兩頁示範模式整合)、
                              external-forces(拖船/錨/泊位解析含舊狀態、指令內容、事件文字與嚴重度、19 種拒絕原因、泊位幾何、
                              艏艉橫向速度、情境 berth 解析與疊圖、示範拖船/錨/繫纜)、external-components(拖船/錨/繫纜面板指令與停用規則、
                              精簡拖船令、操船台讀數、教官站只在有欄位時顯示面板、拒絕原因標在教官指令紀錄)
                              fixtures/:E07_steering_failure.yaml、summary-E07.json(取自設計文件第 8.2、4.8 節);
                              record-E07-core.jsonl、record-E01-core.jsonl(`dotnet run --project src/SimCore.Host -- --scenario … --record … --duration 300 --timescale 10` 的縮短版:
                              header、每 5/10 s 一筆 state、全部 event、3 筆 snapshot、summary、footer;footer 的 lines/sha256 為原檔值)
                              report-marker-form(錨定描述檔、表單驗證、時間輸入、標記帶入、一致率、匯出入)、report-debrief(E07 夾具產生報告、
                              manoeuvres 比較表、參數集來源、列印樣板)、report-training-record(聲明文字、加入班次、個人紀錄、CSV 欄位、JSON)、
                              report-app(報告頁元件、交接、教官站入口)、report-review-fixes(2026-10-17 審查發現:穩定紀錄鍵 startedUtc、
                              一致率需評完、未完成評估表不計分、未綁定評估表、評估員判定/備註、觀察時刻、損壞檔、CSS/CSV 注入、出席矩陣、本地日期)
```

船舶參數(LPP、船寬、舵角上限、舵機速率、MCR 轉速、車鐘各段轉速與速度、側推參數、吃水)直接由 `data/ships/FSB1|FSB2/particulars.json` 在建置時匯入(`src/lib/ship-config.ts`),與核心共用同一份資料。

## 已知限制(第一版)

- 自動舵的開/關與設定航向由本頁面保存;C# 核心的 `state.autopilot` 目前只用於「不可用」標示與核心脫開時同步關閉(設定航向仍以本頁為準)。
- 學員站儀器頁的操舵模式只有 FU/NFU/自動舵(舵機泵選擇與應急操舵目前只在教官站的操舵模式面板);航跡模式與第 7.2 節其他儀器(雷達、ECDIS、VHF)不在本版範圍;BAM 中央警報面板與 BNWAS 指示見上方「警報管理(BAM)與 BNWAS」(ALF/ALC 句型、責任轉移未實作)。
- 鳥瞰圖有自船、目標船與情境航線/區域,尚無海圖;目標船只畫真值位置(AIS 報告值 `ais` 已解析但未另畫 AIS 位置);3D 視景另見 `src/Visual.*`。
- 示範模式為簡化運動學,不可用於訓練或驗證。
- UKC 警報門檻先固定 1.0 m(警告 2.0 m),之後改由公司程序參數集提供(規劃書第 9.1 節)。
- 尚未接 USB 操控台(`src/Controls.Hid`);鍵盤與滑鼠操作為第一版替代。
- 教官站:時間倍率與快照清單由本頁保存(狀態訊息沒有這些欄位),核心自動快照不會列出;學員操作紀錄由狀態推導,自動舵的舵令也會被記為舵令;`loadScenario` 在 Python 參考伺服器尚未實作;自船覆寫借用 `reset` 指令(C# 核心忽略 `args`);無通訊角色扮演;教官站紀錄與 BRM 標記未簽章、不含錄音錄影;本地推導的講評閾值為常數;重播檢視沒有儀器時序曲線。
- 情境引擎(教官站側):`${params:…}` 不在教官站解析,引用的門檻顯示為「參數 key」,必要事件倒數在 `within_s` 為參數引用時需核心事件 data 帶 `deadline_t` 或 `within_s`(否則只顯示「計時中」);情境檔中的參數引用**必須加引號**(`"${params:x}"`)—— 未加引號時在 YAML 流式映射 `{ … }` 內是語法錯誤(`{`、`}` 為流式指示字元),教官站會把該情境標「有問題」;時間表項目的 `holdFor_s` / `delay_s` / `cooldown_s` 只顯示,不在教官站計時;語音音檔由 Vite 於建置時打包(新增音檔需重建),瀏覽器可能要求先有使用者互動才允許自動播放(紀錄標「被瀏覽器阻擋」,仍顯示文字);航線/區域疊圖的經緯度以情境 `origin` 平面近似換算(與核心的投影可能有公尺級差異,只供視覺參考);事件標記的位置取事件時刻的狀態抽樣(1 s)。

雷達 PPI 顯示頁(`radar.html`,雷達服務 WebSocket、示範模式、EBL/VRM/PI、ARPA 表與試操船)見 [RADAR.md](RADAR.md)。
