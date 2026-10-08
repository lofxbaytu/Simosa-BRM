# SimCore — 模擬核心(.NET 8)

中塑海運 FS Bitumen No.1 / No.2 駕駛台 BRM 操船模擬器的核心類別庫與主控程序(規劃書第 5 章、第 6 章、第 11 章)。
本資料夾含三個專案與一組測試,全部由根目錄的 `SimosaBRM.sln` 建置,輸出在 `build/`(`Directory.Build.props`):

| 專案 | 內容 |
|---|---|
| `src/SimCore` | 類別庫:契約型別、船舶資料、物理介面與暫代模型、RK4、引擎、環境、情境、紀錄/重播 |
| `src/SimCore.Host` | 主控台程式:WebSocket(ws://0.0.0.0:8765)、UDP 多播狀態匯流排、NMEA 閘道、紀錄、重播 |
| `src/Gateway.Nmea` | NMEA 0183 句型產生與 UDP 輸出 |
| `tests/SimCore.Tests` | xunit:單位、RK4、確定性、快照/還原、紀錄→重播、NMEA、情境、暫代模型行為 |

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
├─ Physics/       StateVector、ControlInput、IShipDynamics、Rk4Integrator、ActuatorModel、PlaceholderDynamics
├─ Environment/   EnvironmentConditions(風、均勻流、水深)、EnvironmentMath(相對風、COG/SOG、squat、UKC)
├─ Geo/           LocalTangentPlane(本地 ENU ↔ WGS-84,平面近似)
├─ Engine/        SimulationEngine(50 Hz、指令佇列、快照、雜湊)、RealtimeRunner、Autopilot、DeterministicRandom
├─ Scenario/      Scenario(YAML,schema 在 src/Contracts/scenario.schema.json)、ScenarioLoader
└─ Recording/     RecordWriter / RecordReader / Replayer(JSON Lines + SHA-256)
```

### 1.1 資料流與時序(規劃書第 5.3 節)

- **核心 50 Hz**:`SimulationEngine.Step()` = 套用佇列中的指令 → 陣風亂數(每秒) → 自動舵 → 致動器(舵機速率限制、主機一階滯後、側推斜率) → 環境取樣 → `Rk4Integrator.Step(IShipDynamics, …)` → tick++ → 擱淺檢查 → 狀態鏈雜湊 → 事件。
- **廣播 25 Hz**:每 2 步觸發 `Broadcast` 事件(`OwnShipState`);Host 序列化一次後同時送 WebSocket 與 UDP 多播。
- **快照每 10 s**:每 500 步 `TakeSnapshot()`(含亂數計數與雜湊鏈),保留最近 60 份供倒帶;`snapshot` / `restore` 指令可手動觸發。
- **NMEA**:`NmeaGateway` 掛在 `Stepped` 事件:HDT/ROT/RSA 每 5 步(10 Hz),GGA/RMC/VTG/VBW/DPT/MWV 每 50 步(1 Hz)。
- **即時驅動**:`RealtimeRunner` 以 20 ms 週期依時間倍率推進應到的步數(×1 … ×10;凍結時只處理指令);落後時丟棄並計數,不無限追趕。

### 1.2 確定性(CLAUDE.md、規劃書第 14 章)

- 固定步長 `dt = 0.02 s`,雙精度,所有亂數來自 `DeterministicRandom(seed)`(包裝 `Random(seed)`,快照保存抽取次數並於還原時重建)。
- 狀態鏈雜湊:`h_n = SHA-256(h_{n-1} ‖ tick ‖ u,v,r,x,y,ψ ‖ 舵角/舵令/轉速/轉速令/側推 ‖ 陣風係數)`,32 位元組,可序列化進快照;相同輸入序列兩次執行、快照後接續、紀錄重播,三者雜湊必須一致(測試已涵蓋)。
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
  --no-ws | --no-udp | --no-nmea
  --multicast 239.255.70.1:7001          狀態匯流排
  --nmea-port 10110                      NMEA 單播(127.0.0.1,OpenCPN 預設)
  --nmea-multicast 239.192.0.1:60001     NMEA 多播(IEC 61162-450 預設群組;目前無 TAG block)
```

主控台每秒印一行狀態(航向、COG/SOG、ROT、舵、rpm、車鐘、UKC、倍率、每秒步數、WebSocket 連線數)。Ctrl+C 結束並關閉紀錄(寫入 footer)。

## 3. 與網頁儀器(src/Instruments.Web)連線

- **WebSocket** `ws://<host>:8765/`(任何路徑皆可):伺服器每 40 ms 推一則文字框,內容為 `OwnShipState` JSON(`src/Contracts/state.schema.json`);客戶端送 `SimCommand` JSON(`command.schema.json`),例如 `{"type":"rudder","value":-20}`、`{"type":"telegraph","value":"HAH"}`、`{"type":"autopilot","args":{"enabled":true,"heading":275,"rotLimit":15}}`、`{"type":"setEnvironment","args":{"wind":{"trueSpeed":20,"trueDir":40},"current":{"set":265,"drift":2.5},"waterDepth":12}}`、`{"type":"timeScale","value":2}`、`{"type":"freeze"}`、`{"type":"snapshot"}`、`{"type":"restore","args":{"tick":12000}}`、`{"type":"injectFault","value":"steeringGear"}`。與 Python 參考實作契約相同。慢速客戶端只拿最新一幀。
- **UDP 多播** `239.255.70.1:7001`:同一份 `OwnShipState` JSON,每幀一個資料包(日後換 MessagePack;視景/雷達訂閱)。
- **NMEA 0183**:`udp://127.0.0.1:10110`(OpenCPN:Connections → Network → UDP、port 10110)與多播;句型 GGA、RMC、VTG(GP)、HDT(HE)、ROT(TI)、VBW(VD)、DPT(SD,換能器視為在龍骨、偏移 0)、MWV R/T(WI)、RSA(ER);時間 = 情境 `startTimeUtc` + 模擬時間。
- 指令會改變行為的故障名稱:`steeringGear`(卡舵)、`mainEngine`(轉速歸零、state=failed)、`bowThruster`(側推不可用);其他名稱只記錄與廣播。

## 4. 情境與紀錄

- 情境 YAML 最小內容見 `src/Contracts/scenario.schema.json` 與 `data/scenarios/E01_baseline.yaml`(麥寮外海、FSB1 壓載、HAH 7.8 kn、風 15 kn NE、流 0.5 kn、水深 30 m、種子 20260101)。
- 紀錄檔(JSON Lines,UTF-8/LF):`header`(含完整情境與動力學模型名稱)、`input`(每個套用的指令與其 tick)、`state`(25 Hz)、`snapshot`(每 10 s 與手動)、`footer`(最終 tick、狀態雜湊、footer 之前所有位元組的 SHA-256)。
- 重播:`Replayer.Replay(path, shipLoader)` 依 header 情境重建引擎,依序在相同 tick 送入指令並推進到相同 tick,`HashMatches` 必須為真;動力學模型不同時會警告(雜湊預期不一致)。

## 5. MMG 完整模型移植介面(給負責移植的工程師)

暫代模型 `Physics/PlaceholderDynamics`(Nomoto 一階 + 速度一階滯後 + 側推 + 風壓漂移)只為了讓管線運作;MMG(規劃書第 6.2 節,Yasukawa & Yoshimura 2015)請以同一介面替換:

```csharp
public interface IShipDynamics
{
    string ModelName { get; }   // 寫入紀錄檔 header,例如 "mmg/1"
    StateVector Derivative(double t, in StateVector state, in EnvironmentSample env, in ControlInput control);
}
```

- `StateVector(U, V, R, X, Y, Psi)`:u、v(m/s,對水,船舯)、r(rad/s)、x 東、y 北(m)、ψ(rad,自北順時針)。回傳同型別的導數 (u̇, v̇, ṙ, ẋ, ẏ, ψ̇)。
- `ControlInput(RudderRad, Rpm, Thruster)`:實際舵角(rad,右正)、實際轉速(rpm,倒車負)、側推實際推力比例(−1…+1,正 = 推艏向右)。致動器動態(舵機速率、主機滯後、側推斜率)已在 `ActuatorModel` 處理,模型不必再做。
- `EnvironmentSample`:真風向量(ENU,m/s,含陣風)、均勻流向量(ENU,m/s)、該位置水深(m)。相對水速、視風在船體座標的分量可用 `EnvironmentMath.ApparentWindBody` / `GroundVelocity`。
- 運動學 (ẋ, ẏ, ψ̇) 直接呼叫 `IShipDynamics.Kinematics(state, env)`(已含均勻流)。
- **模型必須是純函數**:所有跨步狀態(風車狀態、四象限表的區段等)都不可存在模型物件內,否則快照/還原與重播的確定性會失效;需要狀態者請放進引擎並納入 `EngineSnapshot` 與雜湊。RK4 一步會呼叫 `Derivative` 四次,環境與控制在整步內視為常數。
- 係數:由 `data/ships/<ID>/coefficients.<loading>.json`(`src/Tools.Calibration` 產生,schema `src/Contracts/coefficients.schema.json`)讀入;建議在建構子讀檔並以 `Func<ShipParticulars, LoadingCondition, IShipDynamics>` 注入 `SimulationEngine`(建構子第 3 個參數),`Host` 只需改一行工廠。
- 驗證:(1) 與 Python 參考實作用相同輸入逐步比對導數(數值一致性,規劃書第 6.3 節);(2) 以 `data/ships/*/trial_targets.json` 的迴旋圈、Z 形、停船公差做黃金測試;(3) 現有 `EngineDeterminismTests`、`SnapshotRestoreTests`、`RecordReplayTests` 在替換模型後仍須全部通過;`PlaceholderDynamicsTests` 的行為測試(右舵 → ROT 正、HAH → 7.8 kn 等)可作為最低要求。

## 6. 已知限制

- 暫代模型無迴旋速度–迴轉率耦合、單俥倒車艉偏、風力矩、浪、淺水附加質量(僅把 Nomoto 時間常數放大)、岸壁效應;倒車航速以前進曲線的 60 % 估計。
- Squat 用 Barrass 開闊水域簡式(`Cb·V²/100`),比海報值偏保守;UKC 由常數水深計算(`IDepthProvider` 已可接水深網格 callback,但 callback 無法進快照)。
- `restore` 只能回到記憶體內的快照歷史;從紀錄檔任意快照接續的講評功能尚未接到 Host。
- WebSocket 以 `System.Net.HttpListener` 實作(無 ASP.NET Core 相依),無 TLS 與驗證(船上/直連區網使用)。Windows 上非管理員且未設 URL ACL 時會退回只監聽 localhost;教官筆電直連前請執行一次 `netsh http add urlacl url=http://*:8765/ user=Everyone`(安裝程式應代為設定)。
- NMEA 多播尚未加 IEC 61162-450 TAG block;無 AIS(VDM/VDO)、目標船、THS、XDR、ALR。
- 主機模型無起動空氣次數上限的實際阻擋(只遞減 `startsRemaining`)、臨界轉速區、倒車起動延遲。
- 時間倍率只影響即時驅動;高倍率下若物理負荷過高會丟步(`RealtimeRunner.DroppedSteps`)。
