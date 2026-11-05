# 簡化 ECDIS 頁設計(ecdis.html;ECDIS-lite)

對應規劃書第 7.2 節儀器列「ECDIS」(需求 R1)、第 5.2 節 Instruments、第 8.3 節航程計畫監控指標;需求追溯表 R1 的 ECDIS 缺項
(第一階段 OpenCPN 整合未做整合測試、自建 S-52 未做、ENC 管理無)。本頁是**自建、簡化、顯示用**的 ECDIS:提供學員在駕駛台以海圖監控航線、
水深、AIS 目標與警報的「可信度」(D5),**不是**型式認可的 ECDIS,**不讀 S-57/S-63 ENC**,底圖為 `data/areas/*.json` 的示意幾何。

| 項目 | 位置 |
|---|---|
| 頁面 | `src/Instruments.Web/ecdis.html`、`src/ecdis-main.ts`(Vite 多頁進入點,與 index/instructor/radar/report 同一套設定與主題) |
| 元件 | `src/components/ecdis/`:`ecdis-app.ts`(`<brm-ecdis-app>` 版面與連線)、`chart-canvas.ts`(`<ecdis-chart>` 海圖畫布)、`toolbar.ts`、`route-panel.ts`、`target-panel.ts`、`alarm-panel.ts`、`ecdis-shared.ts`(基底、樣式、配色) |
| 程式庫 | `src/lib/ecdis/`:`projection.ts`(投影/EBL/VRM)、`safety-depth.ts`(水深模型與安全等深線)、`route-monitor.ts`(航線監控)、`alarms.ts`(本地警報)、`chart-data.ts`(底圖組裝)、`ecdis-store.ts`(狀態容器與警報評估) |
| 測試 | `tests/ecdis-projection`、`ecdis-safety-depth`、`ecdis-route-monitor`、`ecdis-alarms`、`ecdis-store`、`ecdis-components`(vitest;後者 happy-dom) |
| 外殼/啟動 | `SimosaBRM.Shell --page ecdis`(`src/Shell.Core/ShellOptions.cs`、`ShellOptionsTests`);`config/ship-laptop.json`、`config/instructor-laptop.json` 的 `web.openPages` 含 `ecdis.html` |

## 1. 範圍與分工

- **資料來源**:與儀器頁、教官站相同的核心 WebSocket 狀態(`ws://<主機>:8765`,`?ws=` 覆寫;`?demo=1` 為示範模式)。頁面**唯讀**:不送任何
  `command.schema.json` 指令(含 `acknowledge`/`alertAcknowledge`),因此不影響核心狀態、評估與狀態雜湊(確定性驗收不受影響)。
- **與 OpenCPN 的分工**(規劃書第 7.2 節「第一階段 OpenCPN」):OpenCPN 仍是「真 ENC 海圖 + NMEA 輸入」的路線,需要 Windows 實機、ENC 授權與
  `src/Gateway.Nmea` 的句子;本頁是離線、零安裝、與其他儀器頁同一套建置與主題的替代/補充顯示,可在 OpenCPN 整合測試完成前供訓練使用,
  並提供 OpenCPN 不方便做的情境耦合(情境航線/區域/禁區、公司 UKC 政策推得的安全等深線、與核心一致的 CPA 門檻)。兩者可並存。
- **情境耦合**:依 `state.scenario.id` 在建置時匯入的情境清單(`lib/scenario.ts loadScenarioList`,與教官站同一來源)找到情境,取 `origin`、
  `route`、`zones`、`assessment.minCpa_nm`、`assessment.ukc.min`、`environment.waterDepth`。情境不明(無 `scenario` 或清單找不到)時由狀態的
  `pos.lat/lon` 與 `pos.x/y` 反推核心原點(`projection.inferOrigin`),示意幾何仍可疊到正確位置,但無航線監控(頂列顯示「原點推估」)。

## 2. 海圖顯示(簡化 S-52 規則)

| 項目 | 作法 |
|---|---|
| 投影 | 本地 ENU 平面(與教官站航跡圖、`lib/timeline.latLonToEnu` 同一近似:111 320 m/度),畫面中心 + 量程 + 旋轉;座標轉換沿用雷達頁 `radar/geometry` 的 `enuToScreen`/`screenToPolar`(`projection.toPpiView`) |
| 定向 | 北上(N UP)/ 航向上(C UP:選擇時擷取的參考航向,「重設」改用目前航向);**電羅經無效時退回北上**(海圖以地理為基準,與雷達頁「只能艏向上」相反) |
| 量程 | 0.25 / 0.5 / 0.75 / 1.5 / 3 / 6 / 12 / 24 nm(畫面中心到上緣);滾輪或 +/− |
| 平移 | 拖曳(自動關閉跟隨),「置中」恢復跟隨自船 |
| 自船 | 放大到 LPP > 24 px 且航向有效時畫真比例外形(`lib/ship-geometry`),否則雙圓符號;航向線(電羅經有效時);COG/SOG 向量(預設 6 分鐘,虛線,每分鐘一點);GNSS 無效時符號虛線、旁標 **DR** |
| 航跡 | `lib/track-history`,間隔 關/10/30/60/180 s,最多 2000 點;DR 時不累積 |
| AIS 目標 | `state.targets[]` 中 `aisOn` 者:等腰三角形依航向、COG/SOG 向量、名稱與「CPA x / TCPA y'」標籤;位置/航向/COG/SOG 優先取 `ais` 報告值(含注入誤差),無則真值;危險(CPA < 門檻且 0 ≤ TCPA < 12 分)紅色加粗;點選顯示資料(MMSI、呼號、船型、尺寸、吃水、航行狀態、目的地、BCR/BCT) |
| 距離圈 | 量程的 1/4 間距 |
| EBL/VRM | 兩組,以自船為原點、真方位;工具模式點海圖同時設定該組 EBL 方位與 VRM 距離;亦可輸入數值 |
| 游標 | 經緯度(有原點時)、自船到游標的真方位與距離、游標處有效水深 |
| 水深著色 | 四階(S-52 概念):淺於**吃水**(very shallow)、淺於**安全等深線**(shallow)、淺於**深水等深線**(medium)、其餘(deep);水深區邊界跨越安全深度者以粗線標示(= 安全等深線) |
| 示意幾何 | `data/areas` 的陸地(米色填色)、防波堤、淺灘(有 `depth_m` 時併入水深區)、錨地(點線)、速限區(虛線)、岸壁(粗線);名稱標籤 |
| 情境區域 | `noGo` 紅色實線 + 淡紅填色;`speedLimit` 洋紅點劃線(附速限);`ukcPolicy` 藍虛線;`reporting` 點線 |
| 航線 | 航段虛線、目前航段實線加粗、走廊(各航段 `xtdLimit_m`)淡紅底;航點圓圈、下一航點加粗、抵達半徑虛圓 |
| 其他 | 北箭頭(隨旋轉)、比例尺、「簡化 ECDIS — 示意幾何,非 ENC,不可用於航行」常駐提示 |

配色依日/黃昏/夜主題(`ecdis-shared.ts ECDIS_PALETTE`),紅色只用於危險目標、禁區與警報;非 S-52 色票。

## 3. 水深模型與安全等深線(`lib/ecdis/safety-depth.ts`)

- **圖載水深**:基底水深(情境 `environment.waterDepth`;無情境時 `state.waterDepth − tideHeight_m`;皆無時 20 m 並標「基底未知」)+ 水深區
  (情境 `zones[].waterDepth_m`、區域檔 `shoals[].depth_m`、`channel.depth_m`);**多區重疊取最淺**(與核心 `ZoneDepth` 相同)。
- **有效水深** = 圖載水深 + `state.tideHeight_m`(情境有潮汐時;潮高顯示於工具列與頂列)。
- **安全等深線** = 自船**最大吃水**(`ship-config.maxDraft`,與核心 UKC/擱淺判定同一值)+ **UKC 下限**:情境 `assessment.ukc.min` 的 `metres` 與
  `fractionOfDraft × 吃水` 中可解析者取較大;參數引用 `${params:…}` 在網頁端不解析(與教官站相同),此時與未給時皆用**公司 UKC 政策最小值 1.0 m**
  (`data/parameters/company-procedures.yaml navigation.ukc.minimum_m` 的預設;工具列標示來源「情境/公司」)。淺水等深線 = 吃水,深水等深線 = 2 × 安全深度。
- **前方掃描**:沿 COG 以 SOG 前進向量長度(預設 6 分鐘;上限 24 nm)每 25 m 取有效水深,第一個 < 安全深度的點即「安全等深線接近」警報,
  海圖以紅叉標出;SOG < 0.1 kn 時只檢查自船位置。

## 4. 航線監控(`lib/ecdis/route-monitor.ts`)

以情境 route 的 ENU 航點(`lib/plot-overlay.overlayRoute`,航點 `xtdLimit_m` 為到達該航點的航段走廊半寬)計算:目前航段、XTD(右正左負)、
走廊超限、到下一航點 DTG、以 SOG 的 TTG(SOG < 0.1 kn 為 `--:--`)、預定航向(航段航向)、到航點方位、下一航段航向與轉向量、轉向點倒數(= TTG)、
剩餘總程。**抵達切換**:進入航點抵達半徑(`arrivalRadius_m`,預設 185 m)或越過航點處與航段垂直的線;可連跳多個已過航點;抵達終點標「已抵達終點」
(caution 警報)。航線面板可手動切換航段。

本模組是頁面本地計算;核心 `state.route`(`legIndex`、`xtd_m`、`distanceToWaypoint_nm`)仍是評估依據,兩者的切換規則可能略有不同(核心以
`routeLeg` 觸發條件/`passedWaypoint` 定義),本頁不以本地結果送任何指令。

## 5. 警報(`lib/ecdis/alarms.ts`,頁面本地)

| 種類 | 優先序 | 條件 |
|---|---|---|
| 安全等深線 `safetyContour` | alarm | 前方掃描命中(第 3 節) |
| 航跡偏差 `xtd` | warning | \|XTD\| > 走廊半寬(走廊未知時不判) |
| CPA/TCPA `cpa:<id>` | alarm | AIS 目標 CPA < 門檻且 0 ≤ TCPA < 12 分;門檻 = 情境 `assessment.minCpa_nm`(可解析時)否則 0.5 nm |
| 區域進入 `zone:<id>` | alarm | 自船位置在 `noGo` 區域內 |
| GNSS `gnss` | warning | `sensors.positionValid = false`(位置凍結於最後有效值,符號標 DR) |
| 電羅經 `gyro` | warning | `sensors.headingValid = false`(航向線隱藏、航向上不可用) |
| 計程儀 `log` | caution | `sensors.stwValid = false` |
| 航線終點 `routeEnd` | caution | 抵達終點 |

狀態機(MSC.302 概念的簡化):新條件 → **未確認**(alarm/warning 有聲響,caution 無);確認後條件仍成立 → 留在清單、無聲;條件消失且已確認 → 移除;
條件消失但未確認 → 保留為「已解除,待確認」直到確認;已確認者消失後再成立視為新警報。聲響沿用操船台 `lib/alert-sound.AlertSounder`
(同一嗶聲型式;「警報聲響」可關,存 `localStorage` 鍵 `simosa-brm.ecdis.alarmSound`;瀏覽器需先有一次點擊/按鍵)。

**與核心警報的關係**:核心 BAM(`state.alerts[]`,`src/SimCore/Alerts/`)與情境引擎的 `cpaAlarm`/`shallowWater`/`xtd` 評估是訓練評估的依據;
本頁警報只是 ECDIS 顯示層對相同狀態的獨立判斷(門檻可能不同,例如安全等深線前方 6 分鐘、公司 UKC 預設),**本頁的確認不送 `acknowledge`/
`alertAcknowledge`**,不會被評估為學員的確認動作;學員的確認仍須在操船台警報面板(或教官站)進行。這樣可避免 ECDIS 頁的本地門檻影響評估與雜湊。

## 6. 感測器故障顯示

| 故障 | 狀態欄位 | 本頁表現 |
|---|---|---|
| GNSS 失效(`gnssFailure`/`gnssDrift` 等) | `sensors.positionValid = false`;核心把頂層 `pos/cog/sog` 凍結在最後有效值 | 頂列紅色閃爍「DR 位置凍結(GNSS 無效)」、自船符號虛線加 **DR**、COG/SOG/位置淡化、航跡停止累積、GNSS 警報;AIS 目標仍以報告位置畫出 |
| 電羅經失效 | `sensors.headingValid = false` | 頂列「航向無效(北上)」、HDG 顯示 ---、航向線隱藏、C UP 停用並退回北上、電羅經警報;目標符號仍以 AIS 航向 |
| 計程儀失效 | `sensors.stwValid = false` | STW 淡化、caution |
| 測深儀失效 | `sensors.depthValid = false` | 工具列「龍骨下(測深)」以警示色標示;安全等深線以圖載水深計算,不受影響 |
| 核心凍結/擱淺 | `flags.frozen` / `flags.aground` | 頂列 chip |

與雷達頁一致:指示值(`heading/pos/cog/sog`)驅動顯示,真值(`state.truth`)不使用。

## 7. 啟動與部署

- 建置:`pnpm build`(`vite.config.ts` 進入點 `ecdis`)→ `build/web/instruments/ecdis.html`;發行版 `app/web/ecdis.html`。
- 啟動腳本:`config/ship-laptop.json web.openPages` 含 `ecdis.html`(`shellMonitors` 第 4 項),`config/instructor-laptop.json` 含
  `ecdis.html?ws=ws://{ship}:{stateWs}`(教官筆電鏡像);WebView2 外殼 `--page ecdis [--ws …]`(`?ws=` 為唯一參數;使用者資料夾 `build/webview2/ecdis`)。
- 網址參數:`?ws=`、`?demo=1`、`?theme=day|dusk|night`、`?ship=FSB1|FSB2`(示範船)、`?range=<nm>`。快捷鍵:+/− 量程、N 北上、C 航向上、Home 置中、Esc 平移工具、A 全部確認。
- 離線:無任何網路資源;示意幾何與情境在建置時匯入(`import.meta.glob`)。

## 8. ENC 匯入後的計畫

1. **底圖替換**:`chart-data.ts` 的 `sketch`/`depth` 改由 ENC 轉出的向量圖層(岸線 COALNE/LNDARE、水深區 DEPARE/DRGARE、航道 FAIRWY、禁區 RESARE、
   浮標/燈標)產生;S-57 → 本地 ENU 的轉檔工具放 `src/Tools.Calibration`(Python,離線批次),輸出 `data/charts/<cell>.json`(含 `chartDatum`、
   `compilationScale`、`issueDate`);`data/charts/` 目前為空,ENC 需正式授權(規劃書附錄 C)。
2. **S-52 顯示庫**:以 ENC 的 DEPARE `DRVAL1/DRVAL2` 取代目前的多邊形最淺值;安全等深線由實際等深線選擇(≥ 安全深度的最近等深線)而非著色邊界;
   加入燈標/浮標符號(簡化 S-52 點符號)與 SCAMIN 依量程取捨。
3. **ENC 管理**:圖檔清單、版次、更新(以簽章更新包 kind=data 派送,`docs/design/packaging-sync.md`)、載入的圖格顯示。
4. **航線**:ENC 到位後可加航線規劃(航點編輯)並匯出為情境 `route`;目前只監控情境航線。
5. **OpenCPN**:整合測試仍列於需求追溯表「需實機」,本頁不取代。

## 9. 限制(需明示於訓練說明)

- 非型式認可(IEC 61174/IHO S-52/S-57/S-63/S-64 皆非目標);底圖為示意幾何(誤差可達數百公尺),**不可用於航行**;畫面常駐提示。
- 平面近似投影(公尺級差異)、情境原點不明時由狀態反推。
- 水深只有多邊形常數值(無等深線插值),潮高為單一值(情境潮汐模型),無 squat 修正(測深儀的龍骨下水深另顯示)。
- 航線監控與核心 `state.route` 的切換規則可能略異;本地警報與核心 BAM 獨立、不送確認。
- 參數引用 `${params:…}` 不解析:安全等深線 UKC 與 CPA 門檻退回公司預設 1.0 m / 0.5 nm。
- 目標只畫 AIS(無雷達目標疊圖;雷達 ARPA 見雷達頁);無航線規劃、無 ENC 管理、無 NMEA 輸出。
- 效能:Canvas 2D 每筆狀態重繪(25 Hz 上限 rAF 合併),示意幾何量小;ENC 到位後需加視野裁切與圖層快取。
