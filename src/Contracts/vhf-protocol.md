# 內建 VHF 角色扮演頻道 — 轉送協定

> 設計:`docs/design/vhf-roleplay.md`;程式:`src/SimCore.Host/VhfRelayServer.cs`(伺服器)、`src/Instruments.Web/src/lib/vhf.ts` 與 `vhf-client.ts`(客戶端);
> 測試:`tests/SimCore.Tests/VhfRelayTests.cs`、`src/Instruments.Web/tests/vhf*.test.ts`。
> 端點:`ws://<核心主機>:8767/`(SimCore.Host `--vhf-port`,`--no-vhf` 關閉;與狀態 WebSocket 8765、雷達 8766 分開)。不經核心引擎、不進狀態雜湊。

## 1. 框型別

| 框 | 方向 | 內容 |
|---|---|---|
| 文字框 | 雙向 | UTF-8 JSON 物件,`{"type":"vhf","kind":…}`;其餘欄位依 `kind`(第 2、3 節) |
| 二進位框 | 雙向 | 音訊片段:`[uint32 little-endian n][id 的 UTF-8 n 位元組][audio/webm;codecs=opus 片段]`;`n` 為 1–64。客戶端只在自己的 `pttStart` 被接受後送,`id` 須為伺服器指派給該連線的 id(不符即丟棄);伺服器原框轉送給同頻道(含雙守聽)的其他站 |

伺服器對非 WebSocket 的 HTTP GET 回純文字說明(健康檢查)。

## 2. 客戶端 → 伺服器

| `kind` | 欄位 | 說明 |
|---|---|---|
| `join` | `station`(站名,≤ 40 字;空白時以 id 代)、`channel?`(預設 `16`)、`watch?`(額外守聽的頻道陣列;教官站雙守聽) | 連線後第一則;之前的其他訊息回 `error`。重連後須重新 `join` |
| `channel` | `channel?`、`watch?` | 轉頻道/改守聽。發話中轉到別的頻道會先結束通話(`pttEnd` reason `channelChanged`)。未給 `watch` 時只移除與主頻道重複者 |
| `pttStart` | `channel?`(預設本站頻道) | 按下 PTT:頻道空閒 → 佔用並向同頻道其他站轉送 `pttStart`;已有人發話 → 只回 `busy` 給本站(不轉送、不記錄) |
| `pttEnd` | — | 放開 PTT:釋放頻道、轉送 `pttEnd {duration_s}`、記錄通話(`vhfCall`) |
| `text` | `text`(≤ 500 字,控制字元換成空白)、`channel?` | 文字訊息(無麥克風時的備援;教官扮演 VTS/引水人/他船):轉送同頻道其他站並回送本站(含 `utc`),記錄 `vhfText` |
| `dsc` | `category`(`distress`/`urgency`/`safety`/`routine`,不分大小寫)、`text?` | DSC 簡化:文字警示送給**所有**已加入的站(含本站,不分頻道,頻道標示 `70`),記錄 `vhfDsc`;類別不合法回 `error` |

頻道只接受 `16 12 13 14 06 08 10 67 68 69 71 72 73 77`(規劃書第 7.2 節);`join` 給不支援的頻道退回 `16`,`channel` 給不支援的頻道回 `error` 並維持原頻道。

## 3. 伺服器 → 客戶端

| `kind` | 欄位 | 說明 |
|---|---|---|
| `joined` | `id`、`station`、`channel`、`watch[]`、`channels[]`、`stations[]` | `join` 的回覆;`id` 為本連線 id(`s1`、`s2`…),之後的二進位框標頭用它 |
| `roster` | `stations[]{id, station, channel, watch[], transmitting}` | 任一站加入/離線/換頻道時廣播給所有站;`transmitting` 為該站發話中的頻道或 `null`(面板以此標示頻道佔用) |
| `channel` | `id`、`station`、`channel`、`watch[]` | 本站 `channel` 訊息的確認(可能被修正) |
| `pttStart` | `id`、`station`、`channel` | 同頻道他站開始發話:接收端開始播放之後標頭 `id` 相同的二進位框 |
| `pttEnd` | `id`、`station`、`channel`、`duration_s`、`reason`(`pttEnd`/`disconnected`/`channelChanged`/`timeout`) | 他站結束發話(斷線或超過 180 s 由伺服器代發) |
| `busy` | `channel`、`by`(佔用者 id)、`byStation` | 本站 `pttStart` 時頻道已被佔用(後發者被壓制) |
| `text` | `id`、`station`、`channel`、`text`、`utc` | 文字訊息(含本站的回送) |
| `dsc` | `id`、`station`、`channel`(`70`)、`category`、`text`、`utc` | DSC 警示(所有站) |
| `error` | `message` | 無法解析、未 `join`、不支援的頻道/類別、未知 `kind` |

## 4. 時序範例(A、B 在 16,C 在 12)

```
A → join {station:"bridge", channel:"16"}        ← joined {id:"s1", …}   (B、C 收到 roster)
A → pttStart                                     B ← pttStart {id:"s1", station:"bridge", channel:"16"}   (C 無)
A → [二進位 s1 片段] ×N                            B ← [相同二進位框] ×N
B → pttStart                                     B ← busy {channel:"16", by:"s1", byStation:"bridge"}
A → pttEnd                                       B ← pttEnd {id:"s1", duration_s: 3.2, reason:"pttEnd"}   → 紀錄 vhfCall
C → text {text:"Port Control …"}                 C ← text(回送);A、B 無(不同頻道)                        → 紀錄 vhfText
B → dsc {category:"urgency", text:"PAN PAN"}     A、B、C ← dsc {channel:"70", category:"urgency", …}         → 紀錄 vhfDsc
```

## 5. 紀錄與事件框

通話結束、文字與 DSC 由主控程序寫進核心紀錄的 `vhf` 行(`src/SimCore/Recording/RecordLines.cs` `RecordVhf`):

```json
{"kind":"vhf","tick":1234,"t":24.68,"event":{"kind":"vhfCall","tick":1234,"t":24.68,"source":"vhf","itemId":"16","message":"VHF 16 bridge 通話 3.2 s","visible":true,"seq":-1,
  "data":{"id":"s1","station":"bridge","channel":"16","duration_s":3.2,"text":null,"category":null,"endReason":"pttEnd","utc":"2026-10-30T01:02:03.456Z"}}}
```

`event` 為 `SimEvent` 形狀,`kind` 為 `vhfCall`(`data.duration_s`、`endReason`)、`vhfText`(`data.text`)或 `vhfDsc`(`data.category`、`text`);`tick`/`t` 為轉送當下的引擎時刻。
**帶外行**:與 `session` 行相同,不進狀態雜湊鏈、不在 `event` 行的事件序列內(重播不比對、不重現),只計入 footer 的檔案 SHA-256;
`RecordReader.Read().Vhf` 讀回,講評站(`record-replay.ts`)把 `event` 併入事件清單供時間軸與「通訊」統計。同一個事件物件也以狀態 WebSocket 的
`{"type":"event","event":…}` 框即時送教官站事件流(與 `scenarioLoadRejected` 相同的帶外途徑)。通話內容**不錄音**,只記次數、頻道與秒數。
