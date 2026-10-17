# SimCore — 模擬核心(.NET 8)

中塑海運 FS Bitumen No.1 / No.2 駕駛台 BRM 操船模擬器的核心類別庫與主控程序(規劃書第 5 章、第 6 章、第 11 章)。
本資料夾含三個專案與一組測試,全部由根目錄的 `SimosaBRM.sln` 建置,輸出在 `build/`(`Directory.Build.props`):

| 專案 | 內容 |
|---|---|
| `src/SimCore` | 類別庫:契約型別、船舶資料、物理介面、MMG 完整模型與暫代模型、RK4、引擎、環境、情境(含時間表/航線/區域/評估規則)、公司程序參數集、目標船/交通(Traffic)、評估(Assessment)、紀錄/重播 |
| `src/SimCore.Host` | 主控台程式:WebSocket(ws://0.0.0.0:8765;狀態、事件與評估框)、UDP 多播狀態匯流排、NMEA 閘道、紀錄、重播/重新評分、情境靜態檢查 |
| `src/Gateway.Nmea` | NMEA 0183 句型產生與 UDP 輸出(含目標船 AIS `!AIVDM` 與 `$RATTM`) |
| `tests/SimCore.Tests` | xunit:單位、RK4、確定性、快照/還原、紀錄→重播、NMEA/AIS、情境、交通(行為模式、CPA/TCPA/BCR、碰撞、觸發、指令)、暫代模型行為、MMG 黃金測試與 C#↔Python 一致性(`Golden/`)、情境引擎(參數集、時間表、漸變、航線/區域/會遇/故障應變/指標、評分黃金摘要 `Golden/assessment-E07-600s.json`、靜態檢查、雜湊相容) |

```
dotnet build SimosaBRM.sln
dotnet test  SimosaBRM.sln
dotnet run --project src/SimCore.Host -- --scenario data/scenarios/E01_baseline.yaml --record
```

## 1. 架構

```
SimCore(類別庫)
├─ Contracts/     OwnShipState、SimCommand(欄位 = src/Contracts/state|command.schema.json)、Units、ContractJson
├─ Ship/          ShipParticulars(data/ships/<ID>/particulars.json,容忍 null)、RepositoryPaths
├─ Physics/       StateVector、ControlInput/ControlStages、IShipDynamics、Rk4Integrator、ActuatorModel(含主機換向狀態機)、PlaceholderDynamics
│  └─ Mmg/        MmgDynamics(MMG 完整模型)、MmgCoefficients(係數檔)、MmgPropeller
├─ Environment/   EnvironmentConditions(風、均勻流、水深)、EnvironmentMath(相對風、COG/SOG、squat、UKC)
├─ Geo/           LocalTangentPlane(本地 ENU ↔ WGS-84,平面近似)
├─ Engine/        SimulationEngine(50 Hz、指令佇列、快照、雜湊)、RealtimeRunner、Autopilot、DeterministicRandom
├─ Engine/        SimEvent(所有核心事件;SimEventKinds/SimEventSources)
├─ Scenario/      Scenario(YAML,schema 在 src/Contracts/scenario.schema.json)、ScenarioTarget(targets[])、ScenarioTimeline(timeline[]:TimelineItem/TriggerCondition/TimelineAction)、
│                 ScenarioRoute(route/zones)、ScenarioAssessment(assessment 規則)、ScenarioBriefing、ScenarioLoader(YAML → 節點樹 → ${params} 替換 → JSON)、
│                 YamlJson、ScenarioValidator(靜態檢查)、TimelineEngine(執行期)、EnvironmentRamp(漸變)、TimelineSnapshot
├─ Parameters/    ParameterSet / ParameterSetLoader(公司程序參數集,parameter-set.schema.json)、ParameterResolver(${params:…} 解析、重新評分)
├─ Assessment/    RouteTracker(XTD/航段)、ZoneTracker(多邊形、區域速限/UKC/水深)、MetricsCollector(操縱/一般/UKC 指標)、EncounterTracker(會遇)、
│                 FaultResponseTracker(故障應變)、AssessmentEngine(硬性失敗/容許帶/必要事件/評分)、AssessmentSummary(summary 行)
├─ Traffic/       TrafficManager(目標船集合、觸發、行為、COLREG、會遇指標、碰撞、targetControl)、TargetShip(運動模型與狀態)、
│                 Encounter(CPA/TCPA/BCR、船體外形多邊形相交)、TrafficEvent(: SimEvent)、TrafficSnapshot
└─ Recording/     RecordWriter / RecordReader / Replayer(JSON Lines + SHA-256;含 event 與 summary 行;重播比對雜湊、事件序列與 summary)
```

### 1.1 資料流與時序(規劃書第 5.3 節)

- **核心 50 Hz**:`SimulationEngine.Step()` = 套用佇列中的指令 → 陣風亂數(每秒) → 自動舵 → 致動器(MMG:主機換向狀態機 + 舵角/軸轉速/側推的一階 ODE 以 RK4 積分並回傳四個階段的控制量;暫代:顯式更新) → 環境取樣 → `Rk4Integrator.Step(IShipDynamics, …, ControlStages, …)` → tick++ → 擱淺檢查 → **目標船**(`TrafficManager.Step`:觸發 → 行為 → 運動模型 → 自船會遇指標 → 碰撞;與自船同 dt,依自船推進後的狀態) → 狀態鏈雜湊 → 事件。
- **廣播 25 Hz**:每 2 步觸發 `Broadcast` 事件(`OwnShipState`);Host 序列化一次後同時送 WebSocket 與 UDP 多播。
- **快照每 10 s**:每 500 步 `TakeSnapshot()`(含亂數計數與雜湊鏈),保留最近 60 份供倒帶;`snapshot` / `restore` 指令可手動觸發。
- **NMEA**:`NmeaGateway` 掛在 `Stepped` 事件:HDT/ROT/RSA 每 5 步(10 Hz),GGA/RMC/VTG/VBW/DPT/MWV 每 50 步(1 Hz);每目標 `$RATTM` 1 Hz、AIS `!AIVDM` 類型 1 每 10 s、類型 5 於出現後數秒與其後每 6 分(第 6 節)。
- **即時驅動**:`RealtimeRunner` 以 20 ms 週期依時間倍率推進應到的步數(×1 … ×10;凍結時只處理指令);落後時丟棄並計數,不無限追趕。

### 1.2 確定性(CLAUDE.md、規劃書第 14 章)

- 固定步長 `dt = 0.02 s`,雙精度,所有亂數來自 `DeterministicRandom(seed)`(包裝 `Random(seed)`,快照保存抽取次數並於還原時重建)。
- 狀態鏈雜湊:`h_n = SHA-256(h_{n-1} ‖ tick ‖ u,v,r,x,y,ψ ‖ 舵角/舵令/轉速/轉速令/側推 ‖ 陣風係數 ‖ 主機狀態機(模式、計時、轉速目標、時間常數) ‖ 各目標船 x,y,ψ,V,r)`,32 位元組,可序列化進快照;相同輸入序列兩次執行、快照後接續、紀錄重播,三者雜湊必須一致(測試已涵蓋)。目標船欄位接在既有欄位之後,無目標船的情境其雜湊與加入交通模組前完全相同;交通模組不抽引擎亂數(錨泊微漂為確定性迴盪),自船的陣風序列不受目標船影響。
- 快照以 System.Text.Json 的最短往返格式輸出 double,還原後位元一致。

### 1.3 座標與單位(規劃書第 11.3 節)

- 內部 SI 與弧度;船體座標原點船舯、x 向艏、y 向右舷(MMG 慣例);u、v 為對水速度,均勻流在運動學加入。
- 位置為本地 ENU(x 東、y 北,m),原點 = 情境 `origin`(未指定時為初始位置);經緯度以 `LocalTangentPlane` 平面近似換算(港區與沿岸數十公里內誤差 < 數 m;跨海區應改 ECEF 或移動原點)。
- 航向 ψ 自北順時針;r 右轉正;舵角右正;對外介面用度、節、0–360、ROT 度/分(`Units`)。

## 2. 執行 Host

```
dotnet run --project src/SimCore.Host -- [選項]
  --ship FSB1|FSB2             覆寫情境自船
  --loading full|ballast|intermediate
  --scenario <path.yaml>       預設 data/scenarios/E01_baseline.yaml
  --record [<file.jsonl>]      紀錄到 build/records/<yyyyMMdd-HHmmss>-<scenario>.jsonl(或指定路徑;loadScenario 時另開新檔)
  --replay <file.jsonl>        離線重播並比對雜湊、事件序列與 summary(結束碼 0 = 一致)
  --parameters <path.yaml>     公司程序參數集(預設 data/parameters/company-procedures.yaml 或情境 assessment.parameterSet)
  --validate                   只做情境靜態檢查(error/warning)後結束
  --rescore                    與 --replay 併用:以 --parameters 的參數集重新評分(雜湊與事件序列不變,印出新 summary)
  --port 8765                  WebSocket 埠
  --timescale 1                初始時間倍率
  --duration <秒>              模擬時間到達後結束(煙霧測試)
  --dynamics mmg|placeholder   動力學模型(預設 mmg;係數檔不存在時以錯誤結束,可改 placeholder)
  --no-ws | --no-udp | --no-nmea
  --multicast 239.255.70.1:7001          狀態匯流排
  --nmea-port 10110                      NMEA 單播(127.0.0.1,OpenCPN 預設)
  --nmea-multicast 239.192.0.1:60001     NMEA 多播(IEC 61162-450 預設群組;目前無 TAG block)
```

主控台每秒印一行狀態(航向、COG/SOG、ROT、舵、rpm、車鐘、UKC、倍率、每秒步數、WebSocket 連線數)。Ctrl+C 結束並關閉紀錄(寫入 footer)。

## 3. 與網頁儀器(src/Instruments.Web)連線

- **WebSocket** `ws://<host>:8765/`(任何路徑皆可):伺服器每 40 ms 推一則文字框,內容為 `OwnShipState` JSON(`src/Contracts/state.schema.json`);客戶端送 `SimCommand` JSON(`command.schema.json`),例如 `{"type":"rudder","value":-20}`、`{"type":"telegraph","value":"HAH"}`、`{"type":"autopilot","args":{"enabled":true,"heading":275,"rotLimit":15}}`、`{"type":"setEnvironment","args":{"wind":{"trueSpeed":20,"trueDir":40},"current":{"set":265,"drift":2.5},"waterDepth":12}}`、`{"type":"timeScale","value":2}`、`{"type":"freeze"}`、`{"type":"snapshot"}`、`{"type":"restore","args":{"tick":12000}}`、`{"type":"injectFault","value":"steeringGear"}`。與 Python 參考實作契約相同。慢速客戶端只拿最新一幀。
- **UDP 多播** `239.255.70.1:7001`:同一份 `OwnShipState` JSON,每幀一個資料包(日後換 MessagePack;視景/雷達訂閱)。
- **NMEA 0183**:`udp://127.0.0.1:10110`(OpenCPN:Connections → Network → UDP、port 10110)與多播;句型 GGA、RMC、VTG(GP)、HDT(HE)、ROT(TI)、VBW(VD)、DPT(SD,換能器視為在龍骨、偏移 0)、MWV R/T(WI)、RSA(ER)、目標船 TTM(RA)與 AIS VDM(AI);時間 = 情境 `startTimeUtc` + 模擬時間。
- 目標船在狀態的 `targets[]`(第 6.3 節),`flags.collision` 為自船與任一目標外形相交;目標船指令 `targetControl`(第 6.4 節)。
- 指令會改變行為的故障名稱:`steeringGear`(卡舵)、`mainEngine`(轉速歸零、state=failed)、`bowThruster`(側推不可用);其他名稱只記錄與廣播。

## 4. 情境與紀錄

- 情境 YAML 最小內容見 `src/Contracts/scenario.schema.json` 與 `data/scenarios/E01_baseline.yaml`(麥寮外海、FSB1 壓載、HAH 7.8 kn、風 15 kn NE、流 0.5 kn、水深 30 m、種子 20260101;另含 1 艘對遇、1 艘錨泊的目標船)。`initial.rpm`(選填)直接指定初始軸轉速;未指定車鐘與轉速時 MMG 取該航速的直航平衡轉速(與 Python `reset()` 相同),暫代模型取航速最接近的車令。`data/scenarios/E05_pilot_traffic.yaml`:高雄港外 Charlie 引水站附近,3 艘交通(對遇、橫越直航、不守規則的漁船)+ 自船接近引水站 1.5 nm 時出現並跟隨的引水船。目標船格式見第 6.2 節。
- 紀錄檔(JSON Lines,UTF-8/LF;`header.version` 2):`header`(完整情境—參數引用已代入、動力學模型名稱、`parameterSet{id, version, path, sha256, resolved[]}`)、`input`(每個套用的教官/學員指令與其 tick,含 `targetControl`、`triggerTimeline`、`acknowledge`、`endExercise`;時間表自動動作**不**進 input 行)、`state`(25 Hz,含 `targets[]`、`scenario`、`route`、`visibility_nm`)、`event`(`SimEvent`:交通、時間表、評估、教官、引擎事件;`seq` 遞增)、`snapshot`(每 10 s 與手動;`EngineSnapshot.Version` 2 含 `timeline`/`assessment`)、`summary`(評估摘要;`endExercise` 時一行、關閉時再一行 `final: true`,講評站取最後一行)、`footer`(最終 tick、狀態雜湊、footer 之前所有位元組的 SHA-256、`summaryLines`)。
- 重播:`Replayer.Replay(path, shipLoader)` 依 header 情境重建引擎,依序在相同 tick 送入指令並推進到相同 tick:`HashMatches`、`EventSequenceMatches`(逐事件 seq/kind/tick/itemId/targetId)、`SummaryMatches`(JSON 相等)必須為真;動力學模型不同時會警告(雜湊預期不一致)。執行中以指令新增的目標船不在 header 情境內,由 `input` 行重播還原;時間表動作由重播引擎在相同 tick 重新產生。`rescore:` 參數以另一參數集代入 header `parameterSet.resolved` 的欄位得到重新評分情境,引擎仍以 header 情境執行(物理/交通/時間表/雜湊/事件序列不變),另建影子評估模組(`SimulationEngine.EnableRescore`、`RescoreAssessment`)產生新門檻的 summary(第 8.6 節)。重播時指令在引擎 tick 等於紀錄 tick 時立即套用(與主控程序等價),`restore`/`reset` 倒帶後的指令各自套在紀錄的 tick。`loadScenario` 不寫 input 行:紀錄器在情境切換前自動關閉(`ScenarioLoading` 事件;footer 為舊情境的最終 tick/雜湊),主控程序為新情境另開新檔;重播遇到舊紀錄的 `loadScenario` 行時中止並 Warning。

## 5. MMG 完整模型(`Physics/Mmg`,ModelName `mmg/1`)

規劃書第 6.2 節的 3 自由度 MMG 模型(Yasukawa & Yoshimura 2015 式 (1)–(3)),逐式移植自 Python 參考實作
`src/Tools.Calibration/simosa_brm/mmg.py`(`forces` / `_derivs` / `_engine_logic`)與 `propeller.py`;Host 預設使用,`--dynamics placeholder` 可切回暫代模型。

### 5.1 內容

| 模組 | 實作(與 Python 相同) |
|---|---|
| 船體 | 線性/非線性多項式(X、Y 以 ½ρLdU²、N 以 ½ρL²dU² 無因次化;v' = v/U、r' = rL/U,U 下限 `uFloor`),含 x_G 耦合的 2×2 聯立求解;阻力 R0'(U) = ITTC-57 黏性(形狀因子、Mumford 濕面積)+ 興波 c_w·(Fn/Fn_ref)^q,含低速下限,永遠與縱向速度反向(倒退時向前推);大漂角(船舯漂角 20–40°)與艏搖通道(atan(½L|r|/|u|) 30–45°)以 smoothstep 權重混入 10 條帶截面橫流阻力,線性升力項隨 |cos β| 淡出,純橫移無縱向力與艏搖力矩、零速純艏搖仍有阻尼 |
| 螺槳 | 伴流 w_P = w_P0·exp(−C·β_P²)、Wageningen B 系列擬合的二次 K_T/K_Q、J > jMax 截止(風車區);倒車/倒退以負 J 外推乘折減並以 ±jClip 截止、軸停止時鎖定圓盤阻力;倒車橫向力(右旋槳艉向左、艏向右) |
| 舵 | MMG 標準舵:u_R 含滑流加速(κ、η 面積加權;流向由面積加權平均流速決定,倒退中正車 u_R 仍為正)、γ_R±、l_R;Schilling 分段升力(35° 線性、35–70° 高升力斜率);滑流旋轉有效舵角偏移 `swirlAngle`(乘滑流加速因子,上限 5);t_R、a_H、x_H |
| 艏側推 | 推力 × exp(−ln2·(u/u_half)²);側推延遲為一階 ODE(τ 2 s、斜率 1/fullThrustDelay) |
| 風 | Blendermann (1994) 參數式,相對風由對地速度(含流)計算;受風面積由係數檔讀入(Python 的 LOA×乾舷+上層建築估計) |
| 流 | 均勻流以相對水速處理:u、v 為對水速度,流速只進入運動學與相對風 |
| 淺水 | Kijima 型倍率 f = 1 + a·(T/(h−T))^n 作用於線性/非線性導數、阻力、附加質量(h/T ≥ 11 無修正);squat 改用 ICORELS(`IShipDynamics.Squat`),UKC 與擱淺檢查據此計算 |
| 主機 | 換向狀態機(`ActuatorModel.EngineLogic`):stopped / stopping(停俥滑行,τ = shaftStopTimeConstant)/ run(一階滯後 τ = rpmTimeConstant + 速率限制 rpmRateLimit)/ reversing(燃油切斷 → 軸轉速衰減 → 逾 reversalDelay 且 \|n\| ≤ 5 % MCR 才反向點火)/ starting(startDelay);轉速令幅度夾到 [minRpm, maxRpm] |
| 舵機 | dδ/dt = clamp((δ_cmd − δ)/1 s, ±rate),rate = 係數檔 `rate_degps`(70° 行程 / hardOverTime35);舵令上限 = `maxAngle_deg`(Schilling 70°) |

- **純函數**:`MmgDynamics.Derivative` 不含任何跨步狀態;所有狀態(主機模式、計時器、轉速目標/時間常數、舵角、側推)在 `ActuatorModel`,並納入 `EngineSnapshot.Actuators` 與狀態鏈雜湊(快照/還原、重播的確定性測試已涵蓋換向中的快照)。
- **致動器積分**:Python 把舵角、軸轉速、側推放進同一狀態向量以 RK4 積分;C# 保持致動器在引擎內,但以同一 RK4 先行積分並把四個階段的控制量(`ControlStages`)交給船體 RK4(第 2/3 階段用半步、第 4 階段用整步),結果與 Python 位元等級一致(見 5.3)。
- **初始化**:情境未指定車鐘/轉速時取該航速的直航平衡轉速(`SteadyRpmForSpeed`,二分法),車鐘以最接近者標示,與 Python `reset()` 相同;`SteadySpeedForRpm` 供速度–轉速穩態測試。
- 介面(給其他模型):`IShipDynamics` 新增三個可選的預設成員 `ActuatorParameters`(模型建議的致動器參數,null = 依 particulars)、`SteadyRpmForSpeed`、`Squat`;`Rk4Integrator.Step` 多一個 `ControlStages` 多載。

### 5.2 係數檔

- `data/ships/<ID>/coefficients.<loading>.json`(schema `src/Contracts/coefficients.schema.json`),由 `src/Tools.Calibration` 產生,C# 與 Python 讀同一份:`MmgCoefficients.LoadFromDataRoot(root, shipId, loading)`,欄位依 schema 命名,各區段未知鍵保留在 `Extra`(`ToJson()` 可原樣回寫);`hull.resistance.model` 支援 `viscous+wave` 與 `constant`(KVLCC2 基準)。
- `FSB1/coefficients.full.json`:參數識別結果(`source.method = identify`,13/14 識別目標在公差內)。
- `FSB1/coefficients.ballast.json`:**未識別**,以 `uv run simosa-brm estimate --ship FSB1 --loading ballast --out data/ships/FSB1/coefficients.ballast.json` 由經驗公式估計(`source.method = estimate`,notes 註明);壓載狀態只有海報估計值,依規劃書 6.4 公差放寬 1.5 倍並註明來源。
- FSB2 目前只有 `coefficients.full.json`;其他裝載狀態需先產生,否則 Host 以錯誤結束(訊息含產生指令)。

### 5.3 驗證(`tests/SimCore.Tests/MmgGoldenTests.cs`、`MmgEngineTests.cs`)

- **黃金測試**(規劃書 6.4 公差、與 Python 測試相同):FSB1 滿載 35° 左右迴旋(前進距離、橫距、戰術直徑、定常速度)、10/10 Z 形(識別區)、20/20 Z 形、緊急停船(航跡距離、停船時間、艏向右偏)、艏側推 90°、速度–轉速穩態(±0.5 kn)與時間積分交叉檢查。Python 已標 xfail 的四項(右迴旋橫距、慣性停船、倒車→進車、側推 90° 左)以 `Skip` 標記並附 Python 實際值;`GoldenReport` 測試印出完整對照表(`dotnet test --filter GoldenReport --logger "console;verbosity=detailed"`)。
- **C# 對 Python 數值一致性**(規劃書第 14 章「軌跡差異 <1%」):`Golden/*.csv` 由 `cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_reference.py` 產生(35° 右迴旋、10/10 Z 形、緊急停船各 600 s,每 1 s 一列,標頭含初始狀態與舵令/車鐘事件 tick),C# 以相同係數、dt 0.02、相同事件 tick 開迴路重播;目前最大位置差 < 1e-9 m、航向差 < 1e-9°(浮點等級),遠低於 1%。
- 力模型檢查(移植自 Python `test_physics_review.py`):倒退阻力方向、純橫移、零速艏搖阻尼、含 x_G 的運動方程式、倒車橫向力、倒退中正車的舵流入、Blendermann 風(7 個風向)、伴流隨漂角;引擎整合:E01 以 MMG 的確定性、換向中快照/還原、紀錄→重播、狀態機時序(EFAS 後約 110.6 s 反向)、停俥→起動延遲、舵機速率與 70° 上限、故障注入。

## 6. 目標船與交通(`Traffic/`,規劃書第 5.2 節 Traffic、第 9.1 節目標船、第 8.3 節避碰指標)

### 6.1 模型

- **TargetShip**:識別與靜態資料(id、船名、MMSI、船型、LOA/B/吃水、呼號/IMO/目的港、燈號類別)、運動狀態(ENU 位置、航向、對水速度、迴轉率、對地速度)、行為狀態、AIS 設定、評估追蹤(最小預測 CPA、最小實際距離、CPA 警報)。
- **運動模型(簡化,無 MMG)**:航向誤差 → 目標迴轉率(誤差 10° 以上即最大迴轉率)→ 迴轉率一階響應(τ_ψ);航速一階滯後(τ_V);位置含均勻流。參數依船長估計:τ_ψ = L/10 s(2–30)、最大迴轉率 3000/L 度/分(15–120)、τ_V = 2 L s(10–400)、最大航速 20 kn(L < 50 m 為 30 kn);可在 `motion:` 覆寫。
- **行為模式**(`behaviour.mode`):`hold`(保持航向航速)、`waypoints`(航點航線,到達半徑預設 2 倍船長≥100 m,各段航速,`loop`/`stopAtEnd`)、`scripted`(自出現起的時間觸發航向/航速指令表,`rot` 可限制該次轉向的迴轉率)、`anchored`(錨泊:船艏朝風(3 % 風速)與流的合成來向,基準艏向以 120 s 時間常數轉向,疊加 `swing`·sin(2πt/`period`) 迴盪,船舯在錨位後方 `scope`;AIS 航行狀態 1、燈號 anchored)、`follow`(跟隨 `leader`= own 或目標 id 的相對方位/距離:所需對地速度 = leader 對地速度 + k·位置誤差(k = min(gain, 0.3/τ_V),避免振盪),減流後化為航向/航速指令;直線路徑穿過 leader 船體時先繞到艉後同側再進站;供引水船、拖船,小艇建議 `motion.speedTau` 5–10 s)、`colreg`(COLREG 覆蓋層 + 基底 = 航點或保持航向)。
- **COLREG 第一版**(`colreg:` 可疊加於 hold/waypoints/scripted;只對自船判定):每秒以目標為本船計算會遇幾何;對遇(航向差 > 165°、自船在艏向 ±15°)→ 右轉 `turn`;橫越且自船在右舷 → 讓路:右轉 `turn` 並減速 `speedFactor`;自船在左舷 → 直航(保持航向航速;`lastResort` 時 CPA < 門檻/2 且 TCPA < 門檻/3 採第 17 條最後手段右轉);追越:目標追越自船時向遠離側轉 `turn`/2,被追越時直航;距離增加中(TCPA ≤ 0)視為無會遇。觸發條件 CPA < `cpaThreshold` 且 0 < TCPA < `tcpaThreshold`;行動後轉向完成且 `escalateAfter` 秒後 CPA 仍低於門檻再加 `turn`(累計 ≤ 90°);通過 CPA(TCPA ≤ 0,距離增加中)後回到基底行為並冷卻 60 s(直航船在風險消失時亦回到基底行為)。`obey: false` 為「不守規則」:辨識並廣播情境(`colreg: "crossing/giveWay(ignored)"`)但不行動。
- **觸發**(`trigger:`):`time`(模擬時間 ≥ time)、`ownDistance`(自船距 point < lessThan / > greaterThan,nm)、`ownHeading`(greaterThan–lessThan 順時針扇區,或單邊比較);每秒評估,成立後目標出現(開始運動、廣播、AIS)並記錄 `targetActivated` 事件;教官可 `activate`。
- **AIS**:`aisOn` 開關;`aisError` 注入位置偏移(距離 + 真方位)、航向/COG/SOG 誤差、靜態資料錯誤(`staticError` 預設:船名加 " II"、船型 pleasure、尺寸減半;或以 `name`/`shipType`/`mmsi` 指定)、`silent`(開啟但不發送)。狀態廣播的位置/航向為真值,AIS 報告值在 `targets[].ais`(null = 不發送)。
- **會遇指標與碰撞**(`Encounter`):等速直線外推的 CPA/TCPA(TCPA 負 = 已通過)、艏越距離/時間(目標相對軌跡與自船艏向線交點;正 = 艏前、負 = 艉後、無交會 null);自船與每艘目標的五邊形船體外形(艏尖、肩在 0.3 L、方艉)以分離軸定理相交 → `flags.collision`(保持為真)與 `collision` 事件。最小 CPA 門檻 `assessment.minCpa_nm`(預設 0.5):CPA 低於門檻且 TCPA > 0 時記錄 `cpaAlarm`,回復後 `cpaClear`(跟隨自船的引水船/拖船不列入);每目標另追蹤最小預測 CPA 與最小實際距離(`TargetShip.MinCpaNm`/`MinRangeNm`)供評估。
- **確定性**:每步與自船同 dt 更新,純函數與狀態,不用亂數;所有狀態(含目前有效的目標定義,因指令可修改)進快照 `EngineSnapshot.Traffic`,並以 x、y、ψ、V、r 進入狀態鏈雜湊。

### 6.2 情境 `targets:` 格式(`scenario.schema.json` `$defs.target`;Python 參考實作忽略)

```yaml
assessment:
  minCpa_nm: 0.5
targets:
  - id: T1                      # 唯一;指令與狀態以此引用
    name: HAI XING 8            # AIS 船名(未指定用 id)
    mmsi: 416123456             # 未指定自 416000001 起配置
    shipType: cargo             # cargo|tanker|fishing|pilot|tug|passenger|pleasure|sar|hsc|sailing|other
    loa: 120
    beam: 19
    draft: 6.5
    callSign: BXHX8
    destination: KAOHSIUNG
    lights: powerDriven         # 選填;預設依行為/船型(anchored、pilot、fishing)
    aisOn: true
    aisError: { positionOffset: 500, positionOffsetBearing: 90, headingError: 20, cogError: 0, sogError: 0, staticError: false, silent: false }
    initial:
      position: { x: -100, y: 9260 }   # 或 { lat, lon }
      heading: 180
      speed: 9                  # kn
      rot: 0                    # 度/分
    behaviour:
      mode: colreg              # hold|waypoints|scripted|anchored|follow|colreg
      waypoints: [ { x: 0, y: 5000, speed: 8, radius: 200 }, { lat: 23.9, lon: 120.05 } ]
      loop: false
      stopAtEnd: false
      script: [ { t: 30, heading: 90 }, { t: 300, speed: 10, rot: 10 } ]
      anchor: { swing: 15, period: 360, scope: 90 }
      follow: { leader: own, bearing: 120, range: 70, gain: 0.05, maxSpeed: 18 }
      colreg: { enabled: true, obey: true, cpaThreshold: 1.0, tcpaThreshold: 12, turn: 30, speedFactor: 0.7, escalateAfter: 60, lastResort: true }
    trigger:
      type: ownDistance         # none|time|ownDistance|ownHeading
      point: { lat: 22.53, lon: 120.24 }
      lessThan: 1.5             # nm(ownHeading 時為度)
    motion: { headingTau: 12, maxRot: 25, speedTau: 240, maxSpeed: 20 }
```

### 6.3 狀態 `targets[]`(`state.schema.json` `$defs.target`;只含已出現的目標,無目標船時省略)

| 欄位 | 內容 |
|---|---|
| `id`、`name`、`mmsi`、`shipType`、`loa`、`beam`、`draft` | 識別與尺寸(真值) |
| `pos{lat,lon,x,y}`、`heading`、`cog`、`sog`、`stw`、`rot` | 真值;度、kn、度/分 |
| `range_nm`、`bearing_deg`、`relBearing_deg` | 自船至目標距離、真方位、相對方位 |
| `cpa_nm`、`tcpa_min`、`bcr_nm`、`bct_min` | 最近會遇距離/時間(負 = 已通過)、艏越距離(正艏前/負艉後,無交會省略)/時間 |
| `aisOn`、`lights`、`sound`、`behaviour`、`colreg` | AIS 開關、燈號類別、鳴放中的聲號、行為模式(`manual` = 教官覆寫中)、COLREG 判定/行動(例如 `headOn/giveWay/avoiding`、`crossing/standOn/holding`、`crossing/giveWay(ignored)`、`resumed`) |
| `ais{…}` | AIS 報告值(含注入錯誤):mmsi、name、callSign、imo、shipType(AIS 碼)、navStatus、lat/lon、cog/sog/heading/rot、dimToBow/Stern/Port/Starboard、draught、destination;AIS 關或 silent 時省略 |

### 6.4 指令 `targetControl`(`command.schema.json`;`args` 以 `id` 指定目標,`add` 除外)

| args | 作用 |
|---|---|
| `add: {目標定義}` | 新增(格式同 7.2;無觸發條件者立即出現) |
| `remove: true` | 刪除 |
| `heading`、`speed` | 教官手動接管(覆寫行為與 COLREG;`behaviour: "manual"`) |
| `release: true` | 解除接管,回到行為模式 |
| `position: {lat,lon\|x,y}` | 位置覆寫 |
| `waypoints: [...]` | 航點修改(hold/scripted/follow/anchored 切為 waypoints;索引歸零) |
| `behaviour: {mode, …}` | 行為切換(腳本時間自切換起算) |
| `aisOn: bool`、`aisError: {…}` / `null` | AIS 開關、錯誤注入/清除 |
| `colreg: giveWay\|standOn\|auto\|off` | 強制讓路(立即右轉 `turn` 並減速,通過後回 auto)、強制直航(直到改回 auto)、依規則、關閉覆蓋層 |
| `lights: "…"`、`sound: "oneShort\|twoShort\|threeShort\|fiveShort\|prolonged"` | 燈號類別、聲號(狀態中維持 4 s,並記錄事件) |
| `activate: true` | 忽略觸發條件立即出現 |

無效的 id 或格式只忽略(指令已進入紀錄,重播結果一致)。C# 便利建構:`SimCommand.TargetControl(new { id = "T1", heading = 250 })`、`TargetOverride`、`TargetRelease`、`TargetRemove`、`TargetAis`。

### 6.5 NMEA(`Gateway.Nmea/AisEncoder.cs`、`NmeaEncoder.Ttm`)

- `$RATTM`:每目標 1 Hz,編號依 `targets[]` 順序自 01 起;距離(nm)/真方位、SOG/COG(真)、CPA(nm)/TCPA(分,負 = 已通過)、單位 N、船名(≤ 12 字)、狀態 T、UTC、取得方式 A。
- `!AIVDM`(ITU-R M.1371、6 位元 ASCII 裝甲、填充位元只在最後一句、每句 ≤ 60 酬載字元 → ≤ 82 字):類型 1 位置報告每 10 s(固定,不依航速縮短;ROT 以 4.733·√|ROT| 編碼、定位精度 1、無線電狀態 0),類型 5 靜態與航程資料(424 位元,2 句、填充 2)於目標出現後第 2+i 秒先送一次再每 6 分;第 i 艘的秒數錯開 i,通道依序 A/B。AIS 關閉或 `silent` 的目標只有 TTM。測試以自寫解碼器驗證公開範例(MMSI 477553000)與編碼往返。
- 無 VDO(自船 AIS)、無 TAG block。

## 7. 已知限制

- MMG(與 Python 參考實作相同的模型結構限制,偏差記錄於黃金測試與驗證報告):右迴旋橫距偏小約 19 %(初期迴轉反應與定常圓的比例);慣性停船減至 5 kn 的時間偏長(試俥航跡彎曲,直線滑行無法重現);倒車 6.2 kn → 全速進的停船時間偏長約 46 %;艏側推 90° 左偏快約 17 %(右 8.8 % 在公差內)。倒車/風車區為簡化四象限(負 J 外推 + 截止),完整 Wageningen 四象限 C_T*(β) 表未納入;無浪、岸壁效應、船間交互作用、橫搖;淺水倍率為趨勢用(未經驗證);風壓面積為估計值(GH-1040 曲線尚未數位化)。
- MMG 壓載係數(FSB1)與 FSB2 均未識別;C# 自動舵仍為引擎的串級 P 控制(與 Python 的 PID 不同,不影響數值一致性測試)。
- 暫代模型無迴旋速度–迴轉率耦合、單俥倒車艉偏、風力矩、浪、淺水附加質量(僅把 Nomoto 時間常數放大)、岸壁效應;倒車航速以前進曲線的 60 % 估計。
- 暫代模型的 squat 用 Barrass 開闊水域簡式(`Cb·V²/100`),比海報值偏保守(MMG 用 ICORELS);UKC 由常數水深計算(`IDepthProvider` 已可接水深網格 callback,但 callback 無法進快照)。
- `restore` 只能回到記憶體內的快照歷史;從紀錄檔任意快照接續的講評功能尚未接到 Host。
- WebSocket 以 `System.Net.HttpListener` 實作(無 ASP.NET Core 相依),無 TLS 與驗證(船上/直連區網使用)。Windows 上非管理員且未設 URL ACL 時會退回只監聽 localhost;教官筆電直連前請執行一次 `netsh http add urlacl url=http://*:8765/ user=Everyone`(安裝程式應代為設定)。
- NMEA 多播尚未加 IEC 61162-450 TAG block;無自船 AIS(VDO)、THS、XDR、ALF/ALC、HDG(警報以 ALR 輸出,第 9 節)。
- 目標船(第 6 節):運動模型為簡化的一階響應(無 MMG、無風壓/淺水、不能倒退);COLREG 只對自船判定(目標間互不避碰),不含第 19 條限制能見度、不含燈號/聲號的自動邏輯(聲號只能由指令觸發);跟隨模式無拖纜動力;錨泊為確定性迴盪(非走錨模型);碰撞只偵測自船與目標外形相交(不偵測目標之間),無碰撞力學;AIS 報告率固定(不依航速/轉向縮短)且無 SOTDMA 時槽模擬;水深、岸線與固定物尚未納入目標船避讓。
- 主機模型無起動空氣次數上限的實際阻擋(只遞減 `startsRemaining`)、臨界轉速區;換向/起動延遲已在 MMG 模式的狀態機實作,暫代模式仍為一階滯後。
- 時間倍率只影響即時驅動;高倍率下若物理負荷過高會丟步(`RealtimeRunner.DroppedSteps`)。

## 8. 情境引擎(`Scenario/`、`Parameters/`、`Assessment/`;設計文件 `docs/design/scenario-engine.md`)

規劃書第 5.3 節(紀錄)、第 7.2 節(故障目錄)、第 8.1 節(練習目錄、三步升級)、第 8.3 節(自動量測指標與評分)、第 8.4 節(講評)、第 9.1 節(教官站、簡化教官模式、程序參數管理 R13/R14)。

### 8.1 情境 YAML 擴充(全部選填;`src/Contracts/scenario.schema.json`)

- `timeline[]`:`{id, name, phase, visibility: instructor|all, repeat: once|always|{every_s,max}, cooldown_s, holdFor_s, delay_s, manual, armed, when, actions[]}`。`when` 為遞迴條件(`all`/`any`/`not` + 14 種 leaf:`time`(可 `since: {event,…}`)、`ownDistance`(point|waypoint|target)、`ownHeading`、`ownSpeed`、`ukc`、`xtd`、`routeLeg`、`zone`、`phase`、`event`、`fault`、`telegraph`、`autopilot`、`manual`)。`actions[]` 每項為單鍵物件:`injectFault`、`clearFault`、`setEnvironment`(含 `ramp_s` 漸變、`visibility_nm`)、`targetControl`、`voiceScript`、`message`、`phase`、`freeze`、`autopilot`、`ownShip`、`timeScale`、`arm`/`disarm`、`endExercise`;`loadScenario`/`reset`/`restore`/`snapshot` 不允許(載入錯誤)。
- `route`:`{name, xtdLimit_m, startLeg, waypoints: [{name, lat/lon|x/y, xtdLimit_m, speedLimit_kn, arrivalRadius_m, checkpoint, report}]}`;航點限制作用於「到達該航點的那一段」。
- `zones[]`:`{id, type: noGo|speedLimit|ukcPolicy|reporting, polygon[≥3], waterDepth_m, ukcMin{metres|fractionOfDraft}, speedLimit_kn}`;`waterDepth_m` 以 `ZoneDepth` 覆寫區域內水深(多區取最淺,進快照)。
- `assessment`:`parameterSet`、`minCpa_nm`、`encounter{detectionRange_nm, substantialCourseChange_deg, substantialSpeedChange_kn, cpaInterest_nm}`、`ukc{min}`、`manoeuvring{rotLimit_degPerMin, hardOver_deg}`、`hardFail[]`(grounding|collision|cpa|ukc|zoneEntered|event)、`bands[]`(指標名稱見 `Assessment/MetricNames.cs`;`fail: 2x`、`direction: below`)、`required[]`(`after` 事件起算、`expect` 可偵測動作、`within_s`、`penalty`、`latePenaltyPer_s`)、`scoring{start, passMark}`。
- `briefing`:字串或 `{text, attachments[], roles[], minParticipants, duration_s}`;`environment.visibility_nm`。
- 參數引用:任何數值欄位整個字串寫 `"${params:<鍵>}"`(**YAML 流式 `{ }` 內須加引號**,因 `}` 會結束純量);載入時以參數集代入(`ScenarioLoader.Load(path, parameterSet)`;未給參數集時依 `assessment.parameterSet`/預設路徑自動載入),未知鍵、單位後綴不符、引用於 id/name/seed 等位置、來源文件未定義皆為 `InvalidDataException`。解析清單進紀錄 `header.parameterSet.resolved[]`,`summary.assessment.bands/required[]` 附 `paramKey/source/revision/clause`。
- 載入靜態檢查 `ScenarioValidator.Check(scenario, ship)`(主控程序啟動/`loadScenario` 印出;`--validate` 只檢查):error = 結構錯誤(id 重複、參照不存在、動作不允許、航點不足/重合、多邊形點數/自交、指標名稱、required.after)與初始狀態已成立的硬性失敗;warning = 初始即成立的條件、過晚的 time 觸發、`always` 無 cooldown、缺音檔、只記錄的故障。

### 8.2 公司程序參數集(`data/parameters/company-procedures.yaml`;`src/Contracts/parameter-set.schema.json`)

`id`、`version`(`YYYY.M.n`)、`effectiveFrom`、`approvedBy/On`、`notes`、`sources{文件編號: {title, revision, approvedBy, date, external}}`、`parameters{鍵: {value, unit, source, clause, description, min, max}}`、`overrides`(保留)。鍵名 `段.群.名_單位後綴`,後綴(`_nm/_m/_s/_min/_kn/_mps/_deg/_degPerMin/_fraction`)須與 `unit` 一致,無後綴者 unit 須為 `score/count/bool/text`;`source` 須在 `sources`。`ParameterSetLoader.Load` 計算檔案 sha256(紀錄標頭、summary、主控程序啟動列印)。

### 8.3 執行時序(每步;`SimulationEngine.Advance`)

陣風亂數(每秒)→ **環境漸變 `RampSet.Apply`**(線性;角度最短弧;完成發 `environmentRampDone`)→ 自動舵 → 致動器 → RK4 → `Tick++` → 擱淺(`Grounded` + `grounding` 事件)→ 目標船 → **評估步(每秒)**:`RouteTracker`/`ZoneTracker` → `MetricsCollector` → `EncounterTracker`/`FaultResponseTracker` → `TimelineEngine.Evaluate`(依 YAML 順序評估 armed 項目,觸發者立刻執行動作或排入 `delay_s`,再執行到期的待執行動作;單步觸發上限 16)→ `AssessmentEngine.EvaluateRules`(硬性失敗、容許帶、必要事件逾時;更新 `LatestSummary`)→ 雜湊 → `Stepped` → 廣播 → 自動快照。時間觸發精度 1 s;同一評估步中後面的項目看得到前面項目產生的事件,反之要等下一秒。

### 8.4 事件、指令與狀態

- `SimEvent`(`Engine/SimEvent.cs`):`kind/tick/t/source/itemId/targetId/message/range_nm/cpa_nm/tcpa_min/phase/visible/seq/data`;`TrafficEvent : SimEvent`(source `traffic`)。引擎事件 `EventRaised` 發所有事件(紀錄器、主控程序訂閱),`TrafficEventRaised` 仍只發交通事件。新 kind 見 `SimEventKinds`(timelineTriggered、faultInjected/faultCleared、environmentChange/environmentRampDone、voiceScript、message、phaseChanged、frozen/resumed、autopilotOverride/ownShipOverride/timeScaleChanged、exerciseEnded、grounding、routeLegChanged/routeCompleted/checkpointPassed/reportPointPassed、xtdExceeded/xtdRestored、zoneEntered/zoneExited、ukcAlarm/ukcClear、speedLimitExceeded/speedLimitRestored、encounterStarted/encounterFirstAction/encounterClosed、requiredEventMet/requiredEventMissed、hardFail、bandWarn/bandFail、acknowledge、timelineManual)。
- 教官指令 `injectFault`/`clearFault`/`setEnvironment`/`freeze`/`resume`/`timeScale` 照舊寫 `input` 行並**也**發對應事件(source `instructor`);時間表動作經 `ApplyInternal` 走同一邏輯但不寫 input 行(source `timeline`)。新指令:`triggerTimeline{id, force}`(manual 項目或 force;once 已觸發需 force;事件 `timelineManual{ok}`)、`acknowledge{kind, itemId, role, note}`、`endExercise{reason}`(凍結、定稿 summary、`exerciseEnded`;之後 resume 無效)、`getAssessment`(主控程序以 `LatestSummary` 直接回覆該客戶端 `{"type":"assessment","final","summary"}`,不進佇列)。WebSocket 另推送 `{"type":"event","event":…}`(每個事件)與 `exerciseEnded` 時的 assessment 框(事件走獨立通道,不被狀態幀丟棄)。
- 狀態 `state.scenario{id, phase, parameterSetVersion, timelineFired, timelinePending, hardFailed, score, ended}`、`state.route{legIndex, nextWaypoint, xtd_m(右正), xtdLimit_m, distanceToWaypoint_nm, bearingToWaypoint_deg, legCourse_deg, speedLimit_kn, complete}`、`state.visibility_nm`。

### 8.5 自動量測指標與評分(`summary`;規劃書第 8.3 節第 1–5 類)

- 航跡(`RouteTracker`):航段 i 由 W[i] 到 W[i+1],`xtd = (P−A)·(u.y, −u.x)`(右正);航段切換於正橫(`s ≥ L`)或到達半徑(預設 `max(2·LOA, 150 m)`;最後一段只用半徑);不回退;走廊超限事件於回到 ≤ 0.8·limit 才恢復;`track.{meanAbsXtd_m, maxXtd_m, maxXtdAt_s, maxXtdLeg, timeOutsideCorridor_s, corridorExcursions, fractionOutsideCorridor, minDistanceToCorridorEdge_m, minDistanceToNoGo_m, meanHeadingDeviation_deg, waypoints[]}`。
- UKC(`ZoneTracker` + `MetricsCollector`):門檻 = 區內 `ukcPolicy.ukcMin`(優先)或 `assessment.ukc.min`;`ukc.{min_m, minAt_s, minSquat_m, timeBelowLimit_s, excursions(離開滯後 +0.2 m), groundings, limit_m, limitSource(參數鍵)}`。
- 避碰(`EncounterTracker`):會遇開始 = 目標 active、`range ≤ detectionRange_nm`、TCPA > 0、CPA < `cpaInterest_nm`(預設 2×minCpa);首次行動 = 舵令 ≥ 5°、車令/轉速令、自動舵改向 ≥ 5°;航向/速度改變幅度與 `substantial`;結束 = TCPA < 0 且距離連續 30 s 增加,或目標移除;關閉後同一目標再次成立開始條件即新開一筆(同 targetId),未成立時仍把更小的實際距離記到最後一筆的 `minRange_nm`;跟隨自船的 escort 不列入。**`collisionAvoidance.minCpa_nm` 與硬性失敗 `cpa` 使用非 escort 目標的最小實際通過距離(minRange)**,每目標另列預測最小 CPA(`targets[].minCpa_nm`)。
- 操縱(`MetricsCollector`):舵角分箱 `0-5/5-10/10-20/20-35/>35`(秒)、滿舵次數/秒數(門檻 = `hardOver_deg` 或目前模型的舵角上限 − 0.5°)、舵令次數(|Δ| ≥ 1°、自動舵不計;1 s 視窗自計入的舵令起算、不隨後續舵令延長)、ROT 最大值與超限秒數、車令段落、轉速/側推令、速限超限秒數(航段與區域取較嚴者)、自動舵時間/切換;`general.{duration_s, freezes, distance_nm, avgSog_kn, maxSog_kn}`。
- 故障應變(`FaultResponseTracker`):每個 `faultInjected`(時間表或教官)一筆 `{fault, itemId, injectedAt_s, source, firstAction{kind, t_s, dt_s}, acknowledgements[], cleared_s, withinFirstAction, withinNotifyEcr}`;相關動作取 `required[]` 中 `after` 指向該故障的 `expect`,無規則時任一舵/車/側推/自動舵指令;`acknowledge` 帶 `itemId` 明確對應,否則對應最近 120 s 內注入者。
- 評分(`AssessmentEngine`):硬性失敗成立即發 `hardFail`(一次),`passed = false`;容許帶每條一次(進入 fail 帶取 `failPenalty`,否則 warn 帶取 `penalty`);必要事件 `after` 後計時,`expect` 達成 → `requiredEventMet`(逾時後達成記 `late`,扣 `min(penalty, latePenaltyPer_s × 逾時秒)`),逾 `within_s` 未達成 → `requiredEventMissed` + `penalty`,`after` 未發生 → `notApplicable`;`score = max(0, start − Σ)`,`passed = !hardFailed && score ≥ passMark`;`advisory: true`(自動評分僅輔助,A-I/12)。

### 8.6 確定性、快照與雜湊

- `EngineSnapshot.Version` 2:`Timeline{phase, seq, assessmentEvents, items[{id, armed, fireCount, lastFiredTick, satisfiedSinceTick}], pendingActions[], ramps[], eventCounts, lastEventTick, phaseHistory, ended}`、`Assessment{route, zones, metrics, encounters, faults, rules}`(各追蹤器狀態經 JSON 深拷貝,double 位元一致)、`parameterSetVersion`、`phase`、`visibilityNm`。Version 1 快照(無這些欄位)可還原。
- 雜湊鏈:**只在情境有時間表/航線/區域/評估規則時**於目標船位元組之後追加 `int64 非評估事件數(seq − assessmentEvents)`、`int64 階段索引`、各項目 `fireCount/lastFiredTick/satisfiedSinceTick`、各進行中漸變 `channel/endTick/to`、`int64 航段`;無時間表的情境(E05)雜湊與舊版位元一致(`HashCompatibilityTests` 以獨立實作的舊版雜湊逐步對照)。評估事件與累計值不進雜湊。「以新參數集重新評分」(`--replay --rescore --parameters`)以影子評估模組實作:物理、交通、時間表與雜湊仍依紀錄原情境(雜湊與事件序列不變,結束碼 0),只有 summary 依新門檻改變。
- 時間表與評估不使用引擎亂數;凍結時不評估;`delay_s` 以模擬時間計;`reset`/`loadScenario` 重建全部狀態。

### 8.7 示範情境與驗證

- `E01_baseline.yaml`:加 `briefing`、`visibility_nm`、時間表(5 分鐘機控室語音、10 分鐘風向轉北 3 分鐘漸變、T1 CPA 警報提示 ×3)、評估(碰撞/擱淺/CPA 硬性失敗、min CPA 與 ROT 容許帶、參數引用)。
- `E07_steering_failure.yaml`:麥寮進港航道舵機故障(航線 4 航點、UKC 政策區含水深覆寫、錨地禁區、內港速限區、2 艘目標、7 項時間表含手動的應急操舵、4 條硬性失敗/5 條容許帶/4 條必要事件;座標為示意,待 ENC 校正)。`dotnet run --project src/SimCore.Host -c Release -- --scenario E07_steering_failure --record build/records/e07-test.jsonl --duration 300 --timescale 10 --no-ws --no-udp --no-nmea`,再 `--replay build/records/e07-test.jsonl`:雜湊、事件序列與 summary 一致。
- 黃金摘要 `tests/SimCore.Tests/Golden/assessment-E07-600s.json`(暫代模型、`TimelineRecordReplayTests.Script` 600 s;含參數集 sha256,參數集檔案或腳本變更時以 `SIMOSA_UPDATE_GOLDEN=1 dotnet test --filter GoldenE07SummaryMatches` 重新產生並在提交說明)。
- 操舵模式(規劃書第 7.2 節;設計文件 D32):指令 `steeringMode`(`value: fu|nfu|autopilot|emergency`,`args {pump: 1|2|both, direction: port|starboard|stop, heading, rotLimit}`)、狀態 `state.steering {mode, pump, nfuActive?}`、事件 `steeringModeChanged {from, to, pump}`;nfu 以推桿方向驅動舵(舵機速率)、emergency 在 `steeringGear` 故障下以降低速率(particulars `emergencyHardOverTime35_s`,無資料時隨動速率一半)再度可動、autopilot 與 `autopilot` 指令同步、`rudder` 指令把 autopilot/nfu 切回 fu;模式變更以 `steeringModeChange` 動作餵必要事件與故障應變;非預設模式進雜湊、進快照。必要事件開始計時發 `requiredEventPending {after, afterItemId, afterSeq, start_t, within_s, deadline_t, penalty}`(D33);`header.parameterSet.path` 為相對專案根目錄(D34)。
- 尚未實作(設計文件第 0.2、10 節):時間表 `steeringMode` 動作、`blackout` 組合故障、參數集 `overrides`、Python 參考實作的 `${params:…}` 替換(`src/Tools.Calibration` 本次未改;Python 目前忽略 assessment/route/zones/timeline,E01/E07 仍可載入)。

## 9. 感測器故障層、警報管理(BAM)與 BNWAS(`Sensors/`、`Alerts/`;設計文件 `docs/design/sensors-alerts.md`)

規劃書第 7.2 節(電羅經/自動舵/GNSS/測深儀/計程儀/操舵故障、BNWAS、中央警報面板)、第 8.1 節 E08、第 8.3 節第 5、7 類;MSC.302(87)、MSC.128(75)。

- **指示值 vs 真值**:`state` 頂層 `heading/pos/cog/sog/stw/depthBelowKeel` 為感測器指示值,`state.truth{heading, pos, cog, sog, stw, depthBelowKeel, headingError_deg, positionError_m}` 為真值,
  `state.sensors{headingValid, positionValid, depthValid, stwValid, magneticHeading}`;無效時頂層為最後有效值。評估、航線 XTD、時間表條件一律用真值。
- **故障**(`FaultNames.Modelled`;args 經 `injectFault {name, args}`):`gyroDrift{rate_degPerMin=1}`(無警報)、`gyroFreeze`、`gyroFailure`(alarm,自動舵脫開)、
  `gpsJump{distance_m=500, bearing_deg=90}`、`gpsOffset{distance_m=200, bearing_deg=45, ramp_s=60}`、`gpsFailure`(alarm)、`echoSounderFailure`(warning)、
  `logFailure{bias_kn?, factor?}`(無 args 時 STW 無效 + warning)、`autopilotFailure`(alarm)、`steeringFuOnly`(fu/自動舵下舵不動,alarm)、`steeringPumpFailure`(舵速減半,alarm)。
- **自動舵**以電羅經指示航向操舵(gyroDrift 時跟隨錯誤航向);故障脫開發 `autopilotTripped`,故障期間啟用被拒(`commandRejected reason autopilotUnavailable`)。`state.autopilot{enabled, setHeading, rotLimit_degPerMin, available}`。
- **警報**(`AlertManager`):14 項目錄(`AlertCatalogue`),狀態 activeUnacknowledged → activeSilenced(30 s 重響)→ activeAcknowledged → normal / rectifiedUnacknowledged;
  warning 未確認 `alerts.escalate_s`(300 s)升級為 alarm;門檻類(`alerts.offHeading_deg`、`headingMonitor_deg`、`shallowWater_m`)未設即關閉。指令 `alertAcknowledge`/`alertSilence`(`id` 省略 = 全部);
  `state.alerts[]`;事件 `alertRaised/alertSilenced/alertReannunciated/alertAcknowledged/alertRectified/alertEscalated`(source engine、itemId = 警報 id)。
- **BNWAS**(`Bnwas`):情境 `bnwas{mode: off|auto|on, dormant_s 180–720, stage2_s 15, stage3_s 90}`(預設 off);指令 `bnwasMode`、`bnwasReset`;舵令/車令/轉速/側推/自動舵/操舵模式/警報確認或靜音視為駕駛台操作而重設;
  `state.bnwas{mode, active, stage, remaining_s, dormant_s}`;事件 `bnwasStage/bnwasReset/bnwasModeChanged`;第 1 階段 caution、第 2 階段起 alarm(警報 `bnwas`)。
- **評估**:`summary.alerts{raised, acknowledged, unacknowledged, escalations, silences, meanAckTime_s, maxAckTime_s, records[], bnwas{stage1, stage2, stage3, resets}}`;`faultResponses[].alertAckAt_s/alertAckDt_s`;
  新指標名 `alerts.*`、`bnwas.stage2Count/stage3Count`。
- **NMEA**:指示值;HDT/DPT 在無效時停送,GGA 品質 0、RMC V/N、VTG N、VBW V;警報 `$IIALR` 1 Hz。
- **確定性**:無亂數、漂移封閉式;`EngineSnapshot.Version` 3 新增 `sensors/alerts/bnwas`;非預設時才追加雜湊位元組(舊情境雜湊不變)。
- **E08**(`data/scenarios/E08_gyro_failure.yaml`):`dotnet run --project src/SimCore.Host -c Release -- --scenario E08_gyro_failure --record build/records/e08.jsonl --duration 600 --timescale 10 --no-ws --no-udp --no-nmea`
  (31 事件,固定於 `GyroFailureScenarioTests`),`--replay build/records/e08.jsonl` 雜湊/事件/summary 一致。


## 10. 拖船、錨與錨鏈、碰墊與繫纜(`Physics/External/`;設計文件 `docs/design/physics-tugs-anchors-mooring.md`)

規劃書第 6.2 節 T、A 項與第 459–460 行、第 3 章錨/鏈規格、R2;E12(錨泊/走錨)、E13(拖船協助靠泊、拖纜斷裂)。

- **外力介面**:`IShipDynamics.SupportsExternalForces` + `Derivative(..., BodyForce external)`;MMG 把外力加到 X/Y/N,暫代模型以概略質量接受,其他模型明確不支援(指令被拒 `externalForcesUnsupported`)。
  `Rk4Integrator.Step(..., IExternalForceField)` 每個 RK4 階段以該階段狀態重算錨鏈/碰墊/纜繩力。
- **拖船**:情境 `tugs[]{id, name, bollardPull_t, attach fwd|aft|…|{x,y}, lineLength_m, modes, riseTime_s, turnRate_degPerSec, push/pull 效能速度, modeChange_s, connect_s, initial}`;
  指令 `tugControl {id, mode push|pull|standby|free, direction_deg(力方向相對船艏), power_pct}`;出力一階上升、方向限速迴轉、自船速度高於門檻效能遞減(推頂 5 kn 約 50 %);
  故障 `tugLineBreak {id}`(id 未定義 → `commandRejected unknownTug`,不加入 faults[]);`state.tugs[]`;事件 `tugCommand`、`tugLineParted`。
  push ↔ pull 換位依就位工作側(`LastWorkingMode`,快照/雜湊)判定,插入 standby 不能略過 modeChange_s;parted 的拖船只調出力/方向時保持 parted;情境 initial.mode 須在 modes 內。
- **錨**:particulars `anchors`(FSB1 實測 + 估計欄位;FSB2 錨重、起錨速率為估計)、情境 `environment.seabed mud|sand|clay|rock`(預設 sand)、`anchors{port|starboard{length_m|length_shackles, bearing_deg}, holdingFactor, dragSpeed_mps}`;
  指令 `anchor {side, action letGo|veer|heave|brake|release, targetLength_shackles | length_m}`;準靜態懸鏈線(躺底/全懸空/拉直)、抓地力 = 錨重 × 抓著係數 × 上拔折減 + 躺底鏈摩擦、走錨、剎車滑出、絞盤速率與停轉(停轉握持上限 = 剎車握持力,超過時同樣滑出)、鏈端;letGo 只在收存或懸吊且未送出時接受(鏈長只增不減);
  `state.anchors[]{side, status stowed|running|holding|dragging|heaving|aweigh, length_m, tension_t, onBottom_m, holding_t, position…}`;
  事件 `anchorLetGo/anchorCommand/anchorBroughtUp/anchorDragging/anchorHolding/anchorAweigh/anchorHoused/anchorChainFullyOut/anchorBrakeSlipping/anchorWindlassStalled`。
- **泊位**:`data/areas/*.json`(碼頭線、碰墊型式與非線性曲線、碰墊/繫船柱里程;`mailiao-berth.json` 為示意值)、情境 `berth{file, id | definition, lines[]}`(載入時內嵌 definition,紀錄 header 自足);
  碰墊只推不拉(曲線 + 阻尼 + 摩擦)、碼頭硬接觸懲罰彈簧、繫纜非線性彈性 + 預張力 + MBL 斷裂;指令 `mooring {lineId, action makeFast|heave|slack|letGo, bollard, tension_t, length_m}`;
  `state.berth{id, contacts[], quayContact, lines[]{id, status, tension_t, …}}`;事件 `fenderContact{fenderId, normalSpeed_mps, energy_kNm, maxReaction_kN, overload, …}`、`quayContact`、`mooringCommand`、`mooringLineParted`。
- **確定性**:無亂數;外力模組只在使用時(有拖船/泊位,或錨曾被使用)走含外力積分並追加雜湊位元組,舊情境雜湊不變;`EngineSnapshot.External`(選填,Version 仍 3);
  三個新指令寫一般 `input` 行,重播一致(`ExternalForceEngineTests`)。
- **限制**:Python 參考實作尚未實作這些力項;3 自由度(垂直分量、鏈彈性不計);拖船無自身運動學與俥流;評估指標(接近速度、接觸能量)尚未接到評估模組(事件已記錄)。
