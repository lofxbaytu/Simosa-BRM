# 教官站航跡圖:圖層、流場疊圖與海圖點選編輯(Instructor Plot)

> 2026-11-01 第二十一輪。對應規劃書第 9.1 節(教官站、情境編輯)、第 9.2 節(重播);需求追溯表 R3(流場)、R4「海圖點選編輯」;
> 情境引擎設計 `docs/design/scenario-engine.md` 第 1.5.2.1 節(`environment.current.field`)與第 6.1 節(情境編輯器)。
> 程式:`src/Instruments.Web/src/components/track-plot.ts`(`<brm-track-plot>`)、`lib/plot-overlay.ts`、`lib/current-field-plot.ts`、
> `lib/plot-editing.ts`、`lib/scenario-editor-plot.ts`、`components/instructor/scenario-editor-plot.ts`。

## 1. 範圍與原則

1. `<brm-track-plot>` 是駕駛台儀器頁、完整/簡化教官站、重播檢視與情境編輯器共用的鳥瞰航跡圖(本地 ENU、北上、Canvas 2D)。
   本輪新增**流場層**(情境空間流場網格的箭頭與自船位置的流)與**點選編輯模式**(情境編輯器內嵌圖),不改既有圖層的繪製與數值。
2. 純視覺:流場層只讀情境與 `state`,不送指令、不影響核心狀態雜湊;編輯模式只改教官站的情境草稿(`ScenarioDraft`),載入核心仍走既有的 `loadScenario`。
3. 無 ENC:底圖為 `data/areas/*.json` 的**示意幾何**(手繪概略輪廓,誤差可達數百公尺;`visual-client.md` 第 11 節第 2 點),一律標示「非海圖」;
   ENC 取得後以同一疊圖介面(`PlotOverlay.sketch`)換成海岸線/碼頭線資料。
4. 幾何與草稿邏輯全部是純函式(`lib/*.ts`),vitest 直接驗證;Canvas 繪製與 DOM 互動在元件內,happy-dom 測試用 640 × 480 的像素換算後備尺寸。

## 2. 圖層(由下而上)

| 層 | 內容 | 資料來源 | 開關 |
|---|---|---|---|
| 格線 | 依比例 10 m–10 km 自動選距;比例尺、北向箭頭 | — | 常開 |
| 示意幾何 `sketch` | 陸地(灰填)、防波堤(深灰填)、淺灘(橙色虛線)、錨地(點線)、速限區(橙虛線)、岸壁(粗線);名稱標籤 | `sketchFromAreas(AREA_FILES, origin, 30 km)`:`land[].points`、`breakwaters/shoals/anchorages/speedLimitZones[].polygon`、`banks[].line`,任一頂點在原點 30 km 內即收錄;經緯度以 `latLonToEnu` 平面近似、`{x, y}` 直接用 | 目前只有情境編輯器內嵌圖帶此層(即時/重播不帶,避免與雷達陸地回波重複) |
| 區域 `zones` | 禁區紅、速限橙、UKC 政策藍、報告灰;多邊形填色 12 % | 情境 `zones[]`(即時:情境摘要;重播:`header.scenario`;編輯器:草稿) | 常開 |
| 泊位 `berth` | 碼頭線、碰墊(受壓著色)、繫船柱 | 情境 `berth`(`file` + `id` 以建置時匯入的區域檔解析;重播用內嵌 `definition`) | 常開 |
| 航跡 | 自船航跡線 | `TrackHistory` | 常開(編輯器內嵌圖為空) |
| 航線 `route` | 中心線、走廊半寬虛線、航點(檢查點/報告點空心)、到達半徑 | 情境 `route` | 常開 |
| 向量/自船 | 航向向量 6 min、COG/SOG 虛線、船體外形(LPP/B) | `state` | 常開 |
| 外力 | 纜繩、錨鏈/錨位、拖船與力向量 | `state.tugs/anchors/berth` | 常開 |
| 目標船 | 三角形、標籤、COG/SOG 短向量、CPA 小表 | `state.targets[]` | `target-table` 屬性 |
| 事件標記 | warning 橙/alarm 紅三角 | 事件流 + 狀態抽樣 | 常開 |
| **流場層** | 網格節點箭頭、網格外框與說明;左下角自船位置的流箭頭 + 數值 + 來源 | `overlay.currentField`(第 3 節)、`state.current`/`state.currentSource` | 工具列「流」(`show-current` 屬性,預設開) |
| **編輯把手** | 航點/頂點/自船/目標船的圓點與標籤、選取圓環、拖曳虛線 | `handles[]`(第 4 節) | `editable` 屬性 |

## 3. 流場層(R3「流場網格視覺化」)

### 3.1 資料

- `lib/current-field-plot.ts` `parseCurrentField(raw)`:把情境 `environment.current.field` 原樣物件解析成 `CurrentFieldSpec`
  (`grid {x0, y0, dx, dy, nx, ny}`、`u_kn[ny][nx]`、`v_kn`、`outside edge|zero`、`timeScale none|tidal|table`);只接受**可畫**的場
  (`dx/dy > 0`、`nx/ny ≥ 2` 整數、陣列尺寸正確、全部有限數值),否則 `null`(結構問題仍由 `currentFieldIssues` 報告在編輯器)。
- 進入疊圖的路徑:即時 `parseScenario` → `ScenarioSummary.currentField` → `overlayFromScenario`;重播 `overlayFromHeaderScenario(header.scenario)`
  → `currentFieldOfScenario`;編輯器 `editorPlotOverlay(draft)` 取草稿 `currentModel.field`。`PlotOverlay.currentField` 有值時 `overlayHasContent` 為真。
- `fieldVectorAt(field, x, y)`:與核心 `CurrentField.Interpolate` / Python `CurrentField.interpolate` 相同運算順序的雙線性內插
  (`outside zero` 格外為 0、`edge` 夾到範圍;先沿 x 再沿 y)。測試以設計文件 1.5.2.1 的範例網格核對節點、格中心、格外。

### 3.2 取樣與繪製

- `sampleFieldArrows(field, viewport, 40)`:兩軸各以 `stride = ⌈n / 40⌉` 等距抽樣(保證 ≤ 40 × 40 支),再只保留落在畫面可見範圍(外擴一格)內的節點;
  節點值為原始資料、不內插(編輯時看到的就是表格裡的數值)。回傳抽樣步距與 `cell_m`(抽樣後相鄰箭頭最小間距)。
- 箭頭長度 `arrowLengthPx(speed·k, max, cellPx)` = `cellPx × 0.8 × speed/max`,上限 60 px,`< 1.5 px` 畫成點;以節點為中心、指向流去向(ENU → 畫面 y 反向)。
- 顏色 `speedColor(speed·k, max)`:色相由 200°(低速,青藍)線性到 0°(高速,紅),飽和 80 %、亮度 55 %;依流速分 **8 桶**,每桶一條 `beginPath … stroke`
  (加上零流速的點一桶),整層一次繪製,不逐支設定樣式。
- 網格外框以虛線矩形標示;左下角說明「流場網格 nx×ny · 格距 dx×dy m · 最大 X kn · k = …(timeScale)· 抽樣 1/sx×1/sy」。

### 3.3 timeScale 當前比例 k(t)

核心在 `timeScale tidal|table` 時把整個場乘以均勻時變模型的流速比例 `k(t) ∈ [0, 1]`,並把 `k(t)·field(自船位置)` 寫進 `state.current`
(`scenario-engine.md` 1.5.2.1)。教官站不重算潮汐/流場表,而由狀態反推(`fieldScaleFromState`):

- `state.currentSource` 以 `field` 開頭 → 場作用中;`timeScale none` → `k = 1`;
- 否則 `k = |state.current.drift| / |field(自船位置)|` 夾到 `[0, 1]`(`derived: true`);場在自船位置 < 0.05 kn 時無法反推,取 `k = 1` 並標「(預設)」;
- `currentSource` 不是 field(常數流、`setEnvironment current` 覆寫後、Python 參考伺服器)→ 場**未作用**:不畫箭頭,說明列改為「流場網格 nx×ny 未作用(來源)」;
- 情境編輯器內嵌圖以 `field-preview` 屬性視為作用中、`k = 1`。

### 3.4 均勻流/時變流

沒有網格(或場未作用)時,流的唯一資訊是 `state.current`(自船位置的值):左下角畫一支固定 24 px 的箭頭(去向)與「流 085° 2.5 kn · 來源」
(`CURRENT_SOURCE_LABELS`:流場表/潮流/流場網格…;常數流標「常數流」;編輯器標「情境預覽」)。有網格時同樣顯示,並註明「(自船位置)」。

### 3.5 效能

每幀工作量上限固定:≤ 1 600 支箭頭、9 條路徑;抽樣只做整數步距,無記憶體配置以外的複雜運算;示意幾何與區域一樣逐形狀一條路徑。
內顯筆電(規劃書 7.3 節)以 20 Hz 狀態更新重繪不成問題;若日後網格 > 200 × 200 仍受 40 × 40 上限保護。

## 4. 海圖點選編輯(R4)

### 4.1 開啟與對象

完整教官模式「情境編輯」分頁的中欄最上方出現「海圖點選編輯 PLOT」面板(`renderPlotEditor`),由以下按鈕開啟並設定**對象**(`EditorHost.plotTarget`):

| 按鈕 | 對象 `PlotEditTarget` | 點擊空白處 |
|---|---|---|
| 航線區段「在圖上編輯」(`button.route-plot`) | `{kind: 'route'}` | 新增航點:落在某航段 ±12 px 內(且投影在段內)插入該段中間,否則接在末端;名稱 `WPn` 唯一 |
| 區域列「在圖上編輯」(`button.z-plot`) | `{kind: 'zone', index}` | 插入離點最近的邊(含閉合邊)的頂點 |
| 船與初始狀態「圖上」(`button.own-plot`) | `{kind: 'own'}` | 設定自船初始位置 |
| 目標船列「圖上」(`button.t-plot`) | `{kind: 'target', index}` | 設定該目標船初始位置 |
| 環境進階「網格預覽」(`button.field-preview`) | `{kind: 'field'}` | 只看,不新增 |

面板上的 `select.plot-target` 可切換對象,「關閉」收起面板。對象切換時清除選取。

### 4.2 把手與互動(`<brm-track-plot editable>`)

- `handles[]`(`plotHandles(draft, target, origin)`):航點 `wp:<i>`(有航線時一律列出,可刪)、編輯中區域的頂點 `zone:<zi>:<j>`(可刪)、自船 `own`、
  全部目標船 `target:<i>`(不可刪)。無法換算的點(無原點的經緯度、參數引用)不列。
- 指標:`pointerdown` 命中把手(9 px)→ 選取並開始拖曳(拖曳中把手畫在游標位置、虛線連回原位,**放開才寫回草稿**,避免每個 move 都重新驗證整份草稿);
  命中空白 → 平移(> 4 px)或點擊(≤ 4 px,發 `plot-click`)。右鍵(`contextmenu`)命中可刪把手 → `plot-handle-remove`;瀏覽器選單一律抑制。
- 事件(`CustomEvent`,bubbles + composed):`plot-click {x, y, px, py}`、`plot-handle-move {key, x, y}`、`plot-handle-remove {key}`、`plot-handle-select {key | null}`。
  面板把它們交給 `addPlotPoint` / `movePlotHandle` / `removePlotHandle`(`lib/scenario-editor-plot.ts`)寫回草稿,新點成為選取把手。
- 讀數列(`.readout`,左上):游標的 `x/y m` 與(有原點時)經緯度 5 位小數、「選取 <把手>」;游標離開時顯示對象的操作提示。

### 4.3 鍵盤可用性

`editable` 時畫布 `tabindex="0"`、有 `aria-label`;點擊畫布取得焦點。方向鍵微調**選取中的把手**:步距 `nudgeStep_m(zoom)` ≈ 10 px 對應的距離取 1/2/5 階
(zoom 0.6 px/m → 20 m;Shift ×10);`Delete`/`Backspace` 刪除可刪把手;`Esc` 取消選取。每次微調即寫回草稿(表格同步)。

### 4.4 座標寫法與換算

- 原點 `plotOrigin(draft)`:範本 `origin`,否則經緯度的初始位置;皆無時只能畫/編 ENU `x/y` 的點(面板提示)。
- 換算沿用疊圖既有的平面近似(`latLonToEnu`,111 320 m/度;反算 `enuToLatLon`),與航線/區域疊圖一致;泊位幾何仍用 WGS-84 曲率半徑(`berthGeometry`),
  兩者在港區尺度差異 < 0.3 %。
- 寫回時**沿用該點既有寫法**(`pointPositionMode`):原本 `lat/lon` 就寫經緯度(5 位小數 ≈ 1 m)、原本 `x/y` 就寫 ENU 整數公尺;新點依最後一點(航線/區域)或
  初始位置的寫法;經緯度但無原點時退回 ENU。其他鍵(航點名稱/速限/檢查點、頂點以外的區域欄位、目標船 heading/behaviour …)完全保留。

### 4.5 與表格的雙向同步

圖與表格讀寫同一份 `ScenarioDraft`:圖上新增/移動/刪除 → `setDraft` → 驗證、localStorage 儲存、表格重繪;表格改座標 → 草稿 → 把手與疊圖重算。
內嵌圖的疊圖(`editorPlotOverlay`)由 `toScenarioObject(draft)` 經 `parseRoute/parseZones` 走與教官站相同的換算,泊位以建置時匯入的 `data/areas` 解析,
流場網格取草稿 `currentModel.field`,示意幾何取原點周圍 30 km。合成狀態(`editorPlotState`)取初始位置/航向/速度與環境面板常數流,讓內嵌圖以初始位置置中並顯示左下角流箭頭。

## 5. 流場網格產生器(環境進階面板)

| 按鈕 | 函式 | 作用 |
|---|---|---|
| 由均勻流產生網格 `button.gen-uniform` | `setCurrentFieldFromUniform(draft)` | 以環境面板的流向/流速(需為數值)填滿全部節點 `u = drift·sin set`、`v = drift·cos set`(0.001 kn);只改 `u_kn/v_kn`,格數取現有 `grid` |
| 由兩端流速線性內插 `button.gen-linear` | `setCurrentFieldLinear(draft, axis, A, B)` | 沿 `x`(西→東,第 0 行到第 nx−1 行)或 `y`(南→北,列)由端點 A 到 B 以**東/北分量**線性內插(方向跨 360° 不繞遠路);A/B 預設為環境面板常數流 |
| 網格預覽 `button.field-preview` | `setPlotTarget({kind: 'field'})` | 開啟海圖點選編輯面板,以流場層(第 3 節,`field-preview`)畫網格 |

產生器需已勾選建立 `field` 且 `grid.nx/ny` 為 ≥ 2 的整數,否則表單錯誤(`guard`)不改草稿;產生後尺寸檢查、`fg-u`/`fg-v` 文字區塊與預覽即時更新。

## 6. 測試(vitest)

- `tests/current-field-plot.test.ts`:解析(合法/不合法)、雙線性內插(範例網格節點/格中心/格外 edge/zero)、向量 ↔ set/drift、最大流速/外框、
  可見範圍取樣與 130 × 95 網格的抽樣(≤ 40 × 40、步距 4/3)、箭頭長度與顏色桶、`fieldScaleFromState`(預覽/未作用/none/tidal 反推與夾限/近零預設)、
  均勻流/兩端內插產生器(含 −0 與跨 360°)、`parseScenario` → `currentField`(`environment.current` 不變)→ 疊圖、`sketchFromAreas`(各種鍵、範圍過濾、ENU 點)。
- `tests/plot-editing.test.ts`:像素 ↔ ENU 往返、把手命中、線段距離、多邊形/折線插入索引、步距階、`enuToLatLon` 反算、草稿座標寫法與 5 位/整數四捨五入、
  讀數文字;草稿層:原點、對象值往返與名稱、把手清單(含無原點)、新增航點(末端/插入、唯一名稱、不動原草稿)、新增頂點、自船/目標船設定、移動(保留其他鍵)、刪除、
  內嵌疊圖與合成狀態。
- `tests/plot-editor-panel.test.ts`(happy-dom):面板開啟/關閉與對象、點擊新增航點(像素換算一致、表格同步、插入航段)、右鍵刪除、拖曳移動(其他鍵保留、表格同步)、
  純點擊只選取、方向鍵/Shift 微調、Delete/Esc、區域頂點新增/拖曳與表格、自船/目標船點選、右鍵不刪自船/目標船、選單切換、無原點的 ENU 區域、
  產生器(均勻流、沿 x/y 線性內插、錯誤訊息)、網格預覽與工具列「流」開關、網格尺寸錯誤時不畫。

## 7. 限制與待辦

1. 底圖為示意幾何(非海圖),不可用於航行判斷;ENC 取得後以 `PlotOverlay.sketch` 換成海岸線/碼頭線/燈浮標(可能改為 S-57 轉出的 GeoJSON)。
2. 平面近似換算(111 320 m/度)在港區尺度足夠;跨數十海里的情境(台灣海峽 F01–F03)誤差增大,但航線/區域疊圖本來就用同一近似。
3. `k(t)` 由狀態反推:場在自船位置近零(例如自船在零流區)時顯示預設 1;日後核心若在 `state` 廣播 `currentScale`(契約變更)即可改用真值。
4. 示意幾何、區域、航線在即時/重播圖都重算一次疊圖(每次 render),情境編輯器內嵌圖亦然;15 份區域檔、數十個形狀無效能問題,若區域檔大量增加可改為快取。
5. 編輯模式只在情境編輯器;即時/重播圖的 `editable` 預設關閉,不提供練習中改情境(與 R4 的「編輯」語意一致)。
6. 鍵盤只支援把手微調/刪除/取消選取;以鍵盤**新增**點(例如 Enter 於游標處)未做,新增仍需指標;觸控以單指平移/點擊,無雙指縮放(用 +/− 鈕)。
