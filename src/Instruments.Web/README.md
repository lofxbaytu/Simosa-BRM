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

Python 參考伺服器對每筆指令回 `{"type":"ack","command":…,"ok":…,"detail":…,"tick":…}`,教官站的「教官指令」面板會顯示;C# 核心目前不回應,只顯示「已送出」。

### C# SimCore(之後)

`SimCore.Host` 的 WebSocket 閘道(規劃書第 5.2 節 Instruments 模組)以相同的 state/command JSON 廣播與接收;儀器端不需改動,只要指到該閘道位址(`?ws=`)。船上直連部署時以固定私有 IP(第 5.4 節)。

### 示範模式

沒有核心時可在標題列切到「示範模式」(或 `?demo=1`):內建簡單運動學(`src/demo/demo-sim.ts`)—— 一階 Nomoto 航向響應(K、T 依瞬時速度換算)、速度一階滯後並隨舵角打折、舵機速率限制、轉速一階滯後、側推隨速度衰減、均勻流、相對風、squat/UKC —— 係數只與 FSB1 試俥概略對齊,**不是**第 6 章的 MMG 船模,僅供儀器獨立預覽與操作練習。

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
- **情境**:建置時以 `import.meta.glob` 匯入 `data/scenarios/*.yaml`(`yaml` 套件解析,`src/lib/scenario.ts`),列出 id、標題、船、裝載、環境摘要、初始狀態、種子與路徑;按「載入」送 `loadScenario`。示範模式下改以情境的船、初始航向/速度、風、流、水深重建示範運動學。
- **執行控制**:凍結/恢復/重設(重設按兩次確認)、時間倍率 ×0.5 / 1 / 2 / 5 / 10、快照(列出最近 8 份的模擬時間與 tick)與還原。
- **環境**:真風速/向、陣風、流向/流速、水深、能見度,分組「套用」或「全部套用」(只送有改的欄位);「帶入目前值」把核心現況填回表單。核心未支援的欄位標示「核心未實作」/「僅 C# 核心」但仍送出。
- **故障**:目錄按鈕(舵機失效、主機失效、側推失效、電羅經漂移、GPS 跳點、雷達失效、全船失電,名稱優先取自 `src/SimCore/Engine/FaultNames.cs`,讀不到時用內建預設;核心不改變行為者標「僅記錄」)、自訂名稱、現行故障清單(由狀態 `faults` 欄位)逐項或全部清除。
- **自船覆寫**:位置 x/y、航向、速度、裝載、拖船數,按兩次確認後以 `reset` 附 `args` 送出(支援情形見上表)。
- **鳥瞰航跡圖**:與儀器頁相同的 `<brm-track-plot>`。
- **學員操作紀錄**:教官站收不到學員站的指令(指令直接進核心),因此由連續狀態推導(`src/lib/operation-log.ts`):舵令(1 秒內的連續變更合併)、車鐘、轉速令、側推、凍結/恢復、故障注入/清除、擱淺/碰撞、時間倒退,附模擬時間與本地時鐘。
- **教官指令**:本站送出的每筆指令與核心回應(Python 參考伺服器的 ack;C# 不回應)。
- **BRM 快速標記**:第 8.2 節五大類(領導、狀況覺知、溝通、團隊合作、決策)各一鈕,可先填一句備註與 ＋(做得好)/ Δ(待改進)再按類別;鍵盤 1–5 亦可(焦點不在輸入欄時)。標記含模擬時間、tick、本地時鐘、情境 id,不經核心;「匯出 JSON / CSV」下載(`src/lib/brm-markers.ts`;CSV 含 UTF-8 BOM 供 Excel)。
- **下載紀錄 JSON**:本站自己的練習紀錄(`src/lib/session-record.ts`,`format: simosa-brm-instructor-record`):狀態每 1 s 抽樣、學員操作、BRM 標記、指令與回應、自動講評摘要;可在重播檢視開啟。這不是核心的 JSON Lines 紀錄(那份在核心主機的 `build/records/`),也尚未簽章(第 5.5 節紀錄包待做)。

### 簡化教官模式(`?mode=simple`)

船長在船上筆電單機用的精簡流程,同一頁、大字:

1. **選擇練習**:情境清單,按「載入」→ 送 `loadScenario` 後立即 `freeze`。
2. **簡報**:大字顯示情境 YAML 的 `briefing` 欄位(schema 尚未定義,可先在情境檔加上);沒有時顯示說明 + 船/裝載 + 初始 + 環境摘要。按「開始練習」送 `resume`,並清空操作紀錄、標記與講評累計。
3. **執行中**:大字狀態列(模擬時間、HDG、ROT、STW/SOG、舵、車鐘、UKC)、鳥瞰航跡圖、大字 BRM 標記鈕(含備註與 ＋/Δ)、學員操作紀錄;「凍結/恢復」、「結束練習」(按兩次確認,送 `freeze`)。
4. **結束**:自動講評摘要(`src/lib/debrief.ts`:練習時間、航程、平均/最大速度、超過港區速限的時間、最小 UKC 與時刻、UKC 低於門檻的時間、最大舵角與時刻、滿舵次數、舵令次數、最大 ROT 與時刻、ROT 超限時間、車鐘變更次數、擱淺/碰撞;超限以警告色標示)、標記清單與匯出、「下載紀錄 JSON」、「新練習」。

閾值暫用常數(港區速限 8 kn、UKC 警報 1.0 m、ROT 上限 30°/min、滿舵 35°;`DEFAULT_THRESHOLDS`),之後由公司程序參數集提供(第 9.1 節、第 8.3 節)。語音腳本、注入時間表、靠離泊預演自動組題(第 9.1 節)不在本版。

### 重播檢視(`?mode=replay`,或教官站標題列「重播檢視」)

以檔案選擇器載入核心的 JSON Lines 紀錄(`src/SimCore/README.md` 第 4 節:`header` / `input` / `state` / `snapshot` / `footer` 各行)或教官站下載的 JSON 紀錄(`src/lib/record-replay.ts` 自動判斷):

- 時間軸拖曳、播放/暫停(×0.25 / 1 / 4 / 16)、上一筆/下一筆;任一時刻顯示 conning 條與鳥瞰航跡圖(航跡畫到該時刻為止)。
- 輸入事件清單(tick × header.dt 換算時間;點選跳至)、BRM 標記(教官站紀錄才有;SimCore 紀錄不含)、整段的自動講評摘要、header 資訊(情境、船、動力學模型、雜湊)、略過的壞行數。
- 不需核心連線;航跡圖只有自船(無海圖與目標船),儀器時序曲線、語音影像同步與評分(第 9.2 節)待後續版本。

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
│  ├─ types/                  state.ts、command.ts(對應 Contracts 的 JSON Schema)
│  ├─ lib/                    angles(正規化、角差)、format(度/分、航向、時間)、state-parser(缺欄位防呆)、
│  │                          command(指令建構與序列化)、ship-config(由 particulars.json 取量程/車鐘表)、
│  │                          ship-geometry(船形、船體→ENU)、track-history、theme(主題讀寫)、download(下載)、
│  │                          scenario(情境 YAML 解析與摘要)、instructor-command(教官指令與故障目錄)、
│  │                          operation-log(學員操作推導)、debrief(講評摘要)、brm-markers(BRM 標記與匯出)、
│  │                          session-record(教官站紀錄)、record-replay(JSON Lines / 教官站紀錄解析)
│  ├─ net/                    sim-source.ts(來源介面,含非狀態訊息)、ws-client.ts(WebSocket + 重連 + ack)
│  ├─ demo/                   demo-sim.ts(示範運動學;另記錄故障名稱)、demo-source.ts(25 Hz 廣播)
│  └─ components/             brm-conning-bar 及其各儀器、brm-track-plot、brm-helm、brm-telegraph、
│     │                       brm-thruster-lever、brm-autopilot、brm-instructor
│     └─ instructor/          scenario-panel、run-control-panel、environment-panel、fault-panel、ownship-panel、
│                             operation-log-view、command-log-view、brm-marker-bar、debrief-summary、replay-view
└─ tests/                     vitest:angles、format、state-parser、command、ship-config、demo-sim、
                              ws-client(假 WebSocket)、components(happy-dom 渲染與指令事件)、
                              scenario、instructor-command、operation-log、debrief、brm-markers、record-replay、
                              instructor-components(教官站元件事件與重播檢視)
```

船舶參數(LPP、船寬、舵角上限、舵機速率、MCR 轉速、車鐘各段轉速與速度、側推參數、吃水)直接由 `data/ships/FSB1|FSB2/particulars.json` 在建置時匯入(`src/lib/ship-config.ts`),與核心共用同一份資料。

## 已知限制(第一版)

- 自動舵的開/關與設定航向由本頁面保存(`state.schema.json` 尚無自動舵狀態欄位);核心若另有自動舵狀態,之後需加欄位同步。
- 操舵模式只有 FU/NFU/自動舵;舵機泵選擇、應急操舵、航跡模式與第 7.2 節其他儀器(雷達、ECDIS、BNWAS、BAM、VHF)不在本版範圍。
- 鳥瞰圖只有自船,尚無目標船、海圖與航線;3D 視景另見 `src/Visual.*`。
- 示範模式為簡化運動學,不可用於訓練或驗證。
- UKC 警報門檻先固定 1.0 m(警告 2.0 m),之後改由公司程序參數集提供(規劃書第 9.1 節)。
- 尚未接 USB 操控台(`src/Controls.Hid`);鍵盤與滑鼠操作為第一版替代。
- 教官站:時間倍率與快照清單由本頁保存(狀態訊息沒有這些欄位),核心自動快照不會列出;學員操作紀錄由狀態推導,自動舵的舵令也會被記為舵令;`loadScenario` 在 Python 參考伺服器尚未實作;自船覆寫借用 `reset` 指令(C# 核心忽略 `args`);無目標船、通訊角色扮演、注入時間表與語音腳本;教官站紀錄與 BRM 標記未簽章、不含錄音錄影;講評閾值為常數;重播檢視沒有儀器時序曲線與評分。
