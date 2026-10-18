# 情境庫設計 — E02、E03、E04、E06(第一批補齊)

> 版本 v0.2(2026-10-18;v0.1 後依審查修正 E02–E04/E06,見第 7.1 節)。依據:規劃書第 3.4 節(營運水域)、第 8.1 節練習目錄與「設計規則」、第 9.1 節(簡化教官模式、練習包角色);
> 情境引擎見 `docs/design/scenario-engine.md`,契約見 `src/Contracts/scenario.schema.json`。本文件的港口與航道座標**全部為示意值,待 ENC 校正**(第 1.2 節)。

| 編號 | 情境檔 | 船/裝載 | 水域(示意區域檔) | 長度 |
|---|---|---|---|---|
| E02 | `data/scenarios/E02_coastal_traffic_strait.yaml` | FSB1 滿載 | 台灣海峽中部分道航行型航道(`data/areas/taiwan-strait-central-lanes.json`) | 55 min |
| E03 | `data/scenarios/E03_restricted_visibility_strait.yaml` | FSB1 滿載 | 同 E02(同一幾何的壞能見度版本) | ≤ 60 min |
| E04 | `data/scenarios/E04_pilot_boarding_kaohsiung.yaml` | FSB2 滿載 | 高雄南口 Charlie 引水站(`data/areas/kaohsiung-south-approach.json`) | ≤ 80 min |
| E06 | `data/scenarios/E06_taichung_channel_flood.yaml`、`E06_taichung_channel_ebb.yaml` | FSB1 滿載 | 台中港防波堤口(`data/areas/taichung-harbour-entrance.json`) | ≤ 30 min |

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
| 無人操作 | degraded 368 s、emergency 668 s,約 988 s 擱淺於北防波堤 | degraded 353 s、emergency 653 s,1800 s 時間到 |

東北風 25 kn(蒲氏 6 級)。

**升級**:normal(交班、MPX 已完成)→ **航段 1 degraded**(`currentIncrease`:180 s 漸變至 2.5/3.0 kn)→ 90 s 後 `leadingLightsObscured`(訊息代表導燈被遮蔽)→ **距口門 0.8 nm 或 degraded 後 300 s emergency**(`outboundInChannel`:出港船 OUT1 由 `targetControl activate` 啟動,沿中心線北側 110 m 出港,左舷對左舷)→ 過口門 `insideBreakwater`(60 s 漸變至 0.3 kn;同時以 `targetControl {behaviour}` 把 OUT1 切換為航點模式)→ 到達迴船池、擱淺或 T+30 結束。

**出港船 OUT1**:流場為均勻流,`waypoints` 模式以「朝向航點」導航、不修正橫向偏移,強橫流下會被壓離設計航跡(審查發現:退潮版壓到中心線南側 +41–69 m 而與靠右的自船碰撞,漲潮版擦過北防波堤堤頭 14.6 m)。改為 `scripted` 固定對水航向 = 航跡 305° 加橫流修正角(7 kn 對水;漲潮 292°、退潮 321°,`asin(橫流/7 kn)`),對地沿中心線北側 110 m 直線出港;自船過口門(流壓消失)時切換為沿同一航跡的航點模式(弱流下偏移可忽略)。跨過 emergency 的時刻流速漸變已完成(degraded 後 180 s 漸變、emergency 最早在進入航段 1 後約 250 s),故修正角固定即可。

**速限**:航段 2(防波堤內)沿用航道速限 `channelSpeedLimit_kn`;內港 6 kn 自迴船池前減速區 `zones[innerSpeedLimit]`(口門內約 900 m 起,`type: speedLimit`)起算。滿載 FSB1 自 7.5–10.5 kn 減到 6 kn 需 5 分鐘以上,原本從口門起算 6 kn 並以超速秒數(warn 0 / fail 120 s)評估,使依簡報計畫進港必定 fail(審查發現)。`speed` 容許帶改以超過速限的最大幅度 `manoeuvring.maxOverSpeed_kn` 評估(warn 1.0 kn、fail 2.5 kn):過口門後 SAH 的簡報計畫漲潮 ok(0.75 kn)、退潮 warn(1.12 kn);口門內 400 m 再減至 DSAH 兩版皆 ok;退潮版完全不減速約 4.5 kn → fail。

**評估**:
- 硬性失敗:擱淺、碰撞、進入北/南防波堤。
- 容許帶:`track.maxXtd_m`(warn `navigation.xtd.channel_m`、fail 2×)、`track.timeOutsideCorridor_s`、`manoeuvring.timeRotOverLimit_s`(`manoeuvring.rotLimit_degPerMin`)、`manoeuvring.rudderOrders`(warn `manoeuvring.channel.rudderOrdersMax`)、`manoeuvring.hardOverCount`、`manoeuvring.maxOverSpeed_kn`(warn 1.0、fail 2.5 kn)、`collisionAvoidance.minCpa_nm`(航道門檻)。
- 必要事件:`handoverChecklist`、`leadingLightReport`(report/verbal)、`outboundResponse`(舵令/車令/轉速令或 verbal)。
- 岸壁效應(規劃書第 6.2 節 B 項、第 458 行)另行實作中(情境 `banks` 區段);合併後可為 E06 防波堤與航道邊坡加 `banks`,並重錄夾具(待辦)。

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
- 核心紀錄夾具 `src/Instruments.Web/tests/fixtures/record-<id>-core.jsonl`(60 s,縮短為每 500 tick 一筆狀態);`src/Tools.Calibration/tests/test_scenario_params.py` 的 `REQUIRED_FIXTURES` 已加入五個 id。情境 YAML 或引用的參數改動後須重錄(審查修正後五個夾具已重錄,`--replay` 雜湊/事件/summary 一致)。
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
