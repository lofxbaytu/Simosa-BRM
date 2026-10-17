# Instruments.Web — 駕駛台儀器、操船台與教官站(第一版)

中塑海運 FS Bitumen No.1(FSB1)/ No.2(FSB2)BRM 操船模擬器的網頁前端,兩個進入點共用程式庫與主題:

| 頁面 | 用途 |
|---|---|
| `index.html` | 駕駛台儀器與操船台(規劃書第 7 章第一版 conning 顯示與操船台);船上筆電以瀏覽器全螢幕顯示,之後包進 WebView2 殼(`src/Instruments.Shell`) |
| `instructor.html` | 教官站(第 9.1 節;訪船船長直連時用)、船上簡化教官模式(`?mode=simple`)與重播檢視(`?mode=replay`,第 9.2 節講評站雛型),見下方「教官站」一節 |

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
- **情境**:建置時以 `import.meta.glob` 匯入 `data/scenarios/*.yaml`(`yaml` 套件解析,`src/lib/scenario.ts`),列出 id、標題、船、裝載、環境摘要、初始狀態、種子與路徑,以及擴充欄位統計「時間表 n 項(手動 m)· 航線 n 航點 · 區域 n · 評估規則 n 條 · 參數引用 n 個 · 目標船 n」(`src/lib/timeline.ts`、`src/lib/assessment.ts` 解析設計文件第 1 節的 `timeline` / `route` / `zones` / `briefing` / `assessment`);`briefing` 物件版顯示角色(扮演者、必要)、最少人數與附件連結(`data/scenarios/<id>/<file>`);`${params:key}` 參數引用在教官站**不解析**,顯示為「參數 key」(核心載入時解析)。按「載入」送 `loadScenario`。示範模式下改以情境的船、初始航向/速度、風、流、水深重建示範運動學。
- **時間表**(`src/components/instructor/timeline-panel.ts`):列出情境 `timeline[]` 的 id、名稱、階段、重複規則、條件摘要(遞迴 all/any/not + 14 種 leaf)、動作摘要與狀態(待觸發 / 待手動 / 已觸發 n 次 @時刻 / 已停用 / 核心拒絕;由 `timelineTriggered` / `timelineArmed` / `timelineDisarmed` / `timelineManual` 事件推得,計數優先用 `state.scenario.timelineFired/Pending`,標題列顯示階段);`manual` 項目有「觸發」鈕(`triggerTimeline`),其他項目需**按住「強制」0.8 秒**(`force: true`);**確認**快速鈕列(口頭辨識、通知機控室、呼叫船長、報告、切換操舵模式、備錨、檢查表、其他)含角色(駕駛員/船長/引水人/舵工/瞭望/帶領者)、對應項目(已觸發且含故障注入的項目;預設自動對應)與備註,送 `acknowledge`;**必要事件倒數**:`assessment.required[]` 的 `after` 事件(例如 `faultInjected steeringFail`、`reportPointPassed WP3`)發生後依 `within_s` 顯示「剩 n s」/「逾時 n s」,`requiredEventMet` / `requiredEventMissed` 事件更新為達成/逾時。`within_s` 為參數引用(教官站不解析)時改由事件 data 取期限:規則 id 的事件(含核心「開始計時」事件)或 `after` 事件本身的 `data.deadline_t` 優先,否則 `data.within_s` + 該事件 `t`(`requiredEventMet` / `Missed` 的 `within_s` 視為自 `after` 事件起算),都沒有才只顯示「計時中」;簡化教官模式的講評摘要同樣顯示剩餘/逾時秒數(核心 summary 的 pending 列,或無 summary 時的「必要事件(即時倒數)」表)。核心不廣播 `state.scenario` 也無事件(Python 參考伺服器)時標「核心不支援,僅顯示」並停用按鈕。
- **執行控制**:凍結/恢復/重設(重設按兩次確認)、**結束練習**(按兩次確認,送 `endExercise`;`state.scenario.ended` 後停用)、時間倍率 ×0.5 / 1 / 2 / 5 / 10、快照(列出最近 8 份的模擬時間與 tick)與還原。
- **環境**:真風速/向、陣風、流向/流速、水深、能見度,分組「套用」或「全部套用」(只送有改的欄位);**漸變 s** 欄位附在每次套用上(`ramp_s`,0 = 立即);「帶入目前值」把核心現況(含能見度)填回表單。只有 C# 核心支援的欄位(陣風、能見度、漸變)標示「僅 C# 核心」但仍送出。
- **故障**:目錄按鈕(舵機失效、主機失效、側推失效、電羅經漂移、GPS 跳點、雷達失效、全船失電,名稱優先取自 `src/SimCore/Engine/FaultNames.cs`,讀不到時用內建預設;核心不改變行為者標「僅記錄」)、自訂名稱、現行故障清單(由狀態 `faults` 欄位)逐項或全部清除。
- **自船覆寫**:位置 x/y、航向、速度、裝載、拖船數,按兩次確認後以 `reset` 附 `args` 送出(支援情形見上表)。
- **操舵模式**(`src/components/instructor/steering-panel.ts`、`src/lib/steering.ts`;規劃書第 7.2 節,設計文件 D32/D35):顯示 `state.steering`(模式、泵、NFU 推舵中;C# SimCore 產生,Python 參考伺服器不輸出時標「核心未回報」。`state-parser.ts` 不認得此欄位,由 `ws-client.ts` 以原始框補上);按鈕 **FU / NFU / 自動舵 / 應急操舵** 送 `steeringMode`(`value` 為模式);NFU 的 **左 / 右推桿按住**送 `args.direction: port|starboard`、**放開**(含指標離開)送 `stop`,「NFU 停」送 `stop`;舵機泵選擇(No.1 / No.2 / 1 + 2)附在每筆指令的 `args.pump`,已在某模式時改選泵即送同模式 + 新泵;自動舵可帶設定航向與 ROT 限制(`args.heading` / `rotLimit`,只在 `autopilot` 時送)。應急操舵中自動舵鈕停用並提示需先切回 FU;核心仍拒絕時(`commandRejected` 事件,C# 不回 ack 框)把最近一筆同類型的教官指令標為「核心拒絕:應急操舵中不可自動舵」。
- **鳥瞰航跡圖**:與儀器頁相同的 `<brm-track-plot>`,疊上情境的航線(航點、檢查點/報告點空心圓、到達半徑、各航段走廊半寬虛線;`route.xtdLimit_m` 為參數引用時無走廊)與區域多邊形(禁區紅、速限橙、UKC 政策藍、報告灰;`src/lib/plot-overlay.ts`,經緯度以情境 `origin` 平面近似換算),以及事件標記(warning 橙、alarm 紅三角,位置取事件時刻的狀態抽樣)。**目標船**(`state.targets[]`,`src/types/target.ts`、`src/lib/targets.ts`):三角形依航向、船名/ID 標籤、COG/SOG 虛線短向量;AIS 關閉者空心虛線並標「(AIS 關)」;處於 `cpaAlarm` 的目標紅色(由事件流 `cpaAlarm` / `cpaClear` 推得);完整模式左上角列 **CPA/TCPA 小表**(目標、距離、方位、CPA、TCPA;警報者在前,其餘依距離,最多 8 列)。簡化教官模式只畫目標船,不列小表。
- **事件**(`src/components/instructor/event-stream.ts`):核心推送的 `{"type":"event"}` 框(`src/lib/sim-event.ts` 的 `parseSimEvent` / `eventLabel`;舊 `TrafficEvent` 欄位不變,`source` 缺時由 kind 推得)依時間列出,最新在上,只保留最近 2,000 筆;左緣顏色依來源(時間表藍、評估綠、目標船黃、教官灰、引擎),`hardFail` / `collision` / `grounding` / `bandFail` / `requiredEventMissed` 紅字,`cpaAlarm` / `faultInjected` / `xtdExceeded` / `zoneEntered` / `ukcAlarm` / `speedLimitExceeded` 橙字,`visible: true` 標「學員可見」;來源篩選鈕;語音事件有「播放」鈕;點擊事件 → 切到重播檢視並跳至該時刻(需已載入紀錄)。
- **語音腳本**(`src/lib/voice-script.ts`):收到 `voiceScript` 事件時,有 `file` 用 `<audio>` 播情境包音檔(`data/scenarios/<id>/<file>`,建置時以 `import.meta.glob` 打包 `*.ogg|mp3|wav|m4a|opus`;找不到或載入失敗退回 TTS),無檔用 `speechSynthesis`(語言依 `lang`,預設 zh-TW);完整教官站按「播放」,簡化教官模式自動播放;每次播放記入學員操作紀錄(kind `voiceScript`,含音檔/TTS/被瀏覽器阻擋/僅顯示),並寫入下載的紀錄。
- **學員操作紀錄**:教官站收不到學員站的指令(指令直接進核心),因此由連續狀態推導(`src/lib/operation-log.ts`):舵令(1 秒內的連續變更合併)、車鐘、轉速令、側推、凍結/恢復、故障注入/清除、擱淺/碰撞、時間倒退、**操舵模式/泵**(`state.steering` 變化,NFU 推桿推舵/放開亦記;核心的 `steeringModeChanged` 事件經 `recordSteering()` 記入,同一模式與泵在 1 s 內已記過時不重複),附模擬時間與本地時鐘。
- **教官指令**:本站送出的每筆指令與核心回應(Python 參考伺服器的 ack;C# 不回應)。
- **參數集版次**:標題列顯示 `state.scenario.parameterSetVersion`(例如「參數集 2026.10.1」)與情境階段(`degraded` 橙、`emergency` 紅)、硬性失敗、練習已結束;以下情形標「參數集不一致」(滑鼠停留看原因):核心載入的情境 id 與所選不同、情境檔有參數引用但核心未回報版次、核心 summary 的版次與狀態不同。
- **BRM 快速標記**:第 8.2 節五大類(領導、狀況覺知、溝通、團隊合作、決策)各一鈕,可先填一句備註與 ＋(做得好)/ Δ(待改進)再按類別;鍵盤 1–5 亦可(焦點不在輸入欄時)。標記含模擬時間、tick、本地時鐘、情境 id,不經核心;「匯出 JSON / CSV」下載(`src/lib/brm-markers.ts`;CSV 含 UTF-8 BOM 供 Excel)。
- **下載紀錄 JSON**:本站自己的練習紀錄(`src/lib/session-record.ts`,`format: simosa-brm-instructor-record`,版本 2):狀態每 1 s 抽樣、學員操作、BRM 標記、指令與回應、本地講評摘要、核心事件(`events`)與核心 summary 原始物件(`coreSummary`,無則 null);可在重播檢視開啟(版本 1 的舊紀錄仍可讀)。這不是核心的 JSON Lines 紀錄(那份在核心主機的 `build/records/`),也尚未簽章(第 5.5 節紀錄包待做)。

### 簡化教官模式(`?mode=simple`)

船長在船上筆電單機用的精簡流程,同一頁、大字:

1. **選擇練習**:情境清單,按「載入」→ 送 `loadScenario` 後立即 `freeze`。
2. **簡報**:大字顯示情境 YAML 的 `briefing`(字串或 `briefing.text`),下方列出角色/扮演者/最少人數與附件標題;標題顯示核心回報的參數集版次。沒有簡報時顯示說明 + 船/裝載 + 初始 + 環境摘要。按「開始練習」送 `resume`,並清空操作紀錄、標記與講評累計。
3. **執行中**:大字狀態列(模擬時間、HDG、ROT、STW/SOG、舵、車鐘、UKC、階段)、最近一則語音/訊息橫幅(語音事件**自動播放**,播放中顯示 ▶)、鳥瞰航跡圖(疊航線/區域)、大字 BRM 標記鈕(含備註與 ＋/Δ)、精簡版時間表面板(只有必要事件倒數與確認鈕)、學員操作紀錄;「凍結/恢復」、「結束練習」(按兩次確認,送 `endExercise` + `freeze`,並查詢 `getAssessment`)。
4. **結束**:講評摘要 —— 有核心 summary 時優先顯示核心評分(分數/及格線/通過或硬性失敗、定稿與否、參數集版次)、硬性失敗、必要事件(達成/逾時/逾時後達成/不適用、時間差與時限、扣分、引用的程序文件與版次/條款)、容許帶、故障應變(首個動作、各確認與時間差)、總覽(含會遇、航跡、UKC 門檻來源、舵角分箱、車令段落、階段)與引用的程序參數;沒有(Python 參考伺服器、離線)時退回本地推導(`src/lib/debrief.ts`:練習時間、航程、平均/最大速度、超過港區速限的時間、最小 UKC 與時刻、UKC 低於門檻的時間、最大舵角與時刻、滿舵次數、舵令次數、最大 ROT 與時刻、ROT 超限時間、車鐘變更次數、擱淺/碰撞;超限以警告色標示);兩者皆有時可「並列顯示」本地推導供交叉核對。另有標記清單與匯出、「下載紀錄 JSON」、「新練習」。

本地推導的閾值為常數(港區速限 8 kn、UKC 警報 1.0 m、ROT 上限 30°/min、滿舵 35°;`DEFAULT_THRESHOLDS`);核心評分的門檻由情境 `assessment` 與公司程序參數集提供(第 9.1 節、第 8.3 節),summary 標示「自動評分僅輔助,最終由評估員判定」。靠離泊預演自動組題(第 9.1 節)不在本版。

### 重播檢視(`?mode=replay`,或教官站標題列「重播檢視」)

以檔案選擇器載入核心的 JSON Lines 紀錄(`src/SimCore/README.md` 第 4 節:`header` / `input` / `state` / `snapshot` / `event` / `summary` / `footer` 各行)或教官站下載的 JSON 紀錄(`src/lib/record-replay.ts` 自動判斷;未知的行種類只計數不列為錯誤,舊紀錄無 `event` / `summary` 行亦可):

- 時間軸拖曳、播放/暫停(×0.25 / 1 / 4 / 16)、上一筆/下一筆;任一時刻顯示 conning 條與鳥瞰航跡圖(航跡畫到該時刻為止,疊 `header.scenario` 的航線走廊與區域,事件標記畫到該時刻;**目標船**取該時刻那筆 `state.targets[]`,`cpaAlarm` 紅色由 t ≤ 該時刻的事件推得,CPA/TCPA 小表在非 compact 時顯示)。
- 時間軸上方的**事件標記**列:顏色依來源(時間表藍、評估綠、目標船黃、教官灰),warning 橙、`hardFail` / `collision` / `grounding` 紅,階段分界細線,BRM 標記黑;**倒帶標記**(`ReplayData.rewinds[]`,restore/reset 後 tick 倒退處)為橙色雙線,滑鼠停留顯示倒帶至的時刻與 tick(由 `t / header.dt` 推得)、來源時刻與丟棄的舊分支狀態/事件筆數,點擊跳至倒帶點;滑鼠停留看時刻與說明,點擊跳至。
- 輸入事件清單(tick × header.dt 換算時間;點選跳至)、事件流(同教官站;點選跳至)、BRM 標記(教官站紀錄才有;SimCore 紀錄不含)、講評摘要(**核心 summary 優先**,取紀錄最後一行 `summary`;顯示評分與是否通過、硬性失敗、必要事件、容許帶、故障應變、參數來源;無則本地推導,兩者皆有可並列)、header 資訊(情境、船、動力學模型、雜湊、**參數集 id/版次與已解析引用數**、評分)、略過的壞行數與未知行種類。
- 不需核心連線;航跡圖無海圖,儀器時序曲線、語音影像同步(第 9.2 節)待後續版本。

### 真實連線測試結果(Python 參考伺服器,2026-10)

以 `uv run simosa-brm serve --ship FSB1 --loading full --port 8765` 測試,結果見上方指令表:`setEnvironment`(風/流/水深)、`injectFault`/`clearFault`(只記錄)、`timeScale`、`snapshot`/`restore`、`freeze`/`resume`、`reset` 附 `args`(位置/航向/速度)皆有作用;`loadScenario` 回 `ok=false`「情境載入尚未實作」;`gustiness`、`visibility_nm`、`loading`、`tugs` 照收無作用。教官站畫面會把 ack 的 `ok`/`detail` 顯示在「教官指令」面板。

## 程式結構

```
src/Instruments.Web/
├─ index.html                 儀器頁入口(載入 theme.css 與 main.ts)
├─ instructor.html            教官站入口(載入 theme.css 與 instructor-main.ts)
├─ vite.config.ts             兩個進入點;輸出到 build\web\instruments、快取到 .cache\vite、@data 別名指到 data\
├─ tsconfig.json              strict、Lit 裝飾器設定
├─ .npmrc                     pnpm 套件庫指到專案 .cache\pnpm
├─ src/
│  ├─ main.ts, app.ts         <brm-app>:儀器頁版面、資料來源、主題、指令路由
│  ├─ instructor-main.ts, instructor-app.ts
│  │                          <brm-instructor-app>:教官站 / 簡化教官模式 / 重播檢視的版面與流程
│  ├─ styles/theme.css        日/黃昏/夜 CSS 變數(穿透 shadow DOM)
│  ├─ types/                  state.ts(含 scenario/route/visibility_nm/steering/targets)、target.ts(TargetState、AIS 報告)、command.ts、event.ts(SimEvent、kind 列舉、
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
│  │                          session-record(教官站紀錄 v2)、record-replay(JSON Lines / 教官站紀錄解析,含 event/summary)
│  ├─ net/                    sim-source.ts(來源介面,含非狀態訊息)、ws-client.ts(WebSocket + 重連 + ack/事件框)
│  ├─ demo/                   demo-sim.ts(示範運動學;假事件、情境/航線狀態、假 summary、操舵模式、假目標船)、demo-source.ts(25 Hz 廣播 + 事件框)
│  └─ components/             brm-conning-bar 及其各儀器(含 route-readout)、brm-track-plot(含疊圖、目標船)、brm-helm、
│     │                       brm-telegraph、brm-thruster-lever、brm-autopilot、brm-instructor
│     └─ instructor/          scenario-panel、timeline-panel、event-stream、run-control-panel、environment-panel、
│                             fault-panel、ownship-panel、steering-panel、operation-log-view、command-log-view、brm-marker-bar、
│                             debrief-summary(核心/本地)、replay-view
└─ tests/                     vitest:angles、format、state-parser、command、ship-config、demo-sim、
                              ws-client(假 WebSocket)、components(happy-dom 渲染與指令事件)、
                              scenario、timeline(含設計文件 E07 範例)、assessment(含設計文件 summary 範例)、
                              sim-event、voice-script、session-record、instructor-command、operation-log、debrief、
                              brm-markers、record-replay、timeline-panel(時間表面板與事件流、必要事件倒數三種來源)、
                              targets(targets[] 解析含舊狀態、cpaAlarm 集合、小表)、steering(指令建構、state.steering 取出、WsClient 補欄位、事件文字)、
                              instructor-components(教官站元件事件、操舵模式面板、講評摘要、重播檢視、航線方塊)、
                              instructor-app(重播檢視常駐、示範模式的操舵模式指令/操作紀錄/commandRejected)、
                              core-record(C# 主控程序實際紀錄的契約一致性:header v2/parameterSet/event/summary/state.scenario+route,
                              以及 header.scenario 與 data/scenarios YAML 經 parseScenario 結構相同)
                              fixtures/:E07_steering_failure.yaml、summary-E07.json(取自設計文件第 8.2、4.8 節);
                              record-E07-core.jsonl、record-E01-core.jsonl(`dotnet run --project src/SimCore.Host -- --scenario … --record … --duration 300 --timescale 10` 的縮短版:
                              header、每 5/10 s 一筆 state、全部 event、3 筆 snapshot、summary、footer;footer 的 lines/sha256 為原檔值)
```

船舶參數(LPP、船寬、舵角上限、舵機速率、MCR 轉速、車鐘各段轉速與速度、側推參數、吃水)直接由 `data/ships/FSB1|FSB2/particulars.json` 在建置時匯入(`src/lib/ship-config.ts`),與核心共用同一份資料。

## 已知限制(第一版)

- 自動舵的開/關與設定航向由本頁面保存(`state.schema.json` 尚無自動舵狀態欄位);核心若另有自動舵狀態,之後需加欄位同步。
- 學員站儀器頁的操舵模式只有 FU/NFU/自動舵(舵機泵選擇與應急操舵目前只在教官站的操舵模式面板);航跡模式與第 7.2 節其他儀器(雷達、ECDIS、BNWAS、BAM、VHF)不在本版範圍。
- 鳥瞰圖有自船、目標船與情境航線/區域,尚無海圖;目標船只畫真值位置(AIS 報告值 `ais` 已解析但未另畫 AIS 位置);3D 視景另見 `src/Visual.*`。
- 示範模式為簡化運動學,不可用於訓練或驗證。
- UKC 警報門檻先固定 1.0 m(警告 2.0 m),之後改由公司程序參數集提供(規劃書第 9.1 節)。
- 尚未接 USB 操控台(`src/Controls.Hid`);鍵盤與滑鼠操作為第一版替代。
- 教官站:時間倍率與快照清單由本頁保存(狀態訊息沒有這些欄位),核心自動快照不會列出;學員操作紀錄由狀態推導,自動舵的舵令也會被記為舵令;`loadScenario` 在 Python 參考伺服器尚未實作;自船覆寫借用 `reset` 指令(C# 核心忽略 `args`);無通訊角色扮演;教官站紀錄與 BRM 標記未簽章、不含錄音錄影;本地推導的講評閾值為常數;重播檢視沒有儀器時序曲線。
- 情境引擎(教官站側):`${params:…}` 不在教官站解析,引用的門檻顯示為「參數 key」,必要事件倒數在 `within_s` 為參數引用時需核心事件 data 帶 `deadline_t` 或 `within_s`(否則只顯示「計時中」);情境檔中的參數引用**必須加引號**(`"${params:x}"`)—— 未加引號時在 YAML 流式映射 `{ … }` 內是語法錯誤(`{`、`}` 為流式指示字元),教官站會把該情境標「有問題」;時間表項目的 `holdFor_s` / `delay_s` / `cooldown_s` 只顯示,不在教官站計時;語音音檔由 Vite 於建置時打包(新增音檔需重建),瀏覽器可能要求先有使用者互動才允許自動播放(紀錄標「被瀏覽器阻擋」,仍顯示文字);航線/區域疊圖的經緯度以情境 `origin` 平面近似換算(與核心的投影可能有公尺級差異,只供視覺參考);事件標記的位置取事件時刻的狀態抽樣(1 s)。
