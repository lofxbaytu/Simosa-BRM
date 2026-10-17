# 介面契約(Contracts)

所有程式(C# SimCore、Python 參考實作、網頁儀器、視景、教官站)共用這裡的 JSON Schema:

| 檔案 | 用途 |
|---|---|
| `state.schema.json` | 自船狀態廣播(25 Hz),欄位、單位與正負慣例;選填 `targets[]` 目標船(含 CPA/TCPA/艏越距離與 AIS 報告值,C# SimCore 產生;Python 參考實作無目標船)、`visibility_nm`、`scenario{id, phase, parameterSetVersion, timelineFired, timelinePending, hardFailed, score, ended}`、`route{legIndex, nextWaypoint, xtd_m(右正), xtdLimit_m, distanceToWaypoint_nm, bearingToWaypoint_deg, legCourse_deg, speedLimit_kn, complete}`(情境引擎;Python 不輸出) |
| `command.schema.json` | 各站送往核心的指令(含 `targetControl` 目標船控制;情境引擎新增 `triggerTimeline`、`acknowledge`、`endExercise`、`getAssessment` 查詢,`setEnvironment` 加 `visibility_nm`/`ramp_s`,`injectFault` 可為 `{name, args}`)。WebSocket 另推送 `{"type":"event","event":SimEvent}` 與 `{"type":"assessment","final","summary"}` 框 |
| `ship-particulars.schema.json` | `data/ships/<ID>/particulars.json` |
| `coefficients.schema.json` | 船模係數檔(由校正工具產生,C# 與 Python 共用) |
| `scenario.schema.json` | 情境 YAML(`data/scenarios/*.yaml`);選填 `targets[]`(目標船)、`assessment`(門檻、硬性失敗、容許帶、必要事件、評分)、`timeline[]`(事件時間表)、`route`(航線)、`zones[]`(區域)、`briefing`(字串或物件)、`environment.visibility_nm`;數值欄位可寫 `"${params:<鍵>}"` 引用參數集(YAML 流式 `{ }` 內須加引號)。Python 參考實作忽略 targets/assessment/timeline/route/zones,但須先解析參數引用 |
| `parameter-set.schema.json` | 公司程序參數集(`data/parameters/company-procedures.yaml`):`id`、`version`(YYYY.M.n)、`sources{文件編號: {title, revision, …}}`、`parameters{鍵: {value, unit, source, clause, description, min, max}}`;鍵名單位後綴須與 `unit` 一致 |
| `fault-catalogue.json` | 故障名稱目錄(規劃書第 7.2 節):`modelled` 者引擎改變行為(`steeringGear`、`mainEngine`、`bowThruster`),其餘只記錄與廣播 |

紀錄檔(JSON Lines)各行型別見 `src/SimCore/README.md` 第 4 節:`header`(version 2,含 `parameterSet`)、`input`、`state`、`event`(`SimEvent`:`kind/tick/t/targetId/message/range_nm/cpa_nm/tcpa_min` 不變,新增 `source/itemId/phase/visible/seq/data`)、`snapshot`、`summary`(評估摘要,footer 之前)、`footer`。

慣例:角度用度、航向 0 至 360、舵角右正左負、ROT 右轉正(度/分);核心內部以弧度與 SI 計算。
