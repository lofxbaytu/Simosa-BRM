# USB 實體操控台 — 序列埠文字行協定(console-serial)

> 需求:規劃書第 5.4 節精簡版操控台(舵輪、車鐘、側推、按鈕、指示燈)、R1/R13;追溯表「實體操控台(Teensy)」。
> 程式:`src/Console.Serial/`(主機端服務,`SimosaBRM.Console.Serial`;可測邏輯在 `Core/`:`ProtocolParser`、`LineChecksum`、`CommandMapper`、`ValueDebouncer`、
> `FeedbackFormatter`、`ConsoleSession`;I/O 在 `Bridge/`:`SerialDeviceLink`(System.IO.Ports,只在 `--port`)、`StreamDeviceLink`(`--loopback` 檔案/具名管道、測試用記憶體串流)、
> `CoreLink`(核心 WebSocket,重連與指令暫存)、`ConsoleBridge`)、`firmware/teensy-console/`(Teensy 4.0 參考韌體);
> 測試:`tests/Console.Serial.Tests/`(含與真 SimCore.Host 子程序的端對端)。啟動:`tools/launch/start-ship-laptop.{sh,ps1} --console <COM 埠>`/`-Console`
> (config `console.enabled/port/baud`;`loopback:<讀取檔>,<寫入檔>` 為測試用假裝置),服務參數 `--port|--loopback`、`--ws`、`--baud`、`--hb-timeout`、`--log`、`--duration`;
> log `build/logs/console.log`;發行版 `app/console/`。安裝與接線:`docs/ops/安裝與啟動.md` 第 4.6 節。
> 傳輸:USB CDC 虛擬序列埠(Teensy `Serial`),**115200 8N1**(CDC 下鮑率僅為形式)、**ASCII**、每行以 `\n` 結尾(主機容忍 `\r\n`),欄位以 `,` 分隔,
> 命令字不分大小寫(建議大寫)。主機端只做「序列行 ↔ SimCommand JSON / 狀態回寫」的轉換,不改核心、不進狀態雜湊;
> 核心看到的只是一般客戶端送來的 `rudder/telegraph/rpm/thruster/steeringMode/alertAcknowledge/alertSilence/bnwasReset` 指令(`command.schema.json`)。

## 1. 行格式與校驗和

```
<命令>[,<欄位>…][*XX]\n
```

- `*XX`:NMEA 式校驗和 — `*` 之前**全部字元**(含命令與逗號,不含 `*`)逐位元 XOR,兩位十六進位(大寫;主機解析不分大小寫)。
- 校驗和**選填**;但裝置的 `HELLO` 宣告 `caps` 含 `CS` 後,該裝置之後每一行**必帶**,缺少即整行拒絕(`checksumMissing`),主機送往裝置的行也一律附校驗和。
  未宣告 `CS` 時若行上有 `*XX` 仍會驗證,錯誤即拒絕(`checksumInvalid`)。
- 空行與 `#` 開頭的行略過(韌體除錯輸出可用 `# …`)。無法辨識的命令整行拒絕並記 log,**不會中斷連線**。
- 範例:`RUD,-12.5*5A`、`HB*0A`(`RUD,-12.5` 全部字元 XOR 為 0x5A、`HB` 為 0x0A)。

## 2. 裝置 → 主機

| 行 | 欄位 | 說明 |
|---|---|---|
| `HELLO,<fw>,<caps>` | `fw` 韌體版本字串;`caps` 以 `+` 相連的能力代號 | 上電/USB 連線後、以及每次未在 2 s 內收到 `ACK` 時重送。`caps`:`RUD`(舵輪)、`TEL`(車鐘檔位)、`RPM`(轉速旋鈕)、`THR`(側推)、`BTN`(按鈕)、`NFU`(非隨動推桿)、`LED`、`DISP`、`BUZZ`、`CS`(每行帶校驗和)。主機回 `ACK` |
| `RUD,<度>` | −35…35,右舵正、左舵負(與 `command.schema.json` 的 `rudder` 相同),小數可 | 舵輪電位計;韌體只在變化 ≥ 0.25° 或每 500 ms 送一次。主機再去抖:每秒最多 20 筆、與上次送核心的值差 < 0.5° 不送(最後一筆遲送,不遺失)。超出 ±36 整行拒絕(`outOfRange`),±35–36 夾到 ±35 |
| `TEL,<檔位>` | `FAH` `HAH` `SAH` `DSAH` `STOP` `DSAS` `SAS` `HAS` `FAS`(另接受 `EFAS`、`NAVF`,對應 `state.schema.json` 的 telegraph 列舉) | 車鐘檔位開關;只在檔位變化時送 → `telegraph` 指令 |
| `RPM,<rpm>` | 轉速設定(數值;\|rpm\| ≤ 1000) | 有轉速旋鈕的操控台;→ `rpm` 指令(去抖:≥ 1 rpm、20 Hz) |
| `THR,<百分比>` | −100…100,正 = 推向右舷(與 `thruster` 指令 −1…1 同號) | 側推電位計;主機除以 100 → `thruster` 指令(去抖:≥ 1 %、20 Hz)。超出 ±101 整行拒絕(`outOfRange`),±100–101 夾到 ±100 |
| `BTN,<名稱>,<1\|0>` | `ACK`(警報確認)、`SILENCE`(警報靜音)、`FU`、`NFU`、`EMERG`(操舵模式)、`BNWAS`(BNWAS 重設);`1` 按下、`0` 放開 | 韌體去抖 ≥ 20 ms。只有 `1` 產生指令:`ACK` → `alertAcknowledge`(全部,station bridge)、`SILENCE` → `alertSilence`、`FU` → `steeringMode fu`、`NFU` → `steeringMode nfu`(direction stop)、`EMERG` → `steeringMode emergency`、`BNWAS` → `bnwasReset`;`0` 不送 |
| `NFU,<L\|R\|0>` | 非隨動推桿:`L` 左推、`R` 右推、`0` 放開 | → `steeringMode nfu` 帶 `direction` port / starboard / stop(核心在 nfu 模式下以舵機速率推舵;規劃書第 7.2 節) |
| `HB` | — | 心跳,**1 Hz**。主機 3 s 內未收到任何行即視為操控台斷線:只記 log、停止回寫、**不**改操舵模式;再收到任一行即恢復並重送全部顯示 |

## 3. 主機 → 裝置

| 行 | 欄位 | 說明 |
|---|---|---|
| `ACK` | — | `HELLO` 的回覆(握手完成);裝置收到後才開始送控制行(避免主機未就緒時遺失) |
| `LED,<名稱>,<0\|1\|blink>` | `FU` `NFU` `AUTO` `EMERG`(目前操舵模式,互斥)、`ALARM`(有未確認警報 `blink`、已確認/靜音仍作用中 `1`、無 `0`)、`BNWAS`(警報階段 ≥ 1 `blink`、監視中 `1`、關閉 `0`)、`LINK`(主機已連上核心 `1`,否則 `0`) | 只在值改變時送;裝置重新連線時全部重送 |
| `DISP,<欄位>,<值>` | `RUD`(實際舵角,度,右正,一位小數)、`RPM`(實際轉速,整數,倒俥為負)、`HDG`(艏向 000–359,整數)、`TEL`(核心目前車鐘檔位) | 七段/LCD 顯示;每欄位 ≤ 5 Hz、只在顯示值改變時送 |
| `BUZZ,<ms>` | 蜂鳴毫秒數(50–5000) | 新警報出現(未確認且可聽)時送一次 `BUZZ,500`;確認/靜音由核心狀態反映在 `LED,ALARM` |

主機不會送其他行;裝置應忽略未知行。

## 4. 時序範例

```
裝置 → HELLO,1.0.0,RUD+TEL+THR+BTN+NFU+LED+DISP+BUZZ+CS*xx     主機 → ACK*xx
裝置 → HB*xx(每秒)                                           主機 → LED,LINK,1*xx  LED,FU,1*xx  DISP,HDG,087*xx  DISP,RUD,0.0*xx  DISP,RPM,0*xx  DISP,TEL,STOP*xx
裝置 → RUD,10.0*xx                                            主機 → 核心 {"type":"rudder","value":10}      之後 DISP,RUD 隨實際舵角 0.0 → 10.0 更新
裝置 → TEL,HAH*xx                                             主機 → 核心 {"type":"telegraph","value":"HAH"}  DISP,TEL,HAH、DISP,RPM 隨轉速上升
裝置 → BTN,NFU,1*xx  NFU,R*xx … NFU,0*xx                      主機 → {"type":"steeringMode","value":"nfu","args":{"direction":"stop"}} → …"starboard" → …"stop";LED,NFU,1
核心有新警報                                                  主機 → BUZZ,500*xx  LED,ALARM,blink*xx
裝置 → BTN,ACK,1*xx                                           主機 → 核心 {"type":"alertAcknowledge"};之後 LED,ALARM,1(仍作用中)或 0
(3 s 無任何行)                                               主機 log「操控台心跳逾時」;停止回寫;再收到行即恢復並重送 LED/DISP
```

## 5. 去抖、速率與錯誤處理(主機端;`src/Console.Serial/Core`)

- `ValueDebouncer`(舵/轉速/側推各一):`Offer(value, now)` 與上次**送出**值差小於門檻 → 丟棄並清除待送;間隔未到 → 記為待送,`Poll` 時間隔到即補送(最後位置不遺失);首筆一律送。`FeedbackFormatter` 的 DISP 5 Hz 限制同樣以待送 + `Poll` 補送。
- 行解析錯誤(`checksumInvalid`、`checksumMissing`、`unknownCommand`、`badArgument`、`outOfRange`)只記 log 與計數,不回應裝置、不中斷。
- 心跳逾時 3 s(`--hb-timeout`)→ `deviceConnected=false`、log 一次;期間核心狀態仍接收但不回寫;恢復時 `FeedbackFormatter.Reset()` 重送全部顯示與燈號(不重送蜂鳴)。任何行(含被拒絕的行)都算裝置活動;第一行(未必是 HELLO)即視為連線。
- 序列埠開啟失敗或拔線(讀寫例外)→ log、每秒重新開啟;`--loopback` 的串流結束即結束程序。
- 核心 WebSocket 斷線:指令暫存(最多 256 筆、滿時丟最舊)、每秒重連;`LED,LINK` 反映連線狀態。

## 6. 回寫資料來源(`state.schema.json`)

| 裝置行 | 狀態欄位 |
|---|---|
| `DISP,RUD` | `rudder`(實際舵角) |
| `DISP,RPM` | `rpm` |
| `DISP,HDG` | `heading` |
| `DISP,TEL` | `telegraph` |
| `LED,FU/NFU/AUTO/EMERG` | `steering.mode`(fu / nfu / autopilot / emergency) |
| `LED,ALARM`、`BUZZ` | `alerts[].state`(`activeUnacknowledged`/`rectifiedUnacknowledged` → blink 與新警報蜂鳴;`activeAcknowledged`/`activeSilenced` → 1)、`alerts[].audible` |
| `LED,BNWAS` | `bnwas.mode`、`bnwas.active`、`bnwas.stage` |
| `LED,LINK` | 主機服務與核心 WebSocket 的連線狀態(非核心欄位) |
