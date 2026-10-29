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
- **確定性**:天線方位由模擬時間決定(方位線全域序號 = ⌊t × rpm/60 × 2048⌋);每格雜訊/雜波/雨起伏由 SplitMix64(種子, 方位線序號, 格號)查 65536 筆種子表;偵測與追蹤以有序清單處理。同種子、同狀態序列、同指令序列 → 位元組相同的影像與相同的追蹤 JSON(`SameSeedGivesIdenticalImagesAndTracks`)。種子:`--seed` 或情境檔 `seed`(E05 = 20260505),讀不到時為情境代號 FNV-1a。雷達內部狀態不進核心雜湊鏈(雷達是核心狀態的純函數 + 本地操作員設定);學員雷達操作由雷達服務轉送為 `radarControl` 指令寫入核心 `input` 行(第 5.1 節):無情境引擎時雜湊不變,有情境引擎時其事件序號計入雜湊(與 `acknowledge` 相同,同一 `input` 序列必得同雜湊;`ScenarioEngineHashCountsRadarControlEventSequenceDeterministically`),重播由 `input` 行重發事件、不需雷達服務(重播限制見第 9 節)。
- **真值與指示值**:回波物理用 `state.truth`(天線隨船實際轉動、目標真實位置);ARPA 穩定化(相對→真方位、地理位置、真向量)用指示值(電羅經 heading、GNSS pos、COG/SOG)。故電羅經誤差會使北向上畫面旋轉、ARPA 向量偏差(`GyroErrorRotatesNorthUpPictureButNotHeadUp`)。`sensors.headingValid = false` 時 ARPA 停止(無法穩定化),影像只能船首向上。
- **CCRP**:ARPA 量測以天線位置(駕駛台頂,船舯後 0.30 LOA)取得,換算到船舶參考點(船舯,與 SimCore 相同)後計算距離/方位/CPA,因此可與 `state.targets[].cpa_nm` 直接比較。

## 2. 影像產生(每條方位線)

1. **場景準備(每狀態幀一次)**:目標以 SimCore 船體五邊形(`Encounter.ShipOutline`)轉到天線座標,求角度範圍、近遠距離、Cyrus–Beck 邊法向量;雷達地平線 d = 2.21(√h天線 + √h目標) nm,部分遮蔽以可見高度比例平方衰減;截面積 σ = 52 √f · D^1.5(Skolnik;排水量 = L·B·T·Cb·1.025,小船下限 5 m²)× 視角因子(0.4 + 0.6|sin 視角|)。目標高度 0.12 L + 5 m(小船 0.25 L + 1 m)。
2. **方位線幾何**(每條線,與距離格無關):
   - 波束:雙程高斯主瓣 σ_b = BW/3.33,目標角寬內積分以 erf 計算(只對角度範圍 ±4.5σ_b 內的目標);角寬小於波束寬的目標每條線都照到整個船體,距離段為船體近遠距離。
   - 陸地(`data/areas`):射線與多邊形交點,波束寬內取最近入射點(海岸展寬);高於地平線遮蔽高度才可見;入射點後 3 km 內以 1.5 km 衰減(地形遮蔽近似),其後無回波;陸地後方的目標與海面被遮蔽。
   - 雨區(圓:射線交段;情境多邊形:射線與各邊交點排序後成對取段,天線在雨區內時自 0 起,凹多邊形可有多段):Z = 200 R^1.6、雙程衰減 2kR^α(X:k 0.0101、α 1.276;S 頻幾乎無衰減)。
   - 陰影扇區:預設艉向一個(X 174–186°、S 172–188°,衰減 60 dB),參數化(`RadarConfig.ShadowSectors`)。
   - 海雜波方位調制:迎風 +3 dB、背風 −3 dB。
3. **畫到距離格**(`SpokeRenderer`,只查表):目標段 [近, 遠 + 脈衝長] 以覆蓋比例累加 σ·波束因子·R⁻⁴ 增益表;海雜波平均表(依海況:1 nm 處 X 頻 SS1 5 dB … SS6 30 dB,R⁻³ 至臨界距離 0.3 + 0.25 SS nm 後 R⁻⁷;S 頻 −10 dB);雨(S 頻 −19.5 dB);雜訊 Exp(1);僅含回波格取對數,其餘查 dB 表;STC = sea% × min(60, 40 log(3 nm/R)) dB;FTC = 減去前段 2 倍脈衝長平均的 rain% × 0.9;增益門檻 40 − 0.43 × gain dB;強度 = (dB − 門檻) × 255/30。調諧損失 20((tune − 50)/50)² dB(自動調諧為 0);`radarDegraded` −15 dB。
4. **脈衝**:≤ 0.75 nm 短(10.5 m)、≤ 3 nm 中(45 m)、≤ 12 nm 中長(75 m)、以上長(180 m)。ARPA 處理影像固定 16 nm、2048 格(14.5 m)、中脈衝 45 m,動態範圍 80 dB(不飽和,質心有次格精度)。由 ARPA 影像重取樣的顯示量程(預設 6、12 nm,第 4.1 節)實際以 ARPA 的 45 m 脈衝繪製,`status.pulse/pulseLength_m` 回報 medium/45(`RadarUnit.DisplayPulseFor`)。
5. **效能**(不變):預算表(R⁻⁴、雜波、STC、陸地、雨、sin/cos)+ 每目標每線少量運算;實測單台(顯示 + ARPA、3 目標、雨、陸地、18 kn 風)每圈 ≈ 0.2 s / 2.5 s(約 8% 單核,`SingleRadarUsesLessThanOneCore`);E05 實跑兩台 + 強制顯示各約 3% 單核。無客戶端時不產生顯示影像。

### 2.1 情境雨區與海況(scenario `environment.radar` → `state.radarEnvironment`)

**選擇**:核心把情境的雷達環境換算後放進狀態幀(`state.radarEnvironment`),雷達服務只讀狀態;不採「雷達服務依 `state.scenario.id` 讀情境檔」。
理由:(1) 雷達服務不需知道情境檔路徑(情境可由 `--scenario <路徑>`、`loadScenario` 或紀錄 header 載入,id 與檔名不一定對應),重播紀錄時情境定義已在 header,
雷達接上重播串流即得到相同雨區;(2) 座標由核心以情境原點換算為本地 ENU(與 `pos.x/y`、目標相同座標系),雷達不需以自船位置錨定經緯度(檔案雨區的錨定誤差不適用);
(3) 內容只來自情境、練習中不變,為核心狀態的純函數,雷達影像仍是「核心狀態序列 + 種子 + 操作員設定」的確定性函數。

```yaml
environment:                                     # 情境原點 23.80N 120.05E(RadarEnvironmentScenarioTests)
  radar:
    seaState: 4                                  # 選填:Douglas 0–9;省略 = 依真風速推估
    rainAreas:
      - { id: SQ1, intensity: 0.75, polygon: [ { lat: 23.82, lon: 120.04 }, { lat: 23.82, lon: 120.06 }, { lat: 23.84, lon: 120.06 }, { lat: 23.84, lon: 120.04 } ] }
      - { intensity: 0.25, center: { x: 3000, y: -2000 }, radius_nm: 1.5 }   # 圓形;id 省略 = rain2
```

```json
"radarEnvironment": { "rainAreas": [
    { "id": "SQ1", "intensity": 0.75, "rate_mmh": 12, "polygon": [ { "lat": 23.82, "lon": 120.04, "x": -1019.08, "y": 2215.11 }, … ] },
    { "id": "rain2", "intensity": 0.25, "rate_mmh": 4, "center": { "lat": 23.781942, "lon": 120.079438, "x": 3000, "y": -2000 }, "radius_m": 2778 } ],
  "seaState": 4 }
```

- 強度 → 降雨率:`rate_mmh = 16 × intensity`(1 = 大雨 16 mm/h、0.25 = 中雨 4 mm/h;`ScenarioRadarEnvironmentBuilder.MaxRainRateMmH`),其後與檔案/教官雨區相同(Z、衰減、FTC、S 頻 −19.5 dB)。
- 結構檢查(情境載入為 error):`polygon`(≥ 3 點)與 `center` + `radius_nm`(> 0)二擇一、`intensity` 0–1、`seaState` 0–9、`id` 不重複。
- 核心:`environment.radar` 不影響物理;引擎初始化時建立一次,以 `state.radarEnvironment` 廣播與記錄(情境未給時省略 → 舊情境狀態 JSON 與雜湊不變)。
  **只在關鍵幀帶出**:載入/重設/倒帶(快照還原)後第一個廣播幀,以及距上次帶出 ≥ 1 s 模擬時間(50 步)的廣播幀;其他幀省略此欄位(以 tick 判定,重播得到相同的廣播內容)。
  理由:內容練習中不變,25 Hz 每幀重複整份雨區定義(每個多邊形頂點含經緯度與 x/y)會使紀錄與 WebSocket 流量倍增(兩個 24 點多邊形 + 一個圓:每狀態行 2.0 kB → 5.8 kB;30 分鐘紀錄約 91 MB → 262 MB);
  關鍵幀只增加約 1/25 的量(`RadarEnvironmentScenarioTests.RecordStateLinesDoNotRepeatRadarEnvironmentEveryFrame`:有/無雨區每行平均相差 < 5 %)。直接呼叫 `BuildState()` 仍帶完整內容(教官快照、測試)。
  內容只來自情境(紀錄 header 有完整定義)、練習中不變,**不進雜湊與快照**(有/無此區段的同一情境雜湊逐步相同,`RadarEnvironmentScenarioTests`)。Python 參考實作忽略。
- 雷達:`RadarEnvironment.ApplyScenario`(帶有 `radarEnvironment` 的幀;內容相同時不變更版本;**省略 = 不變**)→ `ScenarioRainCells`、`ScenarioRainPolygons`、`ScenarioSeaState`;與檔案/教官雨區**相加**;
  海況優先序:教官 `environment` 指令 / 環境檔 `seaState` > 情境 `seaState` > 核心浪場 `state.waves.significantHeight_m`(Douglas 海況表:Hs 0 → 0、< 0.1 → 1、< 0.5 → 2、< 1.25 → 3、< 2.5 → 4、< 4 → 5、< 6 → 6、< 9 → 7、≥ 9 → 8;`VideoGrid.SeaStateFromWaveHeight`,練習中 `setEnvironment waves` 變更後下一圈生效)> 依真風速推估(`WaveSeaStateTests`)。模擬時間倒退(重設/倒帶)或情境切換(`state.scenario.id` 改變)時以該幀內容重設、該幀無此欄位則清除,待下一個關鍵幀(≤ 1 s)重新套用;
  雷達服務中途連線時,最遲 1 s 內收到關鍵幀(`ScenarioWeatherAndSBandFaultTests.ScenarioEnvironmentPersistsBetweenKeyframesAndClearsOnReset`)。
- 只支援靜態雨區;移動雨胞、`setEnvironment` 改雨區為後續。

## 3. ARPA

- **偵測**:ARPA 影像強度 ≥ 門檻(增益門檻以上 7.5 dB)的連續段,依掃描時間順序與前一條線 8 連通合併(串流連通元件,跨 2047→0 自然連續);元件結束時以(強度 − 門檻)加權求質心,每段先以該線的指示航向與天線位置換成 ENU,距離扣除半個脈衝長(脈衝展寬偏差)。格數 < 3、方位線 < 2 為雜訊;徑向或橫向 > 800 m 或 > 256 條線為大面積回波(陸地/雨區),不擷取也不更新追蹤。
- **追蹤**:等速卡爾曼(x, y, vx, vy),過程雜訊 2e-4 m²/s³;量測共變異數依距離/方位(σ_r = 0.3 格 + 2 m、σ_θ = 0.03°)與回波大小。關聯:每圈每目標一次,預測閘門 Mahalanobis² < 16 或距離 < 40 m ×(1 + 漏掃描),上限 600 m;一個偵測落入兩個閘門、或兩目標距離 < 250 m → `swapRisk`。
- **操船偵測**:(1) NIS > 9.2 連續 2 次 → 過程雜訊 0.5、速度共變異數 +4 m²/s²、30 s;(2) 並行快速濾波器(q = 0.02),精確濾波器 NIS 指數平均 > 3 倍快速者(且 > 0.2)→ 改用快速濾波器狀態、30 s 內 q = 0.02;(3) 新息指數平均偏差檢定。`manoeuvring` 旗標輸出。
- **擷取**:手動 `acquire{bearing, range}`(搜尋半徑 300 m,3 圈無回波 → `acquireFailed`);保護區 `guardZone`:候選須 ≥ 10 格,5 次命中(7 圈內),第 3 次起須落在等速外推 50 m 內(排除海雜波斑點)→ `newTarget`;追蹤中軌跡外推位置 300 m(2 × GuardGateM)內的回波不作候選(失追軌跡不算,否則雷達故障或航向無效恢復後要等失追軌跡保留期屆滿才會重新擷取)。最多 40 目標(MSC.192(79) ≥ 30),編號 1–99 重用。
- **狀態**:`acquiring`(擷取後 60 s 且 ≥ 5 次命中前不給向量)→ `tracking`(`stable` = 追蹤 ≥ 180 s)→ 連續 6 圈漏掃描 `lost`(保留 60 s 後 `deleted`)。
- **輸出**:真向量(course/speed)、相對向量、CPA/TCPA/BCR/BCT(`Encounter.Compute`,BCR 以自船艏向線、正 = 艏前通過)、過去位置(每 30 s,10 點)、危險目標(CPA < 1.0 nm 且 0 ≤ TCPA ≤ 12 分,可由 `cpaLimits` 改)。
- **試操船**:`trialManoeuvre{course, speed, delay(分)}`:自船以目前速度向量航行 delay 後瞬間改為新向量,兩段相對運動各取最近點(`ArpaTracker.TrialCpa`,與暴力取樣比對);`trialCpa_nm`/`trialTcpa_min`。
- **精度驗收**:`ArpaAccuracyTests`(對遇、右舷橫越、左舷橫越、追越、被追越):擷取後 1 分鐘趨勢 + 3 分鐘穩定追蹤,CPA 誤差 0.0006–0.0070 nm(< 0.02 nm)、TCPA < 0.2 分、航向 < 0.2°、速度 < 0.14 kn;`E05IntegrationTests` 直接接 SimCore 引擎(MMG),T2/T3 CPA 誤差 < 0.005 nm。

### 3.1 AIS 融合(`ArpaTracker.AssociateAis`;IEC 62288 的雷達/AIS 關聯概念)

- **輸入**:`state.targets[].ais`(`aisOn` 且有報告者;含注入的 `aisError`)→ `RadarScene.AisReports`:報告經緯度以自船指示位置為原點換成 ENU,帶 COG/SOG/MMSI/船名;順序同 `targets[]`(確定性)。
- **關聯**(每個追蹤清單週期 1 Hz,只對 `tracking` 且有估計的目標):距離門檻 |ARPA 預測位置 − AIS 報告位置| ≤ `AisGateM`(150 m)+ `AisGateFraction`(2%)× 距離;
  兩者 SOG ≥ `AisMinSpeedKn`(2 kn)時再檢查 |ΔCOG| ≤ `AisCourseDeg`(30°)、|ΔSOG| ≤ `AisSpeedKn`(3 kn);取門檻內最近者為候選。同一 MMSI 連續 `AisHits`(3)次 → `associated`(事件 `aisAssociated`)。
- **保持與解除**:已關聯者以 `AisHoldGateFactor`(2×)的保持門檻維持,期間 `positionOffset_m` 顯示差異(`aisError` 中等偏移);連續 `AisMisses`(3)次失敗才解除。一筆報告最多關聯一個目標:先處理已關聯者(保持優先),再依目標順序處理其餘。
- **不關聯**:AIS 關閉(報告不在清單)、報告偏離門檻(`aisError` 大偏移)、航向/速度不一致(報告互換而向量不同)。兩目標同向同速且報告互換時,關聯依報告位置交換(MMSI 跟著位置走)。
- **輸出**:`tracks[].ais{mmsi, name, associated, positionOffset_m}`(無候選時省略);網頁 ARPA 表 `SRC` 欄以 IEC 62288 符號概念顯示 ○ 雷達、◬ 融合(圓內三角;PPI 同)、○△ 有 AIS 候選未關聯,目標資料面板顯示 AIS 名稱/MMSI/位置差。
- 測試 `AisFusionTests`:正確關聯(追蹤後 2–4 s)、AIS 關閉/大偏移/速度不一致不關聯、中等偏移保持並顯示差異、報告互換。

### 3.2 純 AIS 目標(`tracks.aisTargets[]`;IEC 62288 AIS 目標符號 △)

- **定義**:本追蹤清單週期有 AIS 報告、且 MMSI 未被任何**追蹤中且已關聯**的 ARPA 目標使用者(`RadarUnit.Tracks()`;候選中未關聯者仍列出並附 `candidateTrackId`)。
  AIS 關閉的目標不會出現(報告不在清單);`aisError` 偏移如實反映在位置。無報告時欄位省略(舊客戶端不受影響)。
- **欄位**(`radar.schema.json $defs/aisTarget`):`mmsi, name, x, y, lat, lon, range_nm, bearing_deg, relBearing_deg, cog_deg, sog_kn, heading_deg, cpa_nm, tcpa_min, dangerous, candidateTrackId?`;
  CPA/TCPA 以自船指示 COG/SOG 與報告 COG/SOG 計算(`Encounter.Compute`,與 ARPA 的危險判定同門檻)。
- **網頁**(`radar/ppi-renderer.ts drawAisTargets`、`ppi-model.ts aisScreenPoints`):等腰三角形頂點指向 COG(畫面定向)、向量線依向量模式/時間、船名標籤,危險者警報色;
  位置自報告時刻以 COG/SOG 外推(≤ 3 s,與 ARPA 相同)。點選:選取工具點符號 → 目標資料面板顯示 AIS 資料與「擷取此目標(ARPA)」鈕;擷取工具點符號 →
  `RadarStore.acquireAis`:以 AIS 報告的方位/距離送 `acquire`(航向無效時相對方位),ARPA 之後以回波建立追蹤、經第 3.1 節關聯成為融合目標,該 MMSI 離開純 AIS 清單。
  示範模式(`demo-radar.ts`)依同一規則產生 T1/T2 的純 AIS 目標。測試:`AisOnlyAndReplayTests.AisTargetsListUnassociatedReportsOnly`、網頁 `radar-ais-targets.test.ts`。

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

### 4.1 每客戶端獨立顯示量程(`RadarUnit.SetClientRange` / `DisplayRanges`)

- `range` 指令只改**發出該指令的連線**的顯示量程(`RadarUnit.ClientRanges`);其他設定(gain/sea/rain/tune/standby/transmit/guardZone/trialManoeuvre/cpaLimits/environment)為整台雷達共用。
  未下過 `range` 的客戶端用預設量程 `Controls.RangeNm`(預設 6 nm;情境切換 `SetUnits` 沿用預設量程與每客戶端量程);無客戶端的 `Apply`(測試、工具)改預設量程。
- **偏心**由客戶端處理:網頁依顯示量程與偏心比例以 `videoRangeFor` 向伺服器要較大的影像量程(`radar-store.syncVideoRange`),伺服器不知道偏心。
- 每個狀態幀,主控程式把各客戶端的量程集合交給 `DisplayRanges`;**相異量程各產生一份影像**(`DisplayView`,格表依量程快取),幀標頭 `range_m` 標示量程,
  `RadarService.Publish` 依每個客戶端的量程分送(`RadarWebSocketServer.SendFrame`);`status.range_nm` 為各連線自己的量程(`Status(client)`),`range` 的回應只送該連線的 status。
- **重取樣 vs 繪製**(`RadarUnit.ResamplesFromArpa`):量程 ≤ ARPA 量程(16 nm)且顯示格 ≥ ARPA 格(512 格 vs 16 nm/2048 格 = 14.5 m → 6、12 nm)時,
  影像由該條 ARPA 影像線最近格重取樣(強度 × 80/30 dB 換成顯示刻度後飽和),不另繪製;這些量程的 `status.pulse/pulseLength_m` 回報 ARPA 處理脈衝(medium、45 m)而非量程脈衝 75 m,
  使契約值與影像的回波徑向長度一致(`StatusPulseMatchesPulseUsedForImage`:6 nm 回波 ≈ 45 m 脈衝 + 目標長度);更短的量程需要短/中脈衝解析度(ARPA 以 45 m 中脈衝、14.5 m 格處理,0.25–3 nm 會失去細節),
  更長的量程超出 ARPA 影像,各自以該量程的脈衝與格表繪製。影像只依量程與種子決定,與連線的客戶端無關(`ImagesDoNotDependOnWhichClientsAreConnected`)。
- 客戶端離線:伺服器把離線通知放進同一指令佇列(`RadarCommandRequest.Disconnected`),處理執行緒 `RemoveClient` 清掉其量程。
- 效能:單台 X 頻、ARPA 2048 格 + 0.75/6/24 nm 三份影像(一份重取樣、兩份繪製)、3 目標、雨區、陸地、海雜波,每圈處理時間 < 掃描週期(`ThreeClientRangesStayUnderOneCore`;`SingleRadarUsesLessThanOneCore` 仍為單量程基準)。

## 5. JSON 訊息(radar.schema.json)

連線後伺服器先送 `status`;之後 `tracks`(模擬時間 1 Hz)、`event`、`ack`(只給送指令的客戶端),設定成功後廣播新的 `status`;情境切換(`RadarService.SetUnits`,試操船與 CPA/TCPA 限值回到預設、天線位置可能隨船改變)後下一個狀態幀也對每台新雷達廣播 `status`。客戶端的試操船設定以最新收到的 `status` 或 `tracks` 為準(兩者都在無試操船時省略 `trial`)。

```json
{"type":"status","radar":"x","band":"X","frequency_GHz":9.41,"transmitting":true,"standby":false,"failed":false,"degraded":false,"headingValid":true,
 "range_nm":6,"rangeScales":[0.125,0.25,0.5,0.75,1.5,3,6,12,24,48,96],"pulse":"medium","pulseLength_m":45,"gain":70,"sea":50,"rain":0,"tune":50,"autoTune":true,
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

### 5.1 控制指令進核心紀錄(`radarControl`;規劃書第 8.3 節第 7 類)

雷達服務執行每個控制指令(`getStatus`/`getTracks` 查詢除外,不論成功與否)後,經 `RadarService.ForwardControl` 把它轉送 SimCore.Host
(`SimStateClient.Send`:狀態訂閱的同一條 WebSocket 改為雙向,斷線期間暫存 256 筆、重連後送出;主控程式 `--no-forward` 可關閉):

```json
{"type":"radarControl","args":{"radar":"x","client":"3f2504e04f8911d39a0c0305e82c3301","cmd":"range","ok":true,"value":3,"request":{"value":3}}}
{"type":"radarControl","args":{"radar":"x","client":"…","cmd":"acquire","ok":true,"id":4,"request":{"bearing":45.2,"range":3.1}}}
{"type":"radarControl","args":{"radar":"s","client":"…","cmd":"acquire","ok":false,"error":"雷達未發射","request":{"bearing":10,"range":2}}}
```

核心(`SimulationEngine` case `RadarControl`)只寫 `input` 行並發事件 `radarControl`(`source` instructor、`itemId` = 雷達 id、`data` = args;與 `acknowledge` 同類):
不影響物理;無情境引擎的情境雜湊完全不變,有情境引擎者只有事件序號計入(與 `acknowledge` 相同),重播由 `input` 行重發事件,序列一致、不需雷達服務
(`RadarControlRecordTests`)。評估 `MetricsCollector.OnRadarControl`:`radar.controlCount`(成功的指令數)、`radar.acquisitions`(`acquire`)、`radar.rangeChanges`(`range`),
`MetricNames` 與 Python `scenario.py METRIC_NAMES` 同步,可作容許帶指標。教官站事件流以「雷達 X 操作:range 3」顯示。

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

目錄(`src/Contracts/fault-catalogue.json`)含 `radarFailure`/`radarDegraded`(「一台雷達失效」= X 頻,主雷達)與 `radarFailureS`/`radarDegradedS`(S 頻單獨故障);
四者在核心都是只記錄(modelled: false,只進 `state.faults`、廣播與紀錄,`ScenarioValidator` 提示「只記錄」),由雷達服務依上表反應。`radarFailureX`/`radarDegradedX`
為雷達服務接受的別名,不在目錄。故障清除後自動恢復發射(`radarRestored`)。測試:`ScenarioWeatherAndSBandFaultTests`(S 頻停發/降敏只影響 S 頻、X 頻影像逐值不變)。

## 8. 主控程式與驗證

```
dotnet build/bin/Sensors.Radar.Host/Release/net8.0/SimosaBRM.Sensors.Radar.Host.dll \
  --sim ws://127.0.0.1:8765/ --port 8766 --guard 0.3,8 --truth-report 30
```

選項見 `--help`:`--radars x,s`、`--seed`、`--areas data/areas`/`--no-areas`、`--environment <json>`(雨區/海況)、`--rpm`、`--cells`、`--no-nmea`、`--nmea-host`、`--talker`、`--duration`、`--truth-report`(教官/驗收:每 N 模擬秒列印 ARPA 與核心真值的位置/CPA/TCPA/航向/速度誤差;結束時列印平均/最大 CPA 誤差與 CPU 使用率)、`--localhost-only`、`--force-display`、`--no-forward`(不把學員雷達操作轉送核心,第 5.1 節)。情境切換(scenario.id 改變)或時間倒退時重建雷達(新種子)並沿用操作員設定與每客戶端量程。

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

### 5.2 重播時重放學員雷達操作(`/radar/replay`;主控程序 `--replay --radar`)

主控程序 `--replay` 時(預設 `--radar ws://localhost:8766/`;雷達服務退回只聽 localhost 時 127.0.0.1 不匹配 HttpListener 前綴,故用 localhost,`--no-radar` 關閉)先嘗試連線雷達服務的**重播接收端** `ws://…:8766/radar/replay`(1.5 s 逾時):

- **已連線**:重播引擎(`Replayer.Replay` 的 `configure/onStep`)在 `CommandApplied` 的相同 tick 把每筆 `radarControl` input 行送成
  `{"type":"radarControlReplay","radar":"x","client":"<紀錄的 client>","t":123.4,"request":{"cmd":"range","value":3}}`(`SimCore.Host/RadarReplayClient.cs`;request = 紀錄的 `args.request` 加回 `cmd`);
  同時以 `--port` 的狀態 WebSocket(`WebSocketStateServer.ReadOnly`:客戶端送來的指令一律忽略,避免改變重播)推送重播中的狀態幀,並依 `--timescale`
  以牆鐘步調推進(`onStep` 睡眠),雷達服務訂閱該狀態即可用同種子重現影像,學員當時的量程/增益/擷取/保護區/試操船隨 `radarControl` 行重現。
  結束時列印送出/接受/拒絕的筆數。
- **雷達服務端**(`RadarWebSocketServer.EnqueueReplay` → `RadarCommandRequest.Replayed`):訊息驗證後以紀錄的 `client`(Guid "N";其他字串以 FNV-1a 固定 Guid)
  排入同一指令佇列,`RadarService.DrainCommands` 套用但**不轉送核心**(避免重播中再寫入 input 行)、不回 ack 給顯示客戶端;`range` 同時套到該雷達所有連線中的
  顯示客戶端(PPI 跟著學員當時的量程)並重發 status;查詢(`getStatus/getTracks`)不處理。每則訊息回 `{"type":"replayAck","ok":…}`。即時操作與重播可並存
  (顯示客戶端自己的指令仍轉送)。
- **未連線**:略過並在重播結果印出 note「雷達服務未連線(…):radarControl 行未重放」,重播不受影響(不步調、不開狀態 WebSocket)。
- 測試:`AisOnlyAndReplayTests.ReplayEndpointAppliesControlsWithoutForwarding`(量程套到顯示客戶端、擷取建立追蹤、不轉送、格式錯誤拒絕)、`ReplayClientIdParsesGuidOrHashesDeterministically`。
  前者的場景推進由測試執行緒受控(餵送執行緒在鎖內推進、送擷取前暫停並以下一幀的 t 算真值,`Arpa.Tracks` 在鎖內讀取),且顯示客戶端的 `ack` 由送出佇列非同步送達、`ForwardControl` 在 ack 排入之後才呼叫,
  所以「已轉送」以等待轉送發生斷言而非收到 ack 即斷言(原寫法依牆鐘餵狀態、CPU 負載高時不穩定)。
- 指標 `summary.radar {controlCount, acquisitions, rangeChanges, byRadar{x,s}}`(有雷達操作時輸出;`MetricsCollector.RadarSummary`;講評摘要「雷達操作」列)。

非操船目標全程(追蹤中 423 筆比對)CPA 誤差 ≤ 0.003 nm;T1 轉向期間 ARPA 向量落後(雙模型偵測後航向誤差約 4–5°、CPA 誤差最大 0.25 nm),此為 ARPA 對操船目標的正常限制(BRM 訓練重點)。另有 1 次 0.30 nm 處近距海雜波被保護區擷取後 3 圈內擷取失敗(保護區內緣宜 ≥ 0.3 nm 或加大海雜波抑制)。`E05IntegrationTests` 以 SimCore 引擎離線重現同一情境(250 s),T2/T3 CPA 誤差 < 0.005 nm。

## 9. 限制與後續

- 無旁瓣、多路徑(lobing)、閃爍(glint)、二次掃描回波、RACON/SART、目標間遮蔽;陸地為多邊形 + 高度,非 DEM。量測雜訊因此比實船小,ARPA 精度偏佳。
- 海況由核心浪場 `state.waves` 的 Hs(Douglas)推估,情境無 `environment.waves` 時依真風速,情境 `environment.radar.seaState` 或教官指令可覆寫;雨區由情境 `environment.radar.rainAreas`(第 2.1 節)、環境檔或教官指令給定,皆為靜態。
- 雷達控制指令經核心 `radarControl` 進紀錄(第 5.1 節);重播時主控程序 `--replay --radar` 經 `/radar/replay` 重放到雷達服務(第 5.2 節),雷達服務須先啟動並訂閱重播中的主控程序狀態(`--port`),重播以 `--timescale` 的牆鐘步調進行(長紀錄可加大倍率)。雷達操作不重設 BNWAS(MSC.128 的「可辨識操作」目前只含舵/車/警報指令)。
- 每客戶端量程:影像份數 = 相異量程數(最多 11),效能測試以 3 份驗證;重取樣只用於 6/12 nm(其餘繪製),0.25–3 nm 的客戶端各自繪製。偏心在客戶端。
- AIS 融合為位置/向量門檻的簡化關聯(無協方差加權、無航跡品質),`aisError` 偏移在保持門檻內時仍關聯並以 `positionOffset_m` 顯示;純 AIS 目標(第 3.2 節)只有報告值與 CPA/TCPA,無 AIS 航跡品質/睡眠目標(sleeping)區分、不發危險目標事件(只標 `dangerous`)。
- 本機無 Windows URL ACL 時 HttpListener 退回 localhost(與 SimCore.Host 相同);教官筆電直連需 `netsh http add urlacl url=http://*:8766/radar/ user=Everyone`。
- 網頁 PPI(WebGL/Canvas)、EBL/VRM、平行索引線由網頁端依本協定實作;AIS 融合符號見第 3.1 節。
