# 雷達 PPI 顯示頁(radar.html)

對應規劃書第 7.2 節雷達列(MSC.192(79))與第 14 章 ARPA 驗收;顯示概念依 MSC.191(79)/IEC 62288(艏向上/北向上/航向上、真/相對運動、EBL/VRM、平行索引線、ARPA 符號)。資料來源為雷達服務 `src/Sensors.Radar`(設計見 `docs/design/radar-arpa.md`,契約 `src/Contracts/radar.schema.json`)。擬真等級依 D5:BRM 可信度,不追求型式認可。

## 執行

```powershell
# 開發(Vite)
pnpm dev                                   # http://localhost:5173/radar.html
# 建置後預覽(輸出在 build\web\instruments\radar.html)
pnpm build; pnpm preview                   # http://localhost:4173/radar.html

# 真實資料:先啟動 SimCore.Host 與雷達服務
dotnet build\bin\SimCore.Host\Release\net8.0\SimosaBRM.SimCore.Host.dll --scenario data\scenarios\E05_pilot_traffic.yaml
dotnet build\bin\Sensors.Radar.Host\Release\net8.0\SimosaBRM.Sensors.Radar.Host.dll --guard 0.3,8
```

| 網址參數 | 說明 |
|---|---|
| `?radar=x` / `?radar=s` | 選台(X 頻 9.41 GHz / S 頻 3.05 GHz),預設 x;頁首 X/S 按鈕也可切換 |
| `?ws=ws://host:8766` | 雷達服務位址(基底或完整 `…/radar/x`);預設 `ws://<頁面主機或 localhost>:8766/radar/<台>` |
| `?demo=1` | 示範模式:不需服務,本地合成影像與 2 個假追蹤目標(T1 右舷橫越危險、T2 被追越;另有未追蹤的 T3 可手動擷取) |
| `?fps=30` | 重繪上限(5–120,預設 30) |
| `?perf=1` | 效能量測:每個 rAF 都重繪並在頁首顯示 fps 與繪製時間 |

斷線時自動重連(1 → 5 s 指數退避);未連線期間的設定類指令(量程、增益、STC、FTC、調諧、待機/發射、保護區、試操船、CPA 限值)保留最後一筆,連上後補送;擷取/取消不補送。

## 畫面與操作

- **PPI**(左):影像、距離圈、圓外方位刻度(依定向旋轉)與北標、艏向線(`HL OFF` 按住暫時隱藏)、自船向量(真向量模式)、EBL ×2(EBL1 長虛線、EBL2 短虛線)、VRM ×2、平行索引線 PI ×2、保護區(橘色虛線)、ARPA 符號、游標十字;四角文字:定向/運動/向量/軌跡、量程/圈距/TRIAL、EBL/VRM 讀數、游標方位距離。
- **ARPA 符號**:擷取中 = 虛線圓;追蹤中 = 實線圓 + 向量(真/相對,向量時間 1–24 分);危險目標(CPA < 限值且 0 ≤ TCPA ≤ 限值)= 紅色,未確認前 1 Hz 閃爍(`確認警示` 後恆亮,目標不再危險後確認自動失效);失追 = 圓 + 交叉(未確認前閃爍);選取目標 = 方框;過去位置 = 小點(每 30 s,真運動為地理位置、相對運動為相對位置;相對位置依自船歷史內插,核心時間倒退時丟棄 t ≥ 新時間的舊歷史點)。
- **純 AIS 目標**(`tracks.aisTargets[]`,未被雷達追蹤的 AIS 船):藍色三角形頂點指向 COG + 向量線 + 船名(危險者紅);選取工具點符號 → 目標資料面板顯示 MMSI/COG/SOG/CPA/TCPA 與「擷取此目標(ARPA)」鈕;擷取工具點符號 → 以 AIS 方位/距離送 `acquire`。
- **游標工具**(滑鼠左鍵):選取(點目標顯示資料)、`ACQ 擷取`(點回波送 `acquire`,真方位;航向無效時送相對方位)、EBL/VRM/PI 的 `點選`、`偏心`(點選新的自船位置,上限 66% 半徑)。右鍵或 Esc 回到選取;滾輪或 +/− 切換量程;A 切換擷取工具;Delete 取消選取的目標。
- **定向**:H UP(艏向上,影像穩定化後依航向旋轉)、N UP(北向上)、C UP(航向向上:以選擇該模式時的 COG 為參考,`C UP 重設` 重取;不隨 COG 抖動)。航向無效(幀 flags bit1 = 0)時只能艏向上且影像未穩定化,ARPA 停止;此時畫面為相對座標(正上方 = 船首),天線偏移、EBL/PI 的 R 參考、點選換算與保護區相對扇區都以畫面參考航向 0 計算(`screenHeading`),不加核心保留的最後航向,游標讀數標示 R。
- **運動**:RM 相對運動(可偏心)、TM 真運動(北向上或航向上;自船依 ARPA 自船位置移動,超過 0.7 R 時重設到航向反側 0.6 R)。偏心/真運動時本頁向服務要求較大的影像量程(涵蓋最遠圓邊的標準量程,通常 2 倍),顯示量程不變(控制面板顯示「影像 12」)。
- **軌跡**:回波軌跡 30 s – 12 min(真運動為真軌跡:軌跡畫布依自船位移平移;相對運動為相對軌跡),量程改變時清除。**餘暉**:掃描線前方較舊的回波逐步變暗(關/短/長)。
- **右側面板**:雷達控制(量程、增益、海雜波 STC、雨雜波 FTC、調諧/AUTO、待機/發射、脈衝、故障/劣化/航向無效)、顯示、EBL/VRM/PI(數值輸入、T/R 參考切換,切換參考時保持同一條線)、ARPA 表(依 CPA/TCPA/距離/編號排序:危險優先、接近中優先、已通過在後;試操船時改顯示試操船 CPA/TCPA)、目標資料(方位、距離、航向、速度、CPA、TCPA、BCR/BCT、相對向量、追蹤時間、漏掃描、旗標)、試操船(航向/速度/延遲 → 各目標預測 CPA;顯示選取目標或最近者;目前設定以最新 `status`/`tracks` 為準,情境切換後不沿用舊值)、危險限值與保護區、雷達事件與指令錯誤。

## 協定使用(摘要;完整見 docs/design/radar-arpa.md 第 4–5 節)

- **二進位幀**(SRAD v1,40 位元組標頭 + spokeCount × cells):`src/radar/frame.ts` 解析;方位線 j 的相對方位 = (firstSpoke + j) × 360 / spokesPerRev;北向上真方位 = 相對方位 + heading_deg;格 i 中心距離 = (i + 0.5) × range_m / cells;強度 0–255。
- **JSON**:`status`(設定、天線位置、保護區、限值)、`tracks`(1 Hz,自船指示值與目標)、`event`、`ack`(帶回本頁的 `reqId`,格式 `w<序號>`)。`src/radar/protocol.ts` 防禦式解析(缺必要欄位略過該訊息/目標)。
- **本頁送出的指令**(JSON 文字框,皆帶 `reqId`):

```json
{"cmd":"range","value":12,"reqId":"w1"}
{"cmd":"gain","value":55,"reqId":"w2"}            // sea、rain 同格式
{"cmd":"tune","value":50}  {"cmd":"tune","auto":true}
{"cmd":"standby"}  {"cmd":"transmit"}
{"cmd":"acquire","bearing":330,"range":2.5}        // 真方位(航向無效時加 "relative":true)
{"cmd":"cancel","id":4}  {"cmd":"cancel","id":"all"}
{"cmd":"guardZone","enabled":true,"inner_nm":0.5,"outer_nm":6,"from":300,"to":60,"relative":true}
{"cmd":"trialManoeuvre","course":90,"speed":8,"delay":1}  {"cmd":"trialManoeuvre","active":false}
{"cmd":"cpaLimits","cpa_nm":1,"tcpa_min":12}
```

- **座標**:ARPA 位置為 ENU(x 東、y 北,m,CCRP = 船舯);PPI 以 CCRP 為自船位置,影像以天線位置(`status.antennaFwd_m/antennaStbd_m` 依航向換算)為中心,因此 EBL/VRM/游標量測自 CCRP,與 ARPA 的距離/方位一致。目標位置在 tracks 之間以真向量外推(≤ 3 s),自船以 COG/SOG 外推到影像幀時間。

## 繪製與效能(內顯文書筆電基準,規劃書第 7.3 節)

- 影像緩衝(`src/radar/video.ts`):方形 RGBA,半徑 = PPI 半徑(裝置像素,256–1600 px),北向上穩定化儲存。預先建立「方位格 → 像素清單」查表,每條方位線只寫自己方位格的像素(寫 floor 與 floor+1 兩格,航向變化時不留空隙),每幀約 33 條線 ≈ 1–3 萬次寫入;只上傳髒矩形(`putImageData` dirty rect)。
- 每次重繪(`src/radar/ppi-renderer.ts`):PPI 底色 → 軌跡 → 影像(北向上、置中且量程相同時為整數像素的直接複製,其餘雙線性重取樣)→ 餘暉(12 個純色扇形,不用圓錐漸層)→ 疊圖 → 「圓外框」靜態層(圓外底色 + 方位刻度,快取,旋轉以 0.1° 量化)。**不使用圓形裁切**:軟體繪製時每個繪圖指令都要套用裁切遮罩,實測是主要成本。
- 只在有新幀、設定/資料變更、游標移動或警示閃爍時重繪,上限 30 fps。
- 量測(Playwright Chromium 無 GPU、軟體繪製,本容器;CPU = 主執行緒 TaskDuration / 牆鐘):

| 條件 | 重繪 fps | 主執行緒 CPU | 單次繪製指令 |
|---|---|---|---|
| 1920×1080,真實服務(E05,X 頻),北向上 | 25(= 影像幀率) | 17% | 0.47 ms |
| 1920×1080,真實服務,`?perf=1&fps=60`(每 rAF 重繪) | 46 | 27% | 0.40 ms |
| 1920×1080,示範模式(含本地合成影像),北向上 | 25 | 18% | 0.47 ms |
| 1920×1080,示範模式,艏向上(影像旋轉重取樣) | 25 | 36% | 0.55 ms |
| 1920×1080,示範模式,真運動 + 3 min 軌跡 | 25 | 39% | 0.51 ms |
| 1366×768,示範模式,北向上 | 25 | 12% | 0.40 ms |

  有 GPU 的筆電上,Canvas 2D 由 GPU 繪製,主執行緒只剩指令與資料處理(上表 script 部分約 2–6%)。

## 程式結構

| 檔案 | 內容 |
|---|---|
| `radar.html`、`src/radar-main.ts` | 進入點 |
| `src/radar/frame.ts` | SRAD 幀解析/編碼 |
| `src/radar/protocol.ts` | JSON 訊息型別與解析、指令序列化、量程與距離圈 |
| `src/radar/geometry.ts` | 定向/運動/偏心座標轉換、EBL/VRM/PI、保護區、真運動重設 |
| `src/radar/arpa.ts` | ARPA 表排序、符號狀態、向量、CPA/TCPA 與試操船 CPA |
| `src/radar/video.ts` | 影像緩衝與掃描轉換 |
| `src/radar/ppi-model.ts` | 顯示設定、PpiView 計算、外推、影像量程 |
| `src/radar/ppi-renderer.ts` | Canvas 繪製 |
| `src/radar/radar-store.ts` | 頁面狀態中樞(來源訂閱、量程同步、警示確認) |
| `src/radar/radar-client.ts` | WebSocket 客戶端(重連、reqId、補送) |
| `src/radar/demo-radar.ts` | 示範模式資料來源 |
| `src/radar/radar-app.ts`、`radar-ppi.ts`、`radar-controls.ts`、`radar-arpa-panels.ts` | Lit 元件 |

測試:`tests/radar-frame.test.ts`(幀)、`radar-geometry.test.ts`(座標轉換、EBL/VRM/PI)、`radar-arpa.test.ts`(排序、符號、CPA、協定)、`radar-client.test.ts`(客戶端、影像緩衝)、`radar-demo.test.ts`(示範資料、狀態中樞、真運動)、`radar-components.test.ts`(元件與整頁示範模式,happy-dom)。

## 已知限制

- 同一台雷達的所有客戶端共用服務端的影像量程(服務只有一個顯示量程):其他客戶端改量程時本頁顯示量程跟著改;偏心/真運動時本頁要求的較大影像量程也會影響其他客戶端。
- 雷達操作進核心紀錄(`radarControl`);重播時由主控程序 `--replay --radar` 重放到雷達服務(docs/design/radar-arpa.md 第 5.2 節),本頁只需連著雷達服務即可看到學員當時的量程/擷取。
- 未做海圖疊加、警報音與 BAM 整合(危險目標只在本頁閃爍與標示)、浮動 EBL、自船船形輪廓;純 AIS 目標只有報告值(無睡眠目標區分、不發危險事件)。
- 真軌跡以整數像素平移累積,長時間後有次像素誤差;影像量程改變或主題切換時清除軌跡。
- 連線後只收到之後的事件(事件不重送);tracks 未到之前沒有自船位置,真運動暫時以相對運動顯示。
- 示範模式為簡化合成影像與真值追蹤,不可用於訓練或驗證。
