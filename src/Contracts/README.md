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
| `fault-catalogue.json` | 故障名稱目錄(規劃書第 7.2 節):`modelled` 者引擎改變行為(操舵 `steeringGear/steeringFuOnly/steeringPumpFailure`、`mainEngine`、`bowThruster`、感測器 `gyroDrift/gyroFreeze/gyroFailure/gpsJump/gpsOffset/gpsFailure/echoSounderFailure/logFailure`、`autopilotFailure`),其餘只記錄與廣播;`effect`、`alert`(BAM 優先序)、`args`(injectFault 參數與預設) |

紀錄檔(JSON Lines)各行型別見 `src/SimCore/README.md` 第 4 節:`header`(version 2,含 `parameterSet{id, version, path(相對專案根目錄,例如 data/parameters/company-procedures.yaml), sha256, resolved[]}`)、`input`、`state`、`event`(`SimEvent`:`kind/tick/t/targetId/message/range_nm/cpa_nm/tcpa_min` 不變,新增 `source/itemId/phase/visible/seq/data`;必要事件開始計時發 `requiredEventPending`,`data {after, afterItemId, afterSeq, start_t, within_s, deadline_t, penalty}` 供教官站倒數,`requiredEventMet/Missed` 的 `data` 含 `within_s`)、`snapshot`、`summary`(評估摘要,footer 之前)、`footer`。

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
