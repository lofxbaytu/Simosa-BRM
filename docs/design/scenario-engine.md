# 情境引擎設計(Scenario Engine)— 時間表、公司程序參數集、自動量測與評分

> 狀態:v0.2(2026-10-17)— C# 核心已依本文件實作(`src/SimCore`、`src/SimCore.Host`、契約、`data/parameters`、E01/E07、`tests/SimCore.Tests`);實作時的偏離與補充見**第 11 節**,以第 11 節為準。網頁端(第 6 節)與 Python(第 2.3 節替換)待實作。對應規劃書第 5.3 節(紀錄)、第 7.2 節(故障目錄)、第 8.1 節(練習目錄與三步升級)、第 8.3 節(自動量測指標與評分)、第 8.4 節(講評)、第 9.1 節(教官站、簡化教官模式、程序參數管理)。需求 R13(簡化教官模式)、R14(程序參數管理)。
> 現況交接:`docs/dev-log/2026-10-08-工作階段交接.md` 第 2 節第 1 項。本文件只寫設計,不含程式變更;實作時以本文件的欄位名稱、檔案清單與測試計畫為準,偏離處回寫本文件。

## 0. 目標、範圍與既有機制的重用

### 0.1 目標

1. 情境 YAML 能描述「什麼時候、什麼條件下發生什麼事」(時間表,timeline),由核心(SimCore)確定性地執行,事件進紀錄檔、重播得到相同事件序列與雜湊。
2. 評估規則不寫死數值,而引用版本化的「公司程序參數集」(parameter set),載入時解析;講評報告標註參數集版次與來源程序文件編號。
3. 核心依狀態與事件自動計算第 8.3 節第 1 至 5 類中可自動量測的指標,輸出 `summary` 紀錄行與 WebSocket 查詢,供教官站/講評站使用;教官站現有的本地推導(`debrief.ts`)保留為離線/Python 後備。
4. 不破壞現有契約與測試:`tests/SimCore.Tests`(161 項)、`src/Instruments.Web`(122 項)、`record-replay.ts` 的紀錄解析。

### 0.2 不在第一版範圍

- 語音辨識、口頭複誦偵測(第 8.3 節第 6 類只做「經介面可偵測」者)。
- 水深網格(UKC 仍用常數水深或 `zones` 的 `ukcPolicy` 區域水深覆寫;`IDepthProvider` 已可接 callback 但無法進快照,見 `src/SimCore/README.md` 第 7 節)。
- 能見度對雷達/視景的物理效果(只保存、漸變、廣播、記錄 `visibility_nm`;儀器端自行反應)。
- 行為標記(第 8.2 節)仍由教官手動標記(`brm-markers.ts`),不由核心產生。
- 參數集的簽章更新包派送(第 5.5 節)與「參數集修改後列出受影響情境」的教官筆電工具(第 9.1 節)列為後續工作,本文件只定義資料格式與載入時的檢查。

### 0.3 既有機制盤點(重用而非重做)

| 既有機制 | 位置 | 在本設計中的角色 |
|---|---|---|
| 目標船觸發 `trigger: {type: time\|ownDistance\|ownHeading}` | `Scenario/ScenarioTarget.cs` `TargetTrigger`、`Traffic/TrafficManager.TriggerSatisfied` | 時間表觸發條件的子集;`TriggerCondition` 的 leaf 型別與其語意相同(`time`、`ownDistance`、`ownHeading`),並共用「每秒評估一次」(`_evalDivider`)的節奏。目標船自己的 `trigger` 保留不改 |
| `TrafficEvent` 與紀錄 `event` 行 | `Traffic/TrafficEvent.cs`、`Recording/RecordLines.cs` `RecordEvent` | 泛化為 `SimEvent`(第 3.2 節);既有 `kind` 字串(`collision`、`cpaAlarm`、`cpaClear`、`targetActivated`、`waypointReached`、`scriptStep`、`colreg`、`soundSignal`、`targetControl`)與 JSON 欄位名不變 |
| `assessment.minCpa_nm` | `ScenarioAssessment`、`TrafficManager.MinCpaThresholdNm` | 保留;可改為參數鍵引用(`${params:...}`),解析後仍是數值 |
| `EngineSnapshot`(含 `Traffic`) | `Engine/EngineSnapshot.cs` | 新增 `Timeline`、`Assessment`、`ParameterSet` 欄位(null 相容) |
| 狀態雜湊鏈 `UpdateHash` | `Engine/SimulationEngine.cs` | 在目標船位元組之後追加時間表狀態與事件計數(無時間表的情境雜湊不變) |
| 故障 `injectFault`/`clearFault` 與 `FaultNames` | `Engine/FaultNames.cs`、`SimulationEngine.Apply` | 時間表動作直接呼叫同一套邏輯;只記錄的故障名稱(教官站 `DEFAULT_FAULTS`)照樣進 `faults[]` |
| `setEnvironment` | `SimulationEngine.ApplyEnvironment` | 時間表動作加 `ramp_s` 漸變;指令本身也加 `ramp_s` |
| `targetControl` | `TrafficManager.ApplyControl` | 時間表動作 `targetControl` 直接轉成同一指令的 `args` |
| 自動舵 `Autopilot` | `Engine/Autopilot.cs` | 時間表動作 `autopilot` |
| 紀錄 `input` 行 + `Replayer` | `Recording/` | 教官手動觸發(`triggerTimeline`)、確認(`acknowledge`)為一般指令 → 進 `input` 行 → 重播還原;時間表自動動作**不**進 `input` 行(由時間表在重播中重新產生) |
| 教官站 `scenario.ts` 已讀 `briefing`、`environment.visibility_nm` | `src/Instruments.Web/src/lib/scenario.ts` | schema 正式加入這兩個欄位 |
| 教官站故障目錄(建置時讀 `FaultNames.cs`) | `lib/instructor-command.ts` | 時間表 `injectFault` 的名稱以同一目錄為準(第 1.1.4 節) |
| `debrief.ts` 本地推導 | `src/Instruments.Web/src/lib/debrief.ts` | 保留為後備;核心 `summary` 可用時優先顯示(第 6 節) |

## 1. 情境 YAML 擴充(`src/Contracts/scenario.schema.json`)

新增四個頂層選填欄位:`timeline`、`route`、`zones`、`briefing`;擴充 `assessment`;`environment` 新增 `visibility_nm`。全部為選填,既有情境檔不變仍有效。Python 參考實作忽略 `timeline`/`route`/`zones`/`assessment`(與 `targets` 相同),但必須接受 `${params:...}` 字串(解析器在載入層,Python 側同樣要先解析再用,第 2.4 節)。

### 1.1 `timeline`:事件時間表

```yaml
timeline:
  - id: steeringFail                 # 情境內唯一;事件、手動觸發、評估規則以此引用
    name: 舵機故障(卡舵)
    description: 進入航道第 2 段後 90 秒,舵卡在當時舵角
    phase: degraded                  # 選填:觸發時把情境階段設為此值(normal → degraded → emergency,第 8.1 節三步升級)
    visibility: instructor           # instructor(預設)| all:all 時事件以 visible=true 廣播,學員站可顯示
    repeat: once                     # once(預設)| always | { every_s: 120, max: 5 }
    cooldown_s: 0                    # repeat 非 once 時,兩次觸發的最短間隔(秒)
    holdFor_s: 0                     # 條件需連續成立多少秒才觸發(0 = 立即)
    delay_s: 0                       # 觸發後延遲多少秒執行動作(動作排入待執行表,進快照)
    manual: false                    # true:只能由教官 triggerTimeline 觸發(when 可省略);false 時教官仍可用 force 強制觸發
    when:                            # 觸發條件(第 1.1.1 節);manual: true 時可省略
      all:
        - { type: routeLeg, atLeast: 1 }
        - { type: time, atLeast: 90, since: { event: routeLegChanged, legIndex: 1 } }
    actions:                         # 依序執行(第 1.1.2 節)
      - injectFault: steeringGear
      - voiceScript: { role: helmsman, channel: bridge, text: "舵不聽使喚!舵角指示停在 12 度", file: audio/E07/helm_no_response.ogg }
      - message: { to: instructor, text: "已注入舵機故障;期望 60 秒內車令/側推或切換 NFU,120 秒內通知機控室" }
```

#### 1.1.1 觸發條件 `when`(`$defs.triggerCondition`)

條件是遞迴結構:leaf(`type` 必填)或組合(`all` / `any` / `not`)。所有 leaf 在**評估時刻**(每秒一次,第 3.3 節)以當時狀態判定為真/假;`holdFor_s` 與「曾經發生」的語意由項目層級的狀態機處理。

| `type` | 欄位 | 語意(與目標船 trigger 相同者標 ★) |
|---|---|---|
| `time` ★ | `atLeast`(秒)、`since`(選填,`{event: kind, itemId?, targetId?, legIndex?}`:自該事件最後一次發生起算;未給時自情境開始起算) | 模擬時間 ≥ atLeast |
| `ownDistance` ★ | `point {lat,lon \| x,y}` 或 `waypoint: <route 航點 name>` 或 `target: <id>`;`lessThan` / `greaterThan`(nm) | 自船與該點/目標的距離比較;兩者皆給為區間 |
| `ownHeading` ★ | `greaterThan`、`lessThan`(度) | 順時針扇區 greaterThan → lessThan;只給一個時為單邊 |
| `ownSpeed` | `metric: sog \| stw`(預設 sog)、`lessThan` / `greaterThan`(kn) | |
| `ukc` | `lessThan` / `greaterThan`(m)或 `lessThanFractionOfDraft` | 以引擎 `UnderKeelClearance()`(含 squat) |
| `xtd` | `greaterThan`(m,絕對值)、`side: port \| starboard \| any` | 需 `route` |
| `routeLeg` | `atLeast` / `equals`(航段索引,0 起)、或 `passedWaypoint: <name>` | 需 `route`;航段切換規則見第 4.2 節 |
| `zone` | `id`、`inside: true \| false` | 自船船舯在多邊形內 |
| `phase` | `equals: normal \| degraded \| emergency \| <自訂>` | 情境階段(第 1.1.3 節) |
| `event` | `kind`、`itemId?`、`targetId?`、`count?: {atLeast}` | 該事件曾發生(次數 ≥ atLeast,預設 1);涵蓋目標船事件(`cpaAlarm`、`targetActivated`、`colreg`…)、時間表事件(`timelineTriggered` + itemId)、引擎事件(`grounding`、`collision`、`faultInjected`) |
| `fault` | `name`、`active: true \| false` | 故障目前是否在 `faults[]` |
| `telegraph` | `in: [STOP, DSAS, …]` 或 `notIn` | 目前車鐘 |
| `autopilot` | `enabled: true \| false` | |
| `manual` | — | 永遠為假;只能由 `triggerTimeline` 觸發(等同項目 `manual: true`) |

組合:`{ all: [c1, c2] }`、`{ any: [...] }`、`{ not: c }`;可巢狀。

#### 1.1.2 動作 `actions`(`$defs.timelineAction`)

每個動作是只有一個鍵的物件(鍵即動作名),依序同步執行;每個動作產生一個事件(第 3.2 節)。動作直接呼叫引擎既有的 `Apply` 邏輯(`ApplyInternal`,第 3.4 節),**不**經指令佇列、**不**寫 `input` 行。

| 動作 | 欄位 | 對應既有機制 / 產生事件 |
|---|---|---|
| `injectFault: <name>` | 名稱依第 7.2 節故障目錄(第 1.1.4 節);可帶物件 `{name, args}` 保留日後參數化(例如 `{name: steeringGear, args: {jamAt_deg: 15}}`;第一版引擎忽略 args 只記錄) | `SimCommandType.InjectFault` → 事件 `faultInjected` |
| `clearFault: <name> \| all` | | `ClearFault` → `faultCleared` |
| `setEnvironment: {wind{trueSpeed,trueDir,gustiness}, current{set,drift}, waterDepth, visibility_nm, ramp_s}` | `ramp_s`(預設 0):自目前值線性漸變到目標值(第 3.5 節) | `ApplyEnvironment` + `EnvironmentRamp` → `environmentChange`(含 `ramp_s`)、漸變完成時 `environmentRampDone` |
| `targetControl: {…}` | 同 `command.schema.json` `targetControl.args` | `TrafficManager.ApplyControl` → 既有 `targetControl` 事件 |
| `voiceScript: {role, channel, text?, file?, lang?, durationEstimate_s?}` | `role`: vts \| pilot \| ecr \| tug \| otherShip \| master \| agent \| helmsman \| lookout \| narrator;`channel`: vhf16 \| vhf13 \| vhfPort \| intercom \| phone \| pa \| bridge;`file` 相對於情境包目錄 `data/scenarios/<id>/`;核心**只記錄事件**,由教官站/簡化教官模式播放(第 9.1 節) | 事件 `voiceScript`(`data` 含全部欄位) |
| `message: {to: instructor \| all, text, severity: info \| warning \| alarm}` | 文字訊息(教官站事件流;`to: all` 時學員站可顯示) | `message` |
| `phase: <name>` | 設定情境階段 | `phaseChanged`(`data.from`, `data.to`) |
| `freeze: {reason?}` | 凍結(需教官 `resume`) | `Freeze` → `frozen` |
| `autopilot: {enabled, heading?, rotLimit?}` | 自動舵覆寫(E08「自動舵跟隨錯誤航向」) | `Autopilot` → `autopilotOverride` |
| `ownShip: {rudder?, telegraph?, rpm?, thruster?}` | 自船操控覆寫(例如 E09 失電時車鐘歸 STOP) | 對應指令 → `ownShipOverride` |
| `timeScale: <n>` | | `TimeScale` → `timeScaleChanged` |
| `arm: <itemId>` / `disarm: <itemId>` | 啟用/停用另一個時間表項目(預設全部 armed;`armed: false` 的項目需被 arm) | `timelineArmed` / `timelineDisarmed` |
| `endExercise: {reason?}` | 凍結 + 產生最終 summary(第 4.6 節) | `exerciseEnded` |

不允許的動作:`loadScenario`、`reset`、`restore`、`snapshot`(載入時驗證錯誤)。

#### 1.1.3 階段 `phase`

情境階段為字串,預設 `normal`;建議值 `normal`、`degraded`、`emergency`(第 8.1 節三步升級),允許自訂。階段只影響:狀態廣播 `scenario.phase`、`phase` 觸發條件、講評時間軸分段、評估規則可依階段啟用(`appliesTo.phase`)。

#### 1.1.4 故障名稱目錄

`injectFault` 名稱以下表為準(與 `src/Instruments.Web/src/lib/instructor-command.ts` `DEFAULT_FAULTS` 合併;`modelled` 者引擎會改變行為,其餘只進 `faults[]`、廣播與記錄,由儀器端自行反應)。建議把此表搬到 `src/Contracts/fault-catalogue.json`,供 schema `enum` 參照、教官站與 Python 共讀;`FaultNames.cs` 保留三個常數。

| 名稱 | 第 7.2 節儀器/故障 | 引擎行為 | 練習 |
|---|---|---|---|
| `steeringGear` | 舵卡在固定角 | modelled(舵角停在目前值) | E07 |
| `steeringFuOnly` *(新,只記錄)* | FU 失效只剩 NFU | 只記錄(第一版) | E07 |
| `steeringPumpFailure` *(新)* | 舵機泵失效(舵速減半) | 只記錄(第一版;後續 ActuatorModel 加半速) | E07 |
| `mainEngine` | 主機停車 | modelled | E09/E10 |
| `engineStartFailure` *(新)* | 起動失敗 | 只記錄 | E14 |
| `bowThruster` | 側推失效 | modelled | F02 |
| `gyroDrift`、`gyroFreeze`、`gyroFailure` | 電羅經漂移/凍結/失效 | 只記錄 | E08 |
| `autopilotFailure` | 自動舵失效 | 只記錄 | E08 |
| `gpsJump`、`gpsOffset`、`gpsFailure` | GNSS | 只記錄 | E08 |
| `radarFailure`、`radarDegraded` | 雷達 | 只記錄 | E03 |
| `aisFailure` | 自船 AIS | 只記錄 | |
| `ecdisFreeze`、`ecdisAlarmFlood` | ECDIS | 只記錄 | E15 |
| `echoSounderFailure`、`logFailure` | 測深儀/計程儀 | 只記錄 | |
| `vhfFailure`、`ecrPhoneFailure` | 通訊 | 只記錄 | E07/E09 |
| `blackout` | 全船失電 | 只記錄(第一版;後續 = mainEngine + steeringGear + 儀器失效的組合) | E09 |
| `fireAlarmEngineRoom` | 機艙火警 | 只記錄 | E19 |
| `cargoHeatingFailure` | 貨物加熱系統故障 | 只記錄 | E21 |

### 1.2 `route` 與 `zones`

```yaml
route:
  name: 麥寮進港航道
  xtdLimit_m: ${params:navigation.xtd.channel_m}   # 預設走廊半寬(可被航點覆寫)
  startLeg: 0                                       # 初始航段(預設 0)
  waypoints:
    - { name: WP1, lat: 23.7900, lon: 120.1200 }                        # 第一點只定義起點
    - { name: WP2, lat: 23.7950, lon: 120.1500, xtdLimit_m: 60, speedLimit_kn: ${params:port.mailiao.speedLimit_kn}, checkpoint: true }
    - { name: WP3, lat: 23.8000, lon: 120.1750, speedLimit_kn: 6, report: "麥寮港 VTS 報告點" }
    - { name: BERTH, x: 11000, y: 1800, arrivalRadius_m: 100 }
zones:
  - id: shoal_N
    name: 北側淺灘禁區
    type: noGo                   # noGo | speedLimit | ukcPolicy | reporting
    polygon: [ {lat: …, lon: …}, {x: …, y: …}, … ]   # ≥ 3 點,經緯度或 ENU 混用
  - id: channel
    type: ukcPolicy
    polygon: [ … ]
    waterDepth_m: 12.0                               # 選填:區域內水深覆寫(ConstantDepth 之外的第一步;進快照)
    ukcMin: { fractionOfDraft: ${params:navigation.ukc.channel_fraction} }   # 或 { metres: 1.0 }
  - id: inner_harbour
    type: speedLimit
    polygon: [ … ]
    speedLimit_kn: ${params:port.mailiao.innerSpeedLimit_kn}
```

語意:
- 航點 `xtdLimit_m`、`speedLimit_kn` 作用於「**到達該航點的那一段**」(航段 i 由 waypoints[i] 到 waypoints[i+1],取 waypoints[i+1] 的限制;未給時取 `route.xtdLimit_m`/無速限)。
- `checkpoint: true` 的航點通過時產生 `checkpointPassed` 事件;`report` 為應報告的說明(事件 `reportPointPassed`,供講評核對是否有 `acknowledge {kind: report}`)。
- `zones[].type` 決定事件與指標:`noGo` 進入 → `zoneEntered`(硬性失敗候選);`speedLimit` 區內超速時間;`ukcPolicy` 區內 UKC 門檻(優先於 `assessment.ukc` 全域門檻)與可選水深覆寫;`reporting` 進入時 `reportPointPassed`。
- 多邊形點數上限 64,自船以船舯點判定內外(ray casting,第 4.3 節)。

### 1.3 `assessment`:評估規則

```yaml
assessment:
  parameterSet: data/parameters/company-procedures.yaml  # 選填;預設此路徑(主控程序 --parameters 可覆寫)
  minCpa_nm: ${params:navigation.cpa.coastal_nm}         # 既有欄位,cpaAlarm 事件門檻
  encounter:
    detectionRange_nm: ${params:navigation.encounter.detectionRange_nm}   # 「首次偵測」= 目標進入此距離且 TCPA > 0
    substantialCourseChange_deg: ${params:navigation.encounter.substantialAlteration_deg}
    substantialSpeedChange_kn: 2
  ukc:
    min: { fractionOfDraft: ${params:navigation.ukc.openSea_fraction} }   # 全域門檻;zones ukcPolicy 覆寫
  manoeuvring:
    rotLimit_degPerMin: ${params:manoeuvring.rotLimit_degPerMin}
    hardOver_deg: 35                                     # 未給時取船舶 rudder.maxAngle_deg(FSB1 70、FSB2 35)
  hardFail:                                              # 任一成立 → passed = false
    - { id: grounding, rule: grounding }
    - { id: collision, rule: collision }
    - { id: cpaHard, rule: cpa, lessThan: ${params:navigation.cpa.hardFail_nm}, excludeEscorts: true }
    - { id: ukcHard, rule: ukc, lessThan: { fractionOfDraft: ${params:navigation.ukc.hardFail_fraction} } }
    - { id: noGo, rule: zoneEntered, zoneId: shoal_N }
  bands:                                                 # 容許帶與扣分(metric 名稱見第 4.5 節)
    - { id: xtdMax, metric: track.maxXtd_m, warn: ${params:navigation.xtd.channel_m}, fail: 2x, penalty: 10, failPenalty: 25 }
    - { id: xtdTime, metric: track.timeOutsideCorridor_s, warn: 0, fail: 60, penalty: 5, failPenalty: 15 }
    - { id: minCpa, metric: collisionAvoidance.minCpa_nm, direction: below, warn: ${params:navigation.cpa.coastal_nm}, fail: ${params:navigation.cpa.hardFail_nm}, penalty: 10, failPenalty: 30, excludeEscorts: true }
    - { id: rotOver, metric: manoeuvring.timeRotOverLimit_s, warn: 0, fail: 30, penalty: 5, failPenalty: 10 }
    - { id: speed, metric: manoeuvring.timeOverSpeedLimit_s, warn: 0, fail: 120, penalty: 5, failPenalty: 15 }
    - { id: ukcTime, metric: ukc.timeBelowLimit_s, warn: 0, fail: 30, penalty: 10, failPenalty: 25 }
  required:                                              # 必要事件(第一版只用可自動偵測者)
    - id: steeringResponse
      after: { event: faultInjected, itemId: steeringFail }           # 計時起點
      expect: { any: [ telegraphChange, thrusterOrder, steeringModeChange, acknowledge ] }   # 任一即算「首個相關動作」
      within_s: ${params:emergency.steeringFailure.firstAction_s}
      penalty: 20                                        # 逾時或未發生
      latePenaltyPer_s: 0.5                              # 選填:逾時每秒再扣(上限 penalty)
    - id: notifyEcr
      after: { event: faultInjected, itemId: steeringFail }
      expect: { acknowledge: { kind: notifyEcr } }       # 教官按「已通知機控室」
      within_s: ${params:emergency.steeringFailure.notifyEcr_s}
      penalty: 10
    - id: vtsReport
      after: { event: reportPointPassed, waypoint: WP3 }
      expect: { acknowledge: { kind: report } }
      within_s: 120
      penalty: 5
  scoring:
    start: 100
    passMark: ${params:assessment.passMark}
    appliesTo: { phase: any }                            # 保留:依階段加權(第一版不實作)
```

`expect` 的可偵測事件(第 8.3 節第 5 類「第一版只能用可自動偵測的」):

| `expect` 項 | 偵測來源 |
|---|---|
| `rudderOrder` | `input` 指令 `rudder`(舵令變化 ≥ 1°;自動舵產生的舵令不算) |
| `telegraphChange` / `rpmOrder` | 指令 `telegraph` / `rpm` |
| `thrusterOrder` | 指令 `thruster`(\|Δ\| ≥ 0.05) |
| `steeringModeChange` | 指令 `autopilot`(enabled 切換)— 第一版「切換 NFU/應急操舵」以自動舵關閉或 `acknowledge {kind: steeringMode}` 代表(操舵模式選擇器尚未在契約中,第 10 節開放問題) |
| `autopilotOn` / `autopilotOff` | 指令 `autopilot` |
| `acknowledge: {kind}` | 教官/帶領者指令 `acknowledge`(第 5.2 節);`kind`: verbal \| notifyEcr \| callMaster \| report \| checklist \| steeringMode \| anchorReady \| other |
| `event: {kind, …}` | 任一核心事件(例如 `faultCleared`、`cpaClear`) |

### 1.4 `briefing`

```yaml
briefing:
  text: |
    麥寮進港,引水人在船。預計 WP2 後舵機可能異常……(大字顯示於簡化教官模式)
  attachments:
    - { title: 航程計畫, file: docs/passage-plan.pdf }
    - { title: 引水卡, file: docs/pilot-card.pdf }
    - { title: 1.39 單元:自動化與監控, file: docs/hf-automation.pdf }
  roles:                                   # 第 9.1 節「需人員扮演的角色與最少人數」
    - { role: pilot, playedBy: leader, required: true }
    - { role: ecr, playedBy: voiceScript }
  minParticipants: 3
```

相容:`scenario.ts` 目前讀 `briefing` 為字串;schema 定義為 `string | object`,字串等同 `{text}`。

### 1.5 `environment.visibility_nm`

新增選填 `visibility_nm`(number,> 0;未給 = 無限制)。核心保存、漸變、廣播(`state.visibility_nm`)、`setEnvironment` 指令與時間表動作可改;無物理效果。

### 1.6 schema 變更清單

- `scenario.schema.json`:`properties` 新增 `timeline`(array of `$defs.timelineItem`)、`route`(`$defs.route`)、`zones`(array of `$defs.zone`)、`briefing`(`oneOf [string, $defs.briefing]`);`environment.visibility_nm`;`assessment` 擴充(第 1.3 節);`$defs` 新增 `triggerCondition`、`timelineAction`、`timelineItem`、`route`、`routeWaypoint`、`zone`、`paramRef`(`{"type":"string","pattern":"^\\$\\{params:[A-Za-z0-9_.\\-]+\\}$"}`)與 `numberOrParam`(`oneOf [number, paramRef]`)。凡可引用參數的數值欄位型別改為 `numberOrParam`(`minCpa_nm`、門檻、速限、`xtdLimit_m` 等)。
- schema `examples[0]` 增補一段 `timeline`、`route`、`assessment.hardFail`,供 `ScenarioTests` 的 schema 範例測試(現有測試只檢查 title 與 example 可解析,新範例必須能被 C# 載入)。

## 2. 公司程序參數集

### 2.1 檔案 `data/parameters/company-procedures.yaml`

```yaml
# 公司程序參數集(規劃書第 8.3、9.1 節 R14);schema:src/Contracts/parameter-set.schema.json
# 由訓練負責人(訪船船長)依核准的 SMS 文件編輯;修改後需 bump version 並重新驗證引用情境(第 9.1 節)。
id: simosa-company-procedures
version: "2026.10.1"                 # 版次字串(年.月.序);紀錄標頭與講評報告顯示
effectiveFrom: 2026-10-01
approvedBy: 訪船船長 ○○○
approvedOn: 2026-09-28
notes: 首版;數值為規劃階段假設值,待公司 SMS 文件核對(附錄 A)
sources:                              # 程序文件索引;parameters[].source 引用此處的鍵
  DWO-007: { title: 船長常規命令(Master's Standing Orders), revision: "3", approvedBy: 船長, date: 2026-06-01 }
  SMS-NAV-012: { title: 航行程序 — UKC 政策, revision: "2", date: 2025-11-15 }
  SMS-EMG-004: { title: 緊急程序 — 舵機故障, revision: "1", date: 2024-03-01 }
  SMS-EMG-005: { title: 緊急程序 — 全船失電, revision: "1", date: 2024-03-01 }
  PORT-MLW: { title: 麥寮港港口規定(港方), revision: "2025", date: 2025-01-01, external: true }
  PORT-KHH: { title: 高雄港港口規定(港方), revision: "2025", date: 2025-01-01, external: true }
  BRM-TRN-001: { title: BRM 訓練程序(評估準則), revision: "draft", date: 2026-10-01 }
parameters:                           # 每個 source 都必須列於 sources(ParameterSet.Validate 否則拒絕載入);以 data/parameters/company-procedures.yaml 為準
  navigation.cpa.openSea_nm:      { value: 1.0,  unit: nm,  source: DWO-007, clause: "4.2", description: 開闊海最小 CPA }
  navigation.cpa.coastal_nm:      { value: 0.5,  unit: nm,  source: DWO-007, clause: "4.2" }
  navigation.cpa.hardFail_nm:     { value: 0.2,  unit: nm,  source: BRM-TRN-001, clause: "5.1", description: 評估硬性失敗門檻 }
  navigation.cpa.channel_nm:      { value: 0.05, unit: nm,  source: DWO-007, clause: "4.2", description: 狹窄航道內最小通過距離(船中心線) }
  navigation.cpa.channelHardFail_nm: { value: 0.025, unit: nm, source: BRM-TRN-001, clause: "5.1" }
  navigation.bcr.min_nm:          { value: 1.0,  unit: nm,  source: DWO-007, clause: "4.3" }
  navigation.callMaster.cpa_nm:   { value: 1.0,  unit: nm,  source: DWO-007, clause: "6.1", description: CPA 低於此值須呼叫船長 }
  navigation.encounter.detectionRange_nm:        { value: 6.0, unit: nm, source: BRM-TRN-001, clause: "5.2" }
  navigation.encounter.substantialAlteration_deg: { value: 30,  unit: deg, source: BRM-TRN-001, clause: "5.2" }
  navigation.ukc.openSea_fraction: { value: 0.20, unit: fractionOfDraft, source: SMS-NAV-012, clause: "3.1" }
  navigation.ukc.channel_fraction: { value: 0.10, unit: fractionOfDraft, source: SMS-NAV-012, clause: "3.2" }
  navigation.ukc.hardFail_fraction:{ value: 0.05, unit: fractionOfDraft, source: BRM-TRN-001, clause: "5.1" }
  navigation.ukc.minimum_m:       { value: 1.0,  unit: m,   source: SMS-NAV-012, clause: "3.3" }
  navigation.xtd.channel_m:       { value: 50,   unit: m,   source: SMS-NAV-012, clause: "4.1", description: 航道走廊半寬 }
  navigation.xtd.coastal_m:       { value: 300,  unit: m,   source: SMS-NAV-012, clause: "4.1" }
  manoeuvring.rotLimit_degPerMin: { value: 30,   unit: degPerMin, source: DWO-007, clause: "5.4" }
  manoeuvring.berthingApproach_mps: { value: 0.15, unit: mps, source: SMS-NAV-012, clause: "6.2", description: 碰墊設計值 0.10–0.20 m/s }
  port.mailiao.speedLimit_kn:     { value: 8,    unit: kn,  source: PORT-MLW, clause: "第 12 條" }
  port.mailiao.innerSpeedLimit_kn:{ value: 5,    unit: kn,  source: PORT-MLW, clause: "第 12 條" }
  port.kaohsiung.speedLimit_kn:   { value: 8,    unit: kn,  source: PORT-KHH, clause: "" }
  emergency.steeringFailure.firstAction_s: { value: 60,  unit: s, source: SMS-EMG-004, clause: "2.1", description: 故障注入到首個相關動作 }
  emergency.steeringFailure.notifyEcr_s:   { value: 120, unit: s, source: SMS-EMG-004, clause: "2.3" }
  emergency.steeringFailure.emergencySteering_s: { value: 300, unit: s, source: SMS-EMG-004, clause: "3.1" }
  emergency.blackout.anchorReady_s: { value: 180, unit: s, source: SMS-EMG-005, clause: "" }
  assessment.passMark:            { value: 70,   unit: score, source: BRM-TRN-001, clause: "6" }
```

規則:
- 鍵名 `段.群.名_單位後綴`,小寫 camelCase,以 `.` 分層;**單位後綴**(`_nm`、`_m`、`_s`、`_kn`、`_deg`、`_degPerMin`、`_mps`、`_fraction`)必須與 `unit` 一致(載入時檢查)。
- 每個參數必填 `value`(number \| boolean \| string)、`unit`、`source`(需存在於 `sources`);選填 `clause`、`description`、`min`/`max`(合理範圍,載入時檢查)。
- 參數集為單一檔案;日後各船差異以 `overrides: { FSB2: { key: {...} } }` 表示(schema 預留,第一版不解析)。

### 2.2 `src/Contracts/parameter-set.schema.json`(摘要)

```json
{
  "$id": "https://simosa-brm/contracts/parameter-set.schema.json",
  "title": "ParameterSet",
  "type": "object",
  "required": ["id", "version", "sources", "parameters"],
  "properties": {
    "id": { "type": "string" },
    "version": { "type": "string", "pattern": "^\\d{4}\\.\\d{1,2}\\.\\d+$" },
    "effectiveFrom": { "type": "string", "format": "date" },
    "approvedBy": { "type": "string" },
    "approvedOn": { "type": "string", "format": "date" },
    "notes": { "type": "string" },
    "sources": { "type": "object", "additionalProperties": { "$ref": "#/$defs/source" } },
    "parameters": { "type": "object", "propertyNames": { "pattern": "^[a-z][A-Za-z0-9]*(\\.[a-z][A-Za-z0-9]*)+(_[A-Za-z]+)?$" }, "additionalProperties": { "$ref": "#/$defs/parameter" } },
    "overrides": { "type": "object", "description": "保留:依船舶 ID 覆寫(第一版不解析)" }
  },
  "$defs": {
    "source": { "type": "object", "required": ["title", "revision"], "properties": { "title": {"type":"string"}, "revision": {"type":"string"}, "approvedBy": {"type":"string"}, "date": {"type":"string","format":"date"}, "external": {"type":"boolean"} } },
    "parameter": { "type": "object", "required": ["value", "unit", "source"], "properties": {
      "value": { "type": ["number", "boolean", "string"] },
      "unit": { "type": "string", "enum": ["nm", "m", "s", "min", "kn", "mps", "deg", "degPerMin", "fractionOfDraft", "score", "count", "bool", "text"] },
      "source": { "type": "string" }, "clause": { "type": "string" }, "description": { "type": "string" },
      "min": { "type": "number" }, "max": { "type": "number" } } }
  }
}
```

### 2.3 引用語法與解析

- 情境中任何數值欄位可寫整個字串 `${params:<key>}`(不支援字串內嵌、不支援運算;`bands[].fail: 2x` 這類「warn 的倍數」是評估規則自己的語法,不是參數引用)。
- 解析時機:**載入情境時**(`ScenarioLoader.Load(path, parameterSet)`),在 YAML 反序列化之前以通用節點樹替換:YamlDotNet → `object` 圖 → 走訪所有字串,符合 `^\$\{params:([A-Za-z0-9_.\-]+)\}$` 者以參數 `value` 取代(記錄 `(欄位路徑, 鍵, 值, unit, source, revision)` 到 `ResolvedParameters` 清單)→ 轉 JSON → `ContractJson.Deserialize<Scenario>`。
- 錯誤(載入失敗、`InvalidDataException`):鍵不存在;參數 `unit` 與欄位後綴不符(例如 `minCpa_nm` 引用 `unit: m`);引用出現在不允許的位置(`id`、`ship.id`);參數集檔案不存在或 schema 不符;`sources` 缺引用的文件。
- 未使用參數集也能載入**沒有** `${params:…}` 的情境(與現況相同);有引用但未提供參數集 → 錯誤並提示 `--parameters`。
- 解析後的情境(數值已代入)存入紀錄 `header.scenario`,`header.parameterSet` 記錄 `{id, version, path, sha256, resolved: [{path, key, value, unit, source, revision}]}`,重播不需要參數集檔案。
- Python 參考實作:`simosa_brm/scenario.py` 加同樣的替換(字典走訪),未解析鍵同樣丟 `ValueError`;其餘評估欄位忽略。

### 2.4 載入時的靜態檢查(第 9.1 節「自動檢查」)

`ScenarioValidator.Check(scenario, ship)` 回傳 `IReadOnlyList<ScenarioIssue>`(`Severity: error | warning`、`Path`、`Message`),主控程序啟動與 `loadScenario` 時印出,`--validate` 旗標只檢查不執行:
- error:時間表 `id` 重複;`when` 引用不存在的 `itemId`/`zone`/`waypoint`/`target`;動作名稱不在允許清單;`route` 航點 < 2 或相鄰航點重合;`zones` 多邊形 < 3 點或自交;`bands[].metric` 不存在;`required[].after` 引用不存在的事件來源。
- error:硬性失敗門檻在**初始狀態**已成立(例如初始 UKC 已低於 `ukc` 硬性門檻、初始位置在 `noGo` 內)。
- warning:時間表項目在初始狀態(t = 0)條件已成立(非 `time` 型);`time` 觸發晚於情境預估長度(`briefing.duration_s` 或 2 小時);`repeat: always` 且無 `cooldown_s`;`voiceScript.file` 不存在於情境包;只記錄的故障名稱(提示儀器端需自行反應)。

### 2.5 版次顯示

- 狀態廣播 `scenario.parameterSetVersion`(第 5.1 節);教官站標題列與簡化教官模式簡報畫面顯示;講評 `summary.parameterSet`。
- 主控程序啟動列印 `參數集 simosa-company-procedures 2026.10.1(sha256 …)`。

## 3. 核心執行(SimCore)

### 3.1 新增/修改檔案清單

| 檔案 | 內容 |
|---|---|
| `src/SimCore/Scenario/ScenarioTimeline.cs` *(新)* | `TimelineItem`、`TriggerCondition`(遞迴 record:`Type`、`All`、`Any`、`Not`、各 leaf 欄位)、`TimelineAction`(以 `Kind` + 各動作的 payload 類別:`InjectFaultAction`、`SetEnvironmentAction`、`VoiceScriptAction`…)、`RepeatSpec`、enum `TimelineVisibility` |
| `src/SimCore/Scenario/ScenarioRoute.cs` *(新)* | `ScenarioRoute`、`RouteWaypoint`、`ScenarioZone`、enum `ZoneType`、`UkcLimit`(`Metres` / `FractionOfDraft`) |
| `src/SimCore/Scenario/ScenarioAssessment.cs` *(自 ScenarioTarget.cs 搬出並擴充)* | `ScenarioAssessment`(保留 `MinCpaNm`)、`EncounterSpec`、`UkcSpec`、`ManoeuvringSpec`、`HardFailRule`、`BandRule`、`RequiredEvent`、`ExpectSpec`、`ScoringSpec` |
| `src/SimCore/Scenario/ScenarioBriefing.cs` *(新)* | `ScenarioBriefing`、`BriefingAttachment`、`BriefingRole`;YAML 字串/物件二擇一的轉換器 |
| `src/SimCore/Scenario/Scenario.cs` | 新增屬性 `Timeline`、`Route`、`Zones`、`Briefing`;`Environment.VisibilityNm`;`Validate()` 呼叫 `ValidateTimeline/Route/Zones/Assessment` |
| `src/SimCore/Scenario/ScenarioLoader.cs` | `Load(path, ParameterSet? parameters)`、`FromYaml(yaml, parameters)`:節點樹替換(第 2.3 節);`LastResolvedParameters` |
| `src/SimCore/Scenario/ScenarioValidator.cs` *(新)* | 第 2.4 節靜態檢查 |
| `src/SimCore/Parameters/ParameterSet.cs` *(新)* | `ParameterSet`、`ParameterSource`、`ParameterEntry`、`ParameterSetLoader.Load(path)`(YamlDotNet + 單位/範圍/來源檢查 + 檔案 sha256)、`ParameterRef` record |
| `src/SimCore/Parameters/ParameterResolver.cs` *(新)* | `Resolve(object yamlGraph, ParameterSet, out List<ParameterRef>)`、`UnitSuffixMatches(fieldName, unit)` |
| `src/SimCore/Engine/SimEvent.cs` *(新)* | `SimEvent` record + `SimEventKinds` 常數(第 3.2 節) |
| `src/SimCore/Traffic/TrafficEvent.cs` | `TrafficEvent : SimEvent`(改為衍生;`TrafficEventKinds` 不變) |
| `src/SimCore/Scenario/TimelineEngine.cs` *(新)* | 執行期:`TimelineEngine(Scenario, ITimelineHost)`;`Evaluate(tick)`;`TriggerManual(id, force, tick)`;`CreateSnapshot()/Restore()`;`WriteHash(Span<byte>)`;`ItemStates` |
| `src/SimCore/Scenario/EnvironmentRamp.cs` *(新)* | `EnvironmentRamp` record(`Channel`、`StartTick`、`EndTick`、`From`、`To`)與 `RampSet.Apply(tick, EnvironmentConditions)` |
| `src/SimCore/Scenario/TimelineSnapshot.cs` *(新)* | `TimelineSnapshot`、`TimelineItemState`、`PendingAction` |
| `src/SimCore/Assessment/RouteTracker.cs` *(新)* | 航段選擇、XTD/沿航跡距離、航點通過、速限(第 4.2 節) |
| `src/SimCore/Assessment/ZoneTracker.cs` *(新)* | 多邊形內外、進出事件、區域速限/UKC 門檻/水深覆寫 |
| `src/SimCore/Assessment/MetricsCollector.cs` *(新)* | 第 4.5 節各類累計器(每秒) |
| `src/SimCore/Assessment/EncounterTracker.cs` *(新)* | 每目標會遇紀錄(首次偵測、首次行動、航向/速度改變、min CPA/TCPA/BCR) |
| `src/SimCore/Assessment/FaultResponseTracker.cs` *(新)* | 故障注入 → 首個相關動作/確認/通知時間戳 |
| `src/SimCore/Assessment/AssessmentEngine.cs` *(新)* | 硬性失敗、容許帶、必要事件、評分;`BuildSummary()`;`CreateSnapshot()/Restore()` |
| `src/SimCore/Assessment/AssessmentSummary.cs` *(新)* | 不可變 record(第 4.6 節 JSON) |
| `src/SimCore/Engine/SimulationEngine.cs` | 新事件 `EventRaised(SimEvent)`;屬性 `Timeline`、`Assessment`、`ParameterSet`、`Phase`、`VisibilityNm`、`LatestSummary`;`Apply` 新指令;`ApplyInternal`;`Advance` 加評估步;`UpdateHash` 追加;快照/還原;`BuildState` 加 `scenario`/`route`/`visibility_nm` |
| `src/SimCore/Engine/EngineSnapshot.cs` | `Version = 2`;新增 `Timeline`、`Assessment`、`ParameterSetVersion`、`VisibilityNm`、`Phase`(皆可 null) |
| `src/SimCore/Contracts/SimCommand.cs` | `SimCommandType` 新增 `TriggerTimeline`、`Acknowledge`、`GetAssessment`、`EndExercise`;便利建構 |
| `src/SimCore/Contracts/OwnShipState.cs` | `Scenario`(`ScenarioStatus`)、`Route`(`RouteStatus`)、`VisibilityNm` |
| `src/SimCore/Environment/EnvironmentConditions.cs` | `VisibilityNm`(double?,無物理效果) |
| `src/SimCore/Recording/RecordLines.cs` | `RecordEvent.Event` 型別改 `SimEvent`;新增 `RecordSummary`(`kind: summary`);`RecordHeader.ParameterSet`、`Version = 2` |
| `src/SimCore/Recording/RecordWriter.cs` | 訂閱 `EventRaised` 取代 `TrafficEventRaised`;`Close()` 前寫 `summary` 行;`WriteSummaryNow()`(endExercise 時) |
| `src/SimCore/Recording/RecordReader.cs` | `RecordFile.Summary`;`Events` 型別 `SimEvent` |
| `src/SimCore/Recording/Replayer.cs` | `ReplayResult.EventsRecorded/EventsReplayed`、`EventSequenceMatches`(kind+tick+itemId+targetId 序列比對)、`SummaryMatches` |
| `src/SimCore.Host/HostOptions.cs`、`Program.cs` | `--parameters <path>`、`--validate`;事件/摘要 WebSocket 框;console 事件列印改用 `EventRaised` |
| `src/SimCore.Host/WebSocketStateServer.cs` | `PublishTo(clientId, frame)`;`getAssessment` 查詢回覆(第 5.2 節) |
| `src/Contracts/scenario.schema.json`、`parameter-set.schema.json` *(新)*、`state.schema.json`、`command.schema.json`、`README.md` | 第 1、2、5 節 |
| `src/SimCore/README.md` | 第 4 節紀錄行加 `summary`;新第 8 節「情境引擎」 |

### 3.2 事件泛化:`SimEvent`

```csharp
/// <summary>核心事件(紀錄 event 行、WebSocket event 框;規劃書第 5.3 節衍生事件)。JSON 欄位名與舊 TrafficEvent 相同,新欄位選填。</summary>
public record SimEvent
{
    [JsonPropertyName("kind")]     public required string Kind { get; init; }
    [JsonPropertyName("tick")]     public required long Tick { get; init; }
    [JsonPropertyName("t")]        public required double T { get; init; }
    [JsonPropertyName("source")]   public string Source { get; init; } = "engine";   // traffic | timeline | assessment | instructor | engine
    [JsonPropertyName("itemId")]   public string? ItemId { get; init; }               // 時間表項目 / 規則 id
    [JsonPropertyName("targetId")] public string? TargetId { get; init; }
    [JsonPropertyName("message")]  public string? Message { get; init; }
    [JsonPropertyName("range_nm")] public double? RangeNm { get; init; }
    [JsonPropertyName("cpa_nm")]   public double? CpaNm { get; init; }
    [JsonPropertyName("tcpa_min")] public double? TcpaMin { get; init; }
    [JsonPropertyName("phase")]    public string? Phase { get; init; }                // 事件發生時的情境階段
    [JsonPropertyName("visible")]  public bool Visible { get; init; }                 // 學員站可顯示
    [JsonPropertyName("seq")]      public long Seq { get; init; }                     // 自情境開始的事件序號(重播比對用)
    [JsonPropertyName("data")]     public JsonElement? Data { get; init; }            // 動作/規則的附加資料(voiceScript 欄位、漸變參數、指標值…)
}
public sealed record TrafficEvent : SimEvent { }   // Source 固定 "traffic";TrafficEventKinds 不變
```

- `RecordEvent.Event` 改為 `SimEvent`;讀回時一律反序列化為 `SimEvent`(無型別鑑別子;`TrafficEvent` 只是建構時的語意標記)。既有測試用 `rec.Events[i].Event.Kind/TargetId` 不受影響;`TrafficEventRaised` 事件保留且仍只發交通事件,新增 `EventRaised` 發所有事件(`RecordWriter` 改訂閱 `EventRaised`,避免交通事件寫兩次)。
- `seq` 自 0 起於 `Initialize()` 歸零;快照保存;重播與原紀錄逐一比對 `(seq, kind, tick, itemId, targetId)`。

新 `kind`(`SimEventKinds`):

| source | kind | 時機 / `data` |
|---|---|---|
| timeline | `timelineTriggered` | 項目觸發(`itemId`;`data: {fireCount, manual, forced}`) |
| timeline | `timelineArmed` / `timelineDisarmed` | |
| timeline | `faultInjected` / `faultCleared` | `data: {fault, args}`;**教官手動 `injectFault` 指令也發**(source = instructor),故評估的 `after: {event: faultInjected}` 對兩者皆有效 |
| timeline / instructor | `environmentChange` | `data: {wind, current, waterDepth, visibility_nm, ramp_s, from, to}`;`environmentRampDone` 於漸變結束 |
| timeline | `voiceScript` | `data: {role, channel, text, file, lang, durationEstimate_s}` |
| timeline | `message` | `data: {to, text, severity}` |
| timeline / instructor | `phaseChanged` | `data: {from, to}` |
| timeline / instructor | `frozen` / `resumed` | 既有 freeze/resume 指令也發(source = instructor) |
| timeline | `autopilotOverride`、`ownShipOverride`、`timeScaleChanged` | |
| timeline / instructor | `exerciseEnded` | `data: {reason}`;之後 summary 固定 |
| engine | `grounding` | 既有 `Grounded` 事件同時發此事件 |
| assessment | `routeLegChanged` | `data: {legIndex, from, to, waypoint}` |
| assessment | `checkpointPassed`、`reportPointPassed` | `data: {waypoint}` |
| assessment | `xtdExceeded` / `xtdRestored` | `data: {xtd_m, limit_m, legIndex}` |
| assessment | `zoneEntered` / `zoneExited` | `data: {zoneId, type}` |
| assessment | `ukcAlarm` / `ukcClear` | `data: {ukc_m, limit_m, zoneId?}` |
| assessment | `speedLimitExceeded` / `speedLimitRestored` | `data: {sog_kn, limit_kn, source: leg\|zone, id}` |
| assessment | `encounterStarted` / `encounterFirstAction` / `encounterClosed` | `targetId`;`data: {range_nm, cpa_nm, tcpa_min, action, headingChange_deg, speedChange_kn}` |
| assessment | `requiredEventMet` / `requiredEventMissed` | `itemId` = 規則 id;`data: {dt_s, within_s, by: kind}` |
| assessment | `hardFail` | `itemId` = 規則 id;`data: {rule, value, limit, paramKey}` |
| assessment | `bandWarn` / `bandFail` | 指標首次進入 warn/fail 帶 |
| instructor | `acknowledge` | `data: {kind, role, note, itemId}` |
| instructor | `timelineManual` | `triggerTimeline` 指令收到(成功與否皆記,`data.ok`) |

### 3.3 執行時序(每步)

`Advance()` 修改後的順序(粗體為新增):

1. 陣風亂數(每秒)— 不變
2. **環境漸變 `Ramps.Apply(Tick)`**(每步線性插值,第 3.5 節;在陣風之後、致動器之前)
3. 自動舵 → 致動器 → RK4 → `Tick++` → 擱淺檢查 — 不變(擱淺時**同時**發 `grounding` SimEvent)
4. 目標船 `_traffic.Step(...)` — 不變(交通事件經 `EventRaised` 轉發)
5. **評估步(`evalTick = Tick % stepsPerSecond == 0`)**:
   1. `RouteTracker.Update` / `ZoneTracker.Update`(航段、XTD、區域進出;產生事件)
   2. `MetricsCollector.Sample(1 s)`(第 4.5 節累計)
   3. `EncounterTracker.Update`、`FaultResponseTracker.Update`
   4. `TimelineEngine.Evaluate(Tick)`:依 YAML 順序評估每個 armed 項目的條件 → 觸發者立刻執行動作(或排入 `delay_s`)→ 執行到期的 `PendingActions`
   5. `AssessmentEngine.Evaluate(Tick)`:硬性失敗、容許帶、必要事件逾時 → 事件;更新 `LatestSummary`(不可變 record,供主控程序回覆查詢)
6. `UpdateHash()`(**追加時間表與事件計數**)→ `Stepped` → 廣播 → 自動快照 — 不變

為什麼每秒而非每步:與目標船觸發(`_evalDivider`)一致,成本低;時間觸發的精度為 1 s(文件化);事件 `tick` 永遠是 50 的倍數(`delay_s` 以秒為單位,四捨五入到秒)。指令觸發的事件(`acknowledge`、`triggerTimeline`、`injectFault`)在 `ProcessPendingCommands` 當下的 tick 發出,可為任意 tick。

評估步的順序固定,且項目依 YAML 順序評估,一個項目的動作所產生的事件在**同一評估步**內可被後面的項目看見(`event` 條件),但不會被前面的項目看見(下一秒才會)— 文件化,避免循環觸發歧義。單一評估步內的觸發鏈上限 16(超過則記 `message` 警告並停止,防止 `always` 互相觸發的無限迴圈)。

### 3.4 動作執行與指令的關係

- `ApplyInternal(SimCommand cmd, string source, string? itemId)`:與 `Apply` 共用 switch,但 (a) 不呼叫 `CommandApplied`(不寫 `input` 行),(b) 事件 `source = "timeline"`。
- 教官指令(`injectFault`、`setEnvironment`、`freeze`…)照舊走 `Apply`,寫 `input` 行,並**也**產生對應 SimEvent(`source = "instructor"`),讓評估規則與教官站事件流對手動與自動注入一視同仁。
- 重播:`input` 行只含教官/學員指令;時間表動作由重播引擎的 `TimelineEngine` 在相同 tick 重新產生 → 事件序列與雜湊一致(測試 7.1 節)。

### 3.5 環境漸變演算法

`EnvironmentRamp { Channel, StartTick, EndTick, From, To }`,`Channel ∈ {windSpeed, windDir, gustiness, currentSet, currentDrift, waterDepth, visibility}`,每個 channel 最多一個進行中的漸變(新的覆蓋舊的,以**當時插值**為新的 From)。

每步(`Tick` 為推進前的 tick):
```
f = clamp((Tick − StartTick) / (EndTick − StartTick), 0, 1)
標量:value = From + (To − From) · f
角度(windDir、currentSet):d = wrap(To − From) 到 (−π, π];value = normalize(From + d · f)   // 最短弧
Tick ≥ EndTick:value = To,移除漸變,發 environmentRampDone
```
- `ramp_s = 0` → 立即(與現行 `setEnvironment` 相同,不建立 ramp)。
- 漸變只用整數 tick 與 double 運算,無亂數;進快照(`TimelineSnapshot.Ramps`);還原後接續。
- 風速漸變與陣風係數相乘(`WindEffectiveSpeedMps` 既有邏輯);水深漸變等同潮汐簡化(E18 潮比預報低)。
- `setEnvironment` **指令**也接受 `ramp_s`(教官站環境面板加欄位),經 `Apply` 走同一路徑。

### 3.6 確定性、快照與雜湊

- `TimelineSnapshot { Phase, Seq(事件序號), Items: [{Id, Armed, FireCount, LastFiredTick, SatisfiedSinceTick(holdFor 用,−1 = 未成立)}], PendingActions: [{ItemId, ActionIndex, ExecuteTick}], Ramps: [...] , EventCounts: {kind → count}(供 event 條件與 since 用), LastEventTick: {kind[:itemId|targetId] → tick} }`。
- `AssessmentSnapshot`:`MetricsCollector`、`RouteTracker`(`LegIndex`、`PassedWaypoints`、`XtdExceeded`)、`ZoneTracker`(`Inside` 集合)、`EncounterTracker`(每目標紀錄)、`FaultResponseTracker`(每故障紀錄)、`AssessmentEngine`(`HardFails`、`BandStates`、`RequiredStates`、`Ended`)。所有數值為 double/long,JSON 往返位元一致(與 `EngineSnapshot` 相同原則)。
- `EngineSnapshot.Version = 2`;舊快照(Version 1)還原時 `Timeline`/`Assessment` 為 null → 沿用引擎目前狀態(與 `Traffic` 相同)。
- `UpdateHash()` 在目標船位元組之後追加(**只在 `Scenario.Timeline.Count > 0 || Route != null || Zones.Count > 0 || Assessment 有規則` 時**,確保 E05 等無時間表情境的雜湊與既有黃金值一致):
  ```
  int64 Seq
  int64 phaseIndex(階段字串在情境出現順序的索引;normal = 0)
  每個時間表項目:int64 FireCount, int64 LastFiredTick, int64 SatisfiedSinceTick
  每個進行中漸變:int64 Channel, int64 EndTick, double To
  int64 RouteTracker.LegIndex
  ```
  評估累計值(XTD 平均等)**不**進雜湊(衍生量;以 summary 比對測試驗證),避免浮點累加順序差異放大。
- 時間表不使用引擎亂數(與交通模組相同原則)。若日後要「時間抖動」(`jitter_s`),用獨立的 `DeterministicRandom(seed ^ 0x54494D45)` 並進快照;第一版不做。
- 凍結:`Frozen` 時 `Advance` 不執行 → 時間表與評估一併暫停;`delay_s` 以模擬時間計。
- `Reset`/`LoadScenario` → `Initialize()` 重建 `TimelineEngine`、`AssessmentEngine`、`Seq = 0`。

## 4. 自動量測指標(第 8.3 節第 1 至 5 類)

### 4.1 總則

- 取樣:每秒一次(評估步),以該 tick 的狀態;時間積分以 1 s 權重。最小/最大值另於**每步**更新者:min CPA/range(交通模組已每步算)、擱淺;其餘每秒即可(文件化:最大 ROT、最大舵角的解析度為 1 s;教官站本地推導為 25 Hz,兩者可能差 ≤ 1 s 的峰值)。
- 凍結期間不取樣;時間倒退(restore)時累計器由快照還原,不會重複累計。
- 全部數值 SI 內部計算,輸出單位與欄位後綴一致(`_nm`、`_m`、`_s`、`_kn`、`_deg`、`_degPerMin`)。

### 4.2 航跡保持(XTD)演算法 — `RouteTracker`

航點轉 ENU `W[0..n−1]`;航段 `i`:`A = W[i]`,`B = W[i+1]`,`d = B − A`,`L = |d|`,單位切線 `u = d/L`,**右舷法線** `nR = (u.y, −u.x)`(ENU 下 x 東、y 北;航向 u 的右側)。自船船舯 `P`:
```
s   = (P − A) · u            // 沿航跡距離(m),0 ≤ s ≤ L 在航段內
xtd = (P − A) · nR           // 航跡偏差(m),正 = 偏右(starboard),負 = 偏左
distToWp = |B − P|;brgToWp = atan2(B − P)
```
航段切換(每秒評估):
- 進入條件:`LegIndex = startLeg`,第一次評估時若 `s < 0`(尚未到起點)仍以航段 0 計算 XTD(延長線)。
- 前進:當 `s ≥ L`(通過 B 的正橫,即 ECDIS 的 wheel-over 簡化)**或** `|B − P| ≤ arrivalRadius_m`(預設 `max(2·LOA, 150 m)`)→ `LegIndex++`,發 `routeLegChanged`;B 若 `checkpoint` → `checkpointPassed`;`report` → `reportPointPassed`;`passedWaypoints` 記錄 `{name, tick, xtd_m, distanceToWp_m}`(第 8.3 節「通過標誌的距離」)。
- 不回退;最後一段通過後 `RouteComplete = true`,XTD 停止累計(`routeCompleted` 事件)。
- 最後一段的「通過」只用 `arrivalRadius_m`(避免 overshoot 判為完成)。
- 邊界:`L < 1 m` 的航段在載入時為 error;自船離航線極遠(> 5 nm)時仍計算但 `summary.track.offRoute = true`。

指標:
| metric | 定義 |
|---|---|
| `track.meanAbsXtd_m` | Σ\|xtd\|·1 s / 取樣秒數(只計航線完成前) |
| `track.maxXtd_m`、`track.maxXtdAt_s`、`track.maxXtdLeg` | |
| `track.timeOutsideCorridor_s`、`track.corridorExcursions` | \|xtd\| > 該航段 `xtdLimit_m` 的秒數與次數(進入 → 事件 `xtdExceeded`;回到 ≤ 0.8·limit 才 `xtdRestored`,滯後避免抖動) |
| `track.fractionOutsideCorridor` | 上項 / 航線取樣秒數 |
| `track.minDistanceToCorridorEdge_m` | min(limit − \|xtd\|),可為負 |
| `track.minDistanceToNoGo_m` | 至 `noGo` 區多邊形邊的最小距離(點到線段距離,每秒) |
| `track.meanHeadingDeviation_deg` | 平均 \|heading − 航段航向\|(只計 \|xtd\| ≤ limit 時,避免修正動作灌水) |
| `track.waypoints[]` | `{name, passedAt_s, xtd_m, distance_m}` |

### 4.3 UKC — `ZoneTracker` + 引擎 `UnderKeelClearance()`

- 門檻 `limit_m = max(assessment.ukc.min 換算, navigation.ukc.minimum_m 若引用)`;`fractionOfDraft` × `Ship.GetLoading(Loading).MaxDraft_m`;自船在 `ukcPolicy` 區內時用該區門檻。
- 多邊形內外:ray casting(偶奇規則),點在邊上視為內。
- 指標:`ukc.min_m`、`ukc.minAt_s`、`ukc.minSquat_m`(min UKC 當下的 squat)、`ukc.timeBelowLimit_s`、`ukc.excursions`(進入事件 `ukcAlarm`,離開滯後 +0.2 m)、`ukc.groundings`(`grounding` 事件數)、`ukc.limitSource`(參數鍵)。

### 4.4 避碰 — `EncounterTracker`(每目標)

交通模組已算每步 `EncounterGeometry` 與 `MinCpaNm`/`MinRangeNm`;本追蹤器每秒讀取:
- **會遇開始**(`encounterStarted`):目標 active 且 `range ≤ detectionRange_nm` 且 `tcpa > 0` 且 `cpa < 2 × minCpa_nm`(可被 `encounter.cpaInterest_nm` 覆寫);記錄 `detectedAt_s`、當時自船 `heading0`、`stw0`。跟隨自船的引水船/拖船(`EscortsOwnShip`)不列入。
- **首次行動**(`encounterFirstAction`):會遇開始後第一個學員指令(`rudder` ≥ 5°、`telegraph`/`rpm`、`autopilot` 改航向 ≥ 5°)→ `firstActionAt_s`、`timeToAction_s`。
- **航向/速度改變幅度**:會遇期間 `max|wrap(heading − heading0)|` 與 `max|stw − stw0|`;「大幅」= ≥ `substantialCourseChange_deg` 或 ≥ `substantialSpeedChange_kn`。
- **會遇結束**(`encounterClosed`):`tcpa < 0` 且 `range` 增加連續 30 s,或目標移除。
- 輸出每目標:`{targetId, name, situation(交通模組的 colreg 判定字串), detectedAt_s, firstActionAt_s, timeToAction_s, firstAction, minCpa_nm, minCpaAt_s, tcpaAtDetection_min, minRange_nm, bcrAtDetection_nm, bcrMin_nm, courseChange_deg, speedChange_kn, substantial: bool, cpaAlarmTime_s(cpaAlarm→cpaClear 累計)}`;彙總 `collisionAvoidance.minCpa_nm`(排除 escorts)、`collisionAvoidance.encounters`、`collisionAvoidance.collisions`。

### 4.5 操縱控制 — `MetricsCollector`

| metric | 定義 |
|---|---|
| `manoeuvring.rudderDistribution_s` | `{"0-5":…, "5-10":…, "10-20":…, "20-35":…, ">35":…}` 以 \|rudder\| 每秒分箱(FSB1 Schilling 70° 時 `>35` 有意義) |
| `manoeuvring.hardOverCount`、`hardOverTime_s` | \|rudder\| ≥ `hardOver_deg − 0.5` 的進入次數與秒數 |
| `manoeuvring.rudderOrders` | `rudder` 指令次數(\|Δ\| ≥ 1°;自動舵不計;以 1 s 視窗合併:視窗自計入的那道舵令起算、不隨後續舵令延長,連續拖曳舵輪每 1 s 最多計 1 次)— 從 `CommandApplied` 計 |
| `manoeuvring.maxRot_degPerMin`、`maxRotAt_s`、`timeRotOverLimit_s`、`rotExcursions` | |
| `manoeuvring.telegraphChanges`、`telegraphOrders[]` | 車令次數與 `{order, from_s, to_s, duration_s}` 各段;`rpmOrders` 次數 |
| `manoeuvring.thrusterOrders`、`thrusterTime_s` | |
| `manoeuvring.timeOverSpeedLimit_s`、`maxOverSpeed_kn`、`speedLimitSource` | 航段/區域速限(取較嚴者),無速限時 0 |
| `manoeuvring.autopilotTime_s`、`autopilotSwitches` | |
| `general.duration_s`、`frozen_s`、`distance_nm`、`avgSog_kn`、`maxSog_kn` | 與 `debrief.ts` 同義,方便交叉驗證 |

### 4.6 故障應變 — `FaultResponseTracker`

每個 `faultInjected` 事件(時間表或教官)建立紀錄 `{fault, itemId, injectedAt_s, source, firstAction: {kind, t_s, dt_s}?, acknowledgements: [{kind, t_s, dt_s, role}], cleared_s?, withinFirstAction: bool?, withinNotifyEcr: bool?}`;「相關動作」取 `assessment.required[]` 中 `after` 指向該故障的 `expect` 定義;若無規則,預設任一 `telegraph/rpm/thruster/rudder/autopilot` 指令。`acknowledge` 指令可帶 `itemId`(明確對應)或不帶(對應最近 120 s 內注入且未確認的故障)。

### 4.7 評分 — `AssessmentEngine`

- 硬性失敗:`grounding`、`collision`(旗標/事件)、`cpa`(任一非 escort 目標 `MinCpaNm < lessThan` 且曾 `tcpa > 0`)、`ukc`、`zoneEntered`、`event {kind}`(通用);成立即發 `hardFail`(一次),`passed = false`。
- 容許帶(`bands`):`direction: above`(預設,值越大越差)或 `below`;`fail: 2x` 表示 `warn × 2`;分數 = `start − Σ penalty`,每條規則一次(進入 fail 帶取 `failPenalty`,否則 warn 帶取 `penalty`);下限 0。
- 必要事件:`after` 事件發生後開始計時;`expect` 任一達成 → `requiredEventMet`(`dt_s`);逾 `within_s` 未達成 → `requiredEventMissed` + `penalty`(若後來達成,改記 `late = true`,扣 `min(penalty, latePenaltyPer_s × 逾時秒)`);`after` 事件未發生 → 規則 `notApplicable`。
- `passed = !hardFailed && score ≥ passMark`;`endExercise` 或紀錄關閉時定稿(`final = true`)。
- 依 A-I/12,summary 標示 `"advisory": true`(自動評分僅輔助,最終由評估員判定;第 8.3 節)。

### 4.8 `summary` 紀錄行與查詢回覆(JSON 範例)

```json
{"kind":"summary","tick":180000,"summary":{
  "version":1,"advisory":true,"final":true,"scenarioId":"E07_steering_failure","shipId":"FSB1","loading":"full",
  "parameterSet":{"id":"simosa-company-procedures","version":"2026.10.1","sha256":"…"},
  "phases":[{"phase":"normal","from_s":0,"to_s":420},{"phase":"degraded","from_s":420,"to_s":612},{"phase":"emergency","from_s":612,"to_s":3600}],
  "general":{"duration_s":3600,"freezes":2,"distance_nm":5.8,"avgSog_kn":5.9,"maxSog_kn":8.4},
  "collisionAvoidance":{"minCpa_nm":0.62,"encounters":1,"collisions":0,"targets":[{"targetId":"T1","situation":"headOn","detectedAt_s":95,"firstActionAt_s":131,"timeToAction_s":36,"firstAction":"rudder","minCpa_nm":0.62,"minCpaAt_s":402,"tcpaAtDetection_min":14.2,"bcrAtDetection_nm":0.1,"courseChange_deg":32,"speedChange_kn":0,"substantial":true,"cpaAlarmTime_s":0}]},
  "track":{"meanAbsXtd_m":18.4,"maxXtd_m":74,"maxXtdAt_s":655,"maxXtdLeg":1,"timeOutsideCorridor_s":41,"corridorExcursions":1,"fractionOutsideCorridor":0.012,"minDistanceToCorridorEdge_m":-24,"minDistanceToNoGo_m":210,"meanHeadingDeviation_deg":2.1,"waypoints":[{"name":"WP2","passedAt_s":418,"xtd_m":6,"distance_m":22}]},
  "ukc":{"min_m":1.42,"minAt_s":2210,"minSquat_m":0.31,"timeBelowLimit_s":0,"excursions":0,"groundings":0,"limit_m":0.57,"limitSource":"navigation.ukc.channel_fraction"},
  "manoeuvring":{"rudderDistribution_s":{"0-5":2900,"5-10":410,"10-20":200,"20-35":45,">35":0},"hardOverCount":0,"hardOverTime_s":0,"rudderOrders":38,"maxRot_degPerMin":22,"maxRotAt_s":640,"timeRotOverLimit_s":0,"rotExcursions":0,"telegraphChanges":4,"telegraphOrders":[{"order":"HAH","from_s":0,"to_s":455,"duration_s":455},{"order":"SAH","from_s":455,"to_s":612,"duration_s":157}],"rpmOrders":0,"thrusterOrders":3,"thrusterTime_s":140,"timeOverSpeedLimit_s":0,"maxOverSpeed_kn":0,"autopilotTime_s":380,"autopilotSwitches":2},
  "faultResponses":[{"fault":"steeringGear","itemId":"steeringFail","injectedAt_s":420,"source":"timeline","firstAction":{"kind":"telegraph","t_s":455,"dt_s":35},"acknowledgements":[{"kind":"verbal","t_s":431,"dt_s":11,"role":"officer"},{"kind":"notifyEcr","t_s":502,"dt_s":82,"role":"master"}],"cleared_s":1300,"withinFirstAction":true,"withinNotifyEcr":true}],
  "events":{"total":57,"byKind":{"timelineTriggered":4,"faultInjected":1,"cpaAlarm":0,"xtdExceeded":1}},
  "assessment":{"score":85,"passMark":70,"passed":true,"hardFailed":false,"hardFails":[],
    "bands":[{"id":"xtdMax","metric":"track.maxXtd_m","value":74,"warn":50,"fail":100,"state":"warn","penalty":10,"paramKey":"navigation.xtd.channel_m","source":"SMS-NAV-012","revision":"2","clause":"4.1"}],
    "required":[{"id":"steeringResponse","state":"met","dt_s":35,"within_s":60,"by":"telegraph","paramKey":"emergency.steeringFailure.firstAction_s","source":"SMS-EMG-004","revision":"1"},{"id":"notifyEcr","state":"met","dt_s":82,"within_s":120,"by":"acknowledge:notifyEcr"},{"id":"vtsReport","state":"missed","within_s":120,"penalty":5}],
    "parameters":[{"key":"navigation.xtd.channel_m","value":50,"unit":"m","source":"SMS-NAV-012","revision":"2","clause":"4.1"}]}
}}
```

- 寫入時機:`RecordWriter.Close()` 前(`final: true`);`endExercise` 當下也寫一行(之後再 Close 時第二行 `final: true`,內容相同);講評站取最後一行。`exerciseEnded` 之後 `AssessmentEngine.EvaluateRules` 不再重建 summary(`Finalize` 的物件即唯一 final summary;定稿在 `exerciseEnded` 發出之前,其 `events` 計數不含 `exerciseEnded` 本身),時間表在同一評估步內也不再觸發後續項目或執行待執行動作(endExercise 由時間表動作觸發時亦然)。
- 查詢:`getAssessment` 回 `{"type":"assessment","final":false,"summary":{…}}`(第 5.2 節),內容為 `LatestSummary`(每秒更新)。

## 5. 契約變更

### 5.1 `state.schema.json`

新增選填:
```json
"visibility_nm": { "type": "number" },
"scenario": { "type": "object", "required": ["id", "phase"], "properties": {
  "id": {"type":"string"}, "phase": {"type":"string"}, "parameterSetVersion": {"type":"string"},
  "timelineFired": {"type":"integer"}, "timelinePending": {"type":"integer"},
  "hardFailed": {"type":"boolean"}, "score": {"type":"number"}, "ended": {"type":"boolean"} } },
"route": { "type": "object", "properties": {
  "legIndex": {"type":"integer"}, "nextWaypoint": {"type":"string"}, "xtd_m": {"type":"number","description":"右正左負"},
  "xtdLimit_m": {"type":"number"}, "distanceToWaypoint_nm": {"type":"number"}, "bearingToWaypoint_deg": {"type":"number"},
  "legCourse_deg": {"type":"number"}, "speedLimit_kn": {"type":"number"}, "complete": {"type":"boolean"} } }
```
`route` 同時滿足第 7.2 節 conning display 的「航跡偏差(XTD)/下一航點」最低資訊;`faults[]` 既有不變。`ContractsTests` 只檢查 required 欄位,新增皆選填 → 不受影響;Python 參考實作不輸出 `scenario`/`route`。

### 5.2 `command.schema.json`

`type` 列舉新增(最少必要):

| type | value / args | 效果 | 進 `input` 行 |
|---|---|---|---|
| `triggerTimeline` | `args: {id, force?: bool}` | 手動觸發項目(`manual: true` 者或 `force` 跳過條件);不存在/已 once 觸發過 → 事件 `timelineManual {ok:false}` | 是 |
| `acknowledge` | `args: {kind: verbal\|notifyEcr\|callMaster\|report\|checklist\|steeringMode\|anchorReady\|other, itemId?, role?, note?}` | 教官/帶領者記錄「口頭辨識/通知」等可見但核心偵測不到的動作;事件 `acknowledge`;餵給必要事件與故障應變 | 是 |
| `endExercise` | `args: {reason?}` | 凍結 + 定稿 summary | 是 |
| `getAssessment` | — | **查詢**:主控程序直接以 `LatestSummary` 回覆該客戶端 `{"type":"assessment", …}`;不進引擎佇列 | 否 |
| `setEnvironment` *(擴充)* | `args` 加 `visibility_nm`、`ramp_s` | 第 3.5 節 | 是 |
| `injectFault` *(擴充)* | `value: string` 或 `args: {name, args}` | 向後相容 | 是 |

WebSocket 新增推送框(與 state 框同通道,以 `type` 區分;`ws-client.ts` 已把非 state 物件交給 `onMessage`):
- `{"type":"event","event":{…SimEvent}}`:每個事件即時推送給所有客戶端(學員站忽略或只顯示 `visible: true`)。
- `{"type":"assessment","final":bool,"summary":{…}}`:回覆 `getAssessment`;`exerciseEnded` 時主動推送一次。
- `{"type":"ack", …}`:C# 主控程序目前不回 ack(交接紀錄),本設計不改;若實作,格式沿用 Python 伺服器。

### 5.3 紀錄行

| 行 | 變更 |
|---|---|
| `header` | `version: 2`;新增 `parameterSet: {id, version, path, sha256, resolved: [...]}`(無參數集時省略);`scenario` 為解析後的完整情境(含 timeline/route/zones/assessment/briefing) |
| `input` | 不變;新指令型別自然出現 |
| `state` | 不變(新增選填欄位) |
| `event` | `event` 物件為 `SimEvent`(舊欄位不變,新欄位選填) |
| `snapshot` | `EngineSnapshot.Version = 2`,新增欄位 |
| `summary` *(新)* | `{"kind":"summary","tick":…,"summary":{…}}`,footer 之前,計入 sha256 |
| `footer` | 不變;可加 `summaryLines` 計數(選填) |

相容性:
- `RecordReader` 已對未知 kind「保留在雜湊內但忽略」→ 舊讀取器讀新檔不會壞;新讀取器讀舊檔 `Summary = null`。
- `record-replay.ts` 目前對 `event` 行回報「未知的 kind」(錯誤清單,非致命);本設計要求加入 `case 'event'` 與 `case 'summary'`(第 6 節),並把既有「未知 kind」改為計數而非逐行錯誤。
- 既有測試:`ReplayReproducesRecordedHash` 等以 E01 執行,E01 更新後含 timeline → 雜湊值由測試執行時計算而非常數,仍通過;`MmgGoldenTests` 用操縱性測試而非情境,不受影響。**注意**:若任何測試把 E01 的雜湊寫成常數,須改用無時間表的測試情境(`TestData.Scenario()` 建議改為內嵌最小情境,而非讀 E01)。

## 6. 教官站工作清單(`src/Instruments.Web`)

| # | 項目 | 檔案 | 說明 |
|---|---|---|---|
| W1 | 事件框解析 | `src/types/event.ts` *(新)*、`src/lib/sim-event.ts` *(新)*、`net/ws-client.ts` | `isSimEvent()`、`SimEventKind` 聯集、事件文字化(`eventLabel(e)`),含既有交通事件 |
| W2 | 事件流面板 | `components/instructor/event-stream.ts` *(新)* | 時間戳、來源、kind 圖示、訊息;篩選(時間表/交通/評估/教官);點擊 → 重播檢視跳至 |
| W3 | 時間表面板 | `components/instructor/timeline-panel.ts` *(新)*、`lib/scenario.ts` | 由情境 YAML 解析 `timeline[]`(`parseTimeline`),顯示 id/name/條件摘要/狀態(待觸發、已觸發 n 次、已停用);手動觸發鈕(`triggerTimeline`,`manual` 項目醒目;其他需按住「強制」);`acknowledge` 快速鈕列(verbal/notifyEcr/callMaster/report/steeringMode/anchorReady)含 role 選擇;狀態來源:`timelineTriggered` 事件 + `state.scenario.timelineFired/Pending` |
| W4 | 語音腳本播放 | `lib/voice-script.ts` *(新)* | 收到 `voiceScript` 事件:有 `file` 用 `<audio>` 播 `data/scenarios/<id>/<file>`(Vite `import.meta.glob` 建置時打包或以靜態路徑);無檔用 `speechSynthesis`(語言依 `lang`);簡化教官模式自動播放,完整教官站顯示「播放」鈕;播放紀錄進 `SessionRecord.operations`(kind `voiceScript`) |
| W5 | 環境面板漸變 | `components/instructor/environment-panel.ts`、`lib/instructor-command.ts` | `ramp_s` 與 `visibility_nm` 欄位;`setEnvironmentCommand` 擴充 |
| W6 | 講評摘要改用核心 summary | `components/instructor/debrief-summary.ts`、`lib/debrief.ts`、`lib/session-record.ts`、`instructor-app.ts` | `SessionRecord` 新增 `coreSummary: AssessmentSummary \| null`;結束練習時送 `getAssessment`(或 `endExercise` 後接收推送);有核心摘要時優先顯示(含評分、硬性失敗、必要事件、參數集版次與來源文件欄);無(Python 伺服器/離線)時退回 `DebriefAccumulator`;兩者皆有時並列「本地推導」供交叉核對 |
| W7 | 參數集版次顯示 | `instructor-app.ts` 標題列、簡化模式簡報與講評頁 | `state.scenario.parameterSetVersion`;版次與情境檔 `assessment.parameterSet` 不一致時警示 |
| W8 | 重播檢視 | `lib/record-replay.ts`、`components/instructor/replay-view.ts` | 解析 `event`/`summary` 行與 header `parameterSet`;時間軸上畫事件標記(顏色依 source;hardFail/collision/grounding 紅);右側摘要改顯示核心 summary;航跡圖疊 `route` 航點/走廊與 `zones` 多邊形(由 header.scenario) |
| W9 | 情境面板 | `components/instructor/scenario-panel.ts`、`lib/scenario.ts` | 摘要加「時間表 n 項、航線 n 航點、評估規則 n 條、參數引用 n 個」;`briefing` 物件版(附件清單連結、角色與最少人數) |
| W10 | 必要事件倒數 | `timeline-panel.ts` | `faultInjected` 後依 `assessment.required` 顯示倒數(within_s),達成/逾時由事件更新 |
| W11 | 學員站(conning) | `src/components/*` | 顯示 `state.route`(XTD/下一航點,第 7.2 節 MSC.252 最低資訊)與 `visibility_nm`;`visible: true` 的 `message`/`voiceScript` 事件以橫幅顯示(可選) |
| W12 | 測試 | `tests/*.test.ts` | 第 7.2 節 |

## 7. 測試計畫

### 7.1 C# xunit(`tests/SimCore.Tests`)

| 檔案 *(新)* | 案例 |
|---|---|
| `ParameterSetTests.cs` | 載入範例參數集(鍵、單位、來源);缺 `source` 定義 → 例外;單位後綴不符 → 例外;`version` 格式;sha256 穩定 |
| `ParameterResolverTests.cs` | 情境 `${params:…}` 替換後數值正確且 `ResolvedParameters` 含路徑/鍵/來源;未知鍵 → `InvalidDataException` 含鍵名與欄位路徑;無引用時不需參數集;引用在 `id` → 錯誤;Python 與 C# 對同一 YAML 解析結果一致(以 JSON 比對,放在既有的跨語言測試) |
| `TimelineTriggerTests.cs` | 每種 leaf 條件(time/ownDistance/ownHeading/ownSpeed/ukc/xtd/routeLeg/zone/phase/event/fault/telegraph/autopilot)在合成狀態下真/假;`all/any/not` 組合;`holdFor_s`(條件中斷重新計時);`repeat: once/always/every`、`cooldown_s`;`delay_s` 排程;`arm/disarm`;觸發鏈上限 16;`manual` 項目只由指令觸發;`force` |
| `TimelineActionTests.cs` | 每種動作產生正確事件與引擎狀態變化(injectFault → `Faults` 含名稱且 `faultInjected` 事件 `source: timeline`;setEnvironment 立即;targetControl 轉送;voiceScript/message 只記事件;phase;freeze;autopilot;ownShip;endExercise);不允許的動作在 `Validate()` 被拒 |
| `EnvironmentRampTests.cs` | 線性插值中點值;角度最短弧(350° → 010° 經 360°);新漸變覆蓋舊漸變以當時值起算;`ramp_s = 0` 無 ramp;完成事件與移除;快照中途還原後終值一致 |
| `TimelineRecordReplayTests.cs` | E07 情境跑 N 秒(含教官 `acknowledge`、`triggerTimeline`)→ 紀錄 → 重播:`HashMatches`、`EventSequenceMatches`(逐事件 `seq/kind/tick/itemId/targetId`)、`summary` 相同(JSON 字串相等);時間表動作不出現在 `input` 行 |
| `TimelineSnapshotTests.cs` | 快照 → 還原 → 續跑 的事件序列與雜湊 = 連續跑(含待執行 `delay_s` 動作、進行中漸變、holdFor 中的項目);Version 1 快照(無 Timeline)可還原 |
| `HashCompatibilityTests.cs` | 無時間表/航線/區域/規則的情境,新版雜湊 = 舊版(以 `EngineDeterminismTests` 既有情境固定值或雙引擎對照) |
| `RouteTrackerTests.cs` | 已知幾何的 XTD 正負號(右偏為正)、沿航跡距離;航段切換於正橫/到達半徑;不回退;最後一段只用半徑;通過航點記錄距離;走廊超限進入/滯後離開事件;黃金案例:直線航線 + 固定橫流的解析解(XTD 隨時間線性) |
| `ZoneTrackerTests.cs` | 凸/凹多邊形內外、邊上;經緯度/ENU 混用;進出事件;`ukcPolicy` 門檻與水深覆寫;`speedLimit` 超限秒數 |
| `EncounterTrackerTests.cs` | 對遇目標:偵測時刻、首次行動(舵令)時間差、航向改變幅度、`substantial`;escort 排除;會遇結束 |
| `FaultResponseTests.cs` | 注入 → 車令 35 s → `firstAction.dt_s = 35`;`acknowledge notifyEcr` 對應(有/無 itemId);教官手動 `injectFault` 也建立紀錄 |
| `MetricsCollectorTests.cs` | 舵角分箱總和 = 取樣秒數;滿舵次數(含 FSB1 70°);車令段落時間相加 = 時長;凍結不計;速限來源優先序 |
| `AssessmentEngineTests.cs` | 硬性失敗各規則(`cpa` 需曾 tcpa > 0;escort 排除);容許帶 `2x`、`direction: below`;必要事件 met/missed/late/notApplicable 與扣分;`passed` 判定;`final` 定稿後不再變;summary 含 `paramKey/source/revision`;黃金案例:合成一段 600 s 腳本(固定舵令/車令序列 + 1 目標)→ summary JSON 與 `tests/SimCore.Tests/Golden/assessment-E07-600s.json` 相等(數值公差 1e-9;更新黃金檔需說明) |
| `ScenarioValidatorTests.cs` | 第 2.4 節每條 error/warning 各一案例;E01、E05、E07 零 error |
| `ContractsTests.cs` *(擴充)* | 新指令範例可解析;`state.schema.json` 新欄位為選填;`parameter-set.schema.json` 範例 |
| `ScenarioTests.cs` *(擴充)* | E07 載入:timeline/route/zones/assessment/briefing 欄位;schema `examples[0]` 可載入 |

### 7.2 網頁 vitest(`src/Instruments.Web/tests`)

| 檔案 | 案例 |
|---|---|
| `sim-event.test.ts` | `isSimEvent` 對交通/時間表/評估事件;`eventLabel` 文字 |
| `record-replay.test.ts` *(擴充)* | 含 `event`/`summary` 行的 JSON Lines 解析無錯誤;`events[]`、`summary`、`header.parameterSet`;舊檔(無 summary)仍可解析 |
| `scenario.test.ts` *(擴充)* | `parseTimeline`、`parseRoute`、`briefing` 字串/物件;`${params:…}` 字串在教官站顯示為「參數引用(鍵)」而非 NaN(教官站不解析參數集,只顯示) |
| `timeline-panel.test.ts` | 狀態由事件更新;手動觸發送出 `triggerTimeline`;acknowledge 鈕送出正確 args;倒數顯示 |
| `debrief-summary.test.ts` *(擴充)* | 有核心 summary 時顯示評分/硬性失敗/必要事件/參數來源;無時退回本地推導 |
| `voice-script.test.ts` | 有 file → audio;無 file → speechSynthesis(mock);播放記錄 |
| `session-record.test.ts` *(擴充)* | `coreSummary` 往返 |

### 7.3 Python(`src/Tools.Calibration`)

- `tests/test_scenario_params.py`:`${params:…}` 解析與未知鍵錯誤;忽略 `timeline/route/zones/assessment` 不影響載入;與 C# 共用 `tests/fixtures/scenario-params.yaml`。

### 7.4 驗收(第 14 章)

- 「相同輸入紀錄得到相同狀態雜湊」擴充為「相同事件序列與相同 summary」。
- 參數集改版(例如 `navigation.xtd.channel_m` 50 → 40)後重播同一紀錄(`--replay --rescore --parameters`):物理、交通、時間表與雜湊仍完全依紀錄 header 的原情境執行(**雜湊與事件序列不變**,結束碼 0),另以新參數集解析的情境建立**影子評估模組**(`SimulationEngine.EnableRescore`;其事件只餵回自己,不對外發出、不進雜湊、不觸發時間表),summary 由影子模組產生:band 判定改變且顯示新版次 — 作為第 9.1 節「以既有練習紀錄重播評分」的驗證案例。因此也可改版餵入交通/時間表條件的參數(例如 `navigation.cpa.coastal_nm` 同時決定 `cpaAlarm`),重播仍一致;重新評分只重算評估,不會改變紀錄中的事件。

## 8. 示範情境

### 8.1 `E01_baseline.yaml` 更新(加一條簡單時間表與評估)

在既有內容之後追加(既有欄位不動;`minCpa_nm` 改為參數引用):

```yaml
environment:
  # …既有…
  visibility_nm: 10
briefing:
  text: |
    麥寮港外開闊水域,壓載、半速進向北。熟悉儀器、舵令/車令與標準用語;
    前方 5 浬有一艘對遇貨船(HAI XING 8),右艏 1.4 浬一艘錨泊油輪。
    觀察目標 CPA/TCPA,依 COLREG 及早、大幅行動;風向將於 10 分鐘後轉北。
  roles:
    - { role: ecr, playedBy: voiceScript }
  minParticipants: 2
timeline:
  - id: windShift
    name: 風向轉北、增強
    when: { type: time, atLeast: 600 }
    actions:
      - setEnvironment: { wind: { trueSpeed: 20, trueDir: 10 }, ramp_s: 180 }
      - message: { to: instructor, text: "風 15 kn/045 → 20 kn/010,3 分鐘漸變", severity: info }
  - id: ecrCheck
    name: 機控室例行通話
    when: { type: time, atLeast: 300 }
    actions:
      - voiceScript: { role: ecr, channel: phone, text: "駕駛台,機控室。主機運轉正常,目前 HAH,有需要請通知。", lang: zh-TW }
  - id: cpaCoach
    name: CPA 警報提示(教官)
    visibility: instructor
    repeat: { every_s: 60, max: 3 }
    when: { type: event, kind: cpaAlarm, targetId: T1 }
    actions:
      - message: { to: instructor, text: "T1 CPA 低於門檻;觀察團隊是否提出並採取行動", severity: warning }
assessment:
  parameterSet: data/parameters/company-procedures.yaml
  minCpa_nm: ${params:navigation.cpa.openSea_nm}
  encounter:
    detectionRange_nm: ${params:navigation.encounter.detectionRange_nm}
    substantialCourseChange_deg: ${params:navigation.encounter.substantialAlteration_deg}
  ukc:
    min: { fractionOfDraft: ${params:navigation.ukc.openSea_fraction} }
  manoeuvring:
    rotLimit_degPerMin: ${params:manoeuvring.rotLimit_degPerMin}
  hardFail:
    - { id: collision, rule: collision }
    - { id: grounding, rule: grounding }
    - { id: cpaHard, rule: cpa, lessThan: ${params:navigation.cpa.hardFail_nm}, excludeEscorts: true }
  bands:
    - { id: minCpa, metric: collisionAvoidance.minCpa_nm, direction: below, warn: ${params:navigation.cpa.openSea_nm}, fail: ${params:navigation.cpa.coastal_nm}, penalty: 10, failPenalty: 30, excludeEscorts: true }
    - { id: rotOver, metric: manoeuvring.timeRotOverLimit_s, warn: 0, fail: 30, penalty: 5, failPenalty: 10 }
  scoring: { start: 100, passMark: ${params:assessment.passMark} }
```

E01 無 `route`/`zones`(開闊水域);雜湊因有 timeline 而與舊版不同(既有測試不以常數比對 E01 雜湊 → 不影響;見第 5.3 節注意事項)。

### 8.2 `E07_steering_failure.yaml`(新)

```yaml
# E07 舵機故障:麥寮進港航道,引水人在船(規劃書第 8.1 節 E07;schema:src/Contracts/scenario.schema.json)
# FSB1 滿載自西南接近麥寮港,沿 3 段進港航道(走廊半寬 50 m)以 HAH 8 kn → 減速進港;退潮流 2.0 kn 向西南橫越航道,東北風 18 kn。
# 時間表:T+5 min 引水人登船語音 → 進入航段 1 後 90 s 舵卡(steeringGear,階段 degraded)→ 60 s 內無相關動作(必要事件 steeringFirstAction 逾時)則機控室來電提示(emergency)
#          → 教官確認「已轉應急操舵」後 clearFault;航段 2 內超速提示;錨地禁區。評估:故障首個相關動作 ≤ 60 s、通知機控室 ≤ 120 s、XTD、UKC、CPA(航道門檻 navigation.cpa.channel_nm)。
id: E07_steering_failure
name: 麥寮進港航道舵機故障
description: >-
  進港航道中段舵機卡住,團隊需辨識(舵角指示 vs 舵令)、以車令/側推控制艏向、通知機控室並轉應急操舵,
  同時維持航道內、監控 UKC 與出港船;評估故障應變時間、航跡保持與溝通。
seed: 20260707
startTimeUtc: "2026-07-07T06:30:00Z"
timeScale: 1
ship:
  id: FSB1
  loading: full
origin: { lat: 23.7800, lon: 120.1000 }
initial:
  position: { lat: 23.7760, lon: 120.0900 }    # WP1 西南 1 nm
  heading: 52
  speed: 8.0
  telegraph: HAH
  rudder: 0
environment:
  wind: { trueSpeed: 18, trueDir: 40, gustiness: 0.15 }
  current: { set: 230, drift: 2.0 }             # 退潮,橫越航道
  waterDepth: 14                                 # 航道外;航道內由 zones channel 覆寫 12.5 m
  visibility_nm: 6
briefing:
  text: |
    麥寮港進港,滿載,引水人已登船(由帶領者扮演)。航道三段:WP1→WP2 052°、WP2→WP3 070°、WP3→泊位 085°;
    走廊半寬 50 m,航道內速限 8 kn、內港 5 kn;退潮 2 kn 橫流。UKC 政策:航道內 ≥ 10% 吃水。
    注意舵令與舵角指示的一致性;發生異常時依公司緊急程序 SMS-EMG-004 處置並通知機控室。
  attachments:
    - { title: 航程計畫(麥寮進港), file: docs/passage-plan-mailiao.pdf }
    - { title: 緊急程序 — 舵機故障, file: docs/SMS-EMG-004.pdf }
  roles:
    - { role: pilot, playedBy: leader, required: true }
    - { role: ecr, playedBy: voiceScript }
    - { role: vts, playedBy: voiceScript }
  minParticipants: 3
route:
  name: 麥寮進港航道
  xtdLimit_m: ${params:navigation.xtd.channel_m}
  waypoints:
    - { name: WP1, lat: 23.7800, lon: 120.1000 }
    - { name: WP2, lat: 23.7960, lon: 120.1220, checkpoint: true, speedLimit_kn: ${params:port.mailiao.speedLimit_kn} }
    - { name: WP3, lat: 23.8050, lon: 120.1490, checkpoint: true, speedLimit_kn: ${params:port.mailiao.speedLimit_kn}, report: "麥寮 VTS 報告點(VHF 13)" }
    - { name: BERTH, lat: 23.8075, lon: 120.1700, arrivalRadius_m: 120, speedLimit_kn: ${params:port.mailiao.innerSpeedLimit_kn} }
zones:
  - id: channel
    name: 進港航道(UKC 政策區)
    type: ukcPolicy
    waterDepth_m: 12.5
    ukcMin: { fractionOfDraft: ${params:navigation.ukc.channel_fraction} }
    polygon:
      - { lat: 23.7780, lon: 120.0980 }
      - { lat: 23.7990, lon: 120.1200 }
      - { lat: 23.8080, lon: 120.1500 }
      - { lat: 23.8100, lon: 120.1720 }
      - { lat: 23.8050, lon: 120.1730 }
      - { lat: 23.8020, lon: 120.1490 }
      - { lat: 23.7930, lon: 120.1230 }
      - { lat: 23.7750, lon: 120.1010 }
  - id: anchorage_S
    name: 南側錨地(禁區)
    type: noGo
    polygon:
      - { lat: 23.7850, lon: 120.1250 }
      - { lat: 23.7900, lon: 120.1500 }
      - { lat: 23.7800, lon: 120.1520 }
      - { lat: 23.7750, lon: 120.1270 }
  - id: inner
    name: 內港速限區
    type: speedLimit
    speedLimit_kn: ${params:port.mailiao.innerSpeedLimit_kn}
    polygon:
      - { lat: 23.8040, lon: 120.1600 }
      - { lat: 23.8110, lon: 120.1600 }
      - { lat: 23.8110, lon: 120.1760 }
      - { lat: 23.8040, lon: 120.1760 }
targets:
  - id: OUT1
    name: TAI PING 12
    mmsi: 416400201
    shipType: cargo
    loa: 95
    beam: 15
    draft: 5.5
    initial:
      position: { lat: 23.8074, lon: 120.1558 }   # 航道內出港:沿自船航線左舷側(航道西北半幅)170 m 的平行線反向航行,左舷對左舷通過(COLREG 第 9/14 條)
      heading: 262
      speed: 6
    behaviour:
      mode: waypoints
      waypoints:                                   # 自船 WP3/WP2/WP1 向左舷平移 170 m;自船守在走廊(半寬 50 m)內時通過距離 120–220 m
        - { lat: 23.8065, lon: 120.1486, speed: 6 }
        - { lat: 23.7973, lon: 120.1212, speed: 6 }
        - { lat: 23.7812, lon: 120.0990, speed: 7 }
      stopAtEnd: true
    trigger: { type: time, time: 240 }
  - id: ANCH1
    name: SHENG LI
    mmsi: 416400202
    shipType: tanker
    loa: 110
    beam: 18
    initial: { position: { lat: 23.7830, lon: 120.1380 }, heading: 40 }
    behaviour: { mode: anchored, anchor: { swing: 20, period: 420 } }
timeline:
  - id: pilotBoarded
    name: 引水人登船(語音)
    when: { type: time, atLeast: 60 }
    actions:
      - voiceScript: { role: pilot, channel: bridge, text: "船長早,引水人登船。計畫走三段航道,WP2 前減到半速,內港五節。", lang: zh-TW, file: audio/E07/pilot_boarded.ogg }
      - message: { to: instructor, text: "引水人 MPX 開始;觀察是否核對航程計畫與航道速限", severity: info }
  - id: vtsCall
    name: VTS 詢問 ETA
    when: { type: routeLeg, atLeast: 1 }
    actions:
      - voiceScript: { role: vts, channel: vhf13, text: "FS Bitumen No.1, Mailiao VTS, 請報 WP3 ETA 與吃水。", lang: zh-TW }
  - id: steeringFail
    name: 舵機故障(卡舵)
    phase: degraded
    holdFor_s: 0
    when:
      all:
        - { type: routeLeg, atLeast: 1 }
        - { type: time, atLeast: 90, since: { event: routeLegChanged, legIndex: 1 } }
    actions:
      - injectFault: steeringGear
      - voiceScript: { role: helmsman, channel: bridge, text: "舵不聽使喚!舵令右十,舵角指示不動。", lang: zh-TW }
      - message: { to: instructor, text: "已注入舵機故障(舵卡在目前角);期望 60 s 內車令/側推/確認,120 s 內通知機控室(SMS-EMG-004)", severity: warning }
  - id: ecrPrompt
    name: 機控室來電(無反應時)
    phase: emergency
    when:                                        # 「無反應」= 必要事件 steeringFirstAction 逾時(核心於故障後 firstAction_s 內無車令/側推/自動舵解除/操舵模式確認時發 requiredEventMissed);
      all:                                       #   不以累計的 acknowledge 次數判斷,故障前的任何確認不會讓此項目失效
        - { type: fault, name: steeringGear, active: true }
        - { type: event, kind: requiredEventMissed, itemId: steeringFirstAction }
    actions:
      - voiceScript: { role: ecr, channel: phone, text: "駕駛台,機控室。舵機液壓油壓低警報,你們舵有問題嗎?", lang: zh-TW }
  - id: emergencySteering
    name: 應急操舵完成(教官確認後解除故障)
    manual: true
    actions:
      - clearFault: steeringGear
      - voiceScript: { role: ecr, channel: phone, text: "駕駛台,舵機房已切換應急操舵,請以電話下舵令。", lang: zh-TW }
      - phase: normal
  - id: speedCoach
    name: 超速提示
    repeat: { every_s: 60, max: 3 }
    when: { type: event, kind: speedLimitExceeded }
    actions:
      - message: { to: instructor, text: "超過航道/內港速限;觀察引水人與船長是否提出", severity: warning }
  - id: end
    name: 到達泊位附近結束
    when: { type: ownDistance, waypoint: BERTH, lessThan: 0.15 }
    actions:
      - endExercise: { reason: 到達泊位接近區 }
assessment:
  parameterSet: data/parameters/company-procedures.yaml
  minCpa_nm: ${params:navigation.cpa.channel_nm}        # 航道內通過距離門檻(D27);沿岸值 0.5 nm 比航道還寬
  encounter:
    detectionRange_nm: 3
    substantialCourseChange_deg: ${params:navigation.encounter.substantialAlteration_deg}
  ukc:
    min: { metres: ${params:navigation.ukc.minimum_m} }
  manoeuvring:
    rotLimit_degPerMin: ${params:manoeuvring.rotLimit_degPerMin}
  hardFail:
    - { id: grounding, rule: grounding }
    - { id: collision, rule: collision }
    - { id: noGo, rule: zoneEntered, zoneId: anchorage_S }
    - { id: ukcHard, rule: ukc, lessThan: { fractionOfDraft: ${params:navigation.ukc.hardFail_fraction} } }
  bands:
    - { id: xtdMax, metric: track.maxXtd_m, warn: ${params:navigation.xtd.channel_m}, fail: 2x, penalty: 10, failPenalty: 25 }
    - { id: xtdTime, metric: track.timeOutsideCorridor_s, warn: 0, fail: 60, penalty: 5, failPenalty: 15 }
    - { id: ukcTime, metric: ukc.timeBelowLimit_s, warn: 0, fail: 30, penalty: 10, failPenalty: 25 }
    - { id: speed, metric: manoeuvring.timeOverSpeedLimit_s, warn: 0, fail: 120, penalty: 5, failPenalty: 15 }
    - { id: minCpa, metric: collisionAvoidance.minCpa_nm, direction: below, warn: ${params:navigation.cpa.channel_nm}, fail: ${params:navigation.cpa.channelHardFail_nm}, penalty: 10, failPenalty: 30 }
  required:
    - id: steeringFirstAction
      after: { event: faultInjected, itemId: steeringFail }
      expect: { any: [ telegraphChange, rpmOrder, thrusterOrder, autopilotOff, { acknowledge: { kind: steeringMode } } ] }
      within_s: ${params:emergency.steeringFailure.firstAction_s}
      penalty: 20
      latePenaltyPer_s: 0.5
    - id: notifyEcr
      after: { event: faultInjected, itemId: steeringFail }
      expect: { acknowledge: { kind: notifyEcr } }
      within_s: ${params:emergency.steeringFailure.notifyEcr_s}
      penalty: 10
    - id: emergencySteering
      after: { event: faultInjected, itemId: steeringFail }
      expect: { event: { kind: timelineTriggered, itemId: emergencySteering } }
      within_s: ${params:emergency.steeringFailure.emergencySteering_s}
      penalty: 10
    - id: vtsReport
      after: { event: reportPointPassed, waypoint: WP3 }
      expect: { acknowledge: { kind: report } }
      within_s: 120
      penalty: 5
  scoring: { start: 100, passMark: ${params:assessment.passMark} }
```

備註:座標為示意(麥寮港外海岸線與航道以實際 ENC/港方資料校正,第 3.4 節);`audio/E07/*.ogg` 不存在時教官站退回 TTS(`voiceScript.file` 缺檔為載入 warning)。情境包目錄 `data/scenarios/E07_steering_failure/`(音檔、附件)為選配。

## 9. 邊界情況與決策

| 情況 | 處理 |
|---|---|
| 時間表項目條件在 t = 0 已成立 | 第一個評估步(tick 50,t = 1 s)觸發;驗證器警告(非 time 型) |
| `repeat: always` + 條件持續為真 | 每秒觸發一次,受 `cooldown_s` 限制;驗證器警告無 cooldown;觸發鏈上限 16 |
| `delay_s` 期間 `reset`/`loadScenario` | 待執行表清空(Initialize) |
| `delay_s` 期間還原到更早快照 | 待執行表由快照還原(可能再次排程同一動作 → 與連續執行一致) |
| 手動 `triggerTimeline` 於 `once` 已觸發項目 | 拒絕(`timelineManual ok:false`),除非 `force: true`(再觸發一次並計入 fireCount) |
| `injectFault` 同名已存在 | 引擎既有行為:不重複加入;事件 `faultInjected` 仍發(`data.alreadyActive = true`),故障應變紀錄不新建 |
| `clearFault: all` | 既有 `ClearFault` 無 value 的語意;事件逐一發 `faultCleared` |
| 漸變中再下 `setEnvironment` 無 `ramp_s` | 立即設定,移除該 channel 的 ramp |
| 水深漸變使 UKC ≤ 0 | 擱淺由引擎既有檢查觸發(`Grounded` + `grounding` 事件) |
| `zones.ukcPolicy.waterDepth_m` 與 `environment.waterDepth` | 自船在區內時 `UnderKeelClearance()` 與狀態 `waterDepth` 用區域值(實作:`ZoneDepth : IDepthProvider` 包裝 `ConstantDepth`,可進快照;多區重疊取最淺) |
| 航線第一航點後方起步 | 以航段 0 延長線計 XTD;`s < 0` 不計入「通過」 |
| 航線被 `reset` | `RouteTracker` 重建(LegIndex = startLeg) |
| 目標船在會遇中被 `remove` | `encounterClosed`(`data.reason = removed`),指標保留 |
| 同一秒多個事件 | `seq` 遞增;WebSocket 框各自推送;紀錄行順序 = 產生順序 |
| 重播時動力學模型不同 | 既有警告;事件序列可能不同,`EventSequenceMatches = false` 與雜湊不一致一併回報 |
| 參數集缺少情境引用的鍵 | 載入失敗(error),不以預設值代替(第 9.1 節:未解析為錯誤) |
| 參數集改版但紀錄用舊版 | 重播用 header 內已解析的情境(舊值);以新參數集「重新評分」為講評站功能(`--parameters` + `--rescore` 旗標,影子評估模組只重算 summary,雜湊與事件序列不變,第 7.4 節) |
| 紀錄中切換情境(`loadScenario`) | 紀錄器在引擎重設前(`ScenarioLoading` 事件)自動關閉(footer 為舊情境的最終 tick/雜湊),主控程序為新情境另開新檔;`loadScenario` 不寫 input 行;重播遇到舊版紀錄的 `loadScenario` input 行時中止並以 Warning 說明 |
| 凍結中按「還原」 | `restore` 指令保留目前的 Frozen/TimeScale(快照值不覆寫),由教官按「繼續」;練習已結束(`endExercise`)後 `restore` 無效(回傳 false),需 `reset`/`loadScenario` |
| 會遇關閉後同一目標再接近 | 再次成立開始條件(range ≤ detectionRange、TCPA > 0、CPA < 關注門檻)即新開一筆會遇紀錄(同 targetId,`collisionAvoidance.encounters` 累計);未成立時仍把更小的實際距離記到該目標最後一筆的 `minRange_nm`,使 `minCpa_nm`、容許帶與硬性失敗 `cpa` 反映真實最小通過距離 |
| `setEnvironment.visibility_nm` 而情境能見度為無限制(null) | 不建立漸變,直接寫入目標值;`environmentChange.data.changes.visibility.from = null` |
| `timeScale ≤ 0` | 視同 `freeze`:發 `frozen` 事件並計入 `general.freezes`(之後 `resume` 有對應的 `resumed`) |
| 自動舵開啟時下手動舵令 | 視為一次 `autopilotOff` 動作(`autopilotSwitches`、必要事件 expect `autopilotOff`),時間表 `ownShip` 覆寫不計 |
| 航段長度 ≤ 到達半徑 / 自船尚未到達航段起點 | 到達半徑判定需 `s ≥ 0`(已過本航段起點);驗證器對航段長 ≤ 半徑的航點發 warning |
| 航道情境的 CPA 門檻 | 驗證器對 `minCpa` 容許帶或 `cpa` 硬性失敗門檻 > 2 × 走廊半寬者發 warning(航道內任何交會都會超出);E07 改用 `navigation.cpa.channel_nm`/`channelHardFail_nm` |
| 無 WebSocket 客戶端時 `voiceScript` | 事件仍記錄;無人播放(簡化教官模式一定有教官站) |
| Python 參考伺服器 | 忽略 timeline/assessment;教官站的時間表面板在 Python 來源時顯示「核心不支援,僅顯示」並停用觸發鈕 |
| 時間倍率 ×10 | 評估步仍以模擬秒計;`voiceScript` 的實際播放時間會比模擬時間長(教官站顯示提示) |
| 大量事件(`always` 誤用) | 紀錄檔每秒最多數行;事件框推送不節流(教官站事件流只保留最近 2,000 筆) |

## 10. 開放問題

1. **操舵模式(FU/NFU/自動舵/應急)**尚未在契約中(規劃書第 7.2 節):E07 的「切換 NFU/應急操舵」第一版以 `acknowledge {kind: steeringMode}` 或教官手動 `emergencySteering` 項目代表;建議下一步加 `steeringMode` 指令與狀態欄位,讓 `steeringModeChange` 可自動偵測。
2. **`blackout` 組合故障**:是否由引擎展開為 `mainEngine + steeringGear + 儀器失效`(含應急發電 45 s 後部分恢復的子時間表),或以情境時間表組合表達;建議後者(時間表 `injectFault` 多個 + `delay_s`),引擎不加特例。
3. **只記錄的故障**在儀器端的反應(gyroDrift 等)需各儀器實作;本設計只保證名稱目錄與事件。
4. **參數集單一檔 vs. 每船覆寫**:schema 預留 `overrides`;何時需要由公司程序決定。
5. **評分權重與通過準則**為假設值(`BRM-TRN-001` 草案);依第 8.3 節自動評分僅輔助,正式數值待 D20。
6. **ack 框**:C# 主控程序是否補 Python 式 `ack`(教官站指令紀錄會顯示核心回應);與本設計無相依,可獨立做。
7. **E07 座標**需以麥寮港 ENC/港方圖資校正;目前為示意值。

## 11. 實作偏離與補充(v0.2,2026-10-17)

實作以本節修正前文;未列者與前文一致。

| # | 前文 | 實作 | 原因 |
|---|---|---|---|
| D1 | 第 1、2.3、8 節 YAML 範例在流式寫法 `{ … }` 內直接寫 `${params:…}` | **流式 `{ }` 內的引用須加引號** `"${params:…}"`(區塊寫法 `key: ${params:…}` 可不加);`data/scenarios/E01`、`E07` 已全部加引號 | YAML 流式上下文中 `}` 會結束純量,原寫法無法解析 |
| D2 | 第 4.4、4.7 節:`collisionAvoidance.minCpa_nm` 與硬性失敗 `cpa` 用目標的預測最小 CPA(`TargetShip.MinCpaNm`) | 用**非 escort 目標的最小實際通過距離**(每目標 `minRange_nm`);每目標仍輸出預測 `minCpa_nm`/`minCpaAt_s` | E01 的對遇目標在 t = 0 預測 CPA 即 0.05 nm,會在練習開始就硬性失敗;實際通過距離才是評估意義上的 CPA |
| D3 | 第 3.6 節雜湊追加 `int64 Seq` | 追加 `int64 (Seq − 評估事件數)`;快照 `TimelineSnapshot.AssessmentEvents` | 評估事件(bandWarn 等)隨參數門檻改變,計入會使第 7.4 節「重新評分雜湊不變」不成立 |
| D4 | 第 4.5 節 `general.frozen_s` | `general.freezes`(凍結次數);不輸出 `frozen_s` | 凍結期間模擬時間不前進,秒數只能用牆鐘,會破壞 summary 的確定性比對 |
| D5 | 第 4.4 節 escort 目標「不列入」彙總 | escort 完全不建立會遇紀錄(不發 encounterStarted) | 引水船跟隨自船時永遠在偵測距離內,紀錄無意義 |
| D6 | 第 1.3 節 `hardOver_deg` 未給時取 `rudder.maxAngle_deg` | 取**目前動力學模型的舵角上限**(`ActuatorParameters.RudderMaxDeg`:MMG 70°、暫代模型 `normalMaxAngle_deg` 35°) | 暫代模型舵角夾在 35°,以 70° 為門檻永遠不會滿舵 |
| D7 | 第 2.3 節「有引用但未提供參數集 → 錯誤」 | `ScenarioLoader.Load(path, null)` 先依 `assessment.parameterSet`(相對於專案根目錄或情境檔目錄;預設 `data/parameters/company-procedures.yaml`)自動載入,仍找不到才錯誤並提示 `--parameters` | 測試與主控程序不必每次顯式傳參數集;`--parameters` 仍可覆寫 |
| D8 | 第 8.1 節 E01 `minCpa_nm: ${params:navigation.cpa.openSea_nm}`(1.0) | E01 `minCpa_nm` 引用 `navigation.cpa.coastal_nm`(0.5),容許帶 warn/fail 仍為 openSea/coastal | 保持既有 cpaAlarm 門檻 0.5 與既有測試(`TrafficScenarioTests`)不變 |
| D9 | 第 8.2 節 E07 `initial.position: {23.7760, 120.0900}` | `{23.7696, 120.0857}`(WP1 西南 1 nm、位於航段 0 延長線上,航向 052°) | 原座標偏離航線延長線 284 m,t = 1 s 即走廊超限 |
| D10 | 第 3.1 節 `ScenarioLoader` 用 YamlDotNet 直接反序列化 | YAML → `YamlStream` 節點樹 → `${params}` 替換 → JSON → System.Text.Json(`YamlJson.cs`);多型欄位(動作單鍵物件、`repeat`、`briefing`、`expect`、`fail: 2x`)只用一套 JSON 轉換器;`ContractJson` 加寬鬆字串轉換器(JSON 數值可讀入字串欄位,與 YamlDotNet 寬鬆行為一致);`ToYaml` 經 JSON 產生 | 單一反序列化路徑;紀錄標頭 JSON 與 YAML 同形 |
| D11 | 第 3.2 節 `data` 欄位內容 | `environmentChange.data = {wind, current, waterDepth, visibility_nm, ramp_s, changes{channel: {from, to}}}`(對外單位);`faultInjected.data = {fault, args, alreadyActive}`;`timelineManual.data = {ok, force, reason}`;`routeLegChanged.data = {legIndex, from, to, waypoint}`;新增 `routeCompleted` kind;`speedLimitExceeded.data.source` 為 `leg`/`zone`、`id` 為航點名/區域 id | 補足前文未定義的細節 |
| D12 | 第 3.3 節「觸發鏈上限 16」 | 單一評估步內**觸發的項目數**上限 16(超過發 `message` 警告並停止本步評估);項目依 YAML 順序單次掃描,同步內只有後面的項目看得到前面項目的事件 | 單次掃描沒有循環;上限改為防 `always` 濫用 |
| D13 | 第 4.2 節 `RouteState.XtdLimitM` | 無走廊限制時為 `null`(非 +∞) | JSON 不能表示無限大,快照會失敗 |
| D14 | 第 2.4 節 `ScenarioValidator.Check(scenario, ship)` | 多一個選填 `scenarioDirectory`(音檔檢查);結構錯誤由 `Scenario.StructuralIssues()` 提供,`Scenario.Validate()` 遇第一個 error 即丟例外(引擎建構時) | 驗證器可列出全部錯誤,引擎仍拒絕錯誤情境 |
| D15 | 第 5.3 節 footer 「可加 `summaryLines`」 | 已加(選填) | — |
| D16 | 第 3.1 節 `RecordReader.Summary` | `Summaries`(全部 summary 行)+ `Summary`(最後一行) | 講評站與測試需核對 endExercise 與關閉兩行相同 |
| D17 | 第 7.1 節 `ParameterResolverTests`「Python 與 C# 對同一 YAML 解析結果一致」、第 7.3 節 Python 測試 | 未做(本次不改 `src/Tools.Calibration`);Python 目前忽略 assessment/route/zones/timeline,E01/E07 仍可載入(78 項測試不變) | 工作分派限制;待 Python 側加 `${params:…}` 替換後補 |
| D18 | 第 3.5 節漸變「每步(Tick 為推進前的 tick)」 | 同前文;注意第 n 步套用的是 `Tick = n−1` 的插值,`ramp_s` 結束於 `EndTick` 當步,`environmentRampDone` 的 tick = EndTick | 文件化測試時序 |
| D19 | 第 1.1 節 `armed` 欄位未列於 YAML 範例 | `armed: true|false`(預設 true)為項目欄位;`arm/disarm` 動作切換 | 前文第 1.1.2 節已提及 |
| D20 | 第 5.2 節 `getAssessment` | 主控程序 WebSocket 在收到該指令時直接回覆該客戶端,不送入引擎(引擎收到時忽略) | 同前文,補 WebSocket 事件框走獨立通道(容量 256,不被狀態幀丟棄) |
| D21 | 第 4.8 節 summary 範例未定義的欄位名 | 以 `AssessmentSummary.cs` 為準:`general.freezes`(D4);`assessment.hardFails[].t_s`(與 `faultResponses` 的 `t_s` 同名,非 `at_s`);`collisionAvoidance.targets[]` 加 `closedAt_s`、`escort`;`track` 加 `legIndex`、`complete`;`assessment.required[]` 與 `header.parameterSet.resolved[]` 加 `clause`;`timeScaleChanged.data = {timeScale, frozen}`。網頁 `assessment.ts`/`sim-event.ts` 已依此解析(`at_s`、`frozen_s` 只為舊示範/夾具相容) | 整合驗證(2026-10-17)以主控程序實際紀錄 `src/Instruments.Web/tests/fixtures/record-E07-core.jsonl`、`record-E01-core.jsonl` 逐欄比對(`tests/core-record.test.ts`) |
| D22 | 第 5.2 節 `command.schema.json` 的 `type` 列舉 | 網頁 `types/command.ts` 補 `targetControl`(核心既有),使紀錄 `input` 行的目標船控制指令在重播檢視不被判為無效 | 整合驗證發現網頁列舉少一項 |
| D23 | 第 7.4 節 / D3「重新評分雜湊不變,事件序列與 summary 依新門檻改變」 | 重新評分以**影子評估模組**實作(`SimulationEngine.EnableRescore`、`RescoreAssessment`):物理/交通/時間表/雜湊依紀錄原情境,**雜湊與事件序列皆不變**,只有 summary 改變;`--replay --rescore` 結束碼 0 | 參數(例如 `assessment.minCpa_nm` 餵 `cpaAlarm`、時間表 `event` 條件綁評估事件)也會改變交通/時間表事件,只排除評估事件無法保證雜湊不變,且結束碼一律 1 |
| D24 | 第 3.4 節 `Replayer`「在該 tick 推進前送入」 | 重播在引擎 tick 等於紀錄 tick 時**立即套用**(`Enqueue` + `ProcessPendingCommands`),與主控程序等價;`restore`/`reset` 倒帶後下一筆 input 才從倒帶後的 tick 推進 | 原本倒帶指令要到下一次 Step 才套用,倒帶 tick 與快照 tick 之間的指令全套在快照 tick,雜湊不一致且無警告 |
| D25 | 第 5.3 節 `input` 行「每個套用的指令」 | `loadScenario` 不寫 input 行;紀錄器在情境切換前自動關閉、主控程序另開新檔(第 9 節);`Replayer` 遇舊紀錄的 `loadScenario` 行中止並 Warning | 引擎不能重播情境切換(header 只有一個情境) |
| D26 | 第 8.2 節 E07 `ecrPrompt` 條件 `not: { event: acknowledge }` | 改為 `event: requiredEventMissed, itemId: steeringFirstAction`(與評估模組「60 s 內無相關動作」同義) | `event` 條件是自情境開始的累計次數,故障前任一次確認會讓 emergency 階段永不觸發 |
| D27 | 第 8.2 節 E07 `OUT1` 航線與 `minCpa` 容許帶 | `OUT1` 改為自船航線向左舷平移 170 m 的平行線(左舷對左舷通過);`assessment.minCpa_nm` 與 `minCpa` 容許帶改用新參數 `navigation.cpa.channel_nm`(0.05 nm)/`channelHardFail_nm`(0.025 nm) | 原航線與自船航線相交;沿岸門檻 0.5/0.2 nm 比航道寬度(約 350 m)還大,正確守航道必然 bandFail |
| D28 | 第 4.5 節 `rudderOrders`「1 s 內合併」 | 1 s 視窗自計入的舵令起算、不隨後續舵令延長(每 1 s 最多計 1 次) | 原本視窗隨每道指令重置,連續拖曳舵輪整段只算 1 次 |
| D29 | 第 5.1 節 `state.scenario.timelinePending` | 核心語意為「待執行(`delay_s` 排程中)的動作數」;教官站時間表面板的「待觸發」由事件推導(armed 且未達 once/max 的項目數),`timelinePending > 0` 另顯示「延遲動作 n」 | 兩端語意原本相反 |
| D30 | 第 4.2 節到達半徑判定 | 加 `s ≥ 0`(自船已過本航段起點)條件;驗證器警告航段長 ≤ 到達半徑 | 航段短於半徑或從 A 之前接近時提早「通過」B |
| D31 | 第 6 節 W8 重播檢視 | 紀錄解析依 `state.tick` 遞減切分倒帶分段:倒帶時丟棄 t > 倒帶點的舊分支狀態與事件(保留最終歷史),`rewinds[]` 記錄倒帶點;不再對 states 全域排序。教官站重播檢視常駐(`hidden` 切換,載入的紀錄不因切回即時而遺失),事件流點擊只在重播檢視已載有紀錄時跳轉 | 原本舊分支與新分支依 t 交錯,航跡鋸齒、事件索引錯亂;重播檢視每次切換都重建 |

主控程序:`--record [<path>]`、`--parameters <path>`、`--validate`、`--rescore`(與 `--replay` 併用);重播結束碼需雜湊、完整性與事件序列皆一致(重新評分亦同,D23)。驗證紀錄:`build/records/e07-test.jsonl`(E07 300 s ×10,9 個事件、1 行 summary;`--replay` 雜湊/事件/summary 一致;`--rescore` 以 `navigation.xtd.channel_m` 50 → 20 重播:雜湊與事件序列不變、分數 85 → 60)。
