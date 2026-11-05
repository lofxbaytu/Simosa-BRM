# OpenCPN / NMEA 實機驗收步驟(規劃書第 14 章「儀器:NMEA 輸出 — 校驗和、句型速率、OpenCPN 正確顯示自船與 AIS 目標」;R1)

容器內已自動驗證的部分:`uv run python -m simosa_brm.nmea_check` 對 Host 實跑 E05 的 UDP 單播、UDP 多播與 `--nmea-file` 檔案三種來源,
校驗和、IEC 61162-1 欄位數、速率(HDT/THS/ROT/RSA 10 Hz,GGA/RMC/VTG/VBW/DPT/MWV 1 Hz,TTM 每目標 1 Hz,ALR/ALF 每警報 1 Hz,ALC ≤ 30 s,AIS 類型 1 每 10 s 含自船 VDO)、
時間戳、GNSS 失效後 GGA 品質 0 / RMC V / VTG N / VBW 對地 V、AIVDM 解碼、以及與核心 WebSocket 狀態的位置/航向/水深/SOG/COG 一致性全部通過
(pytest `tests/test_nmea_check.py`、xunit `NmeaTests`/`NmeaSensorTests`/`AisTests`)。**本文件是只能在實機做的部分**:OpenCPN 的實際顯示。

需要:船上筆電(或岸上單機)+ 教官筆電(已安裝 OpenCPN 5.x 與 Dashboard 外掛;連線設定見 `config/opencpn/README.md`)+ 直連網路線。
時間約 30 分鐘。結果記在 `build/nmea/opencpn-check-<yyyyMMdd>.md`(本範本複製後填寫)與截圖 `build/nmea/*.png`,隨驗收紀錄歸檔(R12)。

## 0. 前置

| # | 步驟 | 預期 | 結果 |
|---|---|---|---|
| 0.1 | 教官筆電依 `docs/ops/安裝與啟動.md` 第 3 節完成直連(192.168.77.21 ↔ 船上 192.168.77.11),Wi-Fi/藍牙停用 | `ping 192.168.77.11` 通 | ☐ |
| 0.2 | OpenCPN:Options → Connections 建好 UDP 多播 239.192.0.1:60001(或單播 10110)、ARPA 10111;Ships → AIS 勾顯示目標/名稱/ARPA | 連線清單顯示 Enabled | ☐ |
| 0.3 | Windows 防火牆允許 opencpn.exe 入站 UDP 10110/10111/60001(所有設定檔) | — | ☐ |
| 0.4 | 教官筆電 `src/Tools.Calibration` 可執行 `uv run python -m simosa_brm.nmea_check --help` | 顯示用法 | ☐ |

## 1. 啟動核心並確認 NMEA 到達

| # | 步驟 | 預期 | 結果 |
|---|---|---|---|
| 1.1 | 船上筆電啟動(`start-ship-laptop.ps1`,`config\ship-laptop.json` 的 `ports.nmeaBind: "192.168.77.11"`、`ports.nmeaFile: "build/nmea/E05-opencpn.nmea"`,多播收不到時 `ports.nmeaTargets: ["192.168.77.21:10110"]`;或手動 `dotnet app\simcore\SimosaBRM.SimCore.Host.dll --scenario data\scenarios\E05_pilot_traffic.yaml --record --nmea-bind 192.168.77.11 --nmea-file build\nmea\E05-opencpn.nmea [--nmea-target 192.168.77.21:10110]`) | 主控台印出「NMEA 0183:udp://127.0.0.1:10110 與 239.192.0.1:60001(綁定 192.168.77.11)…」與「NMEA 檔案:build/nmea/E05-opencpn.nmea」;情境無 `ship.mmsi` 時另有一行「NMEA:自船 AIS !AIVDO 不送」 | ☐ |
| 1.2 | 教官筆電:`uv run python -m simosa_brm.nmea_check --multicast 239.192.0.1:60001 --iface 192.168.77.21 --seconds 30 --ws ws://192.168.77.11:8765 --report ..\..\build\nmea\report-multicast.md` | 結束碼 0;報告第 1 節全部 ok、第 7 節「比對秒數 ≥ 28、不一致 0」 | ☐ |
| 1.3 | (若用單播備援)關閉 OpenCPN 後 `--udp 10110 --seconds 30`,再重開 OpenCPN | 結束碼 0 | ☐ |
| 1.4 | OpenCPN 狀態列連線指示燈(左下)閃綠;Options → Connections → *Show NMEA Debug Window* 可見 `$GPGGA`、`$HEHDT`/`$HETHS`、`!AIVDM`、`$RATTM`、`$IIALF` 流動 | 每秒約 50 行(3 目標時) | ☐ |

## 2. 自船顯示

| # | 步驟 | 預期 | 結果 |
|---|---|---|---|
| 2.1 | 海圖置中自船(Ctrl+O / 自船圖示) | 自船位於高雄港外西南(E05 初始 22°29.7′N 120°12.3′E 附近);與教官站航跡圖的自船位置一致(無 ENC 時 CM93 示意圖僅供參考) | ☐ |
| 2.2 | 學員下舵 20° 右(操船台或教官站) | OpenCPN 的艏向線(HDT/THS)右轉、COG 預測線滯後跟上;Dashboard 舵角 +20、ROT 為正且與儀器頁 ROT 相同(±0.5 °/min) | ☐ |
| 2.3 | 車鐘 半速 → 全速 | Dashboard SOG/STW 逐漸增加,與儀器頁 SOG(±0.1 kn)一致 | ☐ |
| 2.4 | Dashboard Depth | 與儀器頁龍骨下水深相同(±0.1 m;E05 約 19 m) | ☐ |
| 2.5 | Dashboard 風 | 相對風(R)與真風(T)數值與儀器頁風儀一致 | ☐ |
| 2.6 | 教官站載入 E01(`loadScenario`) | OpenCPN 自船跳到麥寮外海(23°48′N 120°03′E),時間改為 E01 的 `startTimeUtc`(02:00Z 起);NMEA 時刻與教官站 UTC 同步 | ☐ |
| 2.7 | (情境有 `ship.mmsi`,例如教官站載入 E05 複本加 `ship: { mmsi: 416100001 }`)Options → Ships → AIS Targets → *MMSI Properties* 把該 MMSI 標為 **Own ship** | 自船不重複出現為 AIS 目標;AIS 目標清單無自船;未標為 Own ship 時會多一個與自船重合的目標(已知限制) | ☐ |

## 3. AIS 與 ARPA 目標

| # | 步驟 | 預期 | 結果 |
|---|---|---|---|
| 3.1 | 等 15 s(類型 5 靜態資料在目標出現後 2+i s 送出) | 三艘目標以 AIS 符號顯示並有名稱 EVER ORIENT / GOLDEN WAVE / CHIN FU 66;滑鼠停留顯示 MMSI 416300101–103、COG/SOG、CPA/TCPA | ☐ |
| 3.2 | AIS 目標清單(Options → Ships → AIS Target List) | 三艘 Class A,位置每 10 s 更新(Age < 11 s),尺寸/吃水/目的港 KAOHSIUNG 等與情境一致 | ☐ |
| 3.3 | 勾 *Show ARPA targets*(雷達服務 10111 連線)或看 Host 的 `$RATTM`(10110/60001 同流) | ARPA 目標與 AIS 目標重合(同一艘);CPA/TCPA 與教官站目標面板一致(±0.05 nm / ±0.5 min) | ☐ |
| 3.4 | 教官站把 T1 的 AIS 關閉(`targetAis T1 false`) | 約 1 分鐘後 OpenCPN 把 EVER ORIENT 標為失聯(灰)再移除;ARPA 目標仍在 | ☐ |
| 3.5 | CPA 警報:E05 T2 CPA 0.09 nm | OpenCPN 的 CPA 警報對話框/聲響觸發(與教官站 cpaAlarm 事件同一艘) | ☐ |

## 4. 故障注入後的顯示

| # | 步驟 | 預期 | 結果 |
|---|---|---|---|
| 4.1 | 教官站注入 **gpsFailure** | NMEA:GGA 品質 0/衛星 00、RMC V、VTG N(`nmea_check` 報告第 4 節逐秒可見);OpenCPN:自船位置停止更新、狀態列 GPS 圖示變紅/「No GPS」,Dashboard GPS 狀態無定位;AIS 目標仍更新 | ☐ |
| 4.2 | 清除 gpsFailure | 下一秒起自船位置恢復(可能跳回真值位置) | ☐ |
| 4.3 | 注入 **gyroFailure** | HDT 停送、THS 改為 `$HETHS,,V`:OpenCPN 數秒後艏向線改依 COG(或消失),Dashboard HDT 顯示「---」;ROT/舵角仍更新;自船 VDO 航向 511 | ☐ |
| 4.4 | 注入 **gyroDrift**(1°/min) | OpenCPN 艏向線(指示值)與教官站真值航向逐漸分離,與儀器頁電羅經相同 | ☐ |
| 4.5 | 注入 **echoSounderFailure** | DPT 停送:Dashboard Depth 顯示「---」 | ☐ |
| 4.6 | 注入 **gpsJump**(1 nm、000°) | OpenCPN 自船北跳 1 nm(指示值),AIS 目標不動;教官站真值不變 | ☐ |
| 4.7 | 全部清除後 `nmea_check --multicast … --seconds 30` 再跑一次 | 結束碼 0 | ☐ |

## 5. 離線重播(選用)

| # | 步驟 | 預期 | 結果 |
|---|---|---|---|
| 5.1 | 把船上 `build\nmea\E05-opencpn.nmea` 複製到教官筆電,OpenCPN VDR 外掛 *Play* 選該檔 | 自船/AIS 依檔案重現;VDR 以固定速率播放(非即時速率) | ☐ |
| 5.2 | `uv run python -m simosa_brm.nmea_check --file E05-opencpn.nmea --state <同次執行的 build\records\*.jsonl> --report …` | 結束碼 0;第 7 節比對秒數 = 執行秒數、不一致 0 | ☐ |

## 6. 判定與記錄

- 第 1–4 節全部 ☑ → 第 14 章「儀器 NMEA/OpenCPN」列可改為「已實作(實機 <日期>,<船/筆電>)」;任何 ☐ 記下現象、截圖與 `nmea_check` 報告路徑,開待辦。
- 已知限制(不列為失敗):OpenCPN 無 ENC 時底圖為 CM93 示意、與模擬器示意座標不一定吻合;ALR/ALF/ALC 不顯示(供外接 BAM/VDR);自船 VDO 只在情境或 particulars 有 MMSI 時送出且須在 OpenCPN 標為 Own ship;航線/區域不輸出。
- 報告與截圖留在 `build/nmea/`(專案內),隨紀錄包或驗收資料夾回岸歸檔。
