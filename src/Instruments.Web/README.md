# Instruments.Web — 駕駛台儀器與操船台(第一版)

中塑海運 FS Bitumen No.1(FSB1)/ No.2(FSB2)BRM 操船模擬器的網頁儀器(規劃書第 7 章「駕駛台儀器與人機介面」第一版 conning 顯示與操船台)。船上筆電以瀏覽器全螢幕顯示,之後包進 WebView2 殼(`src/Instruments.Shell`)。

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

網址參數:

| 參數 | 說明 | 例 |
|---|---|---|
| `ws` | 模擬核心 WebSocket 位址,預設 `ws://localhost:8765` | `?ws=ws://192.168.10.2:8765` |
| `demo=1` | 啟動即進入示範模式(不連線) | `?demo=1` |
| `ship` | 示範模式的船(`FSB1` / `FSB2`;即時模式由狀態訊息的 `shipId` 決定) | `?demo=1&ship=FSB2` |
| `theme` | `day` / `dusk` / `night`(未指定時沿用上次選擇,預設 dusk) | `?theme=night` |

例:`http://localhost:5173/?demo=1&ship=FSB2&theme=day`。

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

### C# SimCore(之後)

`SimCore.Host` 的 WebSocket 閘道(規劃書第 5.2 節 Instruments 模組)以相同的 state/command JSON 廣播與接收;儀器端不需改動,只要指到該閘道位址(`?ws=`)。船上直連部署時以固定私有 IP(第 5.4 節)。

### 示範模式

沒有核心時可在標題列切到「示範模式」(或 `?demo=1`):內建簡單運動學(`src/demo/demo-sim.ts`)—— 一階 Nomoto 航向響應(K、T 依瞬時速度換算)、速度一階滯後並隨舵角打折、舵機速率限制、轉速一階滯後、側推隨速度衰減、均勻流、相對風、squat/UKC —— 係數只與 FSB1 試俥概略對齊,**不是**第 6 章的 MMG 船模,僅供儀器獨立預覽與操作練習。

## 程式結構

```
src/Instruments.Web/
├─ index.html                 入口(載入 theme.css 與 main.ts)
├─ vite.config.ts             輸出到 build\web\instruments、快取到 .cache\vite、@data 別名指到 data\
├─ tsconfig.json              strict、Lit 裝飾器設定
├─ .npmrc                     pnpm 套件庫指到專案 .cache\pnpm
├─ src/
│  ├─ main.ts, app.ts         <brm-app>:版面、資料來源、主題、指令路由
│  ├─ styles/theme.css        日/黃昏/夜 CSS 變數(穿透 shadow DOM)
│  ├─ types/                  state.ts、command.ts(對應 Contracts 的 JSON Schema)
│  ├─ lib/                    angles(正規化、角差)、format(度/分、航向、時間)、state-parser(缺欄位防呆)、
│  │                          command(指令建構與序列化)、ship-config(由 particulars.json 取量程/車鐘表)、
│  │                          ship-geometry(船形、船體→ENU)、track-history
│  ├─ net/                    sim-source.ts(來源介面)、ws-client.ts(WebSocket + 重連)
│  ├─ demo/                   demo-sim.ts(示範運動學)、demo-source.ts(25 Hz 廣播)
│  └─ components/             brm-conning-bar 及其各儀器、brm-track-plot、brm-helm、brm-telegraph、
│                             brm-thruster-lever、brm-autopilot、brm-instructor
└─ tests/                     vitest:angles、format、state-parser、command、ship-config、demo-sim、
                              ws-client(假 WebSocket)、components(happy-dom 渲染與指令事件)
```

船舶參數(LPP、船寬、舵角上限、舵機速率、MCR 轉速、車鐘各段轉速與速度、側推參數、吃水)直接由 `data/ships/FSB1|FSB2/particulars.json` 在建置時匯入(`src/lib/ship-config.ts`),與核心共用同一份資料。

## 已知限制(第一版)

- 自動舵的開/關與設定航向由本頁面保存(`state.schema.json` 尚無自動舵狀態欄位);核心若另有自動舵狀態,之後需加欄位同步。
- 操舵模式只有 FU/NFU/自動舵;舵機泵選擇、應急操舵、航跡模式與第 7.2 節其他儀器(雷達、ECDIS、BNWAS、BAM、VHF)不在本版範圍。
- 鳥瞰圖只有自船,尚無目標船、海圖與航線;3D 視景另見 `src/Visual.*`。
- 示範模式為簡化運動學,不可用於訓練或驗證。
- UKC 警報門檻先固定 1.0 m(警告 2.0 m),之後改由公司程序參數集提供(規劃書第 9.1 節)。
- 尚未接 USB 操控台(`src/Controls.Hid`);鍵盤與滑鼠操作為第一版替代。
