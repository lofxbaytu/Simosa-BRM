# 情境庫設計 — E02、E03、E04、E06(第一批補齊)

> 版本 v0.2(2026-10-18;v0.1 後依審查修正 E02–E04/E06,見第 7.1 節)。依據:規劃書第 3.4 節(營運水域)、第 8.1 節練習目錄與「設計規則」、第 9.1 節(簡化教官模式、練習包角色);
> 情境引擎見 `docs/design/scenario-engine.md`,契約見 `src/Contracts/scenario.schema.json`。本文件的港口與航道座標**全部為示意值,待 ENC 校正**(第 1.2 節)。

| 編號 | 情境檔 | 船/裝載 | 水域(示意區域檔) | 長度 |
|---|---|---|---|---|
| E02 | `data/scenarios/E02_coastal_traffic_strait.yaml` | FSB1 滿載 | 台灣海峽中部分道航行型航道(`data/areas/taiwan-strait-central-lanes.json`) | 55 min |
| E03 | `data/scenarios/E03_restricted_visibility_strait.yaml` | FSB1 滿載 | 同 E02(同一幾何的壞能見度版本) | ≤ 60 min |
| E04 | `data/scenarios/E04_pilot_boarding_kaohsiung.yaml` | FSB2 滿載 | 高雄南口 Charlie 引水站(`data/areas/kaohsiung-south-approach.json`) | ≤ 80 min |
| E06 | `data/scenarios/E06_taichung_channel_flood.yaml`、`E06_taichung_channel_ebb.yaml` | FSB1 滿載 | 台中港防波堤口(`data/areas/taichung-harbour-entrance.json`) | ≤ 30 min |
| E06b | `data/scenarios/E06b_tidal_stream.yaml`(2026-10-29) | FSB1 滿載 | 同 E06 幾何;潮汐與潮流以 `environment.tide` + `current.tidal` 表示,進港途中漲潮 → 平潮 → 退潮自動切換(scenario-engine.md 第 1.5.2 節) | ≤ 30 min |

## 1. 共同設計

### 1.1 第 8.1 節設計規則的落實

| 規則 | 落實方式 |
|---|---|
| 由真實的交接班與書面航程計畫開始 | 每個情境的第一個時間表項目為 `handover`(t = 1 s,`narrator` 語音朗讀交班要點);`briefing.text` 以「交班(地方時)」開頭並列出航程計畫、交通、常規命令;`briefing.attachments` 附航程計畫與交班檢查表;必要事件 `handoverChecklist`(交班後 `bridge.handover.checklist_s` 內 `acknowledge {kind: checklist}`)。 |
| 三步升級(正常 → 劣化 → 緊急) | 每個情境恰好一個 `phase: degraded` 與一個 `phase: emergency` 項目;**無人操作時依序觸發**(xunit `*_NoOperatorEscalatesInOrder` 固定時刻)。E06 的緊急階段同時以位置(距口門 0.8 nm)與時間後備(degraded 後 `navigation.channel.e06.emergencyAfter_s`)觸發,確保自動舵漂向防波堤擱淺前也會升級。 |
| 課程前段每次只注入一個新技術故障 | 本批四個練習屬課程前段(交通、能見度、引水、受限水域),**皆不注入技術故障**(測試檢查無 `injectFault`);升級來自交通、環境、通訊與人員(引水人/VTS)。 |
| 每組至少一個「引水人/教官是錯的」情境 | E04:引水船要求改左舷(上風舷)且航速保持 10 kn(超過 `pilot.boardingSpeedMax_kn`),必要事件 `boardingChallenge` 要求質疑;E02:他船以 VHF 要求「向左轉、右舷對右舷」(違反第 14 條且會橫越其船艏),必要事件 `vhfChallenge`。 |
| 每位學員至少輪 2 個角色 | `briefing.roles` 列出船長/船副/瞭望/舵工/引水人等;由帶領者輪替(本批不在核心強制)。 |
| 同一幾何的日/夜、好/壞能見度版本 | E02(黃昏、能見度 10 → 6 nm)與 E03(能見度 2 → 0.5 → 0.2 nm)共用同一航道與航線;E06 以漲潮/退潮兩個版本共用同一幾何與時間表。 |

### 1.2 示意資料的限制(待 ENC 校正)

- 三個新區域檔的 `source`/`description` 與情境檔頭都標註「示意,待 ENC 校正」;座標依公開地理常識放置,誤差可達數百公尺,**不可用於實際航行或評估**。
- 台灣海峽沒有 IMO 採用的分道航行制;`taiwan-strait-central-lanes.json` 是依規劃書「分道航行型航道」需求虛構的北行/南行航道(各 2 nm)與 0.5 nm 分隔帶,位置在彰化外海約 18–25 nm,水深一律 60 m。
- 高雄 Charlie 引水站位置與 E05 原點相同;第二港口口門、VTS 報告點 K1、等候區、出港航道延長線為示意;外錨地沿用既有 `kaohsiung-anchorage.json`(E04 的 `anchorage` 區域多邊形與之相同,測試檢查)。VTS 頻道與呼叫用語為示意。
- 台中港口門寬 350 m、航道 125°、北/南防波堤(禁區、水深 2 m → 擱淺)、導燈位置皆為示意;潮流值取規劃書第 3.4 節(漲潮 085° 2.5 kn、退潮 265° 3.0 kn),流場為均勻流(以時間表漸變表示「航道中流速增加」與「防波堤內流壓消失」),非港方流況資料。
- `taichung-harbour-entrance.json` 含 `land`(海岸與防波堤)供雷達陸地回波;其檔名排序在 `kaohsiung-approach.json` 之後(Sensors.Radar 測試以 `Land[0]` 為高雄海岸)。其他新檔不含 `land`。
- `taichung-inner-harbour.json`(審查修正新增,不改既有檔):E06 迴船池前減速區(口門內沿航道 900–1800 m、寬 350 m,6 kn)與出港船設計航跡(中心線北側 110 m 的 4 點);不含 `land`。
- 情境的航線航點、區域多邊形與區域檔逐點相同(`ScenarioLibraryTests.AreaFilesAreMarkedSchematicAndMatchScenarios`);以 ENC 校正時兩者須一起更新並重錄夾具。

### 1.3 參數集新增鍵(`data/parameters/company-procedures.yaml`,全部為「假設值,待公司程序」)

來源 `ASSUMED-HANDOVER`、`ASSUMED-E02`、`ASSUMED-E03`、`ASSUMED-E04`、`ASSUMED-E06`(標題皆以「假設值,待公司程序」開頭,測試檢查)。

| 鍵 | 值 | 用途 |
|---|---|---|
| `bridge.handover.checklist_s` | 300 s | 交班 → 交班檢查表(四個情境) |
| `navigation.xtd.coastalLane_m` | 1500 m | E02/E03 航道內走廊半寬 |
| `navigation.tss.separationZoneClearance_m` | 370 m | 與分隔帶距離容許帶(進入 = fail) |
| `navigation.encounter.earlyAction_s` | 360 s | E02 讓路船:會遇開始 → 首次行動 |
| `navigation.encounter.standOnAction_s` | 300 s | E02 直航船:讓路船未行動提示 → 自船行動/通知船長 |
| `navigation.vhf.challengeResponse_s` | 120 s | E02 VHF 干擾 → 回應/回報 |
| `navigation.vts.report_s` | 120 s | 通過 VTS 報告點 → 報告(E02、E04) |
| `navigation.xtd.approach_m` | 900 m | E04 引水站外接近段走廊半寬 |
| `navigation.cpa.restrictedVisibility_nm` | 1.0 nm | E03 cpaAlarm 門檻與 CPA 容許帶 warn |
| `navigation.restrictedVisibility.safeSpeed_kn` / `denseFogSpeed_kn` | 8 / 6 kn | E03 霧區航段速限(0.5 / 0.2 nm) |
| `navigation.restrictedVisibility.speedReduction_s` | 300 s | E03 超速時間容許帶 warn(fail 2×) |
| `navigation.restrictedVisibility.callMaster_s` / `checklist_s` / `lookoutReport_s` / `closeQuartersAction_s` | 120 / 300 / 180 / 120 s | E03 必要事件期限 |
| `pilot.boardingSpeedMax_kn` | 8 kn | E04 登船時對水速度上限 |
| `pilot.mpx_s` / `revisedEtaReport_s` / `challenge_s` | 600 / 300 / 180 s | E04 必要事件期限 |
| `pilot.e04.pilotLateAt_s` / `vtsHoldAt_s` / `emergencyAt_s` / `boardingSideChangeAt_s` / `holdResponse_s` | 420 / 900 / 1980 / 2400 / 300 s | E04 時間表時刻與等候減速期限 |
| `port.taichung.channelSpeedLimit_kn` / `innerSpeedLimit_kn` | 10 / 6 kn | E06 航道速限(口門外/內)/迴船池前減速區速限(對地速度;減速區 `zones[innerSpeedLimit]`) |
| `pilot.leeConfirmBeforeBoarding_s` | 300 s | E04 引水人登船前多久內的「下風舷確認」仍計入 `boardingLee`(審查修正新增) |
| `navigation.channel.leadingLightReport_s` / `outboundResponse_s` / `e06.emergencyAfter_s` | 60 / 90 / 300 s | E06 必要事件期限與緊急階段時間後備 |
| `manoeuvring.channel.rudderOrdersMax` | 40 次 | E06 舵令紀律容許帶 warn(fail 2×) |

既有鍵沿用:`navigation.cpa.coastal_nm`/`hardFail_nm`/`channel_nm`/`channelHardFail_nm`、`navigation.encounter.detectionRange_nm`/`substantialAlteration_deg`、`navigation.xtd.channel_m`/`coastal_m`、`manoeuvring.rotLimit_degPerMin`、`assessment.passMark`。

## 2. E02 沿岸航行與密集交通/COLREG

**學習目標**:狀況覺知(多目標同時評估)、決策、讓路船及早大幅行動(第 8、16 條)、直航船在讓路船不行動時的行動(第 17 條)、追越與漁船群(第 13、18 條)、分道航行(第 10 條)、不盲從 VHF 避碰協議。STCW A-II/1 BRM;IMO 1.22。

**角色**:船長(帶領者,可被叫上駕駛台)、船副(必要)、瞭望(必要;語音提示由 `lookout` 腳本代表)、舵工、他船與 VTS(語音腳本)。

**場景**:16:20 地方時交班,FSB1 滿載北上,FAH 10.5 kn、自動舵 020°,北向流 1.0 kn、東北風 18 kn,能見度 10 nm;約 17:10 日落(T+30 起能見度 900 s 漸變至 6 nm)。10 艘目標:

| 目標 | 角色 | 無人操作時 |
|---|---|---|
| X2 | 右舷橫越,自船讓路(目標為直航船,`lastResort`) | T+15 CPA ≈ 0.1 nm |
| X1 | 左舷橫越的讓路船,`colreg.obey: false` | T+22 艏前 ≈ 0.23 nm |
| O1 | 右舷後方追越(讓路,只在 CPA < 0.4 nm 才讓) | T+25 右舷 ≈ 0.6 nm 正橫 |
| F1–F4 | 漁船群(T+25 出現,F2–F4 無 AIS,集中在航線上與左側) | F1 ≈ 0.03 nm |
| X3 | 近對遇南下,T+40 以 VHF 要求右舷對右舷 | T+45 左舷 ≈ 0.35 nm |
| S1、S2 | 南行航道對向船(無會遇) | 2.4 nm |

**升級**:normal(交班、X2 讓路)→ **T+10 min degraded**(`crossingNoAction`:瞭望報告 X1 方位不變)→ **T+25 min emergency**(`overtakingAndFishing`:O1 追越 + 漁船群,右轉空間受限)→ T+30 黃昏 → **T+40 VHF 干擾**(兩段 `otherShip` ch16 語音)→ T+55 結束(或通過 CL4)。

**評估**:
- 硬性失敗:碰撞、CPA < `navigation.cpa.hardFail_nm`。
- 容許帶:`collisionAvoidance.minCpa_nm`(warn 沿岸 0.5、fail 0.2 nm)、`track.maxXtd_m`(warn `coastalLane_m`、fail 1.3×)、`track.minDistanceToNoGo_m`(分隔帶;進入即 fail)、`manoeuvring.timeRotOverLimit_s`。
- 必要事件:`handoverChecklist`;`giveWayEarlyAction`(X2 `encounterStarted` → `encounterFirstAction` X2 ≤ `earlyAction_s`,逾時每秒 0.05 分);`standOnAction`(T+10 提示後新的舵令/車令/轉速令/解除自動舵、X1 首次行動,或教官「口頭」確認 ≤ `standOnAction_s`;**以自動舵改設定航向的改向**核心觀察為 `autopilotHeading`,但 `ExpectSpec.SimpleNames` 與 schema 尚未列入,故 `crossingNoAction` 的教官提示寫明此時按「口頭」確認,見第 7.1 節);`callMasterCrossing`;`vhfChallenge`(acknowledge report/verbal/callMaster);`vtsReport`(CL2)。
- 「改變幅度」:核心尚無以改向幅度計分的容許帶指標,以 `summary.collisionAvoidance.targets[].courseChange_deg / speedChange_kn / substantial`(門檻 `substantialAlteration_deg`)與 `timeToAction_s` 於講評呈現。TCPA 同樣只在講評(`tcpaAtDetection_min`)。
- 注意:`EncounterTracker` 把一次行動同時記為所有未行動會遇的首次行動,故 X1 的直航船行動以「T+10 後的新動作」判定。

## 3. E03 限制能見度沿岸

**學習目標**:第 19 條(安全速度、只以雷達發現的近距離情況、避免對正橫前方目標向左轉、聽到正橫前方霧號時減至最低舵效速度)、瞭望與雷達整合、減速決策、通知船長。SIRE 4.2.3。

**角色**:船長(帶領者)、船副、瞭望、舵工(皆必要;霧中手操舵)、機控室(語音)。

**場景**:與 E02 同航道;06:00 交班,能見度 2 nm,北向流 0.5 kn(速限以對地速度計)、西南風 8 kn。目標:R1 漁船(無 AIS,**航段 1** 沿航道 3.8 nm、中心線左 0.6 nm,對水 0.5 kn 向 200° 抵銷北向流而對地幾乎停止;自船 1 nm 內每 2 分鐘 `prolongedTwoShort`,以安全速度操作時也在 denseFog 之前聽到)、R2 機動船(無 AIS,自船距 CL2 0.25 nm 時出現在右舷前方約 2.3 nm(前 2.2、右 0.6 nm),120 s 後由 220° 轉 275° 轉向自船,出現起每 2 分鐘 `prolonged`)、S1/S2 對向船(AIS)。
R2 的出現距離經掃描決定:保持 FAH 不應變時 R2 最近距離約 0.14 nm(cpaHard),而 R2 出現 10–25 s 內減至 DSAH、停俥、向右 060–090° 的應變都 ≥ 0.59 nm(xunit `ScenarioLibraryReviewTests.E03_ReasonableResponsesToR2DoNotFailMinCpa`);只保持 SAH 不應變約 0.52 nm(`closeQuartersAction` missed)。

**升級**(位置觸發的霧區):normal(2 nm)→ **航段 1 degraded**(`fogThickens`:300 s 漸變至 0.5 nm,航段速限 `safeSpeed_kn`)→ R2 出現(`r2Heard` 20 s 後瞭望報告霧號)→ **航段 2 emergency**(`denseFog`:240 s 漸變至 0.2 nm,速限 `denseFogSpeed_kn`)→ 距 CL3 0.3 nm 或 T+60 結束。無人操作(FAH)時刻:581 / 1445 / 1487 / 2336 s(xunit 固定)。

**霧號**:核心支援目標聲號(`targetControl {id, sound}` → `soundSignal` 事件與 `targets[].sound`,鳴放 4 s),故以時間表 `repeat: {every_s: 120}` 的 `targetControl` 表示;名稱 `prolonged`(第 35 條 (a))、`prolongedTwoShort`(第 35 條 (c),教官站目前只顯示字串)。自船霧號由「能見度不良檢查表」確認代表。

**評估**:
- 硬性失敗:碰撞、CPA < `hardFail_nm`。
- 容許帶:`manoeuvring.timeOverSpeedLimit_s`(warn `speedReduction_s` 300 s、fail 2×;滿載船自 FAH 減到 8 kn 約需 5 分鐘,交班時能見度已 2 nm,良好做法是開場即減速)、`collisionAvoidance.minCpa_nm`(warn 1.0、fail 0.5 nm;不為 R1 改向時 R1 約 0.62 nm → warn)、走廊、分隔帶。
- 必要事件:`handoverChecklist`、`callMaster`、`fogChecklist`(進霧區後)、`lookoutReportR1`(R1 會遇開始 → `acknowledge {kind: lookoutReport}`;**教官站與 schema 列舉尚無此種類前以 `report` 代替**)、`closeQuartersAction`(R2 出現 → 車令/轉速令/舵令或 R2 首次行動 ≤ `closeQuartersAction_s`,逾時每秒 0.1 分)。

## 4. E04 進港與引水人登船(高雄南口)

**學習目標**:航程計畫監控(XTD、報告點、流壓修正、ETA 修正)、工作負荷、等候操船、質疑不當的登船要求、MPX。SIRE 4.3.3;IMO A.960;SOLAS V/23。

**角色**:船長(帶領者)、船副(必要)、引水人(帶領者扮演,必要)、VTS/引水站/代理行(語音)。

**場景**:08:00 交班,FSB2 滿載(7.2 m)距 Charlie 引水站 10 nm(西南方),FAH 10 kn、自動舵 045°;漲潮流向西北 1.2 kn(左舷橫流)、東北偏北風 22 kn(右舷為下風舷);原定 T+60 右舷登船。交通:等候引水的貨櫃船 INB1、漁船 FISH1(無 AIS)、T+33 出港油輪 OUT1(吃水受限)、T+55 引水船 PILOT(跟隨自船左舷後方待命)。

**升級**:normal → **T+7 degraded**(`pilotLate`:延誤約 20 分鐘)→ T+15 VTS 要求在西南 3 nm 外等候 → K1 報告點(VTS 回覆)→ **T+33 emergency**(`floodAndOutbound`:流 1.2 → 1.8 kn 600 s 漸變 + OUT1 由 `targetControl activate` 啟動)→ **T+40 引水船要求改左舷、10 kn**(「引水人/教官是錯的」)→ T+55 引水船出發 → 教官手動 `pilotOnBoard` → 30 s 後引水船離開(移除目標)→ 660 s 後結束;或 T+80 時間到。進入出港航道延長線/外錨地時 VTS 呼叫或教官提示。

**登船評估(教官手動項目代表)**:`pilotOnBoard`(教官按「引水人登船」)後 5 s 內,自動項目 `boardingSpeedOk` 以 `ownSpeed {metric: stw, lessThan: pilot.boardingSpeedMax_kn}` 判定;下風舷由教官手動項目 `leeConfirmed` 代表:實務上常在引水船靠上時先確認引水梯在下風舷,引水人才登船,故自動項目 `leeAtBoarding`(`pilotOnBoard` 與 `leeConfirmed` 都已觸發,且最近一次確認距今未超過 `pilot.leeConfirmBeforeBoarding_s`)不論先後都成立,`boardingLee` 期望此項目(登船後 120 s 內);`leeConfirmed` 可重複(`repeat: {every_s: 10, max: 5}`),太早按下時登船後可再按一次。

**引水船**:`follow {leader: own, bearing: 225, range: 120}`(左舷後方待命,船舯後 85 m、左 85 m)。核心跟隨邏輯在直線路徑穿過自船避讓矩形(船長/船寬各加 max(10 m, 引水船長) 邊界)時,先繞到「目前所在舷」的艉後過渡點;站位須在矩形之後,從右舷過渡點到左舷站位的直線才不會再穿過自船。原設定 240°/70 m 的站位在矩形旁,引水船自艏前接近時停在右舷後方 153°、約 110 m(審查發現)。

**評估**:
- 硬性失敗:碰撞、CPA < `hardFail_nm`。
- 容許帶:`track.maxXtd_m`(warn `navigation.xtd.approach_m`、fail 2×)、`track.timeOutsideCorridor_s`(fail 300 s)、`collisionAvoidance.minCpa_nm`。
- 必要事件:`handoverChecklist`、`vtsReport`(K1)、`revisedEta`(report)、`holdSpeed`(VTS 等候 → 車令/轉速令)、`boardingChallenge`(verbal/report)、`boardingSpeed`、`boardingLee`、`mpx`(checklist)。後三項在教官未按「引水人登船」時為 `notApplicable`。

## 5. E06 狹窄航道/防波堤口橫流(台中)

**學習目標**:流壓預判(偏航角)、過口門後流壓消失時及時回正、ROT 控制、舵令紀律(複誦、少而明確的舵令)、團隊監控引水人、導燈失效時改用雷達平行指標/ECDIS、口門會船。STCW A-II/2 操船。

**角色**:引水人(帶領者扮演,下舵令/車令,必要)、船長、船副(監控 XTD/ROT/航速,必要)、舵工(必要)、VTS(語音)。

**兩個版本**(同一幾何、同一時間表與評估):

| | 漲潮 `E06_taichung_channel_flood` | 退潮 `E06_taichung_channel_ebb` |
|---|---|---|
| 港外流 → 外航道 | 085° 1.5 → 2.5 kn(左舷橫流約 1.6 kn,壓向北防波堤) | 265° 1.8 → 3.0 kn(右舷橫流約 1.9 kn 並逆流約 2.3 kn,壓向南防波堤) |
| 初始車令 / 自動舵修正 | HAH 7.5 kn / 132° | FAH 10.5 kn / 116° |
| 無人操作 | degraded 368 s、emergency 668 s,983.7 s 擱淺於北防波堤(無岸壁效應時 987.2 s) | degraded 353 s、emergency 653 s,1800 s 時間到 |

東北風 25 kn(蒲氏 6 級)。

**變體 E06b `E06b_tidal_stream`(2026-10-29;時變流場)**:同一幾何、目標船與評估規則;流場改以 `environment.tide`(示意潮高表,相對 CD:T+8 前漲潮全速、T+8–11 斜率 42 %、T+11–13 高潮平潮平段、T+13–16 退潮 42 %、T+16 起退潮全速)+ `environment.current.tidal`(漲 085° 2.5 kn、退 265° 3.0 kn、`slackWindow_min: 20`)表示,
核心每步由潮高變化率決定流向/流速(`state.currentSource = tidal`、`state.current` 隨時間變化、`state.tideHeight_m` 輸出);時間表**不含 `setEnvironment`**(覆寫會停止時變):degraded 改為 `tideTurning`(T+8,VTS 通報提早轉流、引水人提示偏航角反向),導燈遮蔽 +90 s,emergency 仍為距口門 0.8 nm 或 degraded 後 300 s(OUT1 `scripted` 固定退潮修正角 321°,出現時多已轉為退潮),
潮高表的變化率為分段常數,潮流在表點**瞬間**切換:t=480 s 2.50→0.91 kn、660 s →0(平潮)、780 s 反向 1.09 kn、960 s →3.00 kn(無漸變;調和潮或更多中間點可平滑,scenario-engine.md 第 1.5.2 節)。`insideBreakwater` 只切換 OUT1 行為與提示(示意:均勻潮流場,口門內流壓不消失)。無人操作(自動舵 132° 不修正):degraded 480 s、導燈 570 s、emergency 662 s(距離觸發)、過口門 999 s、1800 s 時間到;漲潮把船壓向北側(XTD 約 −260 m),轉流後退潮把船壓回南側越過走廊,**不擱淺**(與漲潮版不同;`TidalStreamTests.E06b_NoOperatorTidalStreamTurnsFromFloodToEbb` 固定)。核心紀錄夾具 `record-E06b_tidal_stream-core.jsonl`(60 s,規則同其他夾具)。

**升級**:normal(交班、MPX 已完成)→ **航段 1 degraded**(`currentIncrease`:180 s 漸變至 2.5/3.0 kn)→ 90 s 後 `leadingLightsObscured`(訊息代表導燈被遮蔽)→ **距口門 0.8 nm 或 degraded 後 300 s emergency**(`outboundInChannel`:出港船 OUT1 由 `targetControl activate` 啟動,沿中心線北側 110 m 出港,左舷對左舷)→ 過口門 `insideBreakwater`(60 s 漸變至 0.3 kn;同時以 `targetControl {behaviour}` 把 OUT1 切換為航點模式)→ 到達迴船池、擱淺或 T+30 結束。

**出港船 OUT1**:流場為均勻流,`waypoints` 模式以「朝向航點」導航、不修正橫向偏移,強橫流下會被壓離設計航跡(審查發現:退潮版壓到中心線南側 +41–69 m 而與靠右的自船碰撞,漲潮版擦過北防波堤堤頭 14.6 m)。改為 `scripted` 固定對水航向 = 航跡 305° 加橫流修正角(7 kn 對水;漲潮 292°、退潮 321°,`asin(橫流/7 kn)`),對地沿中心線北側 110 m 直線出港;自船過口門(流壓消失)時切換為沿同一航跡的航點模式(弱流下偏移可忽略)。跨過 emergency 的時刻流速漸變已完成(degraded 後 180 s 漸變、emergency 最早在進入航段 1 後約 250 s),故修正角固定即可。

**速限**:航段 2(防波堤內)沿用航道速限 `channelSpeedLimit_kn`;內港 6 kn 自迴船池前減速區 `zones[innerSpeedLimit]`(口門內約 900 m 起,`type: speedLimit`)起算。滿載 FSB1 自 7.5–10.5 kn 減到 6 kn 需 5 分鐘以上,原本從口門起算 6 kn 並以超速秒數(warn 0 / fail 120 s)評估,使依簡報計畫進港必定 fail(審查發現)。`speed` 容許帶改以超過速限的最大幅度 `manoeuvring.maxOverSpeed_kn` 評估(warn 1.0 kn、fail 2.5 kn):過口門後 SAH 的簡報計畫漲潮 ok(0.75 kn)、退潮 warn(1.12 kn);口門內 400 m 再減至 DSAH 兩版皆 ok;退潮版完全不減速約 4.5 kn → fail。

**評估**:
- 硬性失敗:擱淺、碰撞、進入北/南防波堤。
- 容許帶:`track.maxXtd_m`(warn `navigation.xtd.channel_m`、fail 2×)、`track.timeOutsideCorridor_s`、`manoeuvring.timeRotOverLimit_s`(`manoeuvring.rotLimit_degPerMin`)、`manoeuvring.rudderOrders`(warn `manoeuvring.channel.rudderOrdersMax`)、`manoeuvring.hardOverCount`、`manoeuvring.maxOverSpeed_kn`(warn 1.0、fail 2.5 kn)、`collisionAvoidance.minCpa_nm`(航道門檻)。
- 必要事件:`handoverChecklist`、`leadingLightReport`(report/verbal)、`outboundResponse`(舵令/車令/轉速令或 verbal)。
- **岸壁效應**(規劃書第 6.2 節 B 項、第 458 行;`docs/design/physics-bank-effect.md`):兩版皆 `banks: { file: data/areas/taichung-harbour-entrance.json }`,
  區域檔 `banks[]` 4 段(示意):外航道南北邊坡 `TCH-CH-N`/`TCH-CH-S`(坡腳 = `channel.polygon` 南北兩邊,自航道西端至堤頭;1:5、航道 14 m、坡頂外 10 m →
  等效岸壁在坡腳外 10 m、強度 0.29)、南北防波堤港內側垂直岸壁 `TCH-BW-N-IN`/`TCH-BW-S-IN`(= 防波堤多邊形港內側邊)。防波堤外側面與迴船池周邊不定義
  (斜向/正面接近不在岸壁模型範圍)。等效岸壁距中心線 185 m、影響寬度約 90 m:**依簡報計畫沿中心線(XTD < 100 m)岸壁力恰為 0**,評估與參考腳本得分不變;
  刻意靠流壓側 110 m 時受該側邊坡吸力(漲潮約 25 kN、退潮約 7 kN)。xunit 固定時刻(航段 1 368/353 s、emergency 668/653 s、導燈、漸變完成)皆在船接近岸壁之前,
  **不變**;無人操作漲潮版被壓向北側邊坡時多了向岸吸力,擱淺由 987.2 s 提前至 983.7 s(`E06_BanksFromAreaFileActOnlyOffTheCentreline` 固定)。夾具已重錄(前 60 s 無岸壁力,
  雜湊因岸壁區塊追加而改變)。

## 6. 契約與 JSON 範例

本批**未變更** schema 與核心;只使用既有功能(審查修正亦同)。新出現的用法:

```json
{"type": "acknowledge", "args": {"kind": "lookoutReport", "role": "lookout", "note": "R1 雷達回波右舷 6 nm,無 AIS"}}
```
- 核心 `acknowledge` 接受任意 `kind` 字串(事件 `acknowledge.data.kind`),E03 必要事件以 `{acknowledge: {kind: lookoutReport}}` 比對並以 `report` 為備援。**待辦(不在本批範圍)**:`scenario.schema.json` `expectSpec.acknowledge.kind` 列舉與教官站 `ACKNOWLEDGE_KINDS`/快速鈕加入 `lookoutReport`。

時間表霧號與延後出現的目標(核心既有 `targetControl` 動作):
```json
{"id": "r2FogSignal", "repeat": {"every_s": 120, "max": 20},
 "when": {"type": "event", "kind": "targetActivated", "targetId": "R2"},
 "actions": [{"targetControl": {"id": "R2", "sound": "prolonged"}}]}
{"id": "OUT1", "trigger": {"type": "time", "time": 86400}}
{"targetControl": {"id": "OUT1", "activate": true}}
```
- 目標觸發無「手動」型別,以不會自然成立的 `time: 86400` 代表「由時間表啟動」。

登船航速的自動判定(教官手動項目 + 5 s 視窗):
```json
{"id": "boardingSpeedOk",
 "when": {"all": [
   {"type": "event", "kind": "timelineTriggered", "itemId": "pilotOnBoard"},
   {"not": {"type": "time", "atLeast": 5, "since": {"event": "timelineTriggered", "itemId": "pilotOnBoard"}}},
   {"type": "ownSpeed", "metric": "stw", "lessThan": 8}]},
 "actions": [{"message": {"to": "instructor", "text": "登船時對水速度在引水站要求以內", "severity": "info"}}]}
{"id": "boardingSpeed", "after": {"event": "timelineTriggered", "itemId": "pilotOnBoard"},
 "expect": {"event": {"kind": "timelineTriggered", "itemId": "boardingSpeedOk"}}, "within_s": 5, "penalty": 10}
```

審查修正新增的用法(皆為既有 schema/核心功能):
```json
{"id": "OUT1", "initial": {"heading": 321, "speed": 7},
 "behaviour": {"mode": "scripted", "script": [{"t": 0, "heading": 321, "speed": 7}]}}
{"id": "insideBreakwater", "when": {"type": "routeLeg", "atLeast": 2},
 "actions": [{"setEnvironment": {"current": {"set": 265, "drift": 0.3}, "ramp_s": 60}},
             {"targetControl": {"id": "OUT1", "behaviour": {"mode": "waypoints", "stopAtEnd": false,
               "waypoints": [{"lat": 24.3102, "lon": 120.46321, "speed": 8}, {"lat": 24.33418, "lon": 120.42585, "speed": 10}]}}}]}
{"id": "innerSpeedLimit", "type": "speedLimit", "speedLimit_kn": 6,
 "polygon": [{"lat": 24.29163, "lon": 120.49325}, {"lat": 24.28697, "lon": 120.50051}, {"lat": 24.28438, "lon": 120.49854}, {"lat": 24.28905, "lon": 120.49127}]}
{"id": "speed", "metric": "manoeuvring.maxOverSpeed_kn", "warn": 1.0, "fail": 2.5, "penalty": 5, "failPenalty": 15}
{"id": "leeConfirmed", "manual": true, "repeat": {"every_s": 10, "max": 5}}
{"id": "leeAtBoarding", "when": {"all": [
   {"type": "event", "kind": "timelineTriggered", "itemId": "pilotOnBoard"},
   {"type": "event", "kind": "timelineTriggered", "itemId": "leeConfirmed"},
   {"not": {"type": "time", "atLeast": 300, "since": {"event": "timelineTriggered", "itemId": "leeConfirmed"}}}]}}
{"id": "boardingLee", "after": {"event": "timelineTriggered", "itemId": "pilotOnBoard"},
 "expect": {"event": {"kind": "timelineTriggered", "itemId": "leeAtBoarding"}}, "within_s": 120, "penalty": 10}
{"id": "standOnAction", "expect": {"any": ["rudderOrder", "telegraphChange", "rpmOrder", "autopilotOff",
   {"event": {"kind": "encounterFirstAction", "targetId": "X1"}}, {"acknowledge": {"kind": "verbal"}}]}}
{"id": "PILOT", "behaviour": {"mode": "follow", "follow": {"leader": "own", "bearing": 225, "range": 120, "gain": 0.05, "maxSpeed": 18}}}
```
- `targetControl.behaviour` 會重設航點/腳本索引與 COLREG 狀態(核心 `TrafficManager.ApplyControl`,已進快照);事件 `targetControl`「行為切換:Waypoints」。
- 教官指令(不變):`{"type": "triggerTimeline", "args": {"id": "leeConfirmed"}}` 於登船前或登船後皆可;`{"type": "acknowledge", "args": {"kind": "verbal", "note": "直航船行動:自動舵 350 → 020 向右改向"}}`。

區域檔(節錄;新增鍵為各檔自有結構,Sensors.Radar 只讀 `land`,網頁建置時匯入但只解析 `berths`):
```json
{"id": "taichung-harbour-entrance", "source": "示意(位置誤差可達數百公尺;待 ENC 校正)",
 "channel": {"axis_deg": 125, "halfWidth_m": 175, "centreline": [{"name": "WP2", "lat": 24.295, "lon": 120.485}]},
 "breakwaters": [{"id": "northBreakwater", "polygon": [{"lat": 24.29649, "lon": 120.48587}]}],
 "tidalStreams": {"flood": {"set_deg": 85, "drift_kn": 2.5}, "ebb": {"set_deg": 265, "drift_kn": 3.0}},
 "land": [{"name": "北防波堤(示意)", "height_m": 8, "points": [[24.29649, 120.48587]]}]}
```

```json
{"id": "taichung-inner-harbour", "source": "示意(依 taichung-harbour-entrance.json 的示意航道換算;待 ENC 校正)",
 "speedLimitZones": [{"id": "innerSpeedLimit", "speedLimit_kn": 6, "alongFromEntrance_m": [900, 1800], "polygon": [{"lat": 24.29163, "lon": 120.49325}]}],
 "outboundTrack": {"offsetFromCentreline_m": -110, "course_deg": 305, "points": [{"name": "OUT-START", "lat": 24.28908, "lon": 120.49611}]}}
```

確定性:新情境只用既有狀態與事件(目標聲號、漸變、時間表、評估皆已進快照/雜湊);未使用新功能的既有情境檔與雜湊不變。審查修正只改 E02/E03/E04/E06 情境內容與新增區域檔/參數鍵,未改核心;其餘情境的狀態雜湊不變(參數集 sha256 只在紀錄標頭,不進狀態雜湊)。

## 7. 測試與夾具

- `tests/SimCore.Tests/ScenarioLibraryTests.cs`:載入/驗證(無錯誤、無警告)與設計規則、區域檔一致、各情境無人操作的三步升級與關鍵時刻、學員腳本(E02 讓路/直航/漁船群/VHF;E03 安全速度與雷達瞭望;E04 等候、質疑與登船航速兩種結果;E06 兩版以偏航角修正通過口門)必要事件 met,紀錄 → 重播雜湊/事件序列/summary 一致(重播後刪除紀錄,長情境 25 Hz 紀錄可達數百 MB)。
- 主控程序:各情境 `--validate` 無錯誤;`--duration 900 --timescale 10` 錄製後 `--replay` 一致。
- 核心紀錄夾具 `src/Instruments.Web/tests/fixtures/record-<id>-core.jsonl`(60 s,縮短為每 500 tick 一筆狀態);`src/Tools.Calibration/tests/test_scenario_params.py` 的 `REQUIRED_FIXTURES` 已加入五個 id(2026-10-29 加入 E06b)。情境 YAML 或引用的參數改動後須重錄(審查修正後五個夾具已重錄,`--replay` 雜湊/事件/summary 一致)。
- `tests/SimCore.Tests/TidalStreamTests.cs`(2026-10-29):E06b 與 E06 幾何/評估一致、無人操作漲 → 平潮 → 退與紀錄/重播一致;時變流場模型、覆寫、結構檢查與 C#↔Python 夾具。
- `tests/SimCore.Tests/ScenarioLibraryReviewTests.cs`:審查修正的回歸測試(第 7.1 節)。

### 7.1 審查修正(2026-10-18)

| 發現 | 修正 | 回歸測試(`ScenarioLibraryReviewTests`) |
|---|---|---|
| E06 OUT1 被橫流壓離設計航跡(退潮版碰撞自船、漲潮版擦過北防波堤堤頭) | OUT1 改 `scripted` 固定橫流修正角,過口門時切換為航點模式;新增 `data/areas/taichung-inner-harbour.json` `outboundTrack` | `E06_OutboundPassesPortToPortAndClearsBreakwaters`(兩版 × 自船中心線/靠右 40 m:無碰撞、minCpa ok 且 ≥ `channel_nm`、會船時 OUT1 在自船左舷且 XTD −150 至 −70 m、距防波堤 > 1 倍船寬);`E06_InnerSpeedZoneAndOutboundTrackMatchAreaFile` |
| E06 速限容許帶使依簡報計畫進港必定扣分、退潮版不及格 | 內港 6 kn 改由迴船池前減速區起算;`speed` 容許帶改評估超速幅度 | `E06_BriefedPlanPassesWithSpeedBand`(簡報計畫及格、漲潮 ok/退潮 warn;口門內 400 m 再 DSAH 兩版 ok) |
| E03 minCpa 對 R2 幾乎達不到、停俥反而 fail | R2 改在約 2.3 nm 出現(其餘腳本不變) | `E03_ReasonableResponsesToR2DoNotFailMinCpa`(5 種應變:R2 ≥ 0.5 nm、容許帶非 fail、無硬性失敗) |
| E02 以自動舵改向的直航船行動不算數 | `standOnAction` 加 `{acknowledge: {kind: verbal}}`,教官提示寫明;核心/schema 加入 `autopilotHeading` 列為待辦 | `E02_StandOnAutopilotAlterationCountsWithInstructorConfirmation` |
| E04 先按「下風舷確認」再登船被判 missed 且無法再按 | `leeAtBoarding` 自動項目 + `leeConfirmed` 可重複 + 參數 `pilot.leeConfirmBeforeBoarding_s` | `E04_LeeConfirmedBeforeBoardingCounts`(登船前 30 s;登船前 900 s 再於登船後 20 s 補按) |
| E03 R1 在航段 2,安全速度時聽不到 R1 霧號 | R1 移到航段 1(沿航道 3.8 nm、左 0.6 nm,對地幾乎停止) | `E03_R1FogSignalHeardAtSafeSpeed`(3 種安全速度腳本:R1 霧號 ≥ 1 次、在 denseFog 之前聽到) |
| E04 引水船停在右舷後方 | follow 站位改 225°/120 m(在避讓矩形之後) | `E04_PilotBoatEndsOnPortQuarter`(出港 1000 s 後相對方位 190–260°、0.04–0.09 nm) |
| E03 `lookoutReport` 不在 schema 列舉 | **未修**:需由契約負責人以針對性 Edit 在 `expectSpec.acknowledge.kind` 列舉與教官站 `ACKNOWLEDGE_KINDS` 加入;改情境只用 `report` 會破壞既有 `ScenarioLibraryTests`(斷言第一分支為 `lookoutReport`),不在本次範圍 | — |

## 8. 待辦

1. 以 ENC/港方資料校正三個區域檔與情境航點(含 E05 共用的 Charlie 引水站),重錄夾具。
2. schema 與教官站加入 `acknowledge` 種類 `lookoutReport`;教官站顯示/播放 `prolongedTwoShort` 等聲號。
2a. 核心 `ExpectSpec.SimpleNames`、Python `scenario.py` 與 `scenario.schema.json` 加入 `autopilotHeading`(核心已觀察 `ActionKinds.AutopilotHeading`);之後 E02 `standOnAction` 可改為自動判定並移除「口頭」備援。
3. 「改變幅度」「TCPA」容許帶指標(目前只在講評)。
4. E06 加岸壁效應 `banks`(岸壁效應功能合併後);E06 夜間版(導燈實際熄滅)、E02 夜間版。
5. 參數集假設值依公司 SMS(DWO-007 等)核定後 bump version 並重新驗證引用情境。

---

# 情境庫課程後段 — E15、E16、E17、E18、E21(第二批)

> 版本 v0.1(2026-10-18)。依據同第一批(規劃書第 3.4、8.1 節與設計規則);拖船/泊位/繫纜見 `docs/design/physics-tugs-anchors-mooring.md`。
> 本批**未變更** schema 與核心(`src/**`),只新增情境、示意區域檔與參數鍵。港口、航道、泊位座標**全部為示意值,待 ENC 校正**。

| 編號 | 情境檔 | 船/裝載 | 水域(示意區域檔) | 長度 |
|---|---|---|---|---|
| E15 | `data/scenarios/E15_distraction_workload_kaohsiung.yaml` | FSB2 滿載 | 高雄第二港口引航進港(`data/areas/kaohsiung-second-port-inbound.json`、`kaohsiung-anchorage.json`) | ≤ 30 min |
| E16 | `data/scenarios/E16_night_approach_keelung.yaml` | FSB1 滿載 | 基隆港北口(`data/areas/keelung-approach.json`) | ≤ 35 min |
| E17 | `data/scenarios/E17_typhoon_departure_kaohsiung.yaml` | FSB1 壓載 | 高雄港南端油品碼頭(`data/areas/kaohsiung-south-oil-berth.json`) | ≤ 45 min |
| E18 | `data/scenarios/E18_ukc_squat_anping.yaml` | FSB2 滿載 | 安平港油品碼頭進港航道(`data/areas/anping-approach.json`) | ≤ 55 min |
| E18b | `data/scenarios/E18b_tide_table.yaml` | FSB2 滿載 | 同 E18 幾何;潮汐以 `environment.tide.table` 表示(基底 CD 7.0 m + 潮高表),時間表無 `setEnvironment` 水深漸變 | ≤ 55 min |
| E21 | `data/scenarios/E21_cargo_heating_pressure_anping.yaml` | FSB1 滿載 | 同 E18(同一幾何、不同船與潮況) | ≤ 65 min |

## 9. E15 干擾與工作負荷過載(高雄引航進港)

**學習目標**:工作負荷管理、授權(delegation)、無干擾駕駛台(sterile bridge)、在噪音中偵測關鍵事件;1.39 工作負荷。

**場景**:09:30 交班,引水人已在 Charlie 登船(帶領者扮演),FSB2 滿載 HAH 約 9 kn,沿出港航道延長線南半側(軸線南 0.12 nm,航向 070°)進第二港口;漲潮向西北 0.8 kn(自動舵 074° 已修正)、東北風 18 kn。

**干擾(每 2 分鐘,T+2 至 T+22)**:貨控室卸貨計畫電話、PSC 檢查郵件、代理行 VHF、ECDIS 警報氾濫(T+8,並以每 20 s 一則、共 8 則的重複訊息表示)、公司海務電話、機控室電話、艏樓報告、VTS 廣播、代理行泊位變更、ECDIS 警報再次氾濫、PSC 追問。以 `voiceScript`(phone/vhfPort/bridge)與 `message` 表示;T+8 以 `injectFault { name: ecdisAlarmFlood, recordOnly: true }` 注入只記錄故障(`state.faults[]` 出現 `ecdisAlarmFlood`,核心行為不變,ECDIS/教官站據以反應;`recordOnly` 明示預期,`--validate` 不警告,第 16 節)。

**關鍵事件**:貨船 X1 在自船航線右側約 1.5 nm 漂航(會遇關注門檻外),T+13 起步以 10 kn 向 340° 橫越(自船為讓路船,X1 不避讓);會遇開始時刻落在干擾 5 與 6 之間。

**升級**:normal → **T+8 degraded**(`ecdisAlarmFlood`:警報氾濫 + 漲潮 0.8 → 1.4 kn,300 s 漸變;未再修正時航跡偏出走廊)→ **X1 進入 1.0 nm(或 T+21)emergency**(`crossingCloseQuarters`,引水人提醒)→ 到達 OUTER 或 T+30 結束。無人操作時刻:degraded 480 s、X1 會遇 798 s、emergency 1043 s、cpaHard 1300 s(xunit 固定)。

**評估**:
- 硬性失敗:碰撞、CPA < `navigation.cpa.hardFail_nm`。
- 容許帶:`track.maxXtd_m`(warn `navigation.xtd.pilotage_m` 450 m、fail 2×)、`track.timeOutsideCorridor_s`(fail 180 s)、`collisionAvoidance.minCpa_nm`(warn `navigation.cpa.pilotage_nm` 0.25 nm、fail 0.2 nm;0.5 nm 會大於走廊寬度而使驗證警告)。
- 必要事件:`handoverChecklist`;無干擾駕駛台 `sterileCargoCall`、`sterilePscEmail`、`sterileCompanyCall`(acknowledge verbal,note 記錄授權/延後)、`alarmFloodManaged`(verbal/report,指派專人處理警報);關鍵事件 `crossingDetected`(**X1 起步橫越 `timelineTriggered crossingTurn`(780 s)** → lookoutReport/report 或 X1 首次行動 ≤ `bridge.distraction.criticalDetection_s`,逾時每秒 0.05 分)、`crossingAction`(同起點 → X1 首次行動 ≤ `navigation.encounter.earlyAction_s`)。計時起點原為 X1 會遇開始:學員循跡修正流壓時會遇約 718 s 就開始(早於 X1 起步),偵測/反應期限被漂航中的 X1 提前消耗,且「看到漂航船就報告」被當成偵測到橫越(第 16 節)。
- 合理操作(xunit):每次干擾 20 s 內授權/延後、循跡修正流壓(每次 ≤ 4°,不被會遇追蹤誤判為避讓)、X1 起步(對地 > 2 kn)即瞭望報告 + DSAH + 向右 20°、X1 通過最近點後回航跡:必要事件 met、容許帶皆 ok、100 分;X1 最近約 0.31 nm、最大 XTD 約 410 m。

## 10. E16 夜間進港燈光混淆(基隆)

**學習目標**:夜間視覺狀況覺知、瞭望報告、雷達/ECDIS 與目視交叉核對、航程危險複審(航船布告);1.22。

**場景**:20:30 交班(夜間),FSB1 滿載自口門外 4.5 nm 依導航線(195°)進港至引水人登船點(口門外 0.8 nm);東北季風 25 kn(陣風 0.25)、沿岸流向 250° 0.6 kn(壓向右舷 = 西側淺礁)、HAH 約 8–9 kn、自動舵 193°。東北湧浪與背景岸燈無核心模型,以簡報與訊息表示(3D 視景另行實作)。

**目標**:F1 無燈無 AIS 漁船(導航線上偏東 0.06 nm、口門外 2.0 nm),自船距 1.2 nm 時才出現(`trigger: ownDistance`,代表湧浪雜波中的微弱回波);F2、F3 開集魚燈有 AIS 的漁船(背景燈光);OUT1 出港貨櫃船(緊急階段啟動,導航線東 0.35 nm,左舷對左舷)。**schema `lights` 列舉沒有「無燈」**,F1 以 `aisOn: false` 與訊息表示,未指定 `lights`(核心推定為 fishing),待辦第 14.5 節。

**升級**:normal(F1 雷達回波)→ **通過 K1 degraded**(`leadingLightsOut`:瞭望報告看不到後導燈 — 本週航船布告(簡報附件,交班時未讀)已公告熄滅;風 25 → 30 kn、流 0.6 → 0.9 kn)→ **距 PILOT 1.0 nm(或 T+25)emergency**(`outboundAndSet`:OUT1 出港 + 流 → 1.2 kn)→ 到達登船點、擱淺或 T+35。無人操作:F1 534 s、degraded 771 s、emergency 1090 s、F1 cpaHard 954 s、到達 1419 s。

**評估**:硬性失敗擱淺(淺礁以 `ukcPolicy` 區域水深 4 m 表示)、碰撞、cpaHard;容許帶 XTD(warn `navigation.xtd.harbourApproach_m` 500 m、fail 1.3× ≈ 西側淺礁距離)、走廊外時間、minCpa(warn 0.25、fail 0.2 nm);必要事件 `handoverChecklist`、`lookoutReportF1`(lookoutReport/report ≤ `navigation.night.lookoutReport_s`)、`vtsReport`(K1)、`leadingLightReport`(callMaster/verbal ≤ `navigation.night.leadingLightReport_s`)、`outboundResponse`(舵令/車令/轉速令/verbal ≤ `navigation.channel.outboundResponse_s`)。合理操作:F1 出現 40 s 內報告、以導航線西側 400 m 通過(F1 最近約 0.27 nm)、F1 通過後回線、OUT1 時口頭確認並減至 SAH:100 分。

## 11. E17 颱風準備與離港決策(高雄港南端油品碼頭)

**學習目標**:不確定下的決策、風險評估、公司溝通(1.39 決策)、一艘拖船離泊。

**場景**:10:00 交班,FSB1 壓載(卸貨完畢)右舷靠 KHH-SOB-1(碼頭線 000°、水域在西、碰墊/繫船柱比照 E14 示意型式),4 纜已繫;中央氣象署颱風海上警報,西風 15 kn 吹攏。原訂兩艘拖船都在 `tugs[]`(`initial.mode: free`,帶纜 90 s):TG1 高港11號(左舷艉)、TG2 高港12號(左舷艏)。

**拖船可用數 2 → 1**:degraded 以時間表動作 `tugControl { id: TG2, mode: unavailable }` 讓 TG2 離開(`physics-tugs-anchors-mooring.md` 第 2.4 節):TG2 失去出力、`state.tugs[]` status `unavailable`、發 `tugUnavailable`(不是 `tugCommand`,不會觸發 `firstTugOrder` 或使 `tugUsed` 達成);之後對 TG2 的指令以 `commandRejected {reason: tugUnavailable}` 拒絕。取代第十一輪以目標船 TUG2 駛離的替代做法(目標船已移除)。T+7 前學員仍可使用 TG2。

**升級**(壓縮時間):T+1 min 港方要求油輪兩小時內離港(決策點)→ T+4 公司詢問可否留港(壓力)→ **T+7 degraded**(`tugReduced`:拖船 2 → 1 + 風 15 → 25 kn,600 s 漸變)→ **T+25 emergency**(`outerBands`:25 → 40 kn、陣風 0.35,600 s 漸變)→ 離開泊位區(船舯離碼頭 > 110 m)60 s 後結束;或擱淺、T+45。無人操作:船在吹攏風下壓在碰墊上(纜未斷),全部決策逾時。

**評估**:硬性失敗擱淺、碼頭硬接觸、繫纜斷裂;容許帶 `berthing.maxEnergyRatio`(warn 0.8、fail 1.0)、港內速限超速時間;必要事件 `handoverChecklist`、`departureDecision`(notifyCompany/report ≤ `typhoon.departureDecision_s`)、`engineStandby`(notifyEcr)、`tugPlan`(verbal/report)、`tugUsed`(拖船縮減後 `tugCommand`)、**`departBeforeStorm`**(港方要求 → `clearOfBerth` ≤ `typhoon.e17.departBy_s` = 強風時刻 − 要求時刻,penalty 20;決策時機)。拖船工作時間與最大出力見 `summary.berthing.tugs`。
合理操作(xunit):早決策、TG1 帶纜待命,留艏倒纜右滿舵 DSAH 開艉 100 s,解倒纜後 SAS 退出,TG1 依開出角(< 15°)拉艉抵抗吹攏風(相對方位 270° 的拉力隨船轉動,持續全力會使船迴轉,故以開出角調節);約 T+17 離開泊位區(強風前),無碼頭硬接觸、無斷纜,及格。

## 12. E18 油碼頭進港 UKC/squat 限制(安平)

**學習目標**:潮窗規劃、UKC 監控、速度相關 squat、ECDIS 安全等深線、對「速度誘惑」的質疑;SIRE 4.2.2。

**幾何與潮汐(示意)**:浚深航道軸 080°、底寬 200 m、維護水深 CD 7.0 m;兩側淺灘 CD 5.0 m 以 `ukcPolicy` 區域水深表示(安全等深線以外,越出即擱淺);航道速限區(`port.anping.channelSpeedLimit_kn` 6 kn)。核心水深 = 基底常數 + 區域覆寫取淺,**潮汐以基底水深(維護水深 + 潮高)的 `setEnvironment waterDepth` 漸變表示**,淺灘的固定區域水深不隨潮(淺灘恆淺於基底,不影響結果);開場到航道外端的 1 nm 沿岸段同為基底水深。

**場景**:13:00 交班,FSB2 滿載 7.2 m 自口門外 4.0 nm,HAH 約 7.5 kn(計畫在 OUTER 前減至 SAH)、自動舵 077°;預報潮高 +1.9 m → 水深 8.9 m、靜態 UKC 1.7 m。MMG(ICORELS)squat 在 8.4–8.9 m 水深:DSAH 約 0.35 m、SAH 約 0.4–0.45 m、HAH 約 0.75–0.8 m、FAH 約 1.0 m。

**升級**:normal → **T+5 degraded**(`tideLower`:潮位比預報低 0.3 m 且提早退潮,8.9 → 8.6 m、600 s)→ T+12 碼頭要求加速到 8 kn 以上(速度誘惑)→ **通過 BAR(或 T+35)emergency**(`barShoaling`:前船回報沙洲段淺於海圖、潮位 → 8.4 m)→ 到達迴船池、擱淺或 T+55。測深儀淺水警報 `alerts.shallowWater_m` = `navigation.ukc.shallowAlarm_m`(龍骨下 0.8 m,含 squat)。無人操作(HAH 不減速):degraded 300 s、淺水警報約 585 s、emergency 1333 s、到達 2167 s;UKC 低於政策 1400 s 以上(fail)、最小 0.48 m(warn)、超速 1.3 kn(warn),不及格。

**評估**:`assessment.ukc.min` 與 `zones[channel].ukcMin` 皆為 `navigation.ukc.channel_fraction`(10 % 吃水,0.72 m;淺水沿岸進港段全程適用,開闊海 20 % 會使沿岸段誤扣);硬性失敗擱淺、`ukcHard`(< 5 % 吃水);容許帶 `ukc.min_m`(warn `navigation.ukc.minWarn_m` 0.6、fail `minFail_m` 0.45 m)、`ukc.timeBelowLimit_s`(fail 120 s)、`manoeuvring.maxOverSpeed_kn`(warn 0.5、fail 2.0 kn)、XTD(warn `navigation.xtd.dredgedChannel_m` 70 m、fail 1.4×)、走廊外時間;必要事件 `handoverChecklist`、`tideReport`(report/verbal ≤ `navigation.ukc.tideReport_s`)、`speedChallenge`(verbal/report)、`ukcAlarmResponse`(`alertRaised shallowWater` → 車令/轉速令 ≤ `navigation.ukc.alarmResponse_s`;警報未響時 notApplicable)。合理操作:開場減至 SAH、通過 BAR 減至 DSAH:UKC 最小約 0.91 m、警報未響、100 分。

**變體 E18b**(`E18b_tide_table.yaml`):同一幾何與升級腳本,潮汐改以 `environment.tide.table`(潮高表)表示,「比預報低且提早退潮」由表點呈現而非 `setEnvironment` 漸變;核心有效水深 = 海圖基底 + 潮高(scenario-engine.md 第 1.5.1 節),`state.tideHeight_m` 可供儀表顯示。

## 13. E21 貨物加熱系統故障下的到港壓力(安平)

**學習目標**:壓力下的短期策略、對上級(公司)的質疑與安全優先(1.39、MRM)、淺水航道的速限與 UKC 遵守;與 E18 同一幾何(重複性)。

**場景**:14:00 交班,FSB1 滿載 5.7 m、瀝青貨溫約 150 °C,自口門外 5.5 nm FAH 約 10 kn、自動舵 077°;潮高 +0.6 m 且退潮中(水深 7.6 → 7.2 → 6.6 m)。FSB1 的 squat 小(DSAH–FAH 約 0.07–0.3 m),UKC 主要受潮位影響:依計畫航速可在潮窗內進港(UKC 約 0.8 m),延誤或放棄則應錨泊等候。

**故障與壓力**:T+2 熱油鍋爐跳脫(`injectFault { name: cargoHeatingFailure, recordOnly: true }`:只記錄故障,`state.faults[]` 出現 `cargoHeatingFailure`;輪機長語音 + 警報訊息保留)→ T+5 修復時間不定(2–6 小時)→ 貨溫下降以每 5 分鐘一則訊息表示(核心無貨物熱力模型)→ **T+10 degraded**(`companyPressure`:公司要求全速、「航道速限不用管」,潮位 7.6 → 7.2 m)→ **通過 OUTER(或 T+25)emergency**(`tideWindowClosing`:港控通報潮窗約 30 分鐘、公司再催、潮位 → 6.6 m,1500 s)→ 到達迴船池、擱淺或 T+65。無人操作(FAH):degraded 600 s、emergency 899 s、到達 2264 s;航道超速約 3.5 kn(fail)、偏出走廊(fail)。

**評估**:硬性失敗擱淺、`ukcHard`;容許帶超速幅度(warn 0.5、fail 2.0 kn)、`ukc.min_m`、UKC 超限時間、XTD;必要事件 `handoverChecklist`、`heatingFailureReport`(callMaster/notifyCompany ≤ `cargo.heatingFailureReport_s`)、`companyChallenge`(verbal/notifyCompany ≤ `management.challenge_s`,penalty 15)、`windowDecision`(verbal/anchorDecision/notifyCompany ≤ `management.windowDecision_s`)。「放棄進港、錨泊等候」為同樣正確的決定(以 acknowledge anchorDecision 表示,之後不進航道即不會有速限/UKC 扣分)。合理操作:質疑公司、OUTER 前減至 SAH(約 6 kn)、決定依計畫進港:UKC 最小約 0.81 m、100 分、約 3520 s 到達。

## 14. 第二批共同:參數、契約、測試、待辦

### 14.1 設計規則的落實

| 規則 | 落實 |
|---|---|
| 交接班與書面計畫開場 | 第一個時間表項目 `handover`(t = 1 s,narrator 語音);`briefing.text` 以「交班(地方時)」開頭;附件含「…計畫」;必要事件 `handoverChecklist`。 |
| 三步升級 | 每個情境恰好一個 `degraded` 與一個 `emergency`;位置/會遇觸發者另有時間後備(E15 `bridge.e15.emergencyAt_s`、E16 `navigation.e16.emergencyAt_s`、E18 `navigation.e18.emergencyAt_s`、E21 `cargo.e21.emergencyAt_s`);無人操作時刻由 xunit 固定。 |
| 課程後段 | 本批不注入技術故障(只記錄故障以訊息表示);升級來自工作負荷、能見度/燈光、天候、潮汐與人員壓力。 |
| 「引水人/教官是錯的」 | E18 碼頭要求加速、E21 公司要求全速且不管速限、E17 公司詢問留港:皆以必要事件或講評引導問題要求質疑。 |
| 同一幾何的重複版本 | E18 與 E21 共用安平幾何(不同船、潮況與壓力來源)。 |

### 14.2 參數集新增鍵(`ASSUMED-E15`/`E16`/`E17`/`E18`/`E21`,標題皆以「假設值,待公司程序」開頭)

| 鍵 | 值 | 用途 |
|---|---|---|
| `bridge.distraction.response_s` / `alarmFloodResponse_s` / `criticalDetection_s` | 60 / 120 / 240 s | E15 干擾授權、警報氾濫處理、關鍵事件偵測 |
| `bridge.e15.alarmFloodAt_s` / `closeQuarters_nm` / `emergencyAt_s` | 480 s / 1.0 nm / 1260 s | E15 時間表 |
| `navigation.cpa.pilotage_nm` / `navigation.xtd.pilotage_m` | 0.25 nm / 450 m | 引航進港 CPA 與走廊(E15、E16 的 minCpa) |
| `navigation.night.lookoutReport_s` / `leadingLightReport_s` | 180 / 120 s | E16 |
| `navigation.xtd.harbourApproach_m` | 500 m | E16 導航線接近段走廊 |
| `navigation.e16.emergencyRange_nm` / `emergencyAt_s` | 1.0 nm / 1500 s | E16 緊急階段 |
| `typhoon.departureDecision_s` / `engineStandby_s` / `tugPlanReport_s` | 300 / 300 / 300 s | E17 決策 |
| `typhoon.e17.portOrderAt_s` / `tugReductionAt_s` / `stormAt_s` / `departBy_s` | 60 / 420 / 1500 / 1440 s | E17 時間表與決策時機 |
| `port.anping.channelSpeedLimit_kn` | 6 kn | 安平航道速限(示意) |
| `navigation.ukc.tideReport_s` / `speedChallenge_s` / `alarmResponse_s` | 180 / 180 / 60 s | E18 |
| `navigation.ukc.shallowAlarm_m` / `minWarn_m` / `minFail_m` | 0.8 / 0.6 / 0.45 m | 測深儀警報與 UKC 容許帶(E18、E21) |
| `navigation.xtd.dredgedChannel_m` | 70 m | 浚深航道走廊 |
| `navigation.e18.tideLowerAt_s` / `speedPressureAt_s` / `emergencyAt_s` | 300 / 720 / 2100 s | E18 時間表 |
| `cargo.heatingFailureReport_s`、`management.challenge_s` / `windowDecision_s` | 180 / 300 / 300 s | E21 |
| `cargo.e21.heatingFailureAt_s` / `companyPressureAt_s` / `emergencyAt_s` | 120 / 600 / 1500 s | E21 時間表 |

既有鍵沿用:`navigation.ukc.channel_fraction`/`hardFail_fraction`、`navigation.cpa.coastal_nm`/`hardFail_nm`、`navigation.encounter.*`、`navigation.vts.report_s`、`navigation.channel.outboundResponse_s`、`port.kaohsiung.berthSpeedLimit_kn`、`manoeuvring.rotLimit_degPerMin`、`bridge.handover.checklist_s`、`assessment.passMark`。

### 14.3 契約與 JSON 範例(皆為既有 schema/核心功能)

```json
{"id": "crossingDetected", "after": {"event": "encounterStarted", "targetId": "X1"},
 "expect": {"any": [{"acknowledge": {"kind": "lookoutReport"}}, {"acknowledge": {"kind": "report"}},
                    {"event": {"kind": "encounterFirstAction", "targetId": "X1"}}]},
 "within_s": 240, "penalty": 10, "latePenaltyPer_s": 0.05}
{"id": "alarmFloodRepeat", "repeat": {"every_s": 20, "max": 8},
 "when": {"type": "event", "kind": "timelineTriggered", "itemId": "ecdisAlarmFlood"},
 "actions": [{"message": {"to": "all", "text": "ECDIS 警報:多項警報未確認(重複)", "severity": "warning"}}]}
{"id": "F1", "shipType": "fishing", "aisOn": false,
 "trigger": {"type": "ownDistance", "point": {"lat": 25.18587, "lon": 121.75561}, "lessThan": 1.2}}
{"id": "westShoal", "type": "ukcPolicy", "waterDepth_m": 4, "polygon": [{"lat": 25.16515, "lon": 121.74164}]}
{"id": "tugReduced", "phase": "degraded", "actions": [
  {"setEnvironment": {"wind": {"trueSpeed": 25, "trueDir": 260, "gustiness": 0.25}, "ramp_s": 600}},
  {"targetControl": {"id": "TUG2", "behaviour": {"mode": "waypoints", "stopAtEnd": false,
    "waypoints": [{"x": -600, "y": -400, "speed": 8}, {"x": -1500, "y": -1500, "speed": 10}]}}}]}
{"id": "TG1", "attach": "aftPort", "connect_s": 90, "initial": {"mode": "free", "direction_deg": 270, "power_pct": 0}}
{"id": "departBeforeStorm", "after": {"event": "timelineTriggered", "itemId": "portOrder"},
 "expect": {"event": {"kind": "timelineTriggered", "itemId": "clearOfBerth"}}, "within_s": 1440, "penalty": 20}
{"id": "tideLower", "phase": "degraded", "actions": [{"setEnvironment": {"waterDepth": 8.6, "ramp_s": 600}}]}
{"alerts": {"shallowWater_m": 0.8},
 "assessment": {"ukc": {"min": {"fractionOfDraft": 0.10}},
   "hardFail": [{"id": "ukcHard", "rule": "ukc", "lessThan": {"fractionOfDraft": 0.05}}],
   "bands": [{"id": "ukcMin", "metric": "ukc.min_m", "direction": "below", "warn": 0.6, "fail": 0.45, "penalty": 10, "failPenalty": 25},
             {"id": "ukcTime", "metric": "ukc.timeBelowLimit_s", "warn": 0, "fail": 120, "penalty": 5, "failPenalty": 15}]}}
{"id": "ukcAlarmResponse", "after": {"event": "alertRaised", "itemId": "shallowWater"},
 "expect": {"any": ["telegraphChange", "rpmOrder"]}, "within_s": 60, "penalty": 10}
```
- 教官/學員指令不變:`{"type": "acknowledge", "args": {"kind": "verbal", "note": "授權大副處理卸貨計畫,引航中不簽字"}}`、`{"kind": "anchorDecision", "note": "潮窗已過,放棄進港錨泊等候"}`、`{"type": "tugControl", "args": {"id": "TG1", "mode": "pull", "direction_deg": 270, "power_pct": 60}}`。
- 區域檔(新增,不含 `land`):`kaohsiung-second-port-inbound.json`(`route`)、`keelung-approach.json`(`route`、`shoals[]`、`leadingLights`、`fishingGround`)、`kaohsiung-south-oil-berth.json`(`berths[]`,網頁建置時匯入解析)、`anping-approach.json`(`route`、`channel`、`shoals[]`、`chartDatum`)。
- 確定性:只使用既有狀態與事件(漸變、時間表、目標控制、拖船/泊位/繫纜、警報、評估皆已進快照/雜湊);未使用這些情境的既有情境雜湊不變(參數集 sha256 只在紀錄標頭)。

### 14.4 已知限制與取捨

- ~~只記錄故障不注入~~(第 16 節已解決):`ecdisAlarmFlood`(E15)、`cargoHeatingFailure`(E21)改以 `injectFault {name, recordOnly: true}` 注入,`faults[]` 會出現,`--validate` 不警告。
- E16 無燈漁船:schema `targets[].lights` 列舉沒有 `none`,只能以 `aisOn: false` 與訊息表示。
- ~~E17 拖船縮減以「目標船駛離」表示~~(第 16 節已解決):時間表 `tugControl` 動作與拖船狀態 `unavailable`。
- E18/E21 淺灘水深固定、不隨潮;潮汐只作用於基底水深。
- 會遇追蹤把 ≥ 5° 的自動舵改向記為首次行動:合理操作腳本循跡時每次改 ≤ 4°,避免把流壓修正誤判為避讓;實際課程中學員的流壓修正可能被記為對 X1/F1 的首次行動(講評時以 `summary.collisionAvoidance.targets[]` 判讀)。

### 14.5 測試與夾具

- `tests/SimCore.Tests/ScenarioLibraryLateCourseTests.cs`(18 項):載入/驗證(無錯誤、無警告)與設計規則、區域檔一致、五個情境無人操作的升級時刻與關鍵事件、合理操作腳本(必要事件 met、容許帶不誤扣、及格)、E17 單拖船與風力剖面、E18/E21 同一幾何;紀錄 → 重播雜湊/事件/summary 一致後刪除紀錄。
- 主控程序:五個情境 `--validate` 無錯誤無警告;`--duration 900 --timescale 10` 錄製後 `--replay` 一致(驗證後刪除)。
- 核心紀錄夾具 `src/Instruments.Web/tests/fixtures/record-E15…E21-core.jsonl`(60 s,每 500 tick 一筆狀態);`src/Tools.Calibration/tests/test_scenario_params.py` 的 `REQUIRED_FIXTURES` 已加入五個 id。

### 14.6 待辦

1. 以 ENC/港方資料校正四個新區域檔(基隆導航線與導燈、高雄南端油品碼頭泊位、安平航道水深與淺灘)並重錄夾具。
2. schema `targets[].lights` 加入 `none`(E16 F1);(「預期只記錄」故障已於第 16 節完成。)
3. 拖船到達延遲(`unavailable` 已有,恢復/延遲到達尚無)與颱風風場/湧浪模型;貨物熱力(貨溫)模型。
4. 區域水深隨潮(潮高網格或區域相對水深),E18/E21 淺灘與沙洲段可隨潮變化。
5. 參數集假設值依公司 SMS 核定後 bump version 並重新驗證引用情境。

## 15. 第二批審查修正(2026-10-18,E15/E16/E17/E21)

已重現的審查發現與處置(回歸測試 `tests/SimCore.Tests/ScenarioLibraryLateCourseReviewFixTests.cs`,修正前失敗、修正後通過;既有 `ScenarioLibraryLateCourseTests` 全部不變通過):

| 發現 | 處置 |
| --- | --- |
| E16 「無燈」漁船 F1 在狀態中為 `lights: fishing`(未指定時依 shipType 推定) | `f1OnRadar`(由 `targetActivated F1` 觸發)加 `targetControl: { id: F1, lights: none }`;F1 啟動時刻 534 s 不變,其後 `state.targets[F1] = { aisOn: false, lights: "none" }`。`targetControl` 動作為自由物件(command.schema.json `targetControl.args`),驗證器無警告。schema `targets[].lights` 加 `none` 仍為契約負責人待辦,之後可改在 `targets[]` 直接宣告。 |
| E17 留在泊位時 40 kn 吹攏風對纜繩沒有影響 | 保留外圍環流(T+25–T+35)吹攏風(強風中才離泊的碼頭硬接觸風險不變),新增 `windVeer`:外圍環流漸強完成後(`since outerBands ≥ 601 s` = 2101 s)風向 250° → 100°(離岸)、40 kn、陣風 0.35、漸變 600 s(持續到 T+45 結束之後,故既有 `environmentRampDone` 序列不變)。無人操作留港:4 纜最大張力 / MBL 由 0.08 升到 0.71(STERN 約 28 t),未斷纜;新增容許帶 `lineTension`(`berthing.maxLineTensionRatio`,warn 0.5 = OCIMF MEG4 合成纖維纜工作負荷限值、fail 0.8;假設值,待公司繫泊程序)。合理操作(T+11 前離泊)最大 0.18,不受影響。檔頭、簡報(預報風向轉離岸)與教官訊息改寫為一致的說法。 |
| E21 潮窗永遠不會關閉,UKC 評估在任何延誤下都不作用 | 新增 `windowClosed`:`all[ since tideWindowClosing ≥ 1800 s(港控「約三十分鐘」), zone channel inside, not passedWaypoint BAR ]` → `setEnvironment waterDepth 6.2 m`(300 s)+ 港控語音。沙洲為控制水深,已通過沙洲的船不受影響;在航道外錨泊等候(放棄進港)不觸發、不受 UKC 扣分;等候後再進入航道即觸發。延誤(潮窗將過 60 s 後停俥、3000 s 才以 SAH 進港):2900 s windowClosed,`ukc.min_m` 0.43(ukcMin fail)、`ukc.timeBelowLimit_s` 766(ukcTime fail);FAH 時 UKC 約 0.24 m < 5 % 吃水(硬性失敗)。依計畫 SAH 進港(BAR 約 2332 s,潮窗將過後 20 分鐘)與無人操作 FAH(2264 s 到達)結果不變。 |
| E15 外錨地區域離航線 2.4 nm 以上,`anchorageEntered` 永遠不觸發;簡報位置不對 | 新增區域檔 `data/areas/kaohsiung-anchorage-b-e15.json`(外錨地 B 區,E15 專用,示意,待 ENC 校正):航線北側、南界距航線 820 m(走廊 450 m 外約 0.2 nm)、北界 2600 m,沿航線 START 西方 300 m 至 FW2 附近。E15 `zones[anchorageB]` 與此檔相同;`zones[anchorage]`(A 區,同 kaohsiung-anchorage.json)保留。`anchorageEntered` 改為 `any[ zone anchorageB, zone anchorage ]`。開場停俥、隨漲潮漂流時 1654 s 進入 B 區(VTS 提醒);無人操作(未修正流壓,最大 XTD 476 m)與合理操作不進入。簡報改為「航線北側(走廊外約 0.2 nm 起)為外錨地 B 區,西北方約 2.5 nm 為外錨地 A 區」,檔頭同步。 |
| E15 檔頭 emergency 時間後備寫 T+18 | 改為 T+21(`bridge.e15.emergencyAt_s` = 1260 s)。 |
| E15 關鍵事件計時起點是 X1 會遇開始(循跡時早於 X1 起步) | **未修正**(見下)。 |

**E15 關鍵事件計時(第十一輪未修正;第 16 節已依 (a) 修正並同步調整測試)**:建議的兩種修法都會使既有測試失敗,而本批不可修改 `ScenarioLibraryLateCourseTests.cs`:(a) 把 `crossingDetected`/`crossingAction` 的 `after` 改為 `{ event: timelineTriggered, itemId: crossingTurn }` 時,AssessmentEngine 只計入 after 之後的 expect,既有合理操作腳本在 X1 會遇開始(循跡時約 718 s)+ 60 s 回報(早於 780 s 起步),`crossingDetected` 改由 `encounterFirstAction` 達成,`Assert.Equal("acknowledge:lookoutReport", …By)` 失敗——這正是審查指出的錯誤行為被寫進了測試;(b) 把 X1 初始位置外移會改變無人操作的固定時刻(會遇 798 s、emergency 1043 s、cpaHard 1300 s)。建議由測試負責人同時:情境改用 (a),並把 `E15_ScriptedSterileBridgeMeetsRequirements` 的瞭望報告改在 `crossingTurn` 之後(例如 X1 對地 > 2 kn 時先 `acknowledge lookoutReport` 再避讓);無人操作測試不受 (a) 影響(必要事件仍全部 missed)。

契約(皆為既有 schema/核心功能,schema 未改):

```yaml
# E16 f1OnRadar
- targetControl: { id: F1, lights: none }
# E17 windVeer 與容許帶
- id: windVeer
  when: { type: time, atLeast: 601, since: { event: timelineTriggered, itemId: outerBands } }
  actions:
    - setEnvironment: { wind: { trueSpeed: 40, trueDir: 100, gustiness: 0.35 }, ramp_s: 600 }
bands:
  - { id: lineTension, metric: berthing.maxLineTensionRatio, warn: 0.5, fail: 0.8, penalty: 10, failPenalty: 20 }
# E21 windowClosed
- id: windowClosed
  when:
    all:
      - { type: time, atLeast: 1800, since: { event: timelineTriggered, itemId: tideWindowClosing } }
      - { type: zone, id: channel, inside: true }
      - { not: { type: routeLeg, passedWaypoint: BAR } }
  actions:
    - setEnvironment: { waterDepth: 6.2, ramp_s: 300 }
```

```json
{"t": 600.0, "targets": [{"id": "F1", "aisOn": false, "lights": "none"}]}
{"kind": "timelineTriggered", "itemId": "windowClosed", "t": 2900.0}
{"bands": [{"id": "lineTension", "metric": "berthing.maxLineTensionRatio", "state": "warn", "value": 0.714}]}
```

確定性:新狀態只用既有機制(時間表、漸變、目標控制、區域追蹤),皆已進快照與雜湊;只改這四個情境,其他情境雜湊不變。四個情境的核心紀錄夾具 `record-E15/E16/E17/E21…-core.jsonl` 已依第 14.5 節程序重錄(Host `--duration 60 --timescale 10`,`--replay` 一致,縮短為每 500 tick 一筆狀態)。參數集未改(新增時刻以情境內常數表示並於本節記錄)。

## 16. 第十一輪後 C# 遺留四項(2026-10-18)

| 項目 | 處理 |
|---|---|
| E15 關鍵事件計時起點 | `crossingDetected`/`crossingAction` 的 `after` 改為 `{ event: timelineTriggered, itemId: crossingTurn }`(X1 實際起步橫越,780 s;與 X1 script t = 780 同時)。合理操作腳本(`E15_ScriptedSterileBridgeMeetsRequirements`)原本在 X1 會遇開始(循跡時約 718 s)+ 60 s 報告,早於新起點而不計入——這正是原本錯誤的行為(偵測的是漂航船,不是橫越);改為 X1 起步(對地 > 2 kn)即瞭望報告並避讓,`crossingDetected.by` 仍為 `acknowledge:lookoutReport`,並固定「會遇開始早於起步」。無人操作測試的固定時刻不變(必要事件仍全部 missed)。參數 `bridge.distraction.criticalDetection_s` 的說明文字(參數集)仍寫「會遇開始」,待參數集負責人改為「橫越目標起步」。 |
| 錨絞盤超載遲滯 | `stalled`/`slipping` 改為超載期間旗標:超過門檻時設立並發一次事件,張力連續 5 s 低於門檻 0.9 倍才解除;停轉期間 ≥ 0.9 × 過載拉力時不收鏈(`physics-tugs-anchors-mooring.md` 第 3.3 節)。C# `AnchorUnit` 與 Python `AnchorUnit` 同步;`ext_anchor_heave_house` 黃金檔重產(停轉事件 194 → 1、滑出 139 → 1),其餘黃金檔逐位元不變。E12 事件序列見下方夾具說明。 |
| 預期只記錄故障標記 | `injectFault` 物件形式加 `recordOnly: true`(`scenario.schema.json` `$defs.timelineAction.properties.injectFault`):`ScenarioValidator` 對標記者不警告、標在 modelled 故障上警告、非布林為結構錯誤(Python `structural_issues` 同步)。執行時與一般注入相同(加入 `faults[]`、發 `faultInjected`)。E15 T+8 注入 `ecdisAlarmFlood`、E21 T+2 注入 `cargoHeatingFailure`(原本的訊息/語音保留)。 |
| 時間表 `tugControl` 與 E17 拖船縮減 | 時間表動作 `tugControl`(args 同指令);拖船模式/狀態 `unavailable`(發 `tugUnavailable`,之後指令以 `tugUnavailable` 拒絕;`physics-tugs-anchors-mooring.md` 第 2.4 節)。E17 兩艘拖船都在 `tugs[]`,`tugReduced` 以 `tugControl { id: TG2, mode: unavailable }` 讓 TG2 離開,移除目標船 TUG2。`firstTugOrder` 語音改為不指名拖船(T+7 前學員可用 TG2)。 |

```yaml
# E15
- injectFault: { name: ecdisAlarmFlood, recordOnly: true }
- id: crossingDetected
  after: { event: timelineTriggered, itemId: crossingTurn }
# E17
- tugControl: { id: TG2, mode: unavailable }
# E21
- injectFault: { name: cargoHeatingFailure, recordOnly: true }
```

```json
{"kind": "tugUnavailable", "source": "timeline", "itemId": "TG2", "t": 420.0,
 "data": {"id": "TG2", "mode": "unavailable", "from": "free", "fromStatus": "free", "status": "unavailable", "force_t": 0.0}}
{"kind": "commandRejected", "data": {"command": "tugControl", "reason": "tugUnavailable", "args": {"id": "TG2", "mode": "pull"}}}
{"tugs": [{"id": "TG1", "mode": "free", "status": "free"}, {"id": "TG2", "mode": "unavailable", "status": "unavailable", "force_t": 0.0}], "faults": ["ecdisAlarmFlood"]}
```

確定性:新狀態(拖船 `Unavailable` 列舉值接在既有值之後;錨的兩個遲滯計時只在旗標設立期間追加進雜湊)皆進快照與雜湊;未使用這些功能的情境雜湊不變(`HashCompatibilityTests`)。E15、E17、E21(以及事件序列有變時的 E12)核心紀錄夾具依第 14.5 節程序重錄。

## 17. 第十二輪核心審查修正(2026-10-18)

| 項目 | 處理 |
|---|---|
| E15 起步前的首次行動吃掉 X1 `encounterFirstAction` | 計時起點改為 `crossingTurn`(780 s)後,循跡學員在 X1 會遇開始(約 718 s)到起步之間的正常動作(先減速、修正流壓一次改 5°)已發出 X1 的 `encounterFirstAction`(每筆會遇只發一次),`crossingAction` 等不到第二次而 missed(扣 10 分)。改以 expect 事件新欄位 `includePrior: true`(`scenario.schema.json` `$defs.expectSpec` 的 `event`;`AssessmentEngine.PriorFirstAction`):開始計時當下 X1 會遇仍進行中且已有首次行動 → `dt_s = 0` 判 met(與舊起點 `after encounterStarted X1` 的判定一致),`crossingDetected` 的 encounterFirstAction 分支同樣加上。回歸測試 `E15_ActionBeforeCrossingTurnStillCountsAsFirstAction`(750 s 車鐘 SAH / 自動舵 +5°)與 `EncounterTrackerTests.IncludePrior*`;未加欄位的規則(E02 `standOnAction`、E03)語意與雜湊不變。 |
| 已離開拖船的 `tugLineBreak {id}` | `InjectFault` 對 `unavailable` 拖船以 `commandRejected {command: injectFault, reason: tugUnavailable}` 拒絕,不加入 `faults[]`、不發 `faultInjected`、不建立故障應變紀錄。Python 參考伺服器(`mmg.py inject_fault`)不在本輪範圍,尚未同步(仍加入 faults[] 但不斷纜)。 |
| 改下錨指令不解除滑出旗標 | `AnchorUnit.Command` 各接受分支改呼叫 `ClearOverload()`(停轉 + 滑出旗標與兩個解除計時),Python `_clear_overload` 同步;剎車滑出後改下 brake/heave,再滑出時再發 `anchorBrakeSlipping`。七個 `ext_*` 黃金檔不變(案例中沒有在滑出期間改下指令)。 |
| 網頁 tugControl / unavailable | 網頁程式不在本輪範圍,由網頁負責人處理(`timeline.ts` 的 `TIMELINE_ACTION_KINDS`、`types/external.ts` 的 `TUG_MODES`/`TUG_STATUSES`)。 |

```yaml
# E15
- id: crossingAction
  after: { event: timelineTriggered, itemId: crossingTurn }
  expect: { event: { kind: encounterFirstAction, targetId: X1, includePrior: true } }
```

```json
{"kind": "requiredEventMet", "source": "assessment", "itemId": "crossingAction", "t": 780.0,
 "data": {"dt_s": 0.0, "within_s": 360, "by": "event:encounterFirstAction", "late": false, "penalty": 0, "prior_t": 749.0}}
{"kind": "commandRejected", "data": {"command": "injectFault", "reason": "tugUnavailable", "args": {"name": "tugLineBreak", "args": {"id": "TG2"}}}}
```

確定性:`includePrior` 只讀已在快照與雜湊中的會遇紀錄(`FirstActionAtS`、`Closed`),沒有新狀態;只有 E15 的內容改變(核心紀錄夾具 `record-E15…-core.jsonl` 依第 14.5 節重錄,60 s 狀態雜湊與重錄前相同)。

---

# 情境庫收尾 — E19、E20、E22(第三批)

> 版本 v0.1(2026-10-18)。依據:規劃書第 8.0 節(1.39 教室模式、E20 總結評估於訪船時執行)、第 8.1 節表列與「設計規則」、第 8.2 節(行為標記;自動評分僅輔助)。
> 本批**未變更** schema 與核心(`src/**`),只新增情境、一個示意區域檔與參數鍵;只記錄故障使用第 16 節的 `injectFault recordOnly: true`。港口、航道、泊位座標**全部為示意值,待 ENC 校正**。

| 編號 | 情境檔 | 船/裝載 | 水域(示意區域檔) | 長度 |
|---|---|---|---|---|
| E19 | `data/scenarios/E19_fire_isps_pilotage_kaohsiung.yaml` | FSB1 滿載 | 高雄第二港口引航進港(同 E15:`kaohsiung-second-port-inbound.json`、`kaohsiung-anchorage*.json`) | ≤ 30 min |
| E20 | `data/scenarios/E20_comprehensive_assessment_kaohsiung.yaml`(教官選 2 項)、`E20_comprehensive_assessment_default.yaml`(預設組合 E15 + E09) | FSB1 滿載 | Charlie → 第二港口油碼頭(`data/areas/kaohsiung-e20-passage.json`,新增) | ≤ 75 min |
| E22a–d | `data/scenarios/E22a_handover_conflict.yaml`、`E22b_fatigue_misjudgement.yaml`、`E22c_challenge_senior.yaml`、`E22d_cultural_communication.yaml` | FSB1 滿載 | 台灣西南外海開闊水域(無區域檔;水深 60 m) | 各 12 min |

## 18. E19 引航中火災/ISPS 警報(選配)

**學習目標**:優先排序(火災 > 保全事件 > 一般干擾)、領導(集合人力 vs 駕駛台當值團隊、指派現場指揮)、與機艙協調停主機時機、VTS 溝通、火災應變中的航跡與速度控制。TMSA 11(緊急應變)。

**場景**:10:00 交班,FSB1 滿載 HAH 約 7.5 kn、自動舵 074°,沿 E15 同一航線(Charlie → OUTER)進港;引水人在船(帶領者扮演)。漲潮 315° 0.8 kn、東北風 18 kn。出港油輪 OUT1 T+10 起沿航線北側 0.36 nm 左舷對左舷通過;外錨地錨泊船 ANC1。

**升級**:normal(T+4 `ispsAlert`:港口保全等級 2 + 艉部限制區門禁警報,訊息/語音)→ **T+8 degraded**(`fireAlarm`:`injectFault { name: fireAlarmEngineRoom, recordOnly: true }` + 機控室語音)→ **火警後 150 s emergency**(`fireConfirmed`:機艙確認火災、全船警報、機艙要求停主機)→ 確認後 120 s `vtsHold`(VTS 要求口門外等候區減速至 `fire.e19.holdSpeed_kn`)→ 集合決定逾時 `musterPrompt`(舵工/瞭望「也要去集合站嗎?」)→ 停俥回覆逾時 `engineStopped`(機艙依程序停主機 `injectFault mainEngine`)→ 確認後 720 s `fireExtinguished`(清除 mainEngine 與火警)→ OUTER 或 T+30 結束。

**只記錄故障**:`fireAlarmEngineRoom` 在故障目錄為 `modelled: false`。原規劃(第 14.4 節)在驗證器對只記錄故障一律警告時改以訊息/語音表示;第 16 節已加入 `recordOnly: true` 標記,本情境改為明示注入(`faults[]` 與儀器端警報清單可見、核心行為不變、`--validate` 無警告),火災的發展(確認、集合、撲滅)仍由時間表項目與語音表示。

**評估**:硬性失敗碰撞、CPA < `navigation.cpa.hardFail_nm`;容許帶 XTD(warn `navigation.xtd.pilotage_m`)、走廊外時間、minCpa(warn `navigation.cpa.pilotage_nm`)、等候區超速幅度 `manoeuvring.maxOverSpeed_kn`(warn 1.0、fail 2.5 kn);必要事件:

| id | 起點 | 期望 | 期限(參數) |
|---|---|---|---|
| `handoverChecklist` | handover | checklist | `bridge.handover.checklist_s` |
| `ispsReport` | ispsAlert | notifyCompany(依 SSP 通報 CSO;可授權他人) | `security.ispsReport_s` 600 s(較長:火災優先) |
| `fireCallMaster` | fireAlarm | callMaster | `fire.callMaster_s` 60 s(逾時每秒 0.1 分) |
| `fireEcrContact` | fireAlarm | notifyEcr | `fire.ecrContact_s` 120 s |
| `musterDecision` | fireConfirmed | verbal(note:當值團隊留駕駛台、現場指揮) | `fire.musterDecision_s` 120 s(penalty 15) |
| `engineDecision` | fireConfirmed | notifyEcr(回覆停主機時機) | `fire.engineDecision_s` 180 s;逾時則機艙停主機 |
| `fireReportVts` | fireConfirmed | report | `fire.vtsReport_s` 300 s |
| `holdSpeedReduction` | vtsHold | telegraphChange / rpmOrder | `fire.speedReduction_s` 180 s |

**無人操作**(xunit `E19_NoOperatorEscalatesInOrder`):ispsAlert 240 s、degraded 480 s、emergency 630 s、vtsHold 750 s、musterPrompt 752 s、engineStopped 812 s(mainEngine)、OUT1 啟動 600 s、fireExtinguished 1350 s、1800 s 時間到;必要事件全部 missed、不及格。
**合理操作**(`E19_ScriptedPrioritisationMeetsRequirements`):ISPS 20 s 內授權大副並通報 CSO、火警 20 s 叫船長、50 s 聯繫機艙、確認後 30/60/90 s 決定集合/回覆機艙/報告 VTS、等候指示 20 s 內 DSAH、自動舵循跡:全部 met、主機未停、容許帶皆 ok、100 分。

## 19. E20 綜合評估航程(A-I/12 總結性評估)

**定位**:完整課程與定期複訓的總結性評估(規劃書第 8.0 節),由訪船船長於訪船時執行並依第 8.2 節行為標記評分、建議雙評;**系統自動評分僅為輔助**(簡報、檔頭、評分 passMark 說明皆註明)。`scoring.passMark` 引用 `assessment.e20.passMark`(假設值 70,可與一般練習分開調整)。

**場景**:09:00 交班,FSB1 滿載 HAH 約 7.5 kn,自 Charlie 引水站沿 E15 航線 START → FW1 → FW2(高雄 VTS 報告點)→ OUTER → ENT(口門)→ IN1 → BERTH(港內航道 060°),左舷靠油碼頭 KHH-E20-OIL;拖船 TG1(右舷艏)、TG2(右舷艉)在港內待命(`initial.mode: free`,帶纜 90 s)。漲潮 0.8 kn,過口門後 2 分鐘內減弱為 0.3 kn(`harbourCurrent`)。出港船 OUT1 在自船距口門 1.2 nm 時由港內航道左側 150 m 出港(左舷對左舷)。

**示意區域檔** `data/areas/kaohsiung-e20-passage.json`(新增,不含 `land`):`route`(前 4 點與 `kaohsiung-second-port-inbound.json` 相同、ENT = `kaohsiung-south-approach.json` 口門)、`breakwaters`(南北防波堤,水深 2 m、noGo)、`channel`(港內航道 12.5 m,中心線兩側 250 m)、`shoals`(港內航道兩側 4 m 淺灘)、`berthSpeedZone`(泊位 500 m 速限)、`outboundTrack`、`fenderTypes`/`berths`(KHH-E20-OIL:碼頭線 060°、長 260 m、水域在右;碰墊與繫船柱配置比照 `mailiao-berth.json` MLO-OIL-3)。港內幾何為虛構示意,與 E09/E17 的港內示意幾何互不相同(三者都待 ENC 校正)。xunit `AreaFilesAreMarkedSchematicAndMatchScenarios` 檢查情境航線/區域/泊位/出港船航跡與區域檔逐點相同。

**故障/干擾模組(教官選 2 項)**:四個手動時間表項目即教官站「注入 X」鈕,動作為對應故障/語音;各模組的後續以「自注入起算」的自動項目進行:

| 鈕 | 動作 | 後續(自動) | 必要事件(未注入時 notApplicable) |
|---|---|---|---|
| `injectE07` | `injectFault steeringGear` + 舵工語音 | `e07EcrPrompt`(首動作逾時)、`e07EmergencySteering`(教官手動,清除故障) | `e07FirstAction`、`e07NotifyEcr`、`e07EmergencySteering` |
| `injectE08` | `injectFault gyroDrift {rate_degPerMin}` | `e08LookoutCue`、`e08GyroFail`(420 s 後 gyroFailure)、`e08GyroRestored`(手動) | `e08DriftDetected`、`e08AlarmAck`、`e08ManualSteering`、`e08CallMaster` |
| `injectE09` | `mainEngine`、`steeringGear`、`gyroFailure`、`blackout`/`radarFailure`(recordOnly) | `e09EmergencyPower`(45 s)、`e09VtsCall`、`e09PilotPrompt`、`e09EngineRestored`(360 s)、`e09InstrumentsRestored`(手動) | `e09FirstAction`、`e09AnchorDecision`、`e09NotifyVts` |
| `injectE15` | 干擾 1(卸貨計畫電話) | 每 2 分鐘:PSC 郵件、公司電話、ECDIS 警報氾濫(`ecdisAlarmFlood` recordOnly + 重複訊息)、港外時漲潮 0.8 → 1.4 kn(關鍵事件:航跡偏差) | `e15SterileCargoCall`、`e15SterilePscEmail`、`e15SterileCompanyCall`、`e15AlarmFloodManaged` |

建議注入時機:E15、E08 於 OUTER 前;E09 於 OUTER 後(距泊位約 1.3 nm,同 E09「距碼頭 1.5 nm」);E07 於口門後港內航道。教官 T+15 min 仍未注入時提示(`selectionReminder`)。

**三步升級(依注入順序)**:`firstModule`(degraded)= 任一 inject 項目已觸發;`secondModule`(emergency)= 任兩項已觸發(六種組合);與模組順序無關。各模組自身不改階段;泊位就緒 `berthReady` 設 `berthing`。

**預設組合版本** `E20_comprehensive_assessment_default.yaml`:供自動測試、無教官時的預演與基準錄影;`injectE15` 改為 T+`emergency.e20.defaultE15At_s`(300 s)、`injectE09` 改為進入航段 3(通過 OUTER)後 `emergency.e20.defaultE09After_s`(60 s)或時間後備 `defaultE09Fallback_s`;其餘內容與主檔完全相同(xunit `E20DefaultDiffersOnlyInDefaultTriggers` 以序列化後比對)。

**評估(所有指標類別)**:
- 硬性失敗:擱淺、碰撞、進入南/北防波堤、`ukcHard`、碼頭硬接觸 `quayContact`、斷纜 `mooringLineParted`。
- 容許帶:航跡 `track.maxXtd_m`(warn `navigation.xtd.pilotage_m`)/`track.timeOutsideCorridor_s`(口門與港內航段走廊 `navigation.xtd.harbourChannel_m` 150 m);UKC `ukc.timeBelowLimit_s`(港內航道 `navigation.ukc.channel_fraction`);會遇 `collisionAvoidance.minCpa_nm`(航道門檻 `navigation.cpa.channel_nm`/`channelHardFail_nm`);速限 `manoeuvring.maxOverSpeed_kn`、ROT;靠泊 `berthing.maxLateralSpeed_mps`(`berthing.approachSpeed_mps`/`approachSpeedFail_mps`)、`berthing.maxEnergyRatio`、`berthing.fenderOverloads`、`berthing.allFastPositionError_m`。
- 必要事件:共同 `handoverChecklist`、`vtsReport`(FW2)、`berthingBrief`(過口門後與引水人/拖船的靠泊簡報,checklist ≤ `berthing.e20.briefing_s`)+ 上表各模組。

**無人操作**(預設組合,`E20Default_NoOperatorEscalatesInOrder`):injectE15 300 s(degraded)、干擾 420/540/660 s、通過 OUTER 1822 s、injectE09 1882 s(emergency)、應急發電機 1927 s、引水人催促 1944 s;自動舵 074° 不隨航線轉向且未修正流壓 → 2009 s 擱淺於北防波堤(下錨決策/VTS 報告期限未到仍 pending)。教官選擇版本無人操作:T+15 min 提示、不升級、模組必要事件全部 notApplicable(`E20_WithoutSelectionRemindsInstructorAndModulesAreNotApplicable`)。

**合理操作**(同一學員腳本 `RunE20Reasonable`;容許帶不誤扣):交班檢查表 → 干擾 20 s 內授權/延後、警報氾濫指派專人 → 自動舵循跡(每次 ≤ 4°)→ FW2 VTS 報告並減至 SAH → OUTER 後手操舵循跡 → 失電 5 s 車鐘歸零、20 s 雙錨備便、30 s 報告 VTS;主機恢復後 DSAH、教官確認儀器重啟 → 過口門靠泊簡報 → IN1 拖船帶纜 → 依距泊位中心的期望速度以車令控速 → 泊位中心前 30 m 內拖船拉住(離碰墊約 40 m,艉不越過碼頭端),之後推頂以 PI 控制朝碼頭速度(吹開風為煞車)→ 繫 4 纜 → 120 s 後教官 allFast。
- 預設組合(`E20Default_ScriptedPassageAndBerthing`):約 4190 s 靠泊完成;橫向接近速度約 0.05 m/s、碰墊能量比約 0.02、繫妥位置誤差約 6 m;全部容許帶 ok、100 分。
- 教官選 E08(T+400 s)+ E07(過口門後舵角 < 3° 時)(`E20_InstructorChoosesE08AndE07`):升級依注入順序、E07/E08 必要事件 met、E09/E15 notApplicable、100 分。

## 20. E22 1.39 教室模式迷你情境(E22a–E22d)

規劃書第 8.0 節:1.39 人因理論不另設講授時段,併入練習的簡報資料與講評引導問題,由簡化教官模式播放、船長帶領討論。E22 以 4 個 12 分鐘的短情境實作,**不套用三步升級**(各有一個 `phase: discussion` 討論點,教官可在此暫停帶領討論),**無硬性失敗、無容許帶**,評估以必要事件(acknowledge/舵令)與講評為主;無人操作時分數低於及格(講評素材)。共同:開闊水域(22.40 N、120.00 E,示意,待 ENC 校正)、交班開場(`handover`,narrator)、`handoverChecklist`、簡報附件含 1.39 單元講義、長度 `briefing.duration_s` 720 s。

| 情境 | 1.39 單元 | 腳本 | 必要事件(期限參數) |
|---|---|---|---|
| E22a 交接班資訊衝突 | 溝通、狀況覺知 | 交班者口頭「漁船停著、CPA 2 nm、航向沒改」;T+60 s 航海日誌(07:30 改向)、T+90 s ARPA(X1 CPA 0.5 nm)互相矛盾;T+240 s 討論點;T+270 s 交班者催促 | `conflictQuery` verbal/report ≤ `bridge.e22.conflictQuery_s`(penalty 25) |
| E22b 疲勞下的誤判 | 疲勞、決策(確認偏誤) | 夜間 03:50,接班者已工作 15 小時;T+120 s 疲勞提示;T+180 s 瞭望「白燈不動,應該是錨泊船」(實為橫越貨船 X1);T+300 s 討論點;T+420 s 瞭望看到紅燈 | `fatigueCallMaster` callMaster ≤ `bridge.e22.fatigueCallMaster_s`;`verifyLight` report/lookoutReport ≤ `bridge.e22.verifyLight_s` |
| E22c 對資深者提出質疑 | 領導、權力距離、PACE | T+150 s 船長對右舷橫越(自船讓路)下令「左舵十,從他船艏前過」;質疑則船長接受(`masterAccepts`,由 `requiredEventMet` 觸發);未質疑則舵工照令左舵 10、60 s 後回正(`masterOrderExecuted`/`helmsmanSteadies`,timeline `ownShip`);T+300 s 討論點 | `challengeMaster` verbal ≤ `bridge.e22.challenge_s`(penalty 25) |
| E22d 文化差異的溝通 | 文化差異、閉環溝通、SMCP | 手操舵;T+150 s 中文「左舵十」→ 外籍舵工「Yes sir」卻操右舵 10(timeline `ownShip`,不計為學員舵令);T+420 s 外籍二管輪英語電話「reduce RPM five minute」;T+540 s 討論點 | `rudderCorrection` rudderOrder ≤ `bridge.e22.closedLoop_s`;`smcpConfirm`、`readBack` verbal ≤ `bridge.e22.languageConfirm_s` |

xunit:`E22_NoOperatorRunsScriptAndMissesRequirements`(腳本時刻、討論點為唯一的階段變化、必要事件全部 missed、分數 < passMark、E22c 未質疑分支)、`E22_ScriptedResponsesMeetRequirements`(各期望行為於期限內:必要事件 met、100 分;E22c 質疑後 180 s 船長接受、舵工照令分支不出現),皆紀錄 → 重播一致。

## 21. 第三批共同:參數、契約、測試、限制、待辦

### 21.1 設計規則的落實

| 規則 | 落實 |
|---|---|
| 交接班與書面計畫開場 | 七個情境第一個時間表項目皆為 `handover`(t = 1 s);`briefing.text` 以「交班」開頭;附件含「…計畫」與交班檢查表;必要事件 `handoverChecklist`。 |
| 三步升級 | E19:degraded(火警)/emergency(確認火災);E20:依模組注入順序(firstModule/secondModule);E22 依 1.39 教室模式例外(`phase: discussion`)。 |
| 「引水人/教官是錯的」 | E22c 船長錯誤舵令(質疑);E19 機艙依程序要停主機(需船長協調時機)。 |
| 示意座標 | 檔頭與區域檔 `source`/`description` 皆標「示意,待 ENC 校正」(xunit 檢查)。 |

### 21.2 參數集新增鍵(`ASSUMED-E19`/`E20`/`E22`,標題皆以「假設值,待公司程序」開頭)

| 鍵 | 值 | 用途 |
|---|---|---|
| `security.ispsReport_s` / `security.e19.ispsAlertAt_s` | 600 / 240 s | E19 ISPS 通報 CSO 期限 / 警報時刻 |
| `fire.callMaster_s` / `ecrContact_s` / `musterDecision_s` / `engineDecision_s` / `vtsReport_s` / `speedReduction_s` | 60 / 120 / 120 / 180 / 300 / 180 s | E19 必要事件期限 |
| `fire.e19.alarmAt_s` / `confirmAfter_s` / `vtsHoldAfter_s` / `extinguishAfter_s` | 480 / 150 / 120 / 720 s | E19 時間表 |
| `fire.e19.holdSpeed_kn` | 6 kn | E19 口門外等候區速限 |
| `navigation.xtd.harbourChannel_m` | 150 m | E20 口門與港內航段走廊半寬 |
| `emergency.e20.selectionReminderAt_s` | 900 s | E20 教官未選模組提示 |
| `emergency.e20.defaultE15At_s` / `defaultE09After_s` / `defaultE09Fallback_s` | 300 / 60 / 2700 s | E20 預設組合觸發 |
| `berthing.e20.briefing_s` | 600 s | E20 靠泊簡報期限 |
| `assessment.e20.passMark` | 70 | E20 總結性評估自動評分及格分數(輔助) |
| `bridge.e22.conflictQuery_s` / `fatigueCallMaster_s` / `verifyLight_s` / `challenge_s` / `closedLoop_s` / `languageConfirm_s` | 180 / 300 / 180 / 90 / 60 / 120 s | E22 必要事件期限 |

既有鍵沿用:`bridge.handover.checklist_s`、`bridge.distraction.*`、`emergency.steeringFailure.*`、`emergency.gyroFailure.*`、`emergency.blackout.*`、`navigation.xtd.pilotage_m`、`navigation.cpa.*`、`navigation.ukc.*`、`navigation.vts.report_s`、`port.kaohsiung.*`、`berthing.approachSpeed_mps`/`approachSpeedFail_mps`/`allFastPositionTolerance_m`、`manoeuvring.rotLimit_degPerMin`、`assessment.passMark`。

### 21.3 契約與 JSON 範例(皆為既有 schema/核心功能;schema 未改)

```yaml
# E19:只記錄故障明示注入(第 16 節 recordOnly)與「逾時則機艙停主機」
- id: fireAlarm
  phase: degraded
  actions:
    - injectFault: { name: fireAlarmEngineRoom, recordOnly: true }
- id: engineStopped
  when: { type: event, kind: requiredEventMissed, itemId: engineDecision }
  actions:
    - injectFault: mainEngine
# E20:教官「注入 X」手動鈕與依注入順序的升級
- id: injectE07
  manual: true
  actions:
    - injectFault: steeringGear
- id: secondModule
  phase: emergency
  when:
    any:
      - all: [ { type: event, kind: timelineTriggered, itemId: injectE07 }, { type: event, kind: timelineTriggered, itemId: injectE08 } ]
      # …共六種兩兩組合
# E20 預設組合版本(其餘內容與主檔相同)
- id: injectE09
  when:
    any:
      - { type: time, atLeast: "${params:emergency.e20.defaultE09After_s}", since: { event: routeLegChanged, legIndex: 3 } }
      - { type: time, atLeast: "${params:emergency.e20.defaultE09Fallback_s}" }
# E22c:質疑與未質疑兩個分支
- id: masterAccepts
  when: { type: event, kind: requiredEventMet, itemId: challengeMaster }
- id: masterOrderExecuted
  when: { type: event, kind: requiredEventMissed, itemId: challengeMaster }
  actions:
    - autopilot: { enabled: false }
    - ownShip: { rudder: -10 }
```

```json
{"type": "triggerTimeline", "args": {"id": "injectE09"}}
{"type": "acknowledge", "args": {"kind": "verbal", "note": "全船警報;消防隊集合由大副現場指揮,舵工、瞭望、二副留駕駛台"}}
{"type": "acknowledge", "args": {"kind": "notifyCompany", "note": "授權大副依 SSP 檢查舵機房限制區並通報 CSO"}}
{"kind": "faultInjected", "source": "timeline", "itemId": "fireAlarm", "t": 480.0, "message": "fireAlarmEngineRoom"}
{"kind": "phaseChanged", "source": "timeline", "itemId": "secondModule", "t": 1882.0, "data": {"from": "degraded", "to": "emergency"}}
{"assessment": {"required": [{"id": "e07FirstAction", "state": "notApplicable"}, {"id": "e09FirstAction", "state": "met", "by": "telegraphChange"}]}}
{"berthing": {"berthId": "KHH-E20-OIL", "maxLateralSpeed_mps": 0.05, "maxEnergyRatio": 0.02, "allFastPositionError_m": -6.2}}
```

區域檔(節錄):
```json
{"id": "kaohsiung-e20-passage", "source": "示意(港外航線同 kaohsiung-second-port-inbound.json、口門同 kaohsiung-south-approach.json;港內航道與泊位為虛構示意;待 ENC 校正)",
 "route": [{"name": "START", "lat": 22.52736, "lon": 120.23803}, {"name": "ENT", "lat": 22.552, "lon": 120.305}, {"name": "BERTH", "lat": 22.55968, "lon": 120.31931}],
 "breakwaters": [{"id": "northBreakwater", "depth_m": 2, "polygon": [{"lat": 22.55318, "lon": 120.30383}]}],
 "channel": {"id": "innerChannel", "depth_m": 12.5}, "shoals": [{"id": "innerShoalS", "depth_m": 4}],
 "berths": [{"id": "KHH-E20-OIL", "quay": [{"lat": 22.55964, "lon": 120.31788}, {"lat": 22.56081, "lon": 120.32007}], "waterSide": "right"}]}
```

確定性:只使用既有狀態與事件(時間表、手動觸發、漸變、目標、拖船/泊位/繫纜、故障、評估皆已進快照/雜湊);未改核心,其他情境雜湊不變(參數集 sha256 只在紀錄標頭)。

### 21.4 已知限制與取捨

- E19 火災本身無物理/煙霧模型,發展以時間表表示;集合造成的人力減少(舵工/瞭望離開)以語音提示與必要事件表示,核心沒有人員狀態。
- E20 的模組時序由教官決定,無人操作的固定時刻只在預設組合版本有意義;教官選擇版本以 `requiredEventPending` 的 `deadline_t` 供教官站倒數。模組不改階段,階段只表示「第幾個模組」。
- E20 E09 模組的 `gyroFailure` 使自動舵脫開;合理操作腳本以真航向代表舵工依磁羅經/疊標操舵(講評時以 `summary` 與錄影判讀)。
- E22 的「質疑」「閉環確認」以教官按確認(acknowledge)代表,核心不做語音辨識;E22d 舵工反舵以 timeline `ownShip` 表示(不計為學員舵令,`rudderCorrection` 只看學員/教官舵令)。
- 主控程序 `--duration` 大於情境 `endExercise` 時刻時(E22 720 s)錄製不會自行結束:錄製驗證以 `--duration 700` 執行。

### 21.5 測試與夾具

- `tests/SimCore.Tests/ScenarioLibraryFinalTests.cs`(23 項):載入/驗證(七檔無錯誤無警告)與設計規則(含 1.39 例外、只記錄故障皆 `recordOnly`)、區域檔一致、E20 兩檔差異只在預設觸發、E20 未選模組時 notApplicable;E19、E20 預設組合、E22a–d 無人操作的固定時刻與關鍵事件;合理操作(E19、E20 預設組合、E20 教官選 E08 + E07、E22a–d)必要事件 met、容許帶皆 ok、100 分;紀錄 → 重播雜湊/事件/summary 一致後刪除紀錄。
- 主控程序:七檔 `--validate` 無錯誤無警告;E19/E20 兩檔 `--duration 900`、E22 `--duration 700`(`--timescale 10`)錄製後 `--replay` 一致(驗證後刪除)。
- 核心紀錄夾具 `src/Instruments.Web/tests/fixtures/record-<id>-core.jsonl`(60 s,每 500 tick 一筆狀態)七檔;`src/Tools.Calibration/tests/test_scenario_params.py` 的 `REQUIRED_FIXTURES` 已加入七個 id。

### 21.6 待辦

1. 以 ENC/港方資料校正 `kaohsiung-e20-passage.json`(口門、防波堤、港內航道、淺灘、油碼頭泊位)並與 E09/E17 的港內示意幾何統一後重錄夾具。
2. E19 人員狀態(集合後駕駛台人力)與火災發展模型;E20 教官站「注入 X」鈕依 `briefing`/項目名稱分組顯示(目前為一般手動項目)。
3. E22 迷你情境的講評引導問題與 1.39 講義由 BRM 教官定稿;參數集假設值依公司 SMS 核定後 bump version 並重新驗證引用情境。

## 22. 第三批收尾審查修正(2026-10-18,E19/E20/E22a/E22b)

審查以探針(同一份 `SimosaBRM.SimCore.dll`、指令經 `Enqueue` 送入)重現五項;每項先寫成 `tests/SimCore.Tests/ScenarioLibraryFinalReviewFixTests.cs` 的正式測試確認失敗,再只改情境 YAML 與參數集(新增鍵)修正。核心、schema、既有測試皆未改。

| # | 情境 | 問題 | 修正 |
|---|---|---|---|
| 1 | E20 兩檔 | E07 與 E09 共用故障名稱 `steeringGear`:`e09EmergencyPower`(E09 + 45 s)把 E07 尚未處理的卡舵一起清除,舵自動恢復,`e07EmergencySteering` 失去意義 | `e09EmergencyPower` 加條件「非(E07 已注入且教官尚未確認應急操舵)」;另增 `e09EmergencyPowerSteeringJammed`(同一時刻、互斥):應急發電機供電但不清除 `steeringGear`,語音/訊息說明操舵未恢復,待教官按 `e07EmergencySteering`。E09 先、45 s 內再注入 E07 亦同 |
| 2 | E19 | `holdSpeedReduction` 只接受 VTS 等候指示(750 s)後的車令/轉速令;確認火災後即主動減速者被扣 10 分 | 新增 `holdAlreadyReduced`:VTS 指示當下(2 s 窗)車鐘已低於 HAH(`notIn [HAH, FAH, NAVF]`)或對地速度低於 `fire.e19.holdSpeed_kn`,且主機非機艙停俥(`mainEngine` 未作用);`holdSpeedReduction.expect` 加入 `{event: {kind: timelineTriggered, itemId: holdAlreadyReduced}}` |
| 3 | E22a | `conflictQuery` 自 `arpaCue`(90 s)起算;看到航海日誌(60 s)即提出查證不計,之後須再確認一次 | `after` 改為 `logbookEntry`(第一個衝突來源),`within_s` 改引用新鍵 `bridge.e22.conflictQueryFromLogbook_s`(210 s);期限時刻不變(60 + 210 = 90 + 180 = 270 s) |
| 4 | E22b | `fatigueCallMaster` 自 `fatigueCue`(120 s)起算;交班時即承認疲勞並依夜令叫船長不計 | `after` 改為 `handover`,`within_s` 改引用新鍵 `bridge.e22.fatigueCallMasterFromHandover_s`(420 s = 疲勞提示 120 s + 原期限 300 s) |
| 5 | E20 兩檔 | E08 與 E09 共用 `gyroFailure`:E09 的電羅經失效警報已確認後,E08 漂移 420 s 再注入 `gyroFailure`(`alreadyActive`)不產生新警報,`e08AlarmAck` 必然逾時;`e09InstrumentsRestored` 宣告「電羅經恢復正常」但 `gyroDrift` 仍在 | `e08GyroFail` 加條件 `{type: fault, name: gyroFailure, active: false}`;另增 `e08GyroFailMasked`(電羅經已因 E09 失效時通知教官,不注入),E08 失效階段三項必要事件(`e08AlarmAck`、`e08ManualSteering`、`e08CallMaster`)為 notApplicable;`e09InstrumentsRestored` 一併 `clearFault: gyroDrift`(同 `e08GyroRestored`,重新啟動後不再失效) |

### 22.1 契約與 JSON 範例(皆為既有 schema/核心功能;schema 未改)

E20 應急發電機的兩個互斥分支(YAML 流式 `{ }` 內參數引用加引號):

```yaml
- id: e09EmergencyPower
  when:
    all:
      - { type: time, atLeast: "${params:emergency.blackout.emergencyPower_s}", since: { event: timelineTriggered, itemId: injectE09 } }
      - not:
          all:
            - { type: event, kind: timelineTriggered, itemId: injectE07 }
            - not: { type: event, kind: timelineTriggered, itemId: e07EmergencySteering }
      - not: { type: event, kind: timelineTriggered, itemId: e09EmergencyPowerSteeringJammed }
  actions: [ { clearFault: steeringGear }, … ]
- id: e09EmergencyPowerSteeringJammed        # 不清除 steeringGear
```

事件序列(測試腳本於 400 s 注入 E07、430 s 注入 E09、560 s 由教官確認應急操舵,事件時刻為 399/429/559 s;`SimEvent` JSON,`t` 秒):

```json
{"kind": "timelineTriggered", "source": "timeline", "itemId": "e09EmergencyPowerSteeringJammed", "t": 474.0}
{"kind": "faultCleared", "source": "timeline", "itemId": "e07EmergencySteering", "message": "steeringGear", "data": {"fault": "steeringGear"}, "t": 559.0}
{"kind": "requiredEventMet", "source": "assessment", "itemId": "e07EmergencySteering", "data": {"dt_s": 160.0, "within_s": 300, "by": "event:timelineTriggered", "late": false, "penalty": 0}}
```

E19 VTS 指示時已在減速(690 s 下 DSAH):

```json
{"kind": "timelineTriggered", "source": "timeline", "itemId": "holdAlreadyReduced", "t": 750.0}
{"kind": "requiredEventMet", "source": "assessment", "itemId": "holdSpeedReduction", "data": {"dt_s": 0.0, "within_s": 180, "by": "event:timelineTriggered", "late": false, "penalty": 0}}
```

E20 E08 + E09(電羅經已失效):`e08GyroFailMasked` 於 E08 + `emergency.gyroFailure.failAfter_s` 觸發(只發教官訊息),`summary.assessment.required[]` 中 `{"id": "e08AlarmAck", "state": "notApplicable"}`。

參數集新增鍵(來源 `ASSUMED-E22`,標題「假設值,待公司程序」;未 bump version,只新增鍵):

```yaml
bridge.e22.conflictQueryFromLogbook_s:      { value: 210, unit: s, source: ASSUMED-E22 }
bridge.e22.fatigueCallMasterFromHandover_s: { value: 420, unit: s, source: ASSUMED-E22 }
```

確定性:只使用既有觸發條件(`event`、`fault`、`telegraph`、`ownSpeed`、`time since`、`not/all/any`)與動作;時間表項目狀態本已進快照/雜湊。未使用 E07+E09、E08+E09 組合或早期確認的路徑行為不變(E19/E20/E22 既有 xunit 時刻與分數斷言全數通過);內容有變的五檔已依規則重錄 60 s 核心紀錄夾具(`--replay` 一致)。

### 22.2 模組組合互動(補充第 19 節)

- **E07 + E09**:兩者都以 `steeringGear` 表示失去操舵。E07 卡舵只由教官 `e07EmergencySteering` 解除;E09 的應急發電機供電只在 E07 未注入或已處理時解除。E07 在 E09 供電之後才注入者,照常卡舵。反向情形(E09 失電 45 s 內教官就按 `e07EmergencySteering`)仍會提早恢復操舵:教官應在應急發電機供電後才確認應急操舵(核心沒有「應急操舵需應急電源」的模型)。
- **E08 + E09**:兩者都以 `gyroFailure` 表示電羅經失效。E08 漂移期間電羅經已因 E09 失效時,E08 的完全失效不另注入(無新警報可確認),失效階段必要事件不適用,改以 E09 必要事件評估;教官按 `e09InstrumentsRestored` 或 `e08GyroRestored` 都會一併清除 `gyroFailure` 與 `gyroDrift`。
- 測試:`ScenarioLibraryFinalReviewFixTests`(14 項)涵蓋上述組合、單一模組的既有行為(E09 單獨照常恢復操舵、E08 單獨照常注入警報)、E19 未減速仍須車令、E22a 日誌前提問不算,並以紀錄 → 重播確認雜湊/事件/summary 一致。
