# 感測器故障層、警報管理(BAM)與 BNWAS — 設計與契約

> 規劃書依據:第 7.2 節儀器表列(電羅經/自動舵/GNSS/測深儀/計程儀/操舵的可注入故障、BNWAS、中央警報面板)、第 8.1 節 E08、第 8.3 節第 5、7 類指標;
> 法規:MSC.302(87) 駕駛台警報管理(BAM)、MSC.128(75) BNWAS;IEC 61162-1(NMEA 0183)。
> 程式:`src/SimCore/Sensors/SensorModel.cs`、`src/SimCore/Alerts/`(`AlertCatalogue.cs`、`AlertManager.cs`、`Bnwas.cs`)、`src/SimCore/Engine/SimulationEngine.cs`、
> `src/Gateway.Nmea/NmeaEncoder.cs`;測試 `tests/SimCore.Tests/SensorFaultTests.cs`、`AlertManagerTests.cs`、`BnwasTests.cs`、`NmeaSensorTests.cs`、
> `GyroFailureScenarioTests.cs`、`SensorAlertContractsTests.cs`。

## 1. 總覽

| 層 | 責任 | 狀態(進快照 Version 3 與雜湊) |
|---|---|---|
| 感測器層 `SensorModel` | 真值 → 指示值(電羅經、GNSS、測深儀、計程儀、磁羅經) | 漂移起點與速率、凍結/保持值、GNSS 偏移量、計程儀偏差 |
| 自動舵 | 以**電羅經指示航向**操舵;`autopilotFailure`/`gyroFailure` 時脫開並拒絕啟用 | 既有 Autopilot 快照 |
| 操舵物理故障 | `steeringFuOnly`(fu/自動舵下舵不動)、`steeringPumpFailure`(舵速 ×0.5) | 由 faults[] 決定 |
| 警報管理 `AlertManager` | 條件判定 + MSC.302 狀態機 + 升級/靜音計時 | 警報清單、偏航持續起點、航向監視/淺水滯後 |
| BNWAS `Bnwas` | MSC.128 模式/休眠期/三階段/重設 | 模式、階段、計時起點、設定 |
| 評估 | 警報確認時間、BNWAS 第二/三階段次數 → `summary.alerts`;故障應變 `alertAck*` | `MetricsState`(評估快照) |

每步執行順序(`SimulationEngine.Advance`):陣風 → 環境漸變 → **自動舵(指示航向)** → 致動器(FU 失效卡舵、泵失效減速)→ RK4 → 擱淺 → 交通 →
**BNWAS.Update → AlertManager.Update** → 每秒評估步(Route/Zone/Metrics/…→ 時間表 → 規則)→ 雜湊 → 廣播 → 自動快照。
指令(含時間表動作)在推進前處理;故障注入當步即改變指示值與自動舵,警報在**下一步**成立(t + 0.02 s)。

確定性:無新亂數;漂移以封閉式 `drift = base + rate × (tick − start) × dt / 60` 計算;所有計時以 tick 為單位。感測器/警報/BNWAS 的雜湊位元組只在
「非預設」時追加(感測器有偏差、有警報或滯後狀態、BNWAS 非 off),**無這些狀態的舊情境雜湊與舊版完全相同**(`HashCompatibilityTests` 仍通過)。

## 2. 感測器層:真值與指示值

### 2.1 對外語意

`state` 頂層的 `heading`、`pos{lat,lon,x,y}`、`cog`、`sog`、`stw`、`depthBelowKeel` 一律為**指示值**(儀器、ECDIS、NMEA、自動舵看到的值);
`state.truth` 為真值(教官站/講評用,學員端儀器不應顯示);`state.sensors` 為有效性旗標與磁羅經。`u/v/r/rot/drift/rudder/…` 仍為真值(ROT 取迴轉率陀螺,不受電羅經故障影響)。
評估(XTD、UKC、CPA、區域、時間表的 `ownHeading` 等條件)一律用真值;`state.route` 也由真值計算(ECDIS 端若要顯示「指示位置的 XTD」需自行以 `pos` 計算)。

無效時頂層數值為**最後有效值**(保持顯示),`sensors.*Valid = false`;儀器端應以「無效/可疑」樣式顯示(規劃書第 7.2 節 conning)。

### 2.2 故障表

| 故障 | args(預設) | 指示值 | 有效旗標 | 警報 |
|---|---|---|---|---|
| `gyroDrift` | `rate_degPerMin`(1.0;正 = 指示值往右) | heading = 真值 + 累積漂移;重複注入自目前誤差以新速率續漂;清除歸零 | 有效 | 無(E08) |
| `gyroFreeze` | — | heading 停在注入時的指示值 | 有效 | 無(可由航向監視偵測) |
| `gyroFailure` | — | heading 保持最後值 | `headingValid` false | `gyroFailure` alarm/B |
| `gpsJump` | `distance_m`(500)、`bearing_deg`(90) | pos 一次跳該距離/方位,持續到清除 | 有效 | 無 |
| `gpsOffset` | `distance_m`(200)、`bearing_deg`(45)、`ramp_s`(60) | pos 於 ramp_s 內線性偏移到該量 | 有效 | 無 |
| `gpsFailure` | — | pos/cog/sog 保持最後值 | `positionValid` false | `gpsFailure` alarm/B |
| `echoSounderFailure` | — | depthBelowKeel 保持最後值 | `depthValid` false | `echoSounderFailure` warning/B |
| `logFailure` | `bias_kn`、`factor`(皆無) | 無 args:stw 保持最後值;有任一:stw = 真值 × factor + bias | 無 args 時 `stwValid` false | 無 args 時 `logFailure` warning/B |
| `autopilotFailure` | — | 自動舵脫開 | `autopilot.available` false | `autopilotFailure` alarm/B |
| `steeringFuOnly` | — | (物理)fu 與自動舵模式下舵停在原處;nfu/emergency 可動 | — | `steeringFuOnly` alarm/B |
| `steeringPumpFailure` | — | (物理)舵機速率 ×0.5(所有模式) | — | `steeringPumpFailure` alarm/B |
| `steeringGear`/`mainEngine`/`bowThruster` | — | 既有行為不變 | — | alarm / alarm / warning(B) |

故障類警報 id = 故障名稱。`src/Contracts/fault-catalogue.json` 的 `modelled`、`effect`、`alert`、`args` 已依此更新(`FaultNames.Modelled`)。

### 2.3 自動舵

- 舵令 = `Autopilot.RudderOrderDeg(指示航向, 真 ROT)`;電羅經未受影響時直接用 `Motion.Psi`(與舊版位元一致)。
- `gyroDrift`:自動舵「準確地」把指示航向保持在設定值,真航向 = 設定值 − 漂移(E08 的關鍵);偏航警報以指示航向判定,不會觸發。
- `gyroFailure` 或 `autopilotFailure` 注入時若自動舵啟用:發 `autopilotTripped {reason: headingInvalid|autopilotFailure, fault}`(source engine)、舵令同步到目前舵角、
  `steeringModeChanged autopilot → fu`(source engine,不視為學員動作);故障期間 `autopilot{enabled:true}` 與 `steeringMode autopilot` 被拒:
  `commandRejected {command, reason: "autopilotUnavailable"}`。

### 2.4 磁羅經

`sensors.magneticHeading = 真航向 + scenario.sensors.magneticCompassError_deg`(修正自差/磁差後的剩餘誤差,預設 0),不受電羅經故障影響;航向監視比對它與電羅經指示值。

## 3. 警報管理(BAM,MSC.302(87))

### 3.1 警報目錄(`AlertCatalogue.All`,固定順序 = 判定順序與 ALR 編號)

| # | id | 預設優先序 | 類別 | 來源 | 條件 |
|---|---|---|---|---|---|
| 001 | gyroFailure | alarm | B | gyro | 故障 |
| 002 | gpsFailure | alarm | B | gnss | 故障 |
| 003 | echoSounderFailure | warning | B | echoSounder | 故障 |
| 004 | logFailure | warning | B | log | 故障(無效模式) |
| 005 | autopilotFailure | alarm | B | autopilot | 故障 |
| 006 | steeringGear | alarm | B | steering | 故障 |
| 007 | steeringFuOnly | alarm | B | steering | 故障 |
| 008 | steeringPumpFailure | alarm | B | steering | 故障 |
| 009 | mainEngine | alarm | B | engine | 故障 |
| 010 | bowThruster | warning | B | thruster | 故障 |
| 011 | shallowWater | alarm | A | echoSounder | 指示龍骨下水深 < `alerts.shallowWater_m`(解除 +0.2 m;測深儀無效時不成立) |
| 012 | offHeading | alarm | A | autopilot | 自動舵啟用且 |設定 − 指示航向| > `alerts.offHeading_deg` 持續 `offHeadingDelay_s` |
| 013 | headingMonitor | warning | A | gyro | |電羅經指示 − 磁羅經| > `alerts.headingMonitor_deg`(解除 −1°;電羅經無效時不成立) |
| 014 | bnwas | caution → alarm | B | bnwas | BNWAS 階段 ≥ 1(第 1 階段 caution,第 2 階段起 alarm) |

門檻類(011–013)未在情境設定即關閉;故障類一律啟用。

### 3.2 狀態機

```
            條件成立                 alertSilence            silence_s 期滿(alertReannunciated)
 (none) ──────────────▶ activeUnacknowledged ─────────▶ activeSilenced ─────────────────────▶ activeUnacknowledged
                          │   ▲     │ alertAcknowledge        │ alertAcknowledge
                          │   │     ▼                         ▼
                          │   │   activeAcknowledged ◀────────┘
          條件解除        │   │     │ 條件解除 → normal(移除;alertRectified state=normal)
                          ▼   │ 再成立(alertRaised repeat=true, count+1)
              rectifiedUnacknowledged ── alertAcknowledge ──▶ normal(移除)
```

- 有聲響(`audible`)= activeUnacknowledged 且優先序為 emergencyAlarm/alarm/warning;caution 只有視覺。
- 升級:warning 在 activeUnacknowledged/activeSilenced 持續 `alerts.escalate_s`(預設 300 s,自最近一次成立起算)未確認 → alarm、`escalated = true`、
  回到 activeUnacknowledged(`alertEscalated {from: warning, to: alarm, reason: unacknowledged, after_s}`)。BNWAS 第 2 階段同樣以 `alertEscalated {reason: source}` 表示。
- BNWAS 警報例外:BNWAS 重設即解除指示(直接 normal,不經 rectifiedUnacknowledged)。
- responsibility transferred 與 Category A「只能在相關工作站確認」本版略過(任一站的 alertAcknowledge 皆可確認;記錄於第 9 節)。

### 3.3 指令

```json
{"type":"alertAcknowledge","args":{"id":"gyroFailure"}}
{"type":"alertAcknowledge"}
{"type":"alertSilence","value":"bnwas"}
{"type":"alertSilence"}
```

`id`(value 字串或 args.id)省略或 `"all"` = 全部;未知 id → `commandRejected {reason: "unknownAlert"}`。兩者都視為駕駛台操作(重設 BNWAS),寫入 `input` 行。

### 3.4 事件(紀錄 `event` 行、WebSocket `{"type":"event","event":…}`)

`source = "engine"`、`itemId = 警報 id`、`visible = true`,`data` 共同欄位 `{id, priority, category, source, state(事件後), count, raisedAt_s, silencedUntil_s?}`:

| kind | 額外 data |
|---|---|
| `alertRaised` | `repeat`(rectifiedUnacknowledged 期間再成立) |
| `alertSilenced` | `silencedUntil_s` |
| `alertReannunciated` | — |
| `alertAcknowledged` | `ackTime_s`(確認 − 最近一次成立) |
| `alertRectified` | `state` = normal 或 rectifiedUnacknowledged |
| `alertEscalated` | `from`、`to`、`reason`(unacknowledged/source)、`after_s` |

```json
{"kind":"alertRaised","tick":24001,"t":480.02,"source":"engine","itemId":"gyroFailure","message":"電羅經失效","phase":"emergency","visible":true,"seq":23,
 "data":{"id":"gyroFailure","priority":"alarm","category":"B","source":"gyro","state":"activeUnacknowledged","count":1,"raisedAt_s":480.02,"repeat":false}}
{"kind":"alertEscalated","tick":27751,"t":555.02,"source":"engine","itemId":"bnwas","message":"BNWAS 值班警報","phase":"emergency","visible":true,"seq":30,
 "data":{"id":"bnwas","priority":"alarm","category":"B","source":"bnwas","state":"activeUnacknowledged","count":1,"raisedAt_s":540.02,"from":"caution","to":"alarm","reason":"source"}}
```

時間表可用 `{type: event, kind: alertRaised, itemId: gyroFailure}` 觸發;必要事件 expect 用 `{event: {kind: alertAcknowledged}}`
(結構檢查要求 expect/after 的 itemId 為時間表項目 id,故不能指定警報 id)。

### 3.5 `state.alerts[]`

依 priority、raisedAt_s 排序;無警報時省略。

```json
"alerts": [
  {"id":"gyroFailure","number":1,"priority":"alarm","category":"B","source":"gyro","state":"activeUnacknowledged","text":"電羅經失效",
   "raisedAt_s":480.02,"escalated":false,"count":1,"audible":true},
  {"id":"bnwas","number":14,"priority":"alarm","category":"B","source":"bnwas","state":"activeSilenced","text":"BNWAS 值班警報",
   "raisedAt_s":540.02,"silencedUntil_s":585.0,"escalated":true,"count":1,"audible":false}
]
```

## 4. BNWAS(MSC.128(75))

- 模式:`off`(預設)/`auto`(自動舵啟用時作動)/`on`。作動開始(off→作動、模式切換、auto 下接上自動舵)時休眠期自該步起算。
- 階段:休眠 `dormant_s`(180–720 s,預設 720)未重設 → **1**(駕駛台視覺,警報 bnwas caution)→ `stage2_s`(15 s)→ **2**(駕駛台聲響,警報升級 alarm)
  → `stage3_s`(90 s)→ **3**(後備值班官/船長;`bnwasStage {stage: 3}`)。停止作動 → 階段 0(`reason: inactive`)。
- 重設:`bnwasReset` 指令,或任何學員/教官經指令佇列下的駕駛台操作:`rudder`、`telegraph`、`rpm`、`thruster`、`autopilot`、`steeringMode`、`alertAcknowledge`、`alertSilence`。
  時間表動作(`ownShip`、`autopilot` 覆寫)不是駕駛台上的人為操作,不重設;`acknowledge`(教官記錄口頭動作)也不重設。
- 事件:`bnwasStage {stage, from, reason: dormantElapsed|notReset|inactive}`(source engine)、`bnwasReset {fromStage, by: 指令名}`(source = 指令來源;
  一般操作只在階段 ≥ 1 時發事件,`bnwasReset` 指令一律發)、`bnwasModeChanged {from, to, fromStage, dormant_s, stage2_s, stage3_s}`。
- 指令:

```json
{"type":"bnwasMode","value":"on","args":{"dormant_s":540}}
{"type":"bnwasMode","value":"auto"}
{"type":"bnwasReset"}
```

  `dormant_s` 夾在 180–720;模式開關在實船受保護(鑰匙/密碼),由教官站操作。
- 狀態:

```json
"bnwas": {"mode":"on","active":true,"stage":2,"remaining_s":45.02,"dormant_s":540}
```

  `remaining_s`:階段 0 為距第 1 階段、階段 1/2 為距下一階段;未作動或第 3 階段省略。

## 5. 評估(規劃書第 8.3 節第 5、7 類)

`summary.alerts`(每次成立一筆紀錄;BNWAS 次數在 `bnwas` 子區塊;以下為示意值):

```json
"alerts": {
  "raised": 2, "acknowledged": 1, "unacknowledged": 1, "escalations": 1, "silences": 0,
  "meanAckTime_s": 4.98, "maxAckTime_s": 4.98,
  "records": [
    {"id":"gyroFailure","priority":"alarm","raisedAt_s":480.02,"acknowledgedAt_s":485.0,"ackTime_s":4.98,"rectifiedAt_s":560.02,"escalated":false,"silences":0},
    {"id":"bnwas","priority":"alarm","raisedAt_s":540.02,"escalated":true,"silences":0}
  ],
  "bnwas": {"stage1": 1, "stage2": 1, "stage3": 0, "resets": 0}
}
```

`faultResponses[]` 新增 `alertAckAt_s`、`alertAckDt_s`(該故障對應警報的第一次確認時刻與距注入秒數)。容許帶可引用的新指標(`MetricNames`):
`alerts.meanAckTime_s`、`alerts.maxAckTime_s`、`alerts.unacknowledged`、`alerts.escalations`、`bnwas.stage2Count`、`bnwas.stage3Count`
(Python `scenario.py` 的 `METRIC_NAMES` 尚未加入,資料夾內情境暫勿在 bands 引用)。

## 6. NMEA(`Gateway.Nmea`)

所有句型取指示值。無效時(依 IEC 61162-1):

| 句型 | 正常 | 無效 |
|---|---|---|
| HDT(HE) | 指示航向 | 電羅經無效:**停送** |
| GGA(GP) | 品質 1 | GNSS 無效:品質 0、衛星 00、位置/HDOP 空白 |
| RMC(GP) | 狀態 A、模式 A | 狀態 V、模式 N、位置/SOG/COG 空白(日期保留) |
| VTG(GP) | 模式 A | 數值空白、模式 N |
| VBW(VD) | 對水/對地狀態 A | 計程儀無效:對水欄位空白 + V;GNSS 或電羅經無效:對地欄位空白 + V |
| DPT(SD) | 龍骨下水深 | 測深儀無效:**停送** |
| ALR(II) | — | 每個非 normal 警報 1 Hz 一句:`$IIALR,hhmmss.ss,nnn,A/V(條件),A/V(確認),英文文字*hh` |

```
$IIALR,133800.00,001,A,V,GYRO FAILURE*hh
$IIALR,133800.00,010,V,V,BOW THRUSTER FAILURE*hh
```

ALR 文字取警報目錄的 ASCII `NmeaText`(中文不進 NMEA)。ALF/ALC(IEC 61162-1 Ed.5 BAM 句型)本版未實作。ROT(TI)不受電羅經故障影響。

## 7. E08 情境(`data/scenarios/E08_gyro_failure.yaml`)

夜間彰化外海沿岸北上(FSB1 滿載 11 kn、自動舵 029°、BNWAS on 休眠 540 s、偏航警報 10°、淺水警報 5 m;航向監視關閉以符合「漂移無警報」)。

| 時間 | 項目 | 內容 |
|---|---|---|
| T+1 s | autopilotOn | 自動舵 029° |
| T+20 s | masterOrders | 船長交班語音 |
| T+60 s(`emergency.gyroFailure.driftAt_s`) | gyroDrift | `injectFault {name: gyroDrift, args: {rate_degPerMin: −1}}`(參數 `driftRate_degPerMin`;指示值偏左 → 真航向偏右往岸側),階段 degraded |
| 必要事件 driftDetected 逾時 | lookoutCue | 瞭望員提示燈塔方位異常 |
| 漂移 + 420 s(`failAfter_s`) | gyroFail | `gyroFailure`:alarm、HDT 停送、自動舵脫開;階段 emergency |
| 教官手動 | gyroRestored | 清除兩故障、階段 normal |
| T+1800 s | end | endExercise |

必要事件:`driftDetected`(漂移後 300 s 內 autopilotOff/steeringModeChange/rudderOrder 或 `acknowledge {kind: gyroCheck}`)、
`alarmAck`(失效後 30 s 內 `alertAcknowledged`)、`manualSteering`(60 s 內舵令或操舵模式)、`callMaster`(180 s 內 `acknowledge callMaster`)。
參數來源 `ASSUMED-E08`(假設值,待公司程序);`bnwas.dormant_s` 暫寫 540(= `bridge.bnwas.dormantPeriod_s`;Python 參考實作尚未保留 `bnwas` 區段,引用會使跨語言檢查失敗)。

無人操作 600 s(`dotnet run --project src/SimCore.Host -c Release -- --scenario E08_gyro_failure --record build/records/e08.jsonl --duration 600 --timescale 10 --no-ws --no-udp --no-nmea`)
共 31 個事件,序列固定於 `GyroFailureScenarioTests.HandsOffEvents`:電羅經失效 480.02 s 警報、BNWAS 第 1 階段 540.02 s、第 2 階段 555.02 s;
600 s 時真航向 37.2°、指示航向保持 28.3°(誤差 −8.9°);`--replay` 狀態雜湊、事件序列、summary 一致。

## 8. 情境 YAML 新區段(全部選填;`scenario.schema.json`)

```yaml
sensors:
  magneticCompassError_deg: 1.0
alerts:
  escalate_s: 300
  silence_s: 30
  offHeading_deg: 10        # 未設 = 關閉
  offHeadingDelay_s: 10
  headingMonitor_deg: 5     # 未設 = 關閉
  shallowWater_m: 5.0       # 未設 = 關閉
bnwas:
  mode: "on"                # off | auto | on(YAML 的 on/off 請加引號)
  dormant_s: 540            # 180–720
  stage2_s: 15
  stage3_s: 90
```

結構檢查(`Scenario.StructuralIssues`):秒數必須為正、門檻必須為正、`dormant_s` 在 180–720。數值欄位可寫 `"${params:…}"`(`mode` 不可),
但 Python 參考實作目前不保留這三個區段,資料夾內情境請先寫數值。`acknowledge` 新增 kind `gyroCheck`(羅經交叉核對發現異常)。

## 9. 偏離與限制

1. responsibility transferred、Category A 只能在相關工作站確認:略(任一站可確認)。Category C 未使用。
2. BNWAS 第 3 階段仍為 alarm 優先序(以 `bnwasStage {stage: 3}` 區分),不使用 emergencyAlarm(BAM 的 emergency alarm 保留給人命危急,如火災)。
3. `state.route`(XTD)以真值計算;`gpsJump/gpsOffset` 時 ECDIS 的指示 XTD 由儀器端以 `pos` 自算。
4. ROT 與 `u/v/r` 為真值;`gpsJump` 不產生 COG/SOG 尖峰。
5. 偏航/航向監視/淺水警報預設關閉,以維持舊情境(E01/E05/E07/E09)的事件序列;故障類警報一律啟用,故 E07/E09 的事件序列多了 `alertRaised/alertRectified`
   (E09 固定序列的 `BlackoutScenarioTests` 已更新為含警報;E07 黃金檔 `Golden/assessment-E07-600s.json` 更新:新增 `alerts` 區塊,以及因參數集附加 E08 參數而改變的 `parameterSet.sha256`)。
6. E09 情境檔註解仍寫 gyroFailure「只記錄」;實際已 modelled(E09 不在本任務檔案範圍,待更新註解)。
7. Python 參考實作(`src/Tools.Calibration`)尚未實作感測器層/警報/BNWAS(不輸出 truth/sensors/alerts/bnwas,新指令應回 ok=false),
   `METRIC_NAMES`、情境 `sensors/alerts/bnwas` 區段待補;網頁儀器/教官站待讀取 `state.truth/sensors/alerts/bnwas` 與送新指令。
8. ALF/ALC 句型、HDG(磁羅經)句型未輸出。
