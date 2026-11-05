# Teensy 4.0 精簡版操控台參考韌體(`teensy-console.ino`)

> 規劃書第 5.4 節精簡版操控台(舵輪、車鐘、側推、按鈕、指示燈)、R1/R13;協定 `src/Contracts/console-serial.md`;
> 主機端服務 `src/Console.Serial`(`start-ship-laptop.* --console <COM 埠>`);安裝與接線 `docs/ops/安裝與啟動.md` 第 4.6 節。
> **本韌體無法在無硬體的建置容器編譯與測試**:以 Arduino IDE 2.x + Teensyduino 編譯燒錄,依第 4 節步驟驗證。接腳與電阻梯門檻為 **ASSUMED**,實體製作時依接線調整常數。

## 1. 硬體(ASSUMED)

| 元件 | 規格 | 接腳 | 說明 |
|---|---|---|---|
| 主板 | Teensy 4.0(USB CDC,3.3 V 邏輯) | — | USB Type 選 **Serial**;供電由 USB |
| 舵輪 | 10 kΩ 線性電位計(單圈或帶減速齒輪的多圈) | `A0`(兩端 3.3 V / GND) | 中點 = 舵中;左舵 −35°、右舵 +35°;`POT_DEADBAND_DEG` 0.3° 死區 |
| 側推 | 10 kΩ 線性電位計(自復中彈簧推桿) | `A1` | 中點 = 0;正 = 推向右舷 |
| 車鐘檔位 | 9 段旋轉開關 + 電阻梯(8 × 1 kΩ 串聯,各檔抽頭) | `A2` | 由低到高 FAS、HAS、SAS、DSAS、STOP、DSAH、SAH、HAH、FAH;門檻 = ADC 均分 9 段 + 遲滯(`TELEGRAPH_HYSTERESIS`) |
| 按鈕 | 瞬時按鈕 × 6(按下接地) | `2` ACK、`3` SILENCE、`4` FU、`5` NFU、`6` EMERG、`7` BNWAS | `INPUT_PULLUP`;去抖 20 ms;按下送 `BTN,<名稱>,1`、放開送 `0` |
| NFU 推桿 | 雙向瞬時搖頭開關(中立自復) | `8` 左、`9` 右(接地) | `NFU,L` / `NFU,R` / `NFU,0` |
| 指示燈 | LED × 7 + 330 Ω | `14` FU、`15` NFU、`16` AUTO、`17` EMERG、`18` ALARM、`19` BNWAS、`20` LINK | `LED,<名稱>,0|1|blink`;blink 為 2 Hz |
| 蜂鳴器 | 主動式 3.3 V(或經電晶體驅動) | `21` | `BUZZ,<ms>`(50–5000 ms) |
| 顯示(選配) | I²C 1602 LCD 或四位七段模組 | `18/19`(SDA/SCL,需改 LED 接腳) | 參考韌體只把 `DISP,<RUD|RPM|HDG|TEL>,<值>` 存在 `displayField()`,接模組時在該函式更新 |

接線注意:電位計兩端接 **3.3 V 與 GND**(Teensy 4.0 類比輸入不耐 5 V);LED 經限流電阻接 GND;按鈕/開關另一端接 GND;線材長度 > 1 m 時類比線加 100 nF 對地電容。

## 2. 行為(與協定一致)

- 上電 → 全部 LED 自檢 300 ms → 等 USB 主機開啟埠(最多 3 s)→ 送 `HELLO,1.0.0,RUD+TEL+THR+BTN+NFU+LED+DISP+BUZZ+CS*XX`;未收到 `ACK` 每 2 s 重送,**收到 ACK 前不送控制行**;USB 重新連線(DTR 落下再起)時重新握手。
- 每行附 `*XX` 校驗和(`CS`);主機送來的行缺校驗和或錯誤即丟棄;未知行忽略。
- `RUD`:變化 ≥ 0.25° 或每 500 ms 送一次(主機再去抖 20 Hz / 0.5°);`THR`:≥ 1 % 或每 500 ms;`TEL`:只在檔位變化時;`BTN`/`NFU`:去抖 20 ms;`HB` 1 Hz。
- `LED`:`1` 亮、`0` 滅、`blink` 2 Hz;`BUZZ`:響指定毫秒;`DISP`:交給 `displayField()`。
- 無外部函式庫,完全離線;掃描週期約 200 Hz。

## 3. 編譯與燒錄(岸上,離線可行)

1. 安裝 Arduino IDE 2.x 與 Teensyduino(Teensy 4.0 板定義;安裝檔岸上下載後離線安裝);**不要**把 IDE 的 sketchbook 設在 `C:\Users`,專案慣例為 `D:\Simosa BRM\.cache\arduino\`(IDE 偏好設定 → Sketchbook location)。
2. 開啟 `firmware/teensy-console/teensy-console.ino`;Tools → Board **Teensy 4.0**、USB Type **Serial**、CPU Speed 600 MHz、Optimize Faster。
3. Verify → Upload(Teensy Loader 自動燒錄;首次需按板上 Program 鍵)。
4. 版本:改 `FW_VERSION`(HELLO 第 2 欄,主機 log 會記錄),並把韌體版本登錄到兩船版次清單(規劃書第 5.5 節)。

## 4. 驗證步驟(實機)

1. 燒錄後以序列監視器(115200,換行 `\n`)觀察:每 2 s 一行 `HELLO,…*XX`;手動送 `ACK*49` 後改為每秒 `HB*0A`,轉舵輪出現 `RUD,…`、轉車鐘出現 `TEL,…`、按鈕出現 `BTN,…,1/0`。
2. 校驗和:用 `src/Console.Serial` 的測試常數核對(`HB*0A`、`RUD,-12.5*5A`)。
3. 關閉序列監視器,啟動主機服務:`dotnet app\console\SimosaBRM.Console.Serial.dll --port COM5`(Linux `/dev/ttyACM0`),log `build\logs\console.log` 應有「HELLO 韌體 1.0.0」與「已連線核心」;核心連線後 LINK 燈亮、FU 燈亮,`DISP` 值隨核心狀態更新。
4. 舵輪左右到底:教官站/操船台的舵令應為 −35 / +35;車鐘各檔位對應正確;按 ACK 可確認警報(ALARM 燈由閃爍轉常亮/熄滅)、按 NFU 後推桿 L/R 可推舵;拔除 USB 3 s 內主機 log「操控台心跳逾時」,重新插上自動恢復。
5. 校正:舵中與 ±35° 若有偏差,調整 `readCentredPot()` 的比例或加入校正表;車鐘門檻依量測的各檔 ADC 值調整 `readTelegraphIndex()`。

## 5. 已知限制

- 未在容器編譯(無硬體與 Teensyduino);接腳與電阻梯為 ASSUMED。
- 顯示模組未實作(只有 hook);無 HID/遊戲控制器模式(協定採序列行,較易除錯與離線驗證)。
- 類比電位計未做非線性校正;需要時以校正表取代 `readCentredPot()`。
