# 介面契約(Contracts)

所有程式(C# SimCore、Python 參考實作、網頁儀器、視景、教官站)共用這裡的 JSON Schema:

| 檔案 | 用途 |
|---|---|
| `state.schema.json` | 自船狀態廣播(25 Hz),欄位、單位與正負慣例;選填 `targets[]` 目標船(含 CPA/TCPA/艏越距離與 AIS 報告值,C# SimCore 產生;Python 參考實作無目標船) |
| `command.schema.json` | 各站送往核心的指令(含 `targetControl` 目標船控制) |
| `ship-particulars.schema.json` | `data/ships/<ID>/particulars.json` |
| `coefficients.schema.json` | 船模係數檔(由校正工具產生,C# 與 Python 共用) |
| `scenario.schema.json` | 情境 YAML(`data/scenarios/*.yaml`);選填 `targets[]`(目標船:船型/尺寸/AIS、初始狀態、行為模式、觸發、AIS 錯誤注入)與 `assessment.minCpa_nm`(Python 參考實作忽略) |

慣例:角度用度、航向 0 至 360、舵角右正左負、ROT 右轉正(度/分);核心內部以弧度與 SI 計算。
