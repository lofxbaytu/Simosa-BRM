# OpenCPN 連線設定(第一階段 ECDIS;規劃書第 5.2 節 NMEA Gateway、第 7.2 節 ECDIS、決策 D4)

OpenCPN 是獨立的 GPL 程式,只經 NMEA 0183 over UDP 接收 SimCore.Host 的輸出,不修改、不連結(規劃書第 10 章授權隔離)。
本資料夾說明如何讓 OpenCPN 顯示模擬器的自船、AIS 目標與水深,以及已知限制;實機驗收步驟見 `tools/opencpn-check.md`,
自動檢驗(校驗和、欄位數、速率、AIS 解碼、與核心狀態一致)用 `uv run python -m simosa_brm.nmea_check`(`src/Tools.Calibration`)。

> 狀態(2026-11):NMEA 的校驗和/欄位數/速率/時間/AIS/ALR/狀態一致性已由 `nmea_check` 在容器自動檢驗通過(UDP 單播、多播、檔案三種來源);
> **OpenCPN 的實際顯示尚未在實機核對**(容器無 OpenCPN),`docs/驗收/需求追溯表.md` 第 14 章「儀器 NMEA」列標為需實機。

## 1. 資料流

```
SimCore.Host(船上筆電 192.168.77.11)
  ├─ UDP 單播 127.0.0.1:10110            同機的 OpenCPN(開發/岸上備課;--nmea-port)
  ├─ UDP 多播 239.192.0.1:60001          IEC 61162-450 預設傳輸群組;教官筆電經直連線收(--nmea-multicast;--nmea-bind 綁直連介面)
  ├─ UDP 單播 <ip:port>(可重複)         --nmea-target 192.168.77.21:10110:直接送到教官筆電的 OpenCPN(多播收不到時的替代)
  └─ 檔案 build/nmea/<name>.nmea          --nmea-file:與 UDP 相同的句型,每句 CR LF(nmea_check --file 與 OpenCPN VDR 外掛離線重播)
Sensors.Radar.Host
  └─ UDP 單播 127.0.0.1:10111(X)/10112(S) ARPA 追蹤目標 $RATTM/$RATLL(docs/design/radar-arpa.md 第 6 節)
```

`tools/launch/start-ship-laptop.*` 目前只帶 `--nmea-port`/`--nmea-multicast`(`config/ship-laptop.json` `ports.nmeaUdp`/`ports.nmeaMulticast`);
`--nmea-target`、`--nmea-bind`、`--nmea-file` 需手動加到啟動命令(或在 PowerShell 腳本的 `$hostArgs` 後追加),見第 5 節。

## 2. 句型與 OpenCPN 顯示

| 句型 | talker | 速率 | 內容(指示值;感測器故障時依 IEC 61162-1 標無效) | OpenCPN 顯示 |
|---|---|---|---|---|
| GGA | GP | 1 Hz | 位置、品質 1/0、衛星 10/00、HDOP | 自船位置;品質 0 時 OpenCPN 不更新位置、狀態列 GPS 圖示變紅(「GNSS 失效」驗收項) |
| RMC | GP | 1 Hz | 狀態 A/V、位置、SOG、COG、日期、模式 A/N | 自船 SOG/COG、時間;狀態 V 時忽略位置 |
| VTG | GP | 1 Hz | COG/SOG(kn、km/h)、模式 A/N | SOG/COG 備援 |
| HDT | HE | 10 Hz | 真航向(電羅經失效時**停送**) | 自船艏向線;停送後 OpenCPN 數秒後改用 COG 畫艏向(可見差異) |
| ROT | TI | 10 Hz | 迴轉率 度/分 | 儀表板 ROT |
| RSA | ER | 10 Hz | 舵角(右正) | 儀表板舵角(Dashboard 外掛) |
| VBW | VD | 1 Hz | 對水/對地縱橫速度、狀態 A/V | 儀表板 STW |
| DPT | SD | 1 Hz | 龍骨下水深、偏移 0.0(測深儀失效時停送) | 儀表板水深(Dashboard 外掛:Depth) |
| MWV | WI | 各 1 Hz | 相對風(R)與真風(T;相對船艏) | 儀表板風向/風速 |
| TTM | RA | 每目標 1 Hz | 情境目標的真值(距離、方位、CPA/TCPA、船名) | 「ARPA」目標(Options → Ships → AIS Targets 勾 *Show ARPA targets*) |
| AIVDM | AI | 類型 1 每 10 s/目標;類型 5 出現後 2+i s 一次、再每 6 min | 情境目標的 AIS(MMSI、位置、COG/SOG/HDG、船名、呼號、尺寸、吃水、目的港) | AIS 目標三角形、名稱、CPA/TCPA 警報 |
| ALR | II | 每警報 1 Hz | 模擬器警報(編號、條件、確認、英文文字) | OpenCPN 不顯示(供外接 BAM/VDR;nmea_check 檢查) |

- 時間 = 情境 `startTimeUtc` + 模擬時間;`loadScenario` 切換情境後 NMEA 與 `state.utc` 一起改為新情境的起點。
- Host **不送 `!AIVDO`(自船 AIS)**,所以 OpenCPN 的自船只來自 GGA/RMC,不會與 AIS 目標重複;若日後加 VDO,須在 OpenCPN 的
  MMSI 屬性(Options → Ships → AIS Targets → *MMSI Properties* → 該 MMSI 勾 *Own ship*)把自船 MMSI 標為自船。
  兩船的 MMSI 不在 `data/ships/*/particulars.json`(交船文件未載),情境目標的 MMSI 在各 `data/scenarios/*.yaml` `targets[].mmsi`(例如 E05 416300101–103)。
- 一批句型(同一 tick 的 HDT/ROT/RSA + 每秒的 GGA…TTM/AIVDM)合成**一個 UDP 資料包**;目標多時可能超過 1472 bytes 而被 IP 分段,OpenCPN 仍能收。

## 3. OpenCPN 設定步驟(GUI;OpenCPN 5.8,其他 5.x 版畫面相近)

1. 安裝 OpenCPN(教官筆電;離線安裝檔由岸上下載後隨更新包帶上船,`docs/ops/安裝與啟動.md` 第 3 節)。
   需要的外掛:**Dashboard**(內建)顯示水深/舵角/風;**VDR**(Voyage Data Recorder,選用)離線重播 `.nmea`;外掛以 *Options → Plugins → Import plugin* 匯入事先下載的離線包(船上無網路)。
2. *Options → Connections → Add Connection*:
   - 同機(船上筆電或岸上單機):Type **Network**、Protocol **UDP**、Address `0.0.0.0`、DataPort **10110**、勾 *Receive Input on this Port*、不勾 *Output on this port*、Priority 1、勾 *Control checksum*;User Comment「Simosa BRM NMEA」。
   - 教官筆電(直連,建議):Type **Network**、Protocol **UDP**、Address **239.192.0.1**、DataPort **60001**(多播;OpenCPN 在位址為 224.0.0.0/4 時會自動加入群組)。
     直連期間教官筆電只有直連介面啟用(Wi-Fi/藍牙停用,規劃書第 5.4 節),OpenCPN 加入群組的介面因此唯一;若仍收不到,改用下一項。
   - 教官筆電(單播備援):Host 加 `--nmea-target 192.168.77.21:10110`,OpenCPN 連線同「同機」設定(Address `0.0.0.0`、DataPort 10110)。
   - ARPA(選用):再加一個 UDP 連線 DataPort **10111**(X 頻雷達的 `$RATTM/$RATLL`),與 AIS 目標比較。
3. *Options → Ships → AIS Targets*:勾 *Show AIS targets*、*Show target names*、CPA/TCPA 警報依訓練需求(例如 CPA 0.5 nm / TCPA 12 min,與情境 `assessment.minCpa_nm` 一致);
   *Mark targets as lost after* 建議 ≥ 60 s(位置報告間隔 10 s)。
4. *Options → Ships → Own Ship*:勾 *Show heading line*、COG predictor 依需求;*Display → Units*:速度 kn、深度 m。
5. *Dashboard* 外掛加儀表:Depth、Rudder angle、Rate of turn、True wind、STW、SOG/COG、GPS status。
6. Windows 防火牆:允許 `opencpn.exe` 的入站 UDP 10110/10111/60001(安裝文件第 2.2 節的規則涵蓋全部設定檔;教官筆電另在第 3 節登錄)。

`opencpn.ini.example` 為對應的設定檔片段(`[Settings/NMEADataSource] DataConnections=`),欄位順序依 OpenCPN 5.8 的 `ConnectionParams::Serialize`
整理(**ASSUMED**,未在實機核對);建議先以 GUI 建立連線一次,再比對/複製 OpenCPN 產生的那一行,而不是直接貼上。

## 4. 海圖與圖層限制

- OpenCPN 不附海圖。台灣 ENC(S-57/S-63)須購買並依安裝端授權(規劃書第 10 章:以 4 個安裝端計價),S-63 加密海圖在 OpenCPN 需 o-charts 外掛;
  `data/charts/` 目前為空(需求追溯表第 3 節「需實機/實資料」)。
- CM93 世界圖僅供示意(版本舊、解析度低、非官方;不得用於航行),港區幾何與模擬器的示意座標(`data/areas/`、規劃書附錄 A 待取得的港方座標)**不一定吻合**;
  練習中應以模擬器儀器頁/雷達為準,OpenCPN 只作 ECDIS 操作面與 AIS 覆蓋的體驗。
- 情境的航線/區域/泊位示意幾何目前不輸出給 OpenCPN(無 GPX 匯出;待辦),教官覆蓋層與故障注入只在模擬器的儀器頁/教官站。
- OpenCPN 缺 IEC 61174 等級的安全等深線/危險物警報與 S-52 呈現(規劃書第 10 章 ECDIS 列);第二階段自建 ECDIS 再補。

## 5. 常用命令

```powershell
# 船上筆電:啟動核心並同時把 NMEA 送到教官筆電、寫檔(驗收時)
dotnet app\simcore\SimosaBRM.SimCore.Host.dll --scenario data\scenarios\E05_pilot_traffic.yaml --record `
  --nmea-bind 192.168.77.11 --nmea-target 192.168.77.21:10110 --nmea-file build\nmea\E05.nmea

# 教官筆電:自動檢驗 30 s(多播或單播),同時連核心狀態比對位置/航向/水深
cd src\Tools.Calibration
uv run python -m simosa_brm.nmea_check --multicast 239.192.0.1:60001 --iface 192.168.77.21 --seconds 30 --ws ws://192.168.77.11:8765 --report ..\..\build\nmea\report.md
uv run python -m simosa_brm.nmea_check --udp 10110 --seconds 30 --report ..\..\build\nmea\report.md   # 須先關閉 OpenCPN(同埠)或改用另一個 --nmea-target 埠

# 離線:檢驗 Host 寫出的檔(可加 --state <紀錄.jsonl> 比對同一次執行的核心紀錄)
uv run python -m simosa_brm.nmea_check --file ..\..\build\nmea\E05.nmea --state ..\..\build\records\<紀錄>.jsonl --report ..\..\build\nmea\report.md
```

結束碼:0 全部通過、1 有不合格項(報告第 8 節列出)、2 參數/輸入錯誤。
