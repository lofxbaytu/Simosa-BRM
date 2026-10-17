# 雷達/ARPA 感測器服務設計(Sensors.Radar)

對應規劃書第 5.2 節(Radar 列:方位掃描、海雨雜波、波束/脈衝展寬、增益/STC/FTC、ARPA 追蹤、CPA/TCPA、試操船)、第 7.2 節雷達列(MSC.192(79))、第 14 章(「ARPA CPA/TCPA 計算 vs 解析值,CPA 誤差 < 0.02 nm」)。擬真等級依 D5:BRM 可信度,不追求型式認可。

| 項目 | 位置 |
|---|---|
| 類別庫 | `src/Sensors.Radar/`(影像、ARPA、幀格式、WebSocket 伺服器、狀態訂閱、NMEA) |
| 主控程式 | `src/Sensors.Radar.Host/`(`SimosaBRM.Sensors.Radar.Host`) |
| 測試 | `tests/Sensors.Radar.Tests/` |
| 契約 | `src/Contracts/radar.schema.json` |
| 陸地資料 | `data/areas/*.json`(目前只有高雄港外示意輪廓,非 ENC) |

## 1. 架構與資料流

```
SimCore.Host ──ws://127.0.0.1:8765(state 25 Hz)──► SimStateClient ──► RadarService.OnState
                                                         │  (指令佇列在狀態幀之間套用)
                                       ┌─────────────────┴─────────────────┐
                                  RadarUnit X(9.41 GHz、1°)        RadarUnit S(3.05 GHz、2°)   ← Parallel.For,各台單執行緒
                                       │ SceneGeometry → SpokeRenderer(ARPA 影像 16 nm × 2048 格、顯示影像 量程 × 512 格)
                                       │ BlobDetector(門檻 + 串流連通元件)→ ArpaTracker(CV 卡爾曼雙模型)
                                       ▼
            ws://0.0.0.0:8766/radar/x|s:二進位影像幀 + JSON(status / tracks 1 Hz / event / ack)
            UDP 127.0.0.1:10111(X)/10112(S):$RATTM + $RATLL 1 Hz
```

- **只讀核心**:不修改 SimCore;以 ProjectReference 取得 `OwnShipState`/`TargetState`、`Encounter`(CPA/TCPA/BCR 同一定義)、`LocalTangentPlane`、`ShipParticulars`、`ScenarioLoader`(讀情境種子與起始 UTC)與 Gateway.Nmea 的 `NmeaSentence`。
- **確定性**:天線方位由模擬時間決定(方位線全域序號 = ⌊t × rpm/60 × 2048⌋);每格雜訊/雜波/雨起伏由 SplitMix64(種子, 方位線序號, 格號)查 65536 筆種子表;偵測與追蹤以有序清單處理。同種子、同狀態序列、同指令序列 → 位元組相同的影像與相同的追蹤 JSON(`SameSeedGivesIdenticalImagesAndTracks`)。種子:`--seed` 或情境檔 `seed`(E05 = 20260505),讀不到時為情境代號 FNV-1a。雷達狀態不進核心雜湊鏈(雷達是核心狀態的純函數 + 本地操作員設定);雷達控制指令目前未寫入核心紀錄(見第 8 節限制)。
- **真值與指示值**:回波物理用 `state.truth`(天線隨船實際轉動、目標真實位置);ARPA 穩定化(相對→真方位、地理位置、真向量)用指示值(電羅經 heading、GNSS pos、COG/SOG)。故電羅經誤差會使北向上畫面旋轉、ARPA 向量偏差(`GyroErrorRotatesNorthUpPictureButNotHeadUp`)。`sensors.headingValid = false` 時 ARPA 停止(無法穩定化),影像只能船首向上。
- **CCRP**:ARPA 量測以天線位置(駕駛台頂,船舯後 0.30 LOA)取得,換算到船舶參考點(船舯,與 SimCore 相同)後計算距離/方位/CPA,因此可與 `state.targets[].cpa_nm` 直接比較。

## 2. 影像產生(每條方位線)

1. **場景準備(每狀態幀一次)**:目標以 SimCore 船體五邊形(`Encounter.ShipOutline`)轉到天線座標,求角度範圍、近遠距離、Cyrus–Beck 邊法向量;雷達地平線 d = 2.21(√h天線 + √h目標) nm,部分遮蔽以可見高度比例平方衰減;截面積 σ = 52 √f · D^1.5(Skolnik;排水量 = L·B·T·Cb·1.025,小船下限 5 m²)× 視角因子(0.4 + 0.6|sin 視角|)。目標高度 0.12 L + 5 m(小船 0.25 L + 1 m)。
2. **方位線幾何**(每條線,與距離格無關):
   - 波束:雙程高斯主瓣 σ_b = BW/3.33,目標角寬內積分以 erf 計算(只對角度範圍 ±4.5σ_b 內的目標);角寬小於波束寬的目標每條線都照到整個船體,距離段為船體近遠距離。
   - 陸地(`data/areas`):射線與多邊形交點,波束寬內取最近入射點(海岸展寬);高於地平線遮蔽高度才可見;入射點後 3 km 內以 1.5 km 衰減(地形遮蔽近似),其後無回波;陸地後方的目標與海面被遮蔽。
   - 雨區(圓):射線交段、Z = 200 R^1.6、雙程衰減 2kR^α(X:k 0.0101、α 1.276;S 頻幾乎無衰減)。
   - 陰影扇區:預設艉向一個(X 174–186°、S 172–188°,衰減 60 dB),參數化(`RadarConfig.ShadowSectors`)。
   - 海雜波方位調制:迎風 +3 dB、背風 −3 dB。
3. **畫到距離格**(`SpokeRenderer`,只查表):目標段 [近, 遠 + 脈衝長] 以覆蓋比例累加 σ·波束因子·R⁻⁴ 增益表;海雜波平均表(依海況:1 nm 處 X 頻 SS1 5 dB … SS6 30 dB,R⁻³ 至臨界距離 0.3 + 0.25 SS nm 後 R⁻⁷;S 頻 −10 dB);雨(S 頻 −19.5 dB);雜訊 Exp(1);僅含回波格取對數,其餘查 dB 表;STC = sea% × min(60, 40 log(3 nm/R)) dB;FTC = 減去前段 2 倍脈衝長平均的 rain% × 0.9;增益門檻 40 − 0.43 × gain dB;強度 = (dB − 門檻) × 255/30。調諧損失 20((tune − 50)/50)² dB(自動調諧為 0);`radarDegraded` −15 dB。
4. **脈衝**:≤ 0.75 nm 短(10.5 m)、≤ 3 nm 中(45 m)、≤ 12 nm 中長(75 m)、以上長(180 m)。ARPA 處理影像固定 16 nm、2048 格(14.5 m)、中脈衝 45 m,動態範圍 80 dB(不飽和,質心有次格精度)。
5. **效能**:預算表(R⁻⁴、雜波、STC、陸地、雨、sin/cos)+ 每目標每線少量運算;實測單台(顯示 + ARPA、3 目標、雨、陸地、18 kn 風)每圈 ≈ 0.2 s / 2.5 s(約 8% 單核,`SingleRadarUsesLessThanOneCore`);E05 實跑兩台 + 強制顯示各約 3% 單核。無客戶端時不產生顯示影像。

## 3. ARPA

- **偵測**:ARPA 影像強度 ≥ 門檻(增益門檻以上 7.5 dB)的連續段,依掃描時間順序與前一條線 8 連通合併(串流連通元件,跨 2047→0 自然連續);元件結束時以(強度 − 門檻)加權求質心,每段先以該線的指示航向與天線位置換成 ENU,距離扣除半個脈衝長(脈衝展寬偏差)。格數 < 3、方位線 < 2 為雜訊;徑向或橫向 > 800 m 或 > 256 條線為大面積回波(陸地/雨區),不擷取也不更新追蹤。
- **追蹤**:等速卡爾曼(x, y, vx, vy),過程雜訊 2e-4 m²/s³;量測共變異數依距離/方位(σ_r = 0.3 格 + 2 m、σ_θ = 0.03°)與回波大小。關聯:每圈每目標一次,預測閘門 Mahalanobis² < 16 或距離 < 40 m ×(1 + 漏掃描),上限 600 m;一個偵測落入兩個閘門、或兩目標距離 < 250 m → `swapRisk`。
- **操船偵測**:(1) NIS > 9.2 連續 2 次 → 過程雜訊 0.5、速度共變異數 +4 m²/s²、30 s;(2) 並行快速濾波器(q = 0.02),精確濾波器 NIS 指數平均 > 3 倍快速者(且 > 0.2)→ 改用快速濾波器狀態、30 s 內 q = 0.02;(3) 新息指數平均偏差檢定。`manoeuvring` 旗標輸出。
- **擷取**:手動 `acquire{bearing, range}`(搜尋半徑 300 m,3 圈無回波 → `acquireFailed`);保護區 `guardZone`:候選須 ≥ 10 格,5 次命中(7 圈內),第 3 次起須落在等速外推 50 m 內(排除海雜波斑點)→ `newTarget`;追蹤中軌跡外推位置 300 m(2 × GuardGateM)內的回波不作候選(失追軌跡不算,否則雷達故障或航向無效恢復後要等失追軌跡保留期屆滿才會重新擷取)。最多 40 目標(MSC.192(79) ≥ 30),編號 1–99 重用。
- **狀態**:`acquiring`(擷取後 60 s 且 ≥ 5 次命中前不給向量)→ `tracking`(`stable` = 追蹤 ≥ 180 s)→ 連續 6 圈漏掃描 `lost`(保留 60 s 後 `deleted`)。
- **輸出**:真向量(course/speed)、相對向量、CPA/TCPA/BCR/BCT(`Encounter.Compute`,BCR 以自船艏向線、正 = 艏前通過)、過去位置(每 30 s,10 點)、危險目標(CPA < 1.0 nm 且 0 ≤ TCPA ≤ 12 分,可由 `cpaLimits` 改)。
- **試操船**:`trialManoeuvre{course, speed, delay(分)}`:自船以目前速度向量航行 delay 後瞬間改為新向量,兩段相對運動各取最近點(`ArpaTracker.TrialCpa`,與暴力取樣比對);`trialCpa_nm`/`trialTcpa_min`。
- **精度驗收**:`ArpaAccuracyTests`(對遇、右舷橫越、左舷橫越、追越、被追越):擷取後 1 分鐘趨勢 + 3 分鐘穩定追蹤,CPA 誤差 0.0006–0.0070 nm(< 0.02 nm)、TCPA < 0.2 分、航向 < 0.2°、速度 < 0.14 kn;`E05IntegrationTests` 直接接 SimCore 引擎(MMG),T2/T3 CPA 誤差 < 0.005 nm。

## 4. 二進位影像幀(WebSocket binary)

little-endian,標頭 40 位元組 + `spokeCount × cells` 位元組強度:

| 偏移 | 型別 | 欄位 | 說明 |
|---:|---|---|---|
| 0 | uint32 | magic | 0x44415253(位元組 `53 52 41 44` = "SRAD") |
| 4 | uint8 | version | 1 |
| 5 | uint8 | radarId | ASCII `'X'` / `'S'` |
| 6 | uint8 | flags | bit0 發射中、bit1 航向有效、bit2 性能劣化 |
| 7 | uint8 | reserved | 0 |
| 8 | uint32 | scan | 圈序號 |
| 12 | uint16 | firstSpoke | 第一條方位線索引 0–2047(相對艏向、順時針) |
| 14 | uint16 | spokeCount | ≤ 32;幀不跨 2047→0 |
| 16 | uint16 | spokesPerRev | 2048 |
| 18 | uint16 | cells | 每線格數(預設 512) |
| 20 | float32 | range_m | 顯示量程(m);格 i 中心距離 = (i + 0.5) × range_m / cells |
| 24 | float32 | heading_deg | 指示航向(北向上:真方位 = 方位 + heading_deg) |
| 28 | float32 | cog_deg | 對地航向(航向向上用) |
| 32 | float64 | t | 模擬時間(s) |
| 40 | uint8[] | data | 方位線依序排列,每線 `cells` 位元組 |

方位線索引 → 相對方位 = index × 360 / 2048。船首向上直接畫;北向上旋轉 heading_deg;航向向上旋轉 heading_deg − cog_deg。24 rpm 時約每 40 ms 一幀(隨狀態幀),每幀約 33 條線 × 512 = 17 KB,單一客戶端約 420 KB/s。慢速客戶端只保留最新 8 幀(其餘丟棄),JSON 另一條佇列不丟。

TypeScript 解析範例:

```ts
const dv = new DataView(buf);
if (dv.getUint32(0, true) !== 0x44415253) throw new Error('not SRAD');
const first = dv.getUint16(12, true), count = dv.getUint16(14, true), perRev = dv.getUint16(16, true), cells = dv.getUint16(18, true);
const rangeM = dv.getFloat32(20, true), headingDeg = dv.getFloat32(24, true);
for (let j = 0; j < count; j++) {
  const relDeg = ((first + j) % perRev) * 360 / perRev;      // 船首向上
  const trueDeg = (relDeg + headingDeg) % 360;                // 北向上
  const line = new Uint8Array(buf, 40 + j * cells, cells);
}
```

## 5. JSON 訊息(radar.schema.json)

連線後伺服器先送 `status`;之後 `tracks`(模擬時間 1 Hz)、`event`、`ack`(只給送指令的客戶端),設定成功後廣播新的 `status`;情境切換(`RadarService.SetUnits`,試操船與 CPA/TCPA 限值回到預設、天線位置可能隨船改變)後下一個狀態幀也對每台新雷達廣播 `status`。客戶端的試操船設定以最新收到的 `status` 或 `tracks` 為準(兩者都在無試操船時省略 `trial`)。

```json
{"type":"status","radar":"x","band":"X","frequency_GHz":9.41,"transmitting":true,"standby":false,"failed":false,"degraded":false,"headingValid":true,
 "range_nm":6,"rangeScales":[0.125,0.25,0.5,0.75,1.5,3,6,12,24,48,96],"pulse":"medium","pulseLength_m":75,"gain":70,"sea":50,"rain":0,"tune":50,"autoTune":true,
 "rpm":24,"spokes":2048,"cells":512,"beamwidth_deg":1,"antennaHeight_m":25.02,"antennaFwd_m":-31.65,"antennaStbd_m":0,
 "shadowSectors":[{"from":174,"to":186,"attenuation_dB":60}],"guardZone":{"enabled":true,"inner_nm":0.3,"outer_nm":8,"from":0,"to":360,"relative":true},
 "cpaLimit_nm":1,"tcpaLimit_min":12,"arpaRange_nm":16,"maxTracks":40,"seaState":2,"rainCells":0,"landPolygons":2,"frame":{"magic":"SRAD","version":1,"headerBytes":40}}
```

```json
{"type":"tracks","radar":"x","t":129.0,"scan":51,"transmitting":true,
 "ownShip":{"x":-2489.1,"y":-2823.6,"lat":22.504517,"lon":120.215804,"heading":47.3,"cog":38.8,"sog":8.2},
 "cpaLimit_nm":1,"tcpaLimit_min":12,"trial":{"course":90,"speed":8,"delay_min":1},
 "targets":[{"id":2,"label":"02","state":"tracking","acquisition":"auto","age_s":119.0,"stable":false,"manoeuvring":false,"dangerous":true,"swapRisk":false,"misses":0,
   "x":1220.5,"y":4310.2,"lat":22.568920,"lon":120.251860,"range_nm":4.21,"bearing_deg":40.1,"relBearing_deg":352.8,
   "course_deg":231.8,"speed_kn":10.06,"relCourse_deg":226.4,"relSpeed_kn":18.21,"cpa_nm":0.428,"tcpa_min":12.0,"bcr_nm":0.512,"bct_min":11.4,
   "trialCpa_nm":1.37,"trialTcpa_min":10.8,"past":[{"t":72.5,"x":1583.2,"y":4596.0,"lat":22.571,"lon":120.2554}]}]}
```

```json
{"type":"event","radar":"x","event":{"kind":"newTarget","t":19.8,"targetId":1,"message":"保護區新目標 01:7.8° 3.14 nm(自動擷取)"}}
{"type":"ack","radar":"x","cmd":"acquire","ok":true,"reqId":"a7","id":4}
{"type":"ack","radar":"x","cmd":"range","ok":true,"reqId":3,"value":6}
```

事件 `kind`:`acquireStarted`、`tracking`、`newTarget`、`acquireFailed`、`targetLost`、`deleted`、`cancelled`、`dangerousTarget`、`radarFailure`、`radarRestored`、`radarDegraded`、`headingLost`。

### 控制指令(客戶端 → 伺服器,文字框)

```json
{"cmd":"range","value":3}                       // nm,換成最近的標準量程(ack.value)
{"cmd":"gain","value":70}                       // 0–100;sea(STC)、rain(FTC)同格式
{"cmd":"tune","value":40}  {"cmd":"tune","auto":true}
{"cmd":"standby"}  {"cmd":"transmit"}           // 待機清除所有追蹤
{"cmd":"acquire","bearing":45.2,"range":3.1}    // 真方位;"relative":true 為相對艏向
{"cmd":"cancel","id":3}  {"cmd":"cancel","id":"all"}
{"cmd":"guardZone","enabled":true,"inner_nm":0.5,"outer_nm":3,"from":300,"to":60,"relative":true}
{"cmd":"trialManoeuvre","course":90,"speed":8,"delay":1}   {"cmd":"trialManoeuvre","active":false}
{"cmd":"cpaLimits","cpa_nm":0.5,"tcpa_min":10}
{"cmd":"environment","seaState":4,"rainCells":[{"lat":22.55,"lon":120.24,"radius_m":2000,"rate_mmh":8}]}   // 教官
{"cmd":"getStatus"}  {"cmd":"getTracks"}
```

任何指令可帶 `reqId`(原樣回傳)。錯誤:`{"type":"ack","cmd":"acquire","ok":false,"error":"追蹤目標已達上限 40"}`。

## 6. NMEA 0183 輸出(ARPA 結果)

- `$RATTM,nn,距離,方位,T,速度,航向,T,CPA,TCPA,N,,狀態,,hhmmss.ss,取得方式*hh`:狀態 T 追蹤中、Q 擷取中、L 失追;取得方式 A 保護區、M 手動;擷取中/失追時速度/航向/CPA/TCPA 留空。
- `$RATLL,nn,緯度,N,經度,E,,hhmmss.ss,狀態,*hh`。
- **與 NMEA 閘道真值 `$RATTM`(UDP 10110)以埠區分**:X 頻 → UDP 127.0.0.1:10111、S 頻 → 10112(`--nmea-port-x/-s`);talker 預設 RA,可用 `--talker AR` 等再區分。UTC = 情境 `startTimeUtc` + 模擬時間。OpenCPN 加一個 UDP 10111 連線即可顯示 ARPA 目標(與 AIS 比較)。

## 7. 故障(只讀 state.faults)

| 故障名 | X 頻 | S 頻 |
|---|---|---|
| `radarFailure`(目錄名)、`radarFailureX` | 停止發射(無影像幀、追蹤漏掃描 → 失追),`status.failed` | — |
| `radarFailureS` | — | 停止發射 |
| `blackout` | 停止發射 | 停止發射 |
| `radarDegraded`(目錄名)、`radarDegradedX` | 靈敏度 −15 dB,flags bit2 | — |
| `radarDegradedS` | — | 靈敏度 −15 dB |

目錄只有 `radarFailure`/`radarDegraded`,對應「一台雷達失效」(X 頻,主雷達);`radarFailureS` 等名稱目前不在 `fault-catalogue.json`(情境檢查會視為未知名稱),需要時由協調者加入目錄。故障清除後自動恢復發射(`radarRestored`)。

## 8. 主控程式與驗證

```
dotnet build/bin/Sensors.Radar.Host/Release/net8.0/SimosaBRM.Sensors.Radar.Host.dll \
  --sim ws://127.0.0.1:8765/ --port 8766 --guard 0.3,8 --truth-report 30
```

選項見 `--help`:`--radars x,s`、`--seed`、`--areas data/areas`/`--no-areas`、`--environment <json>`(雨區/海況)、`--rpm`、`--cells`、`--no-nmea`、`--nmea-host`、`--talker`、`--duration`、`--truth-report`(教官/驗收:每 N 模擬秒列印 ARPA 與核心真值的位置/CPA/TCPA/航向/速度誤差;結束時列印平均/最大 CPA 誤差與 CPU 使用率)、`--localhost-only`、`--force-display`。情境切換(scenario.id 改變)或時間倒退時重建雷達(新種子)並沿用操作員設定。

**E05 實跑**(2026-10-17,SimCore.Host `--scenario data/scenarios/E05_pilot_traffic.yaml --no-udp --no-nmea --port 8765`(即時 1×)+ 雷達主控 `--guard 0.3,8 --truth-report 30 --force-display`,205 s 牆鐘、5121 狀態幀):

| 項目 | X 頻 | S 頻 |
|---|---|---|
| 自動擷取 | t = 14.8–15.2 s 擷取 T3、T1、T2(3.16/4.23/4.93 nm) | 同左 |
| 進入追蹤(向量有效) | t ≈ 65–68 s | 同左 |
| 結束時追蹤數 | 3/3 | 3/3 |
| T3(漁船,等速)最後 CPA 誤差 | 0.0009 nm | 0.0001 nm |
| T2(油輪,等速)最後 CPA 誤差 | 0.0029 nm | 0.0029 nm |
| T1(貨櫃船,t ≈ 130 s 起依 COLREG 右轉) | 0.158 nm(轉向中,航向誤差約 4°) | 0.185 nm |
| 處理 CPU(含顯示影像) | 6.5 s / 205 s ≈ 3.2% 單核 | 3.0% 單核 |

非操船目標全程(追蹤中 423 筆比對)CPA 誤差 ≤ 0.003 nm;T1 轉向期間 ARPA 向量落後(雙模型偵測後航向誤差約 4–5°、CPA 誤差最大 0.25 nm),此為 ARPA 對操船目標的正常限制(BRM 訓練重點)。另有 1 次 0.30 nm 處近距海雜波被保護區擷取後 3 圈內擷取失敗(保護區內緣宜 ≥ 0.3 nm 或加大海雜波抑制)。`E05IntegrationTests` 以 SimCore 引擎離線重現同一情境(250 s),T2/T3 CPA 誤差 < 0.005 nm。

## 9. 限制與後續

- 無旁瓣、多路徑(lobing)、閃爍(glint)、二次掃描回波、RACON/SART、目標間遮蔽;陸地為多邊形 + 高度,非 DEM。量測雜訊因此比實船小,ARPA 精度偏佳。
- 海況由真風速推估(核心尚無浪場);雨區由環境檔/教官指令給定(情境 schema 尚無雨區欄位,需要時可在 scenario.schema.json 增加 `environment.radar` 指向雷達環境)。
- 雷達控制指令只在雷達服務內,不進核心紀錄;重播時畫面一致(同種子、同狀態),但學員的雷達操作需另行記錄(後續:雷達服務寫 `build/records/*.radar.jsonl` 或經核心 `command` 轉送)。
- 本機無 Windows URL ACL 時 HttpListener 退回 localhost(與 SimCore.Host 相同);教官筆電直連需 `netsh http add urlacl url=http://*:8766/radar/ user=Everyone`。
- 網頁 PPI(WebGL/Canvas)、EBL/VRM、平行索引線、AIS 目標融合由網頁端依本協定實作。
