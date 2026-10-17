# 船舶熟悉模組 F01–F03、E10/E11 情境與操縱量測(ManoeuvreTracker)

> 狀態:v0.2(2026-10-17)— v0.2 依審查修正:逐步下舵(舵輪拖曳/鍵盤/NFU)的開始序列、逐步反舵、先停俥再倒俥、Z 形反舵時機與可比性、
> 低速停俥漂移、同 id 指標取可比量測、approach 車鐘、目標檔雜湊;F01–F03 示範先回穩定直航、本段量測完成(邊緣觸發)、逾時略過;F02 bow blow-off 改風舷角 25°;
> F03 全速退移到 WP2–WP3 航段(第 2.2–2.5、3、4、7–9 節)。v0.1 已實作:`src/SimCore/Assessment/ManoeuvreTracker.cs`、`ManoeuvreRecord.cs`、`ManoeuvreTargets.cs`(與 `AssessmentEngine`、`AssessmentSummary`、`MetricNames` 的小幅掛勾)、
> 情境 `data/scenarios/F01_*`、`F02_*`、`F03_*`、`E10_engine_failure_lee_shore.yaml`、`E11_man_overboard.yaml`、參數集新增鍵(`data/parameters/company-procedures.yaml`)、
> `src/Contracts/scenario.schema.json` assessment 段說明與 acknowledge kind、測試 `tests/SimCore.Tests/ManoeuvreTrackerTests.cs`、`ShipFamiliarisationScenarioTests.cs`。
> 對應規劃書:第 1.4 節含義 (2)(兩船專屬船舶熟悉)、第 3.5 節(操縱特性實測值與資料品質)、第 6.4 節(公差)、第 8.0 節(船舶熟悉形式)、第 8.1 節 F01–F03、E10、E11 列與其下說明、第 8.3 節(自動量測)、第 14 章(情境驗收)。

## 1. 目標與範圍

1. **操縱量測**:在任何使用它的情境中,自動辨識學員(或時間表示範)執行的標準操縱試驗,依 MSC.137(76) / ITTC 7.5-02-06-03 的定義量測,
   並與 `data/ships/<ID>/trial_targets.json` 同船同裝載的目標值比較(差距、公差、是否可比、資料品質);結果進 `summary.manoeuvres[]`、
   發 `manoeuvreMeasured` 事件,可被時間表條件、必要事件與容許帶(`manoeuvres.*` 指標)引用。
2. **F01–F03**:兩船專屬的船舶熟悉模組(每船一版),時間表引導「預測 → 執行 → 量測 → 比較」,不設三步升級與操作類硬性失敗,講評以操縱特性為主。
3. **E10、E11**:主機失效下風岸漂流與人員落水兩個緊急情境(有評分)。
4. 硬性約束:只讀引擎狀態、不改物理(`src/SimCore/Physics/**` 與 `SimulationEngine.cs` 未改);確定性(不用亂數,狀態進快照,事件為 assessment 來源、與其他評估事件一樣不進雜湊鏈);
   **未使用操縱量測的舊情境**不建立量測、不發事件、summary 無 `manoeuvres` 鍵,雜湊、事件序列與 summary 均不變。

不在範圍:錨的物理(另一位工程師平行實作,E10 以 acknowledge 代表)、拖船物理(E10 以語音/訊息代表)、Python 參考實作與網頁端的顯示(第 9 節待辦)。

## 2. 操縱量測(ManoeuvreTracker)

### 2.1 啟用條件

情境不新增欄位(C# `ScenarioAssessment` 位於 `src/SimCore/Scenario/`,不在本次修改範圍;且紀錄標頭以序列化的 `Scenario` 重播,未知欄位會遺失)。
以「情境已使用新功能」判定(`ManoeuvreTracker.IsReferenced`):

- `assessment.bands[].metric` 以 `manoeuvres.` 開頭;或
- `assessment.hardFail[].kind`、`assessment.required[].after.event` / `expect` 的事件 kind、時間表條件(含 `since.event`)的事件 kind 以 `manoeuvre` 開頭;或
- `targets[]` 有 id 以 `MOB` 開頭的目標船(人員落水)。

未啟用時追蹤器完全不動作(`AssessmentSnapshot.Manoeuvres = null`、`summary.manoeuvres` 省略)。

### 2.2 取樣與開始條件

- 每秒評估步(`AssessmentEngine.Update`)讀引擎自船狀態 `Motion`(x、y、ψ、u、v、r)、舵角、轉速;開始時刻與開始狀態取**指令套用當下**:
  學員指令經 `ObservedAction`(rudderOrder/telegraphChange/rpmOrder/thrusterOrder),時間表 `ownShip` 動作經 `ownShipOverride` 事件,
  兩者都在指令已套用、尚未推進的同一 tick 讀引擎狀態(與黃金測試 `MmgTestSupport` 在 tick 0 下令再 Step 相同)。
  自動舵啟用時的舵令變化只在每秒輪詢中更新(不作為開始,只作為進行中量測的結束條件);自動舵**未**啟用時,輪詢到 ≥ 1° 的累計舵令變化
  (網頁 NFU 每 100 ms 約 0.18° 的增量、慢速拖曳舵輪 < 1° 的增量,引擎不觀察為 rudderOrder)可開始舵令類量測,開始時刻取該次輪詢(誤差 ≤ 1 s,記入 notes)。
- **開始序列**(`StartSettleS` 2 s、`StartSequenceMaxS` 30 s、`GrowWindowS` 3 s):自正中附近(原舵令 ≤ 5°)離開的舵令,若是一次到 |δ| ≥ 25° 的明確舵令
  (預設按鈕、命令列、時間表示範)即開始迴旋;其他(舵輪拖曳每 50 ms 一道中間舵令、鍵盤 ±1°/±5° 逐步、NFU、Z 形舵角)先開「開始序列」:
  以**第一道離開正中的舵令**的時刻與狀態(位置、航向、approach、environment)為開始,不發事件、樣本暫存;同向加大的舵令延續序列,
  最後一道加大後 2 s 無變化(或減舵/反向舵令、序列滿 30 s)即依**最終舵角**確定:≥ 25° 迴旋、5°–25°(序列前原舵令 ≤ 2.5°)Z 形、其餘放棄(不發事件);
  確定時發 `manoeuvreStarted`(`data.start_s` = 開始時刻,可早於事件)並依序補入暫存樣本。序列中改車令/側推即放棄序列(之後照常判斷停船/側推)。
  開始後(或序列最後一道舵令後)3 s 內同向加大舵令(35° → 70° Schilling、10° → 35°)改判量測 id,開始時刻與狀態不變,重發 `manoeuvreStarted`(`data.supersedes` = 原 id)。
  操舵模式 NFU 推桿(`steeringMode nfu direction port|starboard`:舵令設為滿舵、舵以舵機速率移動;`stop`:舵令 = 當時舵角)一律走開始序列,
  推桿中不以「舵令穩定」確定,放開時的舵角為最終舵角(舵到 35° 放開 → `turning35.*`,不再被當成 70° 迴旋後因放開而中止)。
  未確定的序列不輸出到 summary,但進快照(還原後結果與雜湊相同)。
- **穩定直航**(MSC.137(76) 定速直航;`Steadiness`):最近 30 s 的每秒樣本 |ROT| ≤ 6°/min、|舵角| ≤ 5°、對水速度與目前差 ≤ 0.5 kn,且 30 s 內車令(轉速令)未變。
  練習開始後尚無任何車/舵令且樣本皆穩定時,情境初始狀態(以平衡轉速建立的直航穩態)視為穩定(`approach.fromInitial = true`)。
- 標記點(90/180/270/360°、執行航向、u = 0、減至結束速度…)以相鄰兩樣本線性內插;Z 形超越角以極值前後三個等間距樣本作拋物線修正。
  與逐步(50 Hz)量測的差異:迴旋距離 < 0.5 m、時間 < 0.1 s、超越角 < 0.05°、停船距離 < 1 m(`ManoeuvreTrackerTests`)。

| 量測 id | 開始(皆為明確指令) | 結束 / 量測值(`values` 鍵) |
|---|---|---|
| `turning<δ>.<port\|starboard>` | 自穩定直航(≥ 3 kn、原舵角 ≤ 5°)下 \|δ\| ≥ 25°(一次下令或開始序列的最終舵角) | 90°:`advance_m`、`transfer_m`;180°:`tacticalDiameter_m`;`timeTo{90,180,270,360}_s`、`speedAt{…}_kn`;360–450° 樣本平均 `steadyDiameter_m`(2V/\|r\|)、`steadySpeed_kn`、`steadyRot_degPerMin`;450° 完成。舵令/車令/側推改變:≥ 360° 仍完整(定常值取至變更時)、≥ 90° 為未完整量測、否則中止 |
| `zigzag<ψ>.<port\|starboard>First` | 自穩定直航(≥ 3 kn、原舵角 ≤ 2.5°)下 5° ≤ \|δ\| < 25°(開始序列的最終舵角);執行航向 ψ = \|δ\| | 同舵角 ±2° 內的微調不理會;舵令離開本舷同舵角(< \|δ\| − 2°)朝對舷即為**反舵**,記於該道舵令(舵輪拖曳/NFU 的逐步反舵於 15 s 內到達對舷同舵角 ±2° 前持續朝新舷的舵令不中止);`reversalLate{n}_deg` = 反舵當下航向變化 − ψ(+ 晚 / − 早,\|值\| > 1° 加註記且第 n 個及之後的超越角不可比);`timeToExecute_s`、`timeToSecondExecute_s`、`overshoot{1,2,3}_deg`;ψ ≤ 15° 取 3 個、否則 2 個超越角後完成;其他舵令/車令 → ≥ 2 個為完整、≥ 1 個為未完整、0 個中止 |
| `crashStop` | 前進中(u > 0.5 m/s、原轉速令 > 0)改為倒俥;**停俥後 15 s 內**改倒俥時由慣性停船改判(開始取停俥,`order` "STOP→EFAS"、`stopToAstern_s`,重發 started);停俥滑行中(原轉速令 0)倒俥亦開始(註明,條件不可比) | `asternStart_s`(轉速過零)、`stopTime_s`(u 過零)、`trackReach_m`、`headReach_m`、`sideReach_m`、`lateralOffset_m`(+ 右)、`headingChange_deg`(+ 右;單俥倒俥艉偏)、`maxRot_degPerMin`;停船前車令變更即中止;用舵加註記 |
| `inertiaStop` | 前進中改為停俥 | 進入 ≥ 7 kn:減至 `endSpeed_kn` = 5 kn;`time_s`、`distance_m`、`headReach_m`、`sideReach_m`、`headingChange_deg`、`speedAt{n}min_kn`(marks minute 另含航向變化);1,800 s 逾時為未完整。進入 < 7 kn:**停俥漂移**(`mode: drift`):觀察 300 s 或減至 0.5 kn 先到者結束,`endSpeed_kn` 為結束時速度,另記 `headingChangeAt{n}min_deg`、`maxHeadingChange_deg`(壓載 bow blow-off),不與試俥比較 |
| `thrusterTurn.<side>` | 側推由 0 到 ≥ 50%,舵 ≤ 5°、\|ROT\| ≤ 10°/min | `headingChange60_deg`、`time30_s`、`time90_s`(完成)、`maxRot_degPerMin`(60 s 後);側推/舵/車令變更 → ≥ 60 s 為未完整 |
| `rudderSwing.<toPort\|toStarboard>` | 舵已到 \|δ\| ≥ 30° 後令對舷 ≥ 30°(與其他量測並行) | `timeToMidships_s`、`timeTo30_s`(SOLAS 35°→對舷 30° ≤ 28 s)、`timeToOrder_s`、`time35to35_s`;記錄使用中的泵 `pump` |
| `manOverboard` | id 以 MOB 開頭的目標出現(`targetActivated`;與其他量測並行) | `initialRange_m`、`reactionTime_s`(首個 ≥ 10° 舵令)、`initialTurnTowardMob`、各舵向段的航向變化(`marks` type turn)、`reciprocalAt_s`、`returnTrackOffset_m`(到對向航向 ±20° 時偏離原航跡)、`minDistance_m`、`minDistanceAt_s`、`speedAtMin_kn`、`returnTime_s`(進入 2 倍船長)、`method` |

MOB 旋迴方式(`ClassifyMob`,依舵向分段的航向變化):單段 ≥ 230° → **anderson**;首段 40–90° 後反向、總變化 180° ± 30° → **williamson**;
首段 200–280° 後反向、總變化 180° ± 30° → **scharnow**;其餘 **other**。完成條件:迴轉 ≥ 150° 後最接近距離已過(距離連續 30 s 大於最小值 + 20 m,且最小值 ≤ 1 nm)或在 2 倍船長內航速 ≤ 1 kn;2,400 s 逾時。

### 2.3 與 trial_targets.json 比較、資料品質(第 3.5 節)

`ManoeuvreTargets`(依船舶 ID 讀一次並快取)只比較**同船同裝載**:

| 資料 | 段 | `dataQuality` | 公差(`tolerances`) |
|---|---|---|---|
| No.1 滿載 2014-02-10 試俥(識別區 identification:35° 迴旋、10/10 Z 形;驗證區 validation:20/20、緊急停船、慣性停船、艏側推) | identification / validation | `trial` | 迴旋 max(10%, 0.3 L)、Z 形 max(3°, 25%)、停船 15%、航速 0.5 kn |
| No.1 海報滿載緊急停船(4.3 分、6.4 鏈) | posterEstimates.loadedCrashStopPoster | `poster` | 同上 |
| No.1 壓載(海報估計:迴旋 90/180/270/360° 時間與航速、緊急停船 3.4 分 5.2 鏈)、淺水 h/T 1.2 迴旋 | posterEstimates.* | `estimate` | 同上 |
| No.2 滿載(海報讀值,`dataGrade: poster`;無 Z 形) | identification / validation | `poster` | No.1 的 1.5 倍 |
| 舵機 35°→35°(交船圖說,依泵) | particulars.rudder.steeringGear | `particulars` | 15% |

每筆比較含 `measured`、`target`(範圍目標另有 `targetMin/Max`)、`deviation`、`deviation_pct`、`tolerance`、`withinTolerance`、`comparable`、`dataQuality`、`source`(目標值在檔案中的路徑)。
`comparable = false` 的原因寫入 `notes`(繁體中文):暫代模型、開始前未穩定直航、風 > 10 kn、流 > 0.2 kn、淺水 h/T < 4(深水目標)、進入速度差超過航速公差、車令不是全速退、結束速度不是 5 kn 等。
另加資料品質說明(海報讀值約 ±5%、估計值非實測、No.2 公差放寬)、已知偏差(No.1 慣性停船、右迴旋橫距,對應 `MmgGoldenTests` 的 Skip)、FSB1 舵角 > 35°「Schilling 35°–70° 區間舵力模型僅經船長驗證」。
`record.dataQuality` 為可比較項目中最好的品質(trial > particulars > poster > estimate),無比較為 `none`。
其他不可比/註記規則:Z 形第 n 次反舵 \|`reversalLate{n}_deg`\| > 1° → 第 n 個及之後的超越角不可比(MSC/Circ.1053 於 ψ 反舵;晚反舵會膨脹超越角,不把操作誤差誤報為船舶特性偏離);
先停俥再倒俥 `stopToAstern_s` ≤ 5 s 只註明條件差異、> 5 s 不可比;停俥漂移不比較。比較過的紀錄記 `targetsSha256`(所用 trial_targets.json 的 sha256),
目標值日後更新時,重播摘要的差異可追溯到目標檔版本(目標內容存入紀錄標頭、重播優先用標頭的部分見第 9 節待辦)。

### 2.4 事件、summary 與 bands 指標(契約)

事件(source `assessment`,itemId = 量測 id;`targetId` 僅 MOB):

| kind | data |
|---|---|
| `manoeuvreStarted` | `{id, kind, side, start_s, rudder_deg?, order?, supersedes?, approach, environment}`;`start_s` 為量測開始時刻(開始序列時早於事件時刻);改判時 `supersedes` = 原 id 並重發 |
| `manoeuvreMeasured` | 完整 `ManoeuvreRecord`(見下例);message 為一句繁中摘要 |
| `manoeuvreAborted` | `{id, kind, reason}` |

`summary.manoeuvres[]`:已結束(measured/aborted)者依序,進行中者附於最後(`state: inProgress`)。範例(主控程序實跑 `F01_FSB1_full` 紀錄的 summary 第一筆,comparisons/marks 摘錄;數值為核心輸出):

```json
{
  "id": "turning35.port", "kind": "turning", "side": "port", "state": "measured", "complete": true,
  "start_s": 240, "end_s": 536, "shipId": "FSB1", "loading": "full", "dynamics": "mmg/1", "rudder_deg": -35,
  "approach": { "speed_kn": 14.50, "sog_kn": 14.50, "heading_deg": 0.41, "rpm": 182.2, "telegraph": "NAVF", "steady": true, "steadyFor_s": 30, "fromInitial": true, "autopilot": true },
  "environment": { "windSpeed_kn": 0, "windDir_deg": 0, "currentDrift_kn": 0, "currentSet_deg": 0, "waterDepth_m": 1000, "depthRatio": 175.1 },
  "values": { "advance_m": 284.238, "transfer_m": 133.692, "timeTo90_s": 54.023, "speedAt90_kn": 9.369, "tacticalDiameter_m": 294.353, "timeTo180_s": 106.491,
              "speedAt180_kn": 6.406, "timeTo270_s": 166.898, "speedAt270_kn": 5.319, "timeTo360_s": 230.612, "speedAt360_kn": 4.974,
              "steadySpeed_kn": 4.912, "steadyRot_degPerMin": 83.336, "steadyDiameter_m": 208.486 },
  "marks": [ { "type": "heading90", "t_s": 54.023, "headingChange_deg": 90, "speed_kn": 9.369, "advance_m": 284.238, "transfer_m": 133.692 } ],
  "comparisons": [
    { "key": "advance_m", "measured": 284.238, "target": 278, "deviation": 6.238, "deviation_pct": 2.24, "tolerance": 29.7, "withinTolerance": true,
      "comparable": true, "dataQuality": "trial", "source": "identification.turningCircle35.port.advance_m" },
    { "key": "steadySpeed_kn", "measured": 4.912, "target": 4.75, "targetMin": 4.6, "targetMax": 4.9, "deviation": 0.012, "deviation_pct": 0.25, "tolerance": 0.5,
      "withinTolerance": true, "comparable": true, "dataQuality": "trial", "source": "identification.turningCircle35.steadySpeedInTurn_kn" }
  ],
  "dataQuality": "trial", "targetsSha256": "<data/ships/FSB1/trial_targets.json 的 sha256>", "notes": []
}
```

`approach.telegraph` 為**下令前**的車鐘(緊急停船/慣性停船不是剛下的 EFAS/STOP);`approach.steadyFor_s` 上限為檢查視窗 30 s(含 `fromInitial`)。
先停俥 3 s 後 EFAS 的 `manoeuvreStarted`(改判)與紀錄摘錄(FSB1 滿載 14.5 kn,`ManoeuvreStartSequenceTests`):

```json
{ "id": "crashStop", "kind": "crashStop", "start_s": 60, "order": "STOP→EFAS", "supersedes": "inertiaStop",
  "approach": { "speed_kn": 14.50, "heading_deg": 2.26, "rpm": 182.2, "telegraph": "NAVF", "steady": true, "steadyFor_s": 30, "fromInitial": true } }
{ "id": "crashStop", "state": "measured", "complete": true, "start_s": 60, "end_s": 351, "order": "STOP→EFAS",
  "values": { "stopToAstern_s": 3, "asternStart_s": 113.539, "stopTime_s": 290.244, "trackReach_m": 1297.26, "headReach_m": 1288.841, "headingChange_deg": 46.358 },
  "notes": [ "先停俥再倒俥:停俥後 3.0 s 改 EFAS,開始時刻取停俥(試俥為由全速進直接倒俥)", "停俥 3.0 s 後倒俥(試俥為直接倒俥;差異 ≤ 5 s,仍比較)" ] }
```

Z 形反舵標記 `{ "type": "reversal", "t_s": 22.1, "headingChange_deg": 10.0, "value": 0.002, "index": 1 }`(`value` = `reversalLate1_deg`);
停俥漂移 `{ "id": "inertiaStop", "mode": "drift", "values": { "headingChangeAt1min_deg": 0.008, …, "headingChangeAt5min_deg": -6.492, "maxHeadingChange_deg": -6.492, "time_s": 300, "endSpeed_kn": 0.794 } }`
(F02_FSB1 D 段實跑)。

容許帶指標(`MetricNames.All` 追加 `ManoeuvreTracker.MetricNameList()`):`manoeuvres.<量測 id>.<values 鍵>`(同 id 多筆時取最近一次**完整且有可比比較**者,
其次最近一次完整者,再其次最近一次 measured;例:F01 的低速 Schilling 35° 段同為 `turning35.port`,指標仍取標準 14.5 kn 迴旋)與彙總
`manoeuvres.count`、`manoeuvres.compared`(可比的比較數)、`manoeuvres.outsideTolerance`、`manoeuvres.maxAbsDeviation_pct`。例:

```yaml
bands:
  - { id: tdPort, metric: manoeuvres.turning35.port.tacticalDiameter_m, warn: 320, penalty: 0 }
  - { id: cmp, metric: manoeuvres.outsideTolerance, warn: 0, penalty: 0 }
```

注意:Python `simosa_brm/scenario.py` 的 `METRIC_NAMES` 尚未加入 `manoeuvres.*`,目前儲存庫情境不在 bands 引用(否則 Python 結構檢查判為未知指標);F01–F03 改以時間表條件/必要事件引用 `manoeuvre*` 事件啟用。
必要事件 `after`/`expect` 的事件 itemId 會被結構檢查當成時間表項目 id(`Scenario.ItemIdRefersToTimeline`),故情境以「時間表項目於 `manoeuvreMeasured <id>` 時觸發、必要事件 expect 該項目的 `timelineTriggered`」間接引用特定量測。

### 2.5 快照、雜湊與重播

- `AssessmentSnapshot.Manoeuvres`(`ManoeuvreState`:已完成紀錄、進行中量測 `Active`/`Swing`/`Mob`(含未確定的開始序列與暫存樣本 `Buffer`)、穩定直航樣本、最近舵令/轉速令/側推/車鐘)經 JSON 深拷貝;
  狀態不含 NaN/∞(未定值以 null)。舊快照無此欄位 → 還原為初始狀態。
- 雜湊鏈:與 RouteTracker 以外的評估追蹤器相同,不進雜湊(評估事件以 `_seq − _assessmentSeq` 排除);量測由已進雜湊的物理狀態決定,重播必然相同。
  時間表條件引用 `manoeuvre*` 事件時,時間表的觸發狀態照常進雜湊(新功能使用者的雜湊才會不同)。
- 重新評分(`--rescore`)的影子評估模組同樣建立追蹤器(只讀非評估事件與指令觀察),結果相同。

## 3. F01 操縱特性體驗(每船一版;滿載與壓載各一輪)

**決定:每船 × 每裝載各一檔**(`F01_FSB1_full`、`F01_FSB1_ballast`、`F01_FSB2_full`、`F01_FSB2_ballast`),不以主控程序 `--loading` 覆寫:
進入速度(No.1 滿載 14.5 kn、壓載 14.7 kn、No.2 滿載 12.8 kn、壓載 13.3 kn)、恢復航速門檻、慣性停船進俥轉速、車令(No.2 無 EFAS → FAS)、
簡報與提示中的海報/試俥數值、Schilling 段(只 No.1)與資料品質說明都不同,分檔讓簡化教官模式的情境清單直接可選,也讓紀錄標頭的情境 id 指明是哪一輪。
四檔以同一流程維護(結構相同,差異只在上述數值與文字)。

流程(開闊深水 1,000 m、無風流;自動舵保持 000°):

1. 每段:`predict…`(提示依駕駛台海報/引水卡預測,學員以 `acknowledge {kind: prediction, itemId: predict…, note: 預測值}` 記錄)→
   `execute…`(確認後或 `familiarisation.predictionTime_s` 後提示執行)→ 學員下令 → 量測完成 → `recover…`(回正舵、自動舵回 000°、恢復航速;恢復不是學習重點)→
   下一段需「`familiarisation.settle_s` 後(自 recover 或 skip)、航速恢復、航向 358°–002°」連續 30 s(`holdFor_s`)才提示,確保 MSC.137 穩定直航。
   每段的時間表項目(v0.2;學員操作沒被辨識、做錯或量測中止時流程不會停住):
   - `started…`(栓鎖):本段提示後(`predict…` 已觸發)3 s 內出現該量測 id 的 `manoeuvreStarted` 即觸發(邊緣觸發;其他段同 id 的舊事件不算)。
   - `demoPrep…`:`demoAfter_s` 後本段仍未辨識到量測開始,或本段的量測開始後中止(`manoeuvreAborted` 邊緣)→ 系統接手:回正舵、**恢復本段的車令**
     (No.1 `NAVF 182.2 rpm`/慣性停船段 `175 rpm`/Schilling `SAH`;No.2 `NAVF 133.8 rpm` 或 `NAVF`)、自動舵回 000°。學員誤下的其他量測(例如左迴旋段誤下右滿舵)因此結束(中止或未完整)。
   - `demo…`:`demoPrep…` 後 `settle_s`、航速帶與航向 358°–002° 連續 30 s,且 60 s 內沒有本量測 id 開始 → 下示範指令(此時必為穩定直航,量測一定開始)。
   - `recover…`(Schilling 段為 `endSchilling…`):本段的 `manoeuvreMeasured`(3 s 邊緣視窗;Schilling 為本段栓鎖 + 航向 170°–178°)。
   - `skip…`:執行提示後 2,400 s(情境常數,含倒俥後恢復航速的時間;未放入參數集,因新增參數會改變參數集 sha256 而使既有黃金檔 `assessment-E07-600s.json` 不一致,
     之後可改為 `familiarisation.segmentTimeout_s` 並重產黃金檔)仍未完成 → 訊息「略過此段」、回正舵、恢復車令、自動舵 000°;
     必要事件 `measured…` 為 missed,下一段照常提示;最後一段略過則照常結束。
2. 段落:35° 左迴旋 → 35° 右迴旋(到 450°)→ 10/10 Z 形(左舷先;示範反舵以航向扇區觸發,扇區門檻依自動舵穩定航向並提前約半個樣本,
   No.1 滿載 `< 350.9° / > 10.0°`、No.1 壓載與 No.2 壓載 `350.5° / 9.6°`、No.2 滿載 `350.7° / 9.8°`;實跑反舵時航向與執行航向差 ≤ 0.6°,超越角可比)→
   全速倒俥緊急停船(舵正中;學員先 STOP 再 EFAS 亦量測為 crashStop)→ 停俥慣性停船(No.1 以 175 rpm 恢復 14.1 kn)→(No.1)Schilling 舵慢速進約 6 kn 下 35° 與 70° 各迴轉到 180°(航向扇區回正舵,量測為未完整但有戰術直徑)。
3. 評估:不設硬性失敗;必要事件只記錄預測與量測完成時刻(扣分 0、passMark 0);講評看 `summary.manoeuvres`。
4. 無人操作的示範流程長度:No.1 滿載約 6,740 s、壓載約 6,160 s、No.2 滿載約 5,700 s、壓載約 4,920 s(含每段的預測/等待與示範準備 2.5 分鐘);學員即時操作約 60–75 分鐘。
   恢復航速段(特別是緊急停船後)較長,帶領者可於教官站加速時間(`timeScale`,不影響確定性)。

No.2 壓載:`data/ships/FSB2/coefficients.ballast.json` 尚未產生,主控程序以 `--dynamics placeholder` 執行(量測 `dynamics` 為暫代模型,比較一律不可比);
係數以 `src/Tools.Calibration` 產生後免改情境(引擎依係數檔選 MMG)。

## 4. F02 低速、側推與壓載風壓(每船一版)/ F03 兩船差異與淺水

**F02**(`F02_FSB1_low_speed`、`F02_FSB2_low_speed`;麥寮港內示意座標、水深 14 m、壓載、東北風 15 kn 以 `setEnvironment ramp_s 900` 增至 25 kn):
A 側推 @ 6–7 kn 60 s(`thrusterTurn`,No.2 海報 5 kn 以上無效)→ B 舵正中半速退至停船(`crashStop` 的 `headingChange_deg` = 單俥倒俥艉偏)→
C 近零速側推 120 s(與 A 比較)→ D 微速進 020°(東北風在右舷艏約 25°;v0.1 的正頂風 045° 風力左右對稱,實跑艏偏只有 −0.014°)約 3 kn 後停俥:
停俥漂移量測(`inertiaStop`、`mode: drift`,300 s 或 0.5 kn;No.1 實跑 5 分鐘艏向 −6.5°,每分鐘 0.0 / 0.0 / −0.8 / −2.6 / −6.5°,艏被吹向左)→
E 左滿舵 35° → 右滿舵 35°(`rudderSwing`;No.1 圖說 19.2 s;No.2 單泵 28 s,學員以 `steeringMode {pump: both}` 改雙泵重做 14 s——時間表無泵切換動作,未切換時 300 s 後結束)。
只用現有物理(MMG 側推隨航速衰減、右旋槳倒俥側力、Blendermann 風力);No.2 壓載同樣暫以暫代模型(無側推航速衰減、無風致偏轉,只供流程演練)。
各段同 F01 加入本段量測開始栓鎖、示範準備(側推歸零、舵正中、恢復該段車令與自動舵)、本段量測完成(邊緣觸發)與逾時略過;D 段新增 `measuredBlowOff` 與必要事件 `measuredBlowOff`。

**F03**(`F03_FSB1_channel`、`F03_FSB2_channel`;航線/航道多邊形與 E07 相同):兩船同航速 7.5 kn(未指定車鐘,引擎取該水深平衡轉速)、
航道外 14 m 右滿舵 35° 迴轉 360°(航向回到 052° 扇區時回正舵;起點向航線左側偏 150/205 m,迴旋偏移後回到航線)→ 自動舵 051.5° 進航道
(航道 12.5 m、`ukcPolicy` 引用 `navigation.ukc.channel_fraction`,`assessment.ukc.min` 引用 `navigation.ukc.minimum_m`,硬性失敗 `navigation.ukc.hardFail_fraction`)
→ 自動舵中 WP2/WP3 依航段轉向(學員手操舵時不介入)→ **WP2–WP3 航段**(速限 8 kn)航向 065°–075° 時舵正中緊急全速退
(v0.1 在 WP3 後執行,但 WP3 → BERTH 為內港速限 5 kn,照腳本 7.5 kn 航行即超速並觸發 speed bandFail)→ 量測完成後以微速(No.1 60 rpm、No.2 52 rpm,約 4.1–4.3 kn)
自動舵 070° 續航過 WP3 後結束(實跑無 speedLimitExceeded;必要事件 `measuredCrash` 改 expect `recoverCrash`)。講評比較兩份紀錄:
`manoeuvres` 的 `turning35.starboard`(淺水戰術直徑)與 `crashStop`(停船距離/時間),`summary.ukc.minSquat_m` / `min_m`。
示範結果:No.1 squat 0.09 m、最小 UKC 6.70 m;No.2 squat 0.52 m、最小 UKC 4.78 m;淺水戰術直徑 344 / 371 m;航道內停船 237 / 284 s;流程約 3,590 / 3,670 s。

## 5. E10 花蓮外海下風岸主機失效

FSB1 壓載(受風面積大)8 kn、010° 沿岸北上,離示意岸線 0.8 nm;東風 30 kn(陣風 0.25)、黑潮 1.8 kn 北流。
T+`emergency.engineFailure.injectAt_s` 主機失效(`mainEngine`,階段 emergency)→ +30 s 機控室估修 30 分鐘 → +240 s 公司:拖船 2 小時後到 →
+`repairEstimate_s` 機控室回報修復延後(不確定性);進入可錨泊水深帶(示意 60 m,岸線 0.45 nm 內;`reporting` 區域)時提示;淺灘禁區(3 m,0.12 nm 內)。
無人操作:約 T+27 分鐘進入可錨泊水深、T+49 分鐘擱淺(拖船 2 小時到不了 → 必須及時下錨)。
錨的物理不依賴:`acknowledge anchorReady`(錨備便)與 `anchorDecision`(決策,note 記錨別/節數/時機),教官判定後觸發手動項目 `anchorHolding` 結束;
主機修復為手動項目 `engineRepaired`。評估:首個相關動作、通知船長、漂流計算 `driftCalculation`、通報公司 `notifyCompany`、海巡/搜救 `notifySar`、
錨備便與下錨決策時間(皆引用 `emergency.engineFailure.*`),離淺灘最小距離容許帶(`track.minDistanceToNoGo_m`),硬性失敗擱淺/進入淺灘。
擱淺結束項目延遲 2 s,讓同一評估步先記錄硬性失敗(`endExercise` 定稿後不再評估規則)。

## 6. E11 人員落水(日間;夜間版改 startTimeUtc/能見度)

FSB1 滿載 12 kn、200°(自動舵),東北風 18 kn、流 0.8 kn 向 200°。T+120 s 左舷落水:目標 `MOB`(hold、航速 0、隨流、AIS 關、1 × 1 m),
置於當時自船船艉左舷外約 30 m(不與船體重疊);第二個複雜因素為北上貨船 T1(原本左舷對左舷 0.4 nm 通過,自船回到對向航向時於落水者附近會遇,並有 VHF 呼叫)。
評估:落水到首個舵令(`immediateAction_s`)、警報 `verbal`、通知船長、GMDSS/海巡 `notifySar`、回到落水者(時間表 `mobReturned` 於 `manoeuvreMeasured manOverboard` 觸發);
硬性失敗碰撞(含輾過落水者)。`minCpa` 指標會含落水者目標,故不設 CPA 容許帶,T1 的通過距離由講評逐目標檢視(`summary.collisionAvoidance.targets[]`)。
目標船 `trigger.time` 寫常數 120(Python 參考實作不保留 targets 的參數代入路徑)。測試以 Williamson 迴旋驗證:方式 williamson、首次轉舵朝落水舷、反應 4 s、回到 2 倍船長內、偏離原航跡 < 300 m。

## 7. 參數集新增鍵(`data/parameters/company-procedures.yaml`,只新增)

來源 `ASSUMED-FAM`、`ASSUMED-E10`、`ASSUMED-E11`(標題皆「假設值,待公司程序」):
`familiarisation.predictionTime_s` 120、`demoAfter_s` 90、`settle_s` 120、`manoeuvreComplete_s` 1800(v0.2 的逾時略過 2,400 s 暫為情境常數,見第 3 節);
`emergency.engineFailure.injectAt_s` 60、`repairEstimate_s` 1800、`tugEta_s` 7200、`firstAction_s` 60、`callMaster_s` 120、`driftCalculation_s` 600、
`notifyCompany_s` 900、`notifySar_s` 900、`anchorReady_s` 600、`anchorDecision_s` 1200;
`emergency.manOverboard.immediateAction_s` 30、`alarm_s` 30、`callMaster_s` 60、`notifySar_s` 300、`return_s` 1200。

## 8. 契約變更摘要

- `scenario.schema.json`(assessment 段,針對性修改):assessment 說明加入操縱量測啟用條件;`expectSpec.acknowledge.kind` enum 新增
  `prediction`、`driftCalculation`、`anchorDecision`、`notifyCompany`、`notifySar`;`bandRule` 說明加入 `manoeuvres.*` 指標。
- 指令:無新指令;`acknowledge {kind, itemId?, role?, note?}` 新增上列 kind(引擎接受任意 kind;`command.schema.json` 的說明文字與網頁 `ACKNOWLEDGE_KINDS` 待同步)。
- 事件:`manoeuvreStarted`、`manoeuvreMeasured`、`manoeuvreAborted`(第 2.4 節);v0.2 `manoeuvreStarted.data` 新增 `start_s`、`supersedes`(改判時重發)。
- 紀錄(v0.2):新增 `mode`(`drift`)、`targetsSha256`;values 新增 `reversalLate{n}_deg`、`stopToAstern_s`、`headingChangeAt{n}min_deg`、`maxHeadingChange_deg`;
  `order` 可為 `"STOP→EFAS"`;reversal 標記 `value` = 反舵時與 ψ 的差、minute 標記加 `headingChange_deg`;`approach.telegraph` 改為下令前車鐘、`steadyFor_s` ≤ 30。
- 情境(v0.2):F01–F03 時間表新增 `started…`、`demoPrep…`、`skip…` 項目(F02 另有 `measuredBlowOff`、`skipSwing`;F03 `recoverCrash`),無新欄位。
- summary:`manoeuvres[]`(未使用時省略)。快照:`assessment.manoeuvres`(未使用時省略)。

## 9. 驗證與待辦

驗證(v0.2 審查修正):`ManoeuvreStartSequenceTests`(舵輪拖曳 −3.5° × 10、鍵盤 5° 步進、NFU 0.18°/100 ms、操舵模式 NFU 推桿到 35° 放開都量測為 35° 迴旋;小修正不開始;
35° → 70° 改判;舵輪拖曳起舵與反舵的 10/10 Z 形完整且反舵差 < 1°;晚 5° 反舵 → reversalLate、不可比;STOP → 3 s → EFAS 為 crashStop;停俥 40 s 後倒俥仍量測;
approach 車鐘與 steadyFor_s;低速停俥漂移;同 id 指標取可比完整者;targetsSha256;序列中快照還原)、`ShipFamiliarisationFlowTests`(F01 舵輪拖曳/誤下右滿舵/STOP→EFAS/
逾時略過、示範 Z 形反舵準時、F02 bow blow-off ≥ 5°、F03 兩船全速退段無超速)。
驗證(v0.1):`ManoeuvreTrackerTests`(FSB1 滿載 35° 左右迴旋、10/10 與 20/20 Z 形兩舷、緊急停船、慣性停船、艏側推與逐步量測一致並與 trial_targets 比較;
啟用條件、物理不受影響、穩定直航、中止、No.2 單/雙泵擺動、MOB 分類、快照還原)、`ShipFamiliarisationScenarioTests`(10 個情境載入/驗證;
F01 四輪、F02 兩船、F03 兩船、E10 無人/有人、E11 Williamson 的關鍵事件;5 個情境 600 s 紀錄 → 重播一致);主控程序 `--validate` 全部無錯誤,
各情境實跑(F01 依流程長度、其他 600 s,`build/records/fam/`)並 `--replay` 雜湊/完整性/事件序列一致。

待辦:
0. (v0.2 審查未修)`trial_targets.json` 尚未存入紀錄標頭:重播時 `ManoeuvreTargets.For` 仍讀目前檔案,目標值更新後舊紀錄的「評估摘要」會與紀錄不一致
   (雜湊與事件序列不受影響,除非情境以 bands 引用 manoeuvres.outsideTolerance 等)。需改 `src/SimCore/Recording/`(RecordHeader 加 `manoeuvreTargets`
   {shipId, path, sha256, content})、`Replayer`(重播優先用標頭內容,`--rescore` 才讀目前檔案)與 `ManoeuvreTracker` 建構子注入目標值;本次範圍只能先在每筆紀錄記 `targetsSha256`。
1. Python `scenario.py` `METRIC_NAMES` 加入 `manoeuvres.*`(之後情境才可在 bands 引用);跨語言夾具:新情境無 C# 紀錄夾具,Python `test_scenarios_load_and_match_csharp_header` 對 10 個新情境 skip(需於 `src/Instruments.Web/tests/fixtures/` 錄製 `record-*-core.jsonl`)。
2. 網頁:`ACKNOWLEDGE_KINDS` / 教官站快速確認鈕加入新 kind(prediction 需可帶 itemId 與 note);講評站顯示 `summary.manoeuvres`(量測值、比較表、資料品質與 notes)。
3. `data/ships/FSB2/coefficients.ballast.json`(Tools.Calibration estimate);產生後 F01_FSB2_ballast、F02_FSB2 改用 MMG,側推 5 kn 以上無效才會呈現。
4. E10 接上錨的物理後,`anchorDecision` 改為下錨指令/錨抓住事件(必要事件 expect 換成錨事件);拖船物理完成後 `tugArrived` 改為實際拖帶。
5. 座標(麥寮港內、花蓮岸線、E07/F03 航道)以 ENC/港方資料校正;參數集數值待公司程序(D20)。
6. `Scenario.ItemIdRefersToTimeline` 若加入 `manoeuvre*` 例外(與 alert*/bnwas* 相同),必要事件即可直接 `expect {event: {kind: manoeuvreMeasured, itemId: turning35.port}}`。
