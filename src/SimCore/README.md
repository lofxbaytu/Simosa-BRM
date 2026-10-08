# SimCore — 模擬核心(.NET 8)

中塑海運 FS Bitumen No.1 / No.2 駕駛台 BRM 操船模擬器的核心類別庫與主控程序(規劃書第 5 章、第 6 章、第 11 章)。
本資料夾含三個專案與一組測試,全部由根目錄的 `SimosaBRM.sln` 建置,輸出在 `build/`(`Directory.Build.props`):

| 專案 | 內容 |
|---|---|
| `src/SimCore` | 類別庫:契約型別、船舶資料、物理介面、MMG 完整模型與暫代模型、RK4、引擎、環境、情境、目標船/交通(Traffic)、紀錄/重播 |
| `src/SimCore.Host` | 主控台程式:WebSocket(ws://0.0.0.0:8765)、UDP 多播狀態匯流排、NMEA 閘道、紀錄、重播 |
| `src/Gateway.Nmea` | NMEA 0183 句型產生與 UDP 輸出(含目標船 AIS `!AIVDM` 與 `$RATTM`) |
| `tests/SimCore.Tests` | xunit:單位、RK4、確定性、快照/還原、紀錄→重播、NMEA/AIS、情境、交通(行為模式、CPA/TCPA/BCR、碰撞、觸發、指令)、暫代模型行為、MMG 黃金測試與 C#↔Python 一致性(`Golden/`) |

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
├─ Scenario/      Scenario(YAML,schema 在 src/Contracts/scenario.schema.json)、ScenarioTarget(targets[] 定義)、ScenarioLoader
├─ Traffic/       TrafficManager(目標船集合、觸發、行為、COLREG、會遇指標、碰撞、targetControl)、TargetShip(運動模型與狀態)、
│                 Encounter(CPA/TCPA/BCR、船體外形多邊形相交)、TrafficEvent、TrafficSnapshot
└─ Recording/     RecordWriter / RecordReader / Replayer(JSON Lines + SHA-256;含 event 行)
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
  --record                     紀錄到 build/records/<yyyyMMdd-HHmmss>-<scenario>.jsonl
  --replay <file.jsonl>        離線重播並比對雜湊(結束碼 0 = 一致)
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
- 紀錄檔(JSON Lines,UTF-8/LF):`header`(含完整情境與動力學模型名稱)、`input`(每個套用的指令與其 tick,含 `targetControl`)、`state`(25 Hz,含 `targets[]`)、`event`(交通事件:碰撞、CPA 警報、目標出現、航點、COLREG、聲號、目標控制)、`snapshot`(每 10 s 與手動,含目標船)、`footer`(最終 tick、狀態雜湊、footer 之前所有位元組的 SHA-256)。
- 重播:`Replayer.Replay(path, shipLoader)` 依 header 情境重建引擎,依序在相同 tick 送入指令並推進到相同 tick,`HashMatches` 必須為真;動力學模型不同時會警告(雜湊預期不一致)。執行中以指令新增的目標船不在 header 情境內,由 `input` 行重播還原。

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
- NMEA 多播尚未加 IEC 61162-450 TAG block;無自船 AIS(VDO)、THS、XDR、ALR。
- 目標船(第 6 節):運動模型為簡化的一階響應(無 MMG、無風壓/淺水、不能倒退);COLREG 只對自船判定(目標間互不避碰),不含第 19 條限制能見度、不含燈號/聲號的自動邏輯(聲號只能由指令觸發);跟隨模式無拖纜動力;錨泊為確定性迴盪(非走錨模型);碰撞只偵測自船與目標外形相交(不偵測目標之間),無碰撞力學;AIS 報告率固定(不依航速/轉向縮短)且無 SOTDMA 時槽模擬;水深、岸線與固定物尚未納入目標船避讓。
- 主機模型無起動空氣次數上限的實際阻擋(只遞減 `startsRemaining`)、臨界轉速區;換向/起動延遲已在 MMG 模式的狀態機實作,暫代模式仍為一階滯後。
- 時間倍率只影響即時驅動;高倍率下若物理負荷過高會丟步(`RealtimeRunner.DroppedSteps`)。
