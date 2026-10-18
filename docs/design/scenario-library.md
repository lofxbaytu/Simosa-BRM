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
| 無人操作 | degraded 368 s、emergency 668 s,983.7 s 擱淺於北防波堤(無岸壁效應時 987.2 s) | degraded 353 s、emergency 653 s,1800 s 時間到 |

東北風 25 kn(蒲氏 6 級)。

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
| E21 | `data/scenarios/E21_cargo_heating_pressure_anping.yaml` | FSB1 滿載 | 同 E18(同一幾何、不同船與潮況) | ≤ 65 min |

## 9. E15 干擾與工作負荷過載(高雄引航進港)

**學習目標**:工作負荷管理、授權(delegation)、無干擾駕駛台(sterile bridge)、在噪音中偵測關鍵事件;1.39 工作負荷。

**場景**:09:30 交班,引水人已在 Charlie 登船(帶領者扮演),FSB2 滿載 HAH 約 9 kn,沿出港航道延長線南半側(軸線南 0.12 nm,航向 070°)進第二港口;漲潮向西北 0.8 kn(自動舵 074° 已修正)、東北風 18 kn。

**干擾(每 2 分鐘,T+2 至 T+22)**:貨控室卸貨計畫電話、PSC 檢查郵件、代理行 VHF、ECDIS 警報氾濫(T+8,並以每 20 s 一則、共 8 則的重複訊息表示)、公司海務電話、機控室電話、艏樓報告、VTS 廣播、代理行泊位變更、ECDIS 警報再次氾濫、PSC 追問。以 `voiceScript`(phone/vhfPort/bridge)與 `message` 表示;`ecdisAlarmFlood` 在故障目錄為只記錄故障,注入會使 `--validate` 出現警告「只記錄與廣播」,故**不注入**(第 14.4 節)。

**關鍵事件**:貨船 X1 在自船航線右側約 1.5 nm 漂航(會遇關注門檻外),T+13 起步以 10 kn 向 340° 橫越(自船為讓路船,X1 不避讓);會遇開始時刻落在干擾 5 與 6 之間。

**升級**:normal → **T+8 degraded**(`ecdisAlarmFlood`:警報氾濫 + 漲潮 0.8 → 1.4 kn,300 s 漸變;未再修正時航跡偏出走廊)→ **X1 進入 1.0 nm(或 T+21)emergency**(`crossingCloseQuarters`,引水人提醒)→ 到達 OUTER 或 T+30 結束。無人操作時刻:degraded 480 s、X1 會遇 798 s、emergency 1043 s、cpaHard 1300 s(xunit 固定)。

**評估**:
- 硬性失敗:碰撞、CPA < `navigation.cpa.hardFail_nm`。
- 容許帶:`track.maxXtd_m`(warn `navigation.xtd.pilotage_m` 450 m、fail 2×)、`track.timeOutsideCorridor_s`(fail 180 s)、`collisionAvoidance.minCpa_nm`(warn `navigation.cpa.pilotage_nm` 0.25 nm、fail 0.2 nm;0.5 nm 會大於走廊寬度而使驗證警告)。
- 必要事件:`handoverChecklist`;無干擾駕駛台 `sterileCargoCall`、`sterilePscEmail`、`sterileCompanyCall`(acknowledge verbal,note 記錄授權/延後)、`alarmFloodManaged`(verbal/report,指派專人處理警報);關鍵事件 `crossingDetected`(X1 會遇開始 → lookoutReport/report 或 X1 首次行動 ≤ `bridge.distraction.criticalDetection_s`,逾時每秒 0.05 分)、`crossingAction`(X1 首次行動 ≤ `navigation.encounter.earlyAction_s`)。
- 合理操作(xunit):每次干擾 20 s 內授權/延後、循跡修正流壓(每次 ≤ 4°,不被會遇追蹤誤判為避讓)、X1 起步即 DSAH + 向右 20°、X1 通過最近點後回航跡:必要事件 met、容許帶皆 ok、100 分;X1 最近約 0.31 nm、最大 XTD 約 410 m。

## 10. E16 夜間進港燈光混淆(基隆)

**學習目標**:夜間視覺狀況覺知、瞭望報告、雷達/ECDIS 與目視交叉核對、航程危險複審(航船布告);1.22。

**場景**:20:30 交班(夜間),FSB1 滿載自口門外 4.5 nm 依導航線(195°)進港至引水人登船點(口門外 0.8 nm);東北季風 25 kn(陣風 0.25)、沿岸流向 250° 0.6 kn(壓向右舷 = 西側淺礁)、HAH 約 8–9 kn、自動舵 193°。東北湧浪與背景岸燈無核心模型,以簡報與訊息表示(3D 視景另行實作)。

**目標**:F1 無燈無 AIS 漁船(導航線上偏東 0.06 nm、口門外 2.0 nm),自船距 1.2 nm 時才出現(`trigger: ownDistance`,代表湧浪雜波中的微弱回波);F2、F3 開集魚燈有 AIS 的漁船(背景燈光);OUT1 出港貨櫃船(緊急階段啟動,導航線東 0.35 nm,左舷對左舷)。**schema `lights` 列舉沒有「無燈」**,F1 以 `aisOn: false` 與訊息表示,未指定 `lights`(核心推定為 fishing),待辦第 14.5 節。

**升級**:normal(F1 雷達回波)→ **通過 K1 degraded**(`leadingLightsOut`:瞭望報告看不到後導燈 — 本週航船布告(簡報附件,交班時未讀)已公告熄滅;風 25 → 30 kn、流 0.6 → 0.9 kn)→ **距 PILOT 1.0 nm(或 T+25)emergency**(`outboundAndSet`:OUT1 出港 + 流 → 1.2 kn)→ 到達登船點、擱淺或 T+35。無人操作:F1 534 s、degraded 771 s、emergency 1090 s、F1 cpaHard 954 s、到達 1419 s。

**評估**:硬性失敗擱淺(淺礁以 `ukcPolicy` 區域水深 4 m 表示)、碰撞、cpaHard;容許帶 XTD(warn `navigation.xtd.harbourApproach_m` 500 m、fail 1.3× ≈ 西側淺礁距離)、走廊外時間、minCpa(warn 0.25、fail 0.2 nm);必要事件 `handoverChecklist`、`lookoutReportF1`(lookoutReport/report ≤ `navigation.night.lookoutReport_s`)、`vtsReport`(K1)、`leadingLightReport`(callMaster/verbal ≤ `navigation.night.leadingLightReport_s`)、`outboundResponse`(舵令/車令/轉速令/verbal ≤ `navigation.channel.outboundResponse_s`)。合理操作:F1 出現 40 s 內報告、以導航線西側 400 m 通過(F1 最近約 0.27 nm)、F1 通過後回線、OUT1 時口頭確認並減至 SAH:100 分。

## 11. E17 颱風準備與離港決策(高雄港南端油品碼頭)

**學習目標**:不確定下的決策、風險評估、公司溝通(1.39 決策)、一艘拖船離泊。

**場景**:10:00 交班,FSB1 壓載(卸貨完畢)右舷靠 KHH-SOB-1(碼頭線 000°、水域在西、碰墊/繫船柱比照 E14 示意型式),4 纜已繫;中央氣象署颱風海上警報,西風 15 kn 吹攏。原訂兩艘拖船:TG1 高港11號(`tugs[]`,左舷艉,`initial.mode: free`,帶纜 90 s)、TUG2 高港12號(港內待命的**目標船**)。

**拖船可用數 2 → 1**:核心時間表動作不含 `tugControl`、也沒有「拖船不可用」狀態,因此只把 TG1 放在 `tugs[]`,TUG2 以 `shipType: tug` 的目標船表示,於 degraded 以 `targetControl {behaviour: waypoints}` 駛離(教官對 TUG2 下拖船令會被 `unknownTug` 拒絕,與「已改派」一致)。

**升級**(壓縮時間):T+1 min 港方要求油輪兩小時內離港(決策點)→ T+4 公司詢問可否留港(壓力)→ **T+7 degraded**(`tugReduced`:拖船 2 → 1 + 風 15 → 25 kn,600 s 漸變)→ **T+25 emergency**(`outerBands`:25 → 40 kn、陣風 0.35,600 s 漸變)→ 離開泊位區(船舯離碼頭 > 110 m)60 s 後結束;或擱淺、T+45。無人操作:船在吹攏風下壓在碰墊上(纜未斷),全部決策逾時。

**評估**:硬性失敗擱淺、碼頭硬接觸、繫纜斷裂;容許帶 `berthing.maxEnergyRatio`(warn 0.8、fail 1.0)、港內速限超速時間;必要事件 `handoverChecklist`、`departureDecision`(notifyCompany/report ≤ `typhoon.departureDecision_s`)、`engineStandby`(notifyEcr)、`tugPlan`(verbal/report)、`tugUsed`(拖船縮減後 `tugCommand`)、**`departBeforeStorm`**(港方要求 → `clearOfBerth` ≤ `typhoon.e17.departBy_s` = 強風時刻 − 要求時刻,penalty 20;決策時機)。拖船工作時間與最大出力見 `summary.berthing.tugs`。
合理操作(xunit):早決策、TG1 帶纜待命,留艏倒纜右滿舵 DSAH 開艉 100 s,解倒纜後 SAS 退出,TG1 依開出角(< 15°)拉艉抵抗吹攏風(相對方位 270° 的拉力隨船轉動,持續全力會使船迴轉,故以開出角調節);約 T+17 離開泊位區(強風前),無碼頭硬接觸、無斷纜,及格。

## 12. E18 油碼頭進港 UKC/squat 限制(安平)

**學習目標**:潮窗規劃、UKC 監控、速度相關 squat、ECDIS 安全等深線、對「速度誘惑」的質疑;SIRE 4.2.2。

**幾何與潮汐(示意)**:浚深航道軸 080°、底寬 200 m、維護水深 CD 7.0 m;兩側淺灘 CD 5.0 m 以 `ukcPolicy` 區域水深表示(安全等深線以外,越出即擱淺);航道速限區(`port.anping.channelSpeedLimit_kn` 6 kn)。核心水深 = 基底常數 + 區域覆寫取淺,**潮汐以基底水深(維護水深 + 潮高)的 `setEnvironment waterDepth` 漸變表示**,淺灘的固定區域水深不隨潮(淺灘恆淺於基底,不影響結果);開場到航道外端的 1 nm 沿岸段同為基底水深。

**場景**:13:00 交班,FSB2 滿載 7.2 m 自口門外 4.0 nm,HAH 約 7.5 kn(計畫在 OUTER 前減至 SAH)、自動舵 077°;預報潮高 +1.9 m → 水深 8.9 m、靜態 UKC 1.7 m。MMG(ICORELS)squat 在 8.4–8.9 m 水深:DSAH 約 0.35 m、SAH 約 0.4–0.45 m、HAH 約 0.75–0.8 m、FAH 約 1.0 m。

**升級**:normal → **T+5 degraded**(`tideLower`:潮位比預報低 0.3 m 且提早退潮,8.9 → 8.6 m、600 s)→ T+12 碼頭要求加速到 8 kn 以上(速度誘惑)→ **通過 BAR(或 T+35)emergency**(`barShoaling`:前船回報沙洲段淺於海圖、潮位 → 8.4 m)→ 到達迴船池、擱淺或 T+55。測深儀淺水警報 `alerts.shallowWater_m` = `navigation.ukc.shallowAlarm_m`(龍骨下 0.8 m,含 squat)。無人操作(HAH 不減速):degraded 300 s、淺水警報約 585 s、emergency 1333 s、到達 2167 s;UKC 低於政策 1400 s 以上(fail)、最小 0.48 m(warn)、超速 1.3 kn(warn),不及格。

**評估**:`assessment.ukc.min` 與 `zones[channel].ukcMin` 皆為 `navigation.ukc.channel_fraction`(10 % 吃水,0.72 m;淺水沿岸進港段全程適用,開闊海 20 % 會使沿岸段誤扣);硬性失敗擱淺、`ukcHard`(< 5 % 吃水);容許帶 `ukc.min_m`(warn `navigation.ukc.minWarn_m` 0.6、fail `minFail_m` 0.45 m)、`ukc.timeBelowLimit_s`(fail 120 s)、`manoeuvring.maxOverSpeed_kn`(warn 0.5、fail 2.0 kn)、XTD(warn `navigation.xtd.dredgedChannel_m` 70 m、fail 1.4×)、走廊外時間;必要事件 `handoverChecklist`、`tideReport`(report/verbal ≤ `navigation.ukc.tideReport_s`)、`speedChallenge`(verbal/report)、`ukcAlarmResponse`(`alertRaised shallowWater` → 車令/轉速令 ≤ `navigation.ukc.alarmResponse_s`;警報未響時 notApplicable)。合理操作:開場減至 SAH、通過 BAR 減至 DSAH:UKC 最小約 0.91 m、警報未響、100 分。

## 13. E21 貨物加熱系統故障下的到港壓力(安平)

**學習目標**:壓力下的短期策略、對上級(公司)的質疑與安全優先(1.39、MRM)、淺水航道的速限與 UKC 遵守;與 E18 同一幾何(重複性)。

**場景**:14:00 交班,FSB1 滿載 5.7 m、瀝青貨溫約 150 °C,自口門外 5.5 nm FAH 約 10 kn、自動舵 077°;潮高 +0.6 m 且退潮中(水深 7.6 → 7.2 → 6.6 m)。FSB1 的 squat 小(DSAH–FAH 約 0.07–0.3 m),UKC 主要受潮位影響:依計畫航速可在潮窗內進港(UKC 約 0.8 m),延誤或放棄則應錨泊等候。

**故障與壓力**:T+2 熱油鍋爐跳脫(`cargoHeatingFailure` 為只記錄故障,同 E15 理由**不注入**,以輪機長語音 + 警報訊息表示)→ T+5 修復時間不定(2–6 小時)→ 貨溫下降以每 5 分鐘一則訊息表示(核心無貨物熱力模型)→ **T+10 degraded**(`companyPressure`:公司要求全速、「航道速限不用管」,潮位 7.6 → 7.2 m)→ **通過 OUTER(或 T+25)emergency**(`tideWindowClosing`:港控通報潮窗約 30 分鐘、公司再催、潮位 → 6.6 m,1500 s)→ 到達迴船池、擱淺或 T+65。無人操作(FAH):degraded 600 s、emergency 899 s、到達 2264 s;航道超速約 3.5 kn(fail)、偏出走廊(fail)。

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

- **只記錄故障不注入**:`ecdisAlarmFlood`(E15)、`cargoHeatingFailure`(E21)在 `src/Contracts/fault-catalogue.json` 為 `modelled: false`,`ScenarioValidator` 對時間表注入只記錄故障一律給警告;本批要求 `--validate` 無警告,故以同名時間表項目 + 訊息/語音表示(`faults[]` 不會出現)。若要讓儀器端(ECDIS 警報清單)收到故障,可加回 `injectFault` 並接受該警告,或由核心/驗證器把這兩項列為「預期只記錄」(待契約負責人決定)。
- E16 無燈漁船:schema `targets[].lights` 列舉沒有 `none`,只能以 `aisOn: false` 與訊息表示。
- E17 拖船縮減以「目標船駛離」表示;核心沒有拖船可用性狀態。
- E18/E21 淺灘水深固定、不隨潮;潮汐只作用於基底水深。
- 會遇追蹤把 ≥ 5° 的自動舵改向記為首次行動:合理操作腳本循跡時每次改 ≤ 4°,避免把流壓修正誤判為避讓;實際課程中學員的流壓修正可能被記為對 X1/F1 的首次行動(講評時以 `summary.collisionAvoidance.targets[]` 判讀)。

### 14.5 測試與夾具

- `tests/SimCore.Tests/ScenarioLibraryLateCourseTests.cs`(18 項):載入/驗證(無錯誤、無警告)與設計規則、區域檔一致、五個情境無人操作的升級時刻與關鍵事件、合理操作腳本(必要事件 met、容許帶不誤扣、及格)、E17 單拖船與風力剖面、E18/E21 同一幾何;紀錄 → 重播雜湊/事件/summary 一致後刪除紀錄。
- 主控程序:五個情境 `--validate` 無錯誤無警告;`--duration 900 --timescale 10` 錄製後 `--replay` 一致(驗證後刪除)。
- 核心紀錄夾具 `src/Instruments.Web/tests/fixtures/record-E15…E21-core.jsonl`(60 s,每 500 tick 一筆狀態);`src/Tools.Calibration/tests/test_scenario_params.py` 的 `REQUIRED_FIXTURES` 已加入五個 id。

### 14.6 待辦

1. 以 ENC/港方資料校正四個新區域檔(基隆導航線與導燈、高雄南端油品碼頭泊位、安平航道水深與淺灘)並重錄夾具。
2. schema `targets[].lights` 加入 `none`(E16 F1);核心/驗證器處理「預期只記錄」故障(E15 `ecdisAlarmFlood`、E21 `cargoHeatingFailure`)後改為注入。
3. 拖船可用性(拖船改派、到達延遲)與颱風風場/湧浪模型;貨物熱力(貨溫)模型。
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

**E15 關鍵事件計時(未修正)**:建議的兩種修法都會使既有測試失敗,而本批不可修改 `ScenarioLibraryLateCourseTests.cs`:(a) 把 `crossingDetected`/`crossingAction` 的 `after` 改為 `{ event: timelineTriggered, itemId: crossingTurn }` 時,AssessmentEngine 只計入 after 之後的 expect,既有合理操作腳本在 X1 會遇開始(循跡時約 718 s)+ 60 s 回報(早於 780 s 起步),`crossingDetected` 改由 `encounterFirstAction` 達成,`Assert.Equal("acknowledge:lookoutReport", …By)` 失敗——這正是審查指出的錯誤行為被寫進了測試;(b) 把 X1 初始位置外移會改變無人操作的固定時刻(會遇 798 s、emergency 1043 s、cpaHard 1300 s)。建議由測試負責人同時:情境改用 (a),並把 `E15_ScriptedSterileBridgeMeetsRequirements` 的瞭望報告改在 `crossingTurn` 之後(例如 X1 對地 > 2 kn 時先 `acknowledge lookoutReport` 再避讓);無人操作測試不受 (a) 影響(必要事件仍全部 missed)。

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
