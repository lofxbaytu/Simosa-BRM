# 介面契約(Contracts)

所有程式(C# SimCore、Python 參考實作、網頁儀器、視景、教官站)共用這裡的 JSON Schema:

| 檔案 | 用途 |
|---|---|
| `state.schema.json` | 自船狀態廣播(25 Hz),欄位、單位與正負慣例;選填 `targets[]` 目標船(含 CPA/TCPA/艏越距離與 AIS 報告值,C# SimCore 產生;Python 參考實作無目標船)、`visibility_nm`、`scenario{id, phase, parameterSetVersion, timelineFired, timelinePending, hardFailed, score, ended}`、`route{legIndex, nextWaypoint, xtd_m(右正), xtdLimit_m, distanceToWaypoint_nm, bearingToWaypoint_deg, legCourse_deg, speedLimit_kn, complete}`(情境引擎;Python 不輸出)、`steering{mode: fu|nfu|autopilot|emergency, pump: 1|2|both, nfuActive?}`(操舵模式,規劃書第 7.2 節;Python 不輸出) |
| `command.schema.json` | 各站送往核心的指令(含 `targetControl` 目標船控制;情境引擎新增 `triggerTimeline`、`acknowledge`、`endExercise`、`getAssessment` 查詢,`setEnvironment` 加 `visibility_nm`/`ramp_s`,`injectFault` 可為 `{name, args}`;`steeringMode` 操舵模式:`value` fu|nfu|autopilot|emergency、`args.pump` 1|2|both、nfu 以 `args.direction` port|starboard|stop 推舵(舵以舵機速率移動,stop 停在原處)、autopilot 等同 `autopilot{enabled:true}`(可帶 heading/rotLimit)、emergency 為應急操舵(`steeringGear` 故障下舵以 particulars `rudder.steeringGear.emergencyHardOverTime35_s` 的速率、無資料時隨動速率一半再度可動);`rudder` 指令會把 autopilot/nfu 切回 fu;模式或泵變更發事件 `steeringModeChanged {from, to, pump}`)。WebSocket 另推送 `{"type":"event","event":SimEvent}` 與 `{"type":"assessment","final","summary"}` 框 |
| `ship-particulars.schema.json` | `data/ships/<ID>/particulars.json` |
| `coefficients.schema.json` | 船模係數檔(由校正工具產生,C# 與 Python 共用) |
| `scenario.schema.json` | 情境 YAML(`data/scenarios/*.yaml`);選填 `targets[]`(目標船)、`assessment`(門檻、硬性失敗、容許帶、必要事件、評分)、`timeline[]`(事件時間表)、`route`(航線)、`zones[]`(區域)、`briefing`(字串或物件)、`environment.visibility_nm`;數值欄位可寫 `"${params:<鍵>}"` 引用參數集(YAML 流式 `{ }` 內須加引號)。Python 參考實作忽略 targets/assessment/timeline/route/zones,但須先解析參數引用 |
| `parameter-set.schema.json` | 公司程序參數集(`data/parameters/company-procedures.yaml`):`id`、`version`(YYYY.M.n)、`sources{文件編號: {title, revision, …}}`、`parameters{鍵: {value, unit, source, clause, description, min, max}}`;鍵名單位後綴須與 `unit` 一致 |
| `radar.schema.json` | 雷達/ARPA 感測器服務(`src/Sensors.Radar`,ws://…:8766/radar/{x\|s})的 JSON 訊息:狀態、ARPA 追蹤目標清單(1 Hz)、事件、控制指令與回應;二進位影像幀(SRAD)格式見 `docs/design/radar-arpa.md`。雷達控制不進核心紀錄與雜湊鏈 |
| `fault-catalogue.json` | 故障名稱目錄(規劃書第 7.2 節):`modelled` 者引擎改變行為(操舵 `steeringGear/steeringFuOnly/steeringPumpFailure`、`mainEngine`、`bowThruster`、感測器 `gyroDrift/gyroFreeze/gyroFailure/gpsJump/gpsOffset/gpsFailure/echoSounderFailure/logFailure`、`autopilotFailure`、`tugLineBreak`、`engineStartFailure {delay_s?, attempts?}`(主機起動延遲/失敗;事件 `engineStartFailed/engineStartBlocked/engineStarted`,封鎖時 `state.engine.state = failed`)),其餘只記錄與廣播;`effect`、`alert`(BAM 優先序)、`args`(injectFault 參數與預設) |

紀錄檔(JSON Lines)各行型別見 `src/SimCore/README.md` 第 4 節:`header`(version 2,含 `parameterSet{id, version, path(相對專案根目錄,例如 data/parameters/company-procedures.yaml), sha256, resolved[]}`;
情境使用操縱量測時另含 `manoeuvreTargets{shipId, loading, path(例如 data/ships/FSB1/trial_targets.json), sha256, content}`,重播優先使用標頭目標值,`--rescore` 才讀目前檔案)、`input`、`state`、`event`(`SimEvent`:`kind/tick/t/targetId/message/range_nm/cpa_nm/tcpa_min` 不變,新增 `source/itemId/phase/visible/seq/data`;必要事件開始計時發 `requiredEventPending`,`data {after, afterItemId, afterSeq, start_t, within_s, deadline_t, penalty}` 供教官站倒數,`requiredEventMet/Missed` 的 `data` 含 `within_s`)、`snapshot`、`summary`(評估摘要,footer 之前)、`footer`。

感測器、警報與 BNWAS(`docs/design/sensors-alerts.md`):`state` 頂層 `heading/pos/cog/sog/stw/depthBelowKeel` 為**指示值**,真值在 `state.truth`,有效性與磁羅經在 `state.sensors`;另有 `state.autopilot{enabled, setHeading, rotLimit_degPerMin, available}`、
`state.alerts[]{id, number, priority: emergencyAlarm|alarm|warning|caution, category: A|B|C, source, state: activeUnacknowledged|activeSilenced|activeAcknowledged|rectifiedUnacknowledged, text, raisedAt_s, acknowledgedAt_s?, silencedUntil_s?, escalated, count, audible}`(無警報時省略)、
`state.bnwas{mode: off|auto|on, active, stage 0–3, remaining_s?, dormant_s}`(C# 產生,Python 不輸出)。指令 `alertAcknowledge`/`alertSilence`(`value` 或 `args.id`,省略 = 全部)、`bnwasMode`(`value` off|auto|on、`args.dormant_s`)、`bnwasReset`;
事件 `alertRaised/alertSilenced/alertReannunciated/alertAcknowledged/alertRectified/alertEscalated`(`itemId` = 警報 id)、`bnwasStage/bnwasReset/bnwasModeChanged`、`autopilotTripped`;`acknowledge.kind` 新增 `gyroCheck`;
情境新增 `sensors{magneticCompassError_deg}`、`alerts{escalate_s, silence_s, offHeading_deg, offHeadingDelay_s, headingMonitor_deg, shallowWater_m}`、`bnwas{mode, dormant_s, stage2_s, stage3_s}`;`summary.alerts{…, bnwas{stage1, stage2, stage3, resets}}`、`faultResponses[].alertAckAt_s/alertAckDt_s`。NMEA 以指示值輸出,無效時 HDT/DPT 停送、GGA 品質 0、RMC V,警報 `$IIALR`。

慣例:角度用度、航向 0 至 360、舵角右正左負、ROT 右轉正(度/分);核心內部以弧度與 SI 計算。

拖船、錨與錨鏈、碰墊與繫纜(`docs/design/physics-tugs-anchors-mooring.md`;C# 產生,Python 參考實作忽略/不輸出):指令 `tugControl {id, mode: push|pull|standby|free, direction_deg, power_pct}`、
`anchor {side: port|starboard, action: letGo|veer|heave|brake|release, targetLength_shackles | length_m}`、`mooring {lineId, action: makeFast|heave|slack|letGo, bollard?, tension_t?, length_m?}`
(`command.schema.json` `$defs.tugControlArgs/anchorArgs/mooringArgs`);狀態 `state.tugs[]{id, mode, status, force_t, direction_deg, …}`、`state.anchors[]{side, status: stowed|running|holding|dragging|heaving|aweigh, length_m, tension_t, onBottom_m, position, …}`、
`state.berth{id, contacts[]{fenderId, deflection_m, reaction_kN, overload}, quayContact, lines[]{id, status, tension_t, …}}`;情境 `tugs[]`、`anchors{port|starboard, holdingFactor, dragSpeed_mps}`、`berth{file, id, definition, lines[]}`、`environment.seabed`
(`scenario.schema.json` `$defs.tug/anchorInitial/berthDefinition/fenderType/mooringLine/bodyPoint`;泊位檔 `data/areas/*.json` 同 `berthDefinition`,另可有共用 `fenderTypes`);
故障 `tugLineBreak {id}`(fault-catalogue);事件 `tugCommand/tugLineParted/anchorLetGo/anchorCommand/anchorBroughtUp/anchorDragging/anchorHolding/anchorAweigh/anchorHoused/anchorChainFullyOut/anchorBrakeSlipping/anchorWindlassStalled/fenderContact/quayContact/mooringCommand/mooringLineParted`;
指令被拒時 `commandRejected {reason}`(原因列表見設計文件第 6.2 節)。力以噸力(t)、碰墊反力 kN、能量 kN·m、方向相對船艏順時針(度)。

靠泊/錨泊評估(`docs/design/physics-tugs-anchors-mooring.md` 第 9 節;C# 產生):`summary.berthing{berthId, maxLateralSpeed_mps/_kn, maxContactSpeed_mps, departedAt_s?, reContacts?, maxReContactSpeed_mps?, maxEnergy_kNm, maxEnergyRatio, fenderContacts, fenderOverloads, quayContacts, lineBreaks, maxLineTensionRatio, tugLineBreaks, tugWorkingTime_s, maxTugForce_t, berthCentreChainage_m, allFastAt_s, allFastMidshipChainage_m, allFastPositionError_m, landings[]{t_s, bow/midship/stern_mps|_kn, max_mps, initial, contactSpeed_mps|_kn}, contacts[]{fenderId, approach, normalSpeed_mps|_kn, energy_kNm, ratedEnergy_kNm, energyRatio, overload, reContact?}, lines[]{lineId, maxTension_t, mbl_t, maxTensionRatio, parted}, tugs[]{id, workingTime_s, maxForce_t, lineParted}}`(情境有泊位或拖船時輸出;`maxLateralSpeed` = 接近接觸(`contacts[].approach`:未繫妥且離開碰墊 ≥ 0.5 m 後的接觸)接觸點法向速度最大值,艏/舯/艉端點速度僅供講評;繫妥 = 所有纜繩受力且艏艉皆已靠上)、
`summary.anchoring{letGoCount, maxLetGoSog_kn, dragEvents, dragTime_s, maxChainTension_t, minScopeRatio, firstDragAt_s, dragDetectedAt_s, dragResponseAt_s, dragResponseTime_s, dragResponseBy, anchors[]{side, letGoSog_kn, targetLength_m, plannedScopeRatio, broughtUpLength_m, scopeRatio, maxTension_t, dragEvents, dragTime_s, …}}`(情境有 anchors 區段或錨曾被使用時輸出);
「已離開泊位」= 曾接觸後所有碰墊壓縮為 0、未繫妥且船體距碼頭線 > 情境 `berth.departedDistance_m`(預設 20 m),之後的接觸為再碰回(`reContact: true`、`maxReContactSpeed_mps`,與 maxLateralSpeed 分開;E14);
容許帶指標 `berthing.*` / `anchoring.*`(`scenario.schema.json` bands.metric 說明;與 Python scenario.py METRIC_NAMES 一致);碼頭硬接觸/斷纜以 hardFail `{rule: event, kind: quayContact | mooringLineParted}`。

岸壁效應(`docs/design/physics-bank-effect.md`;C# 產生,Python 參考實作忽略/不輸出):情境 `banks{file?, ids?, lines[], definitions[], scale}`(`scenario.schema.json` `$defs.bankDefinition{id, type: vertical|sloped, line[], waterSide: left|right, slope, topDepth_m, channelDepth_m}`;
岸壁檔 `data/areas/*.json` 的 `banks[]` 同 `bankDefinition`,`file` 於載入時內嵌為 `definitions`);狀態 `state.bank{Y_kN, N_kNm, side: port|starboard|none, parameter, distance_m?, clearance_m?, nearestSide?, bankId?, port_m?, starboard_m?, depthRatio?, influence_m}`
(力為船體座標:Y 右舷正、N 艏向右正;`side` = `Y_kN` 指向的舷側(力的方向,|Y| < 1e-6 N 時 none),岸在哪一舷看 `nearestSide`,兩者在船艏先進入岸壁時相反;
只有情境有 `banks` 時輸出,未使用者雜湊與狀態不變)。

雷達環境(`docs/design/radar-arpa.md` 第 2.1 節;C# 產生,Python 參考實作忽略/不輸出):情境 `environment.radar{rainAreas[]{id?, polygon[] | center + radius_nm, intensity 0–1}, seaState?}`
→ `state.radarEnvironment{rainAreas[]{id, intensity, rate_mmh(= 16 × intensity), polygon[]{lat, lon, x, y} | center{lat, lon, x, y} + radius_m}, seaState?}`(本地 ENU;情境未給時省略;只來自情境、不進雜湊;廣播/紀錄只在關鍵幀 — 載入/重設/倒帶後第一幀與每秒一次 — 帶出,其他幀省略 = 不變);
雷達服務據以產生雨雜波與雨衰減、覆寫海況(教官 `environment` 指令優先)。S 頻單獨故障 `radarFailureS` / `radarDegradedS`(fault-catalogue,只記錄於核心;雷達服務 S 頻停發/降敏,`radarFailure`/`radarDegraded` 只影響 X 頻)。
