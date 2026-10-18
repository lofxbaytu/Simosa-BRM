# 岸壁效應(bank effect)— 設計與契約

> 規劃書依據:第 6.2 節運動方程式下標 B(岸壁與淺水修正)與第 458 行「岸壁效應與船間交互作用:AMC(塔斯馬尼亞)與 Flanders Hydraulics(Lataire, Vantorre)的回歸模型,
> 產生向岸吸力與艏離岸力矩」;R2「受限水域含 squat/UKC、岸壁與運河效應」;STCW A-II/2「本船與岸壁之間的交互作用」、B-I/12「受限水道含淺水與岸壁效應」;第 3.4 節港口;第 8.1 節 F03 航道;第 14 章確定性。
> 程式:`src/SimCore/Physics/External/BankEffect.cs`(`BankEffectModel`、`BankEffectCoefficients`、`BankLine`、`SumForceField`、`BankEffectState`)、
> `src/SimCore/Scenario/ScenarioBanks.cs`、`src/SimCore/Engine/SimulationEngine.cs`(接線、雜湊、狀態)、`src/SimCore/Contracts/OwnShipState.cs`(`bank` 欄位);
> 資料 `data/areas/mailiao-channel-banks.json`(示意);測試 `tests/SimCore.Tests/BankEffectTests.cs`、`tests/SimCore.Tests/BankEffectReviewTests.cs`。

## 1. 總覽與資料來源聲明

- 力模型採 Lataire & Vantorre(Flanders Hydraulics Research / Ghent University,2008 前後的岸壁系列試驗與 Lataire, Vantorre, Delefortrie 2018 的整理)與
  AMC(Ch'ng, Doctors & Renilson 1993)回歸模型的**結構**:橫向力與艏搖力矩 ∝ ½ρ·L·T·V²(力矩再乘 L),乘上「至岸距離」的加權參數(倒數距離、沿船長加權,
  y_inf 型)與水深吃水比 h/T 的修正,另有與螺槳載荷有關的一項。
- **數值係數為文獻量級的估計值**:FS Bitumen No.1 / No.2 沒有岸壁模型試驗;本專案離線開發(CLAUDE.md:不連網際網路取資料),未能逐式核對原文的回歸係數,
  因此下文第 4 節的係數是依公開文獻常見的無因次量級(Y′ 約 0.01–0.1、作用點在船舯後 0.1–0.3 L、h/T → 1 時數倍放大)選定,並在程式中集中於
  `BankEffectCoefficients`。**取得原文係數、模型試驗或引水人/船長在麥寮航道的經驗後必須校正**;情境 `banks.scale` 可先整體調整。
- 只有情境有 `banks` 區段時才計算;沒有此區段的情境(現有全部 `data/scenarios/*.yaml`)雜湊、狀態與事件與舊版逐位元相同。

| 項目 | 本模型 | 狀態 |
|---|---|---|
| 方程式結構(V²、加權距離、h/T、螺槳項) | Lataire & Vantorre / AMC 型回歸 | 依文獻結構(定性) |
| 係數 c_lead、c_trail、作用點、a_Y、a_N、C_YP | 第 4 節 | **估計**,待校正 |
| 影響寬度 y_inf = 5B(1 + 16 Fnh⁸) | AMC 影響寬度概念 | 形式參考、k = 5 為**估計** |
| 斜坡岸等效位置(半深處)、淹沒岸倍率 | 第 3.2 節 | **假設** |
| 岸壁幾何(麥寮航道) | `data/areas/mailiao-channel-banks.json` | **示意**,非浚深圖/ENC |

## 2. 情境與資料格式

### 2.1 情境 `banks`(`scenario.schema.json` `properties.banks`、`$defs.bankDefinition`)

```yaml
banks:
  file: data/areas/mailiao-channel-banks.json   # 選填:岸壁檔(相對專案根目錄或情境檔目錄);載入時內嵌為 definitions
  ids: [MLC-N, MLC-S]                           # 選填:檔案中的 banks[].id(省略 = 全部)
  scale: 1.0                                    # 選填:整體倍率(預設 1;0 = 只顯示距離不施力)
  lines:                                        # 選填:情境內直接定義(與檔案合併,lines 在前)
    - id: QW1
      name: 內港護岸
      type: vertical                            # vertical(預設)| sloped
      waterSide: right                          # 沿折線方向看水域在 right(預設)| left
      line: [ { lat: 23.8061, lon: 120.1488 }, { lat: 23.8080, lon: 120.1646 } ]   # 或 { x, y }(本地 ENU,m)
    - id: SL1
      type: sloped
      slope: 5                                  # 1:5(水平/垂直);sloped 必填
      topDepth_m: 5                             # 岸頂/淺灘水深(預設 0 = 露出水面)
      channelDepth_m: 12.5                      # 岸腳(航道)水深 = 適用水深(省略 = 自船所在水深)
      waterSide: left
      line: [ { x: -200, y: -3000 }, { x: -200, y: 3000 } ]
```

- `vertical`:折線即岸壁面(碼頭、護岸、防波堤內側)。`sloped`:折線為**坡腳**(航道底邊線),陸側為邊坡。
- 結構檢查(`ScenarioBankLoader.CheckStructure`,情境載入時為 error):至少一段岸壁、id 不重複、折線 ≥ 2 點且座標可解析、`waterSide` left|right、
  `type` vertical|sloped、sloped 需要 `slope > 0`、`topDepth_m ≥ 0`、`channelDepth_m > topDepth_m`、`scale ≥ 0`;`file` 找不到或 `ids` 不存在時載入失敗。
- `file` 於 `ScenarioLoader.FromYaml` 解析(與 `berth.file` 相同),結果寫入 `banks.definitions`;紀錄 header 的情境 JSON 因此含完整幾何,重播不讀檔。

### 2.2 岸壁檔(`data/areas/*.json`)

```json
{ "id": "mailiao-channel-banks", "description": "…示意…", "source": "示意(…)",
  "banks": [ { "id": "MLC-N", "name": "…", "type": "sloped", "slope": 5, "topDepth_m": 5, "channelDepth_m": 12.5, "waterSide": "right",
               "line": [ { "lat": 23.780885, "lon": 120.09924 }, … ] } ] }
```

元素格式同 `bankDefinition`;檔案頂層 `source` 補到未標來源的岸壁。雷達服務讀同資料夾時只取 `land`,忽略 `banks`。

`mailiao-channel-banks.json`(**示意**):以 F03/E07 航線 WP1→WP2→WP3→BERTH 為中心線,假設浚深底寬 250 m(兩側各 125 m 為坡腳)、邊坡 1:5、航道水深 12.5 m
(與 F03 `zones.channel` 相同)、坡頂外淺灘 5 m;`MLC-N`(北側邊坡 WP1–WP3)、`MLC-S`(南側邊坡 WP1–BERTH)、`MLC-N-WALL`(WP3 以東北側垂直護岸,終止於
`mailiao-berth.json` 碼頭線之前)。斜坡岸的等效垂直岸壁在坡腳向陸側 5·(12.5 − 5)/2 = 18.75 m(中心線 143.75 m),在航道中線上 FSB1 不受岸壁力
(影響寬度約 90 m)。取得港方浚深圖/ENC 後取代。

## 3. 幾何:加權距離參數

### 3.1 船體座標與岸側

船體座標 x 向艏、y 向右舷、原點船舯(與 MMG、`BodyForce` 相同);岸壁線段端點換成船體座標 (x_a, y_a)–(x_b, y_b)。
每段的陸側單位法向量 n_land(沿折線方向、水域在 right → 陸側在左)決定岸在哪一舷:σ = sign(n_land · ŷ_body),ŷ_body(ENU)= (cos ψ, −sin ψ);σ = +1 為右舷岸。
船到岸的「橫向距離」y_s(x) = σ·y(x)(沿船長位置 x 處、垂直於船中心線),船在水域側時為正。

### 3.2 等效垂直岸壁與岸型

| 岸型 | 等效岸壁位置(自折線向陸側) | 強度倍率 φ |
|---|---|---|
| vertical,`topDepth_m = 0` | 0 | 1 |
| sloped 1:n,岸頂水深 h_top | n·(h_c − h_top)/2(邊坡上岸腳與岸頂之間的半深處) | (h_c − h_top)/h_c |
| 淹沒岸(h_top > 0) | 同上(vertical 時 0) | (h_c − h_top)/h_c(h_top ≥ h_c 時 0 = 無效應) |

h_c = `channelDepth_m`,省略時為自船所在水深 h。y_eq(x) = y_s(x) + 偏移。**假設**:以半深處為等效位置、以被阻擋的水深比例為強度,
定性反映「緩坡岸與淹沒岸的岸壁效應較弱」(Lataire 等人對斜坡岸與淹沒岸試驗的結論);定量待校正。

### 3.3 加權距離(y_inf 型)

$$
q(y) = \beta(y)\cdot\max\!\left(0,\ \frac{1}{\max(y,\ y_{\min})} - \frac{1}{y_\infty}\right),\qquad
\beta(y) = \operatorname{clamp}\!\left(\frac{y + B/2}{B},\ 0,\ 1\right),\qquad w(x) = 1 - \frac{|x|}{L}\ \ (|x| \le L)
$$

$$
D_{\text{fore}} = \frac{B}{2}\cdot\frac{2}{L}\int_{0}^{L} w(x)\sum_{\text{段}} \sigma\,\varphi\,q\big(y_{eq}(x)\big)\,dx,\qquad
D_{\text{aft}} = \frac{B}{2}\cdot\frac{2}{L}\int_{-L}^{0}(\cdots)\,dx,\qquad D = \frac{D_{\text{fore}} + D_{\text{aft}}}{2}
$$

- 積分變數是**船體 x**(沿船中心線的投影長度):與船平行的岸全長計入,斜交的岸依 |cos α| 折減,與船垂直的岸(正前方)不產生橫向力。
- 窗 [−L, +L]、三角權重:岸壁起訖點進出窗口時權重為 0;q(y) 對 y 連續(見下方 y_min 與 β),因此沿岸移動、橫越岸線、轉向時力都**連續**(無階躍;
  測試以沿岸 0.1 m、橫越岸線 0.01 m 步進驗證,另以引擎實跑「東流把船推過淹沒岸」逐步檢查)。積分以每段至多 0.1 L 的 3 點 Gauss–Legendre 計算,
  分段點在 x = 0(前後半)與窗口邊界。
- 長直平行岸(距船舯 y):D_fore = D_aft = D = (B/2)(1/y − 1/y_inf)(解析值,測試比對)。兩岸等距(運河中線)D = 0;偏向一岸時 D 的符號指向較近的岸。
- y_inf = k·B·(1 + 16 Fnh⁸),k = 5,Fnh = V/√(g h)(上限 0.95):岸距船舯超過 y_inf 時該處貢獻**恰為 0**。FSB1(B 18 m)在 Fnh < 0.5 時約 90–96 m(5 B),
  Fnh 接近 1 時可達數倍船長。
- y_min = 0.6 B(船側距等效岸壁 0.1 B):B/2 ≤ y_eq < y_min 時以 y_min 計(外推限制;實際已接近觸岸)。
- **船寬跨越等效岸壁線**(|y_eq| < B/2):淹沒岸(`topDepth_m` > T)與斜坡岸上方船可合法浮在淺灘/邊坡上,此時再乘 β = 船寬在等效岸壁水域側的比例,
  自 y_eq = B/2 的 q(y_min) 線性降到 y_eq = −B/2 的 0:船舯恰在等效岸壁線上時為最大值的一半,整個船寬都在陸側(y_eq ≤ −B/2)時不計此岸。
  因此不會在船已越過岸線時以最大吸力把船繼續拉向淺灘,也沒有舊版「越過 −B 時由最大值跳到 0」的階躍。
- **被陸地隔開的岸**:同一規則也處理薄堤/突堤/板樁堤的背面與被陸地隔開的另一航段。船在近面的水域側(y_eq,near ≥ B/2)時,背面的
  y_eq,far = −(y_near + t) ≤ −(B/2 + t) < −B/2(t = 堤厚 > 0),β = 0,不計入也不顯示為另一舷的岸(`port_m`/`starboard_m`);不依固定的 −B 門檻,
  任何堤厚皆然。各自的水域都朝向船的兩岸(運河、航道兩側)照常相加。
- 仍未處理的遮蔽:同一舷**兩道都面向船**的岸(例如資料把碼頭線與其後方邊坡同時定義)會相加,岸壁資料不要重疊定義同一段岸。
- 淺灘上方的反向效應(船整個在淺灘上、深水航道在旁)不在本模型範圍:β = 0 後只剩 zones 水深造成的淺水效應。

## 4. 力與力矩

$$
F_Y = 1 + a_Y\frac{T}{h - T},\qquad F_N = 1 + a_N\frac{T}{h - T}\qquad (h/T \ge 1.05;\ 深水/無水深時 = 1)
$$

正俥(u ≥ 0)時前導半 = 船艏半(D_lead = D_fore)、後隨半 = 船艉半;倒退時對調,且 u² 項的力矩取負號(前導端成為船艉):

$$
Y_B = s\,F_Y\,\tfrac12\rho L T\Big[u^2\big(c_{lead}D_{lead} + c_{trail}D_{trail}\big) + (nD_P)^2\,C_{YP}\,D_{\text{aft}}\Big]
$$

$$
N_B = s\,F_N\,\tfrac12\rho L^2 T\Big[\pm u^2\big(\ell_{lead}c_{lead}D_{lead} + \ell_{trail}c_{trail}D_{trail}\big) + (nD_P)^2\,\ell_P\,C_{YP}\,D_{\text{aft}}\Big]
$$

s = `banks.scale`;u = 船體縱向對水速度(m/s);n = 步首實際螺槳轉速(rps,**只計正車**);D_P = 螺槳直徑(particulars `propeller.diameter_m`,缺資料時不計螺槳項);
L = 垂標間長、B = 型寬、T = 該裝載平均吃水、ρ = 1025 kg/m³。X 方向(岸壁/運河阻力增加)不計。

| 係數(`BankEffectCoefficients`) | 值 | 意義與依據 |
|---|---|---|
| c_lead `CYLead` | −0.005 | 前導半(船艏)艏部壓力墊:把船艏推離岸。**估計** |
| c_trail `CYTrail` | 0.045 | 後隨半(船艉)吸力。c_lead + c_trail = C_Y = 0.040(平行岸 Y′ = C_Y·F_Y·D)。**估計**(文獻 Y′ 量級 0.01–0.1) |
| ℓ_lead `LeverLead` / ℓ_trail `LeverTrail` | +0.30 / −0.20(× L) | 兩半合力作用點;平行岸 C_N = −(ℓ_lead c_lead + ℓ_trail c_trail) = 0.0105,合力作用點在船舯後 0.26 L(艏離岸)。**估計** |
| C_YP `CYP`、ℓ_P `LeverP` | 0.004、−0.40 | 螺槳載荷使艉部吸力與艏離岸力矩增大(Lataire 等人的定性結論),以 ½ρLT(nD)² 無因次化,乘船艉半 D_aft。**估計**:FSB1 SAH 80 rpm、6 kn 時橫向力約增加 25 % |
| a_Y / a_N | 0.40 / 0.30 | h/T 修正:h/T 1.5 → ×1.8、1.2 → ×3.0、1.1 → ×5.0(相對深水)。**估計**(文獻:h/T 由 1.5 降到 1.1 岸壁力約增 2–3 倍) |
| `MinDepthRatio` | 1.05 | h/T 外推下限 |
| `InfluenceWidthB` | 5 | y_inf = 5B(1 + 16 Fnh⁸) |
| `MinClearanceB` | 0.1 | y_min = (0.5 + 0.1) B |
| `WindowL` | 1.0 | 加權窗半寬 |

**慣例與正負號**:與 MMG/`BodyForce` 相同(Y 右舷正、N 艏向右正)。右舷岸 → D > 0 → Y_B > 0(吸向右舷岸)、N_B < 0(艏向左 = 離岸);左舷岸鏡像對稱(測試逐位比對 |Y|、|N|)。
推導出的行為:平行岸、舵正中時船身吸向岸、船艏偏離岸(sheer away);只有船艉半在岸旁(岸壁到船舯為止,駛離岸壁)時吸力集中在艉部;岸壁自船舯開始(船艏先進入)時
艏部壓力墊使合力暫時為離岸方向。

**量級**(FSB1 滿載、6 kn、SAH 80 rpm、h/T 2.19、右舷 32 m 垂直岸壁 = 船側距岸 23 m):Y_B = 33.5 kN、N_B = −903 kN·m(作用點船舯後 27 m)。
F03 示意航道(北側坡腳 30 m、7.5 kn、自動舵):Y_B = −13.8 kN、N_B = +363 kN·m,自動舵需多約 1.6° 左舵抵銷,船偏離北岸後漸減為 0。

## 5. 狀態(`state.schema.json` `properties.bank`;C# 產生,Python 不輸出)

情境有 `banks` 區段且動力學接受外力(MMG、暫代模型)時輸出;數值為該步步末的計算結果。

```json
"bank": {
  "Y_kN": 33.51, "N_kNm": -903.1, "side": "starboard", "parameter": 0.1813,
  "distance_m": 32.0, "clearance_m": 23.0, "nearestSide": "starboard", "bankId": "E1",
  "starboard_m": 32.0, "depthRatio": 2.193, "influence_m": 90.05
}
```

F03 示意航道(左舷北側邊坡):

```json
"bank": { "Y_kN": -13.80, "N_kNm": 362.8, "side": "port", "parameter": -0.0479, "distance_m": 50.15, "clearance_m": 41.15,
          "nearestSide": "port", "bankId": "MLC-N", "port_m": 50.15, "depthRatio": 2.193, "influence_m": 90.31 }
```

| 欄位 | 意義 |
|---|---|
| `Y_kN` / `N_kNm` | 岸壁橫向力(右舷正)/ 艏搖力矩(艏向右正) |
| `side` | 岸壁橫向力 `Y_kN` 指向的舷側(Y 的正負;\|Y\| < 1e-6 N 時 `none`,例如 `scale: 0`)。**不等於岸在哪一舷**:船艏先進入岸壁時艏部壓力墊使力推離岸(岸在右舷、`side` = `port`) |
| `parameter` | 加權距離參數 D(無因次;第 3.3 節;正 = 右舷岸較近、負 = 左舷岸較近) |
| `distance_m` / `clearance_m` | 船舯(x = 0)至最近等效岸壁的橫向距離 / 船側至等效岸壁(− B/2);影響寬度內無岸壁時省略 |
| `nearestSide`、`bankId` | 最近岸壁的舷側與 id |
| `port_m` / `starboard_m` | 左/右舷最近等效岸壁距離(船舯;影響寬度內無時省略) |
| `depthRatio` | h/T(深水時省略) |
| `influence_m` | 影響寬度 y_inf |

教官站/操船台畫岸壁力箭頭時以 `Y_kN` 的正負(= `side`)決定方向、`N_kNm` 決定艏搖,岸的位置用 `nearestSide`/`bankId`/`clearance_m`(網頁端尚未實作)。
船舯在等效岸壁線上或已跨越時 `distance_m` 為 0、`clearance_m` 為負(船側已越過等效岸壁線);整個船寬在陸側時該岸不再列出。

船艏先進入右舷岸壁(岸壁只在船舯前方,FSB1 滿載、6 kn、右舷 25 m):

```json
"bank": { "Y_kN": -4.52, "N_kNm": -125.8, "side": "port", "parameter": 0.1300,
          "distance_m": 25.0, "clearance_m": 16.0, "nearestSide": "starboard", "bankId": "FWD", "starboard_m": 25.0, "depthRatio": 2.193, "influence_m": 90.04 }
```

## 6. 引擎接線與確定性

- 介面:`BankEffectModel : IExternalForceField`,以 RK4 每個階段的狀態重算(位置、航向、u 隨階段改變);螺槳轉速取步首實際值(`PreStep(controls.C1.Rpm)`,四個階段共用,
  與拖船出力整步固定相同處理)。外力模組(拖船/錨/泊位)也使用時以 `SumForceField` 相加;只有外力模組時仍直接傳 `_external`(與舊版相同的運算順序)。
- 動力學不接受外力(`SupportsExternalForces = false`)時岸壁效應不啟用(`Active = false`,不施力、不輸出 state.bank、不追加雜湊)。
- 每步時序:… 致動器 → 外力 PreStep、岸壁 PreStep → RK4(含外力場)→ tick++ → 外力 PostStep → **岸壁 UpdateLast**(步末位置的水深、步末實際轉速)→ 擱淺 → … → 雜湊。
- **雜湊**:啟用時於雜湊鏈最後(engineStartFailure 區塊之後)追加 9 個 int64:標記 `BANKEFCT`、岸壁段數、步首轉速、D、D_fore、D_aft、Y、N、y_inf。
  未使用 `banks` 的情境不追加(`BankEffectTests.WithoutBanksHashAndStateAreUnchanged` 以獨立的舊版雜湊實作逐步比對,MMG 與暫代模型皆然)。
  影響寬度外的岸壁力恰為 0、運動與無岸者位元相同,但雜湊因追加區塊而不同(`FarBankLeavesMotionUnchangedButHashesItsBlock`)。
- **快照**:模型為準靜態純函數,沒有跨步狀態(岸壁幾何來自情境,力只取決於狀態向量、水深與轉速),`EngineSnapshot` 不需新欄位、Version 不變;
  還原時依情境重建岸壁並以還原後的狀態重算步末結果,續跑雜湊與連續執行逐位元相同(`SnapshotRestoreContinuesBitIdentical`,含拖船 + 岸壁與只有岸壁兩種)。
- **紀錄/重播**:`banks.file` 載入時內嵌,header 含 `definitions`;重播雜湊與事件序列一致(`RecordReplayReproducesHashWithBanksFromFile`)。

## 7. 適用範圍與外推限制

| 項目 | 適用範圍(文獻試驗常見範圍) | 超出時 |
|---|---|---|
| h/T | 約 1.1–2.0(淺水)至深水 | h/T < 1.05 以 1.05 計;深水時 F = 1(深水仍有岸壁吸力) |
| 船側至岸壁 | 約 0.1 B–5 B | < 0.1 B 以 0.1 B 計(已接近觸岸,觸岸本身不模擬:碼頭硬接觸只在 `berth`);船寬跨越等效岸壁線時依 β 線性淡出、整個船寬在陸側為 0(第 3.3 節);> y_inf 為 0 |
| 漂角/與岸夾角 | 小角度(近平行航行) | 以投影長度 \|cos α\| 折減;大角度接近(正面駛向岸)不是本模型範圍 |
| 速度 | 低至中速、Fnh < 約 0.7 | Fnh 上限 0.95(影響寬度);跨臨界淺水現象不模擬 |
| 倒退 | 未經驗證 | 吸力仍 ∝ u²,力矩以前導/後隨對調;螺槳倒車項不計 |
| 岸型 | 垂直岸、1:1–1:5 左右的斜坡岸、淹沒岸 | 半深處等效位置與強度倍率為假設 |
| 船型 | 方塊係數 0.7–0.85 的商船(兩船 0.70–0.74) | 係數為估計,未分船型 |

## 8. 測試(`tests/SimCore.Tests/BankEffectTests.cs`)

| 測試 | 內容 |
|---|---|
| `StarboardBankSucksTowardBankAndPushesBowAway` | 右舷岸:Y > 0、N < 0、D > 0、距離與 state 欄位、量級(數十 kN) |
| `PortAndStarboardAreMirrorSymmetric` | 左右岸鏡像:Y、N 反號等值;運河中線 Y = N = 0;偏向一岸時吸向較近者 |
| `ScalesWithSpeedSquaredAtZeroRpm` | 深水 Y、N ∝ u²(比值 4);u = 0 時 0;倒退吸力同、力矩反號 |
| `IncreasesAsDistanceDecreasesAndVanishesBeyondInfluenceWidth` | 距離遞減 → 力遞增;y_inf 外恰為 0;0.1 B 下限 |
| `StrongerInShallowerWater` | h/T 10.5 → 1.14 單調增加;h/T 1.05 下限 |
| `MatchesAnalyticValueForLongStraightWall` | 長直岸與第 3.3、4 節解析式相同(含螺槳項) |
| `PropellerLoadingIncreasesEffectAheadOnly` | 正車增加吸力與艏離岸力矩;倒車不計;停船踢車仍有吸力 |
| `SlopedAndSubmergedBanksUseEquivalentWall` | 斜坡岸 = 半深處垂直岸;淹沒岸倍率;岸頂深於水深時 0 |
| `BankEndsFadeContinuouslyAndObliqueApproachKeepsSigns` | 岸壁止於船舯 / 始於船舯;通過端點後 0;0.1 m 步進無階躍;斜向仍吸向岸、艏離岸;垂直岸無橫向力 |
| `ScaleMultipliesAndZeroDisablesForce` | `banks.scale` 倍率;0 時不施力但顯示距離 |
| `ScenarioLoadsFileAndValidates` | 岸壁檔解析、ids、JSON 往返;8 種結構錯誤 |
| `WithoutBanksHashAndStateAreUnchanged` | 無 banks:雜湊與舊版逐步相同、無 state.bank;不支援外力的模型不啟用 |
| `FarBankLeavesMotionUnchangedButHashesItsBlock` | 影響寬度外:運動位元相同、雜湊追加區塊 |
| `MmgShipIsDrawnTowardBankAndBowSheersAway` | MMG:舵正中時艏離岸偏轉(r 較無岸者偏左)、狀態 JSON 欄位 |
| `SnapshotRestoreContinuesBitIdentical` | 拖船 + 岸壁、只有岸壁:快照 JSON 往返後續跑雜湊/運動/state.bank 相同 |
| `RecordReplayReproducesHashWithBanksFromFile` | F03 + 示意岸壁檔:header 內嵌 3 段岸壁,重播雜湊與事件一致 |
| `F03ChannelWithIllustrativeBanksStaysBoundedUnderAutopilot` | F03 航道(北側坡腳 30 m、7.5 kn、自動舵)300 s:數值有限、ROT/舵角/力有界、航向誤差 < 3°、自動舵需額外舵角抵銷艏離岸力矩 |
| `ContractsDescribeBanksAndBankState` | state/scenario schema 欄位、state JSON 往返 |

`tests/SimCore.Tests/BankEffectReviewTests.cs`(審查發現回歸):

| 測試 | 內容 |
|---|---|
| `CrossingSubmergedVerticalBankOntoShoalIsContinuousAndFadesOut` / `CrossingSlopedBankIsContinuousAndFadesOut` | 船自水域側 40 m 橫越淹沒岸(top 8 m)/斜坡岸 1:5 到陸側 40 m,0.01 m 步進:Y、N 單步變化 < 0.2 % 最大值、不反向、不超過 y_min 上限、跨越時單調遞減、船舯在線上為一半、整個船寬在陸側為 0 |
| `ShipSideInsideMinimumClearanceKeepsSaturatedValue` | 船側距等效岸壁 0–0.1 B 仍以 y_min 計 |
| `EngineDriftingAcrossSubmergedBankHasNoForceJumps` | 暫代模型與 MMG:東流 1.5 kn 把船推過淹沒岸到淺灘上方,步末 Y 單步變化 < 0.5 kN(舊版約 5 kN)、最後為 0 |
| `ThinPierBackFaceDoesNotReverseNearFaceSuction` | 堤厚 1、5、9 m 的兩面堤:與只有近面時逐值相同、背面不顯示為左舷岸;堤另一側只受遠面吸力 |
| `CanalBetweenTwoWallsStillSumsBothSides` | 兩岸水域都朝向船時仍相加 |
| `StateSideFollowsLateralForceDirection` | 船艏先進入右舷岸壁:Y < 0、`side` port、`nearestSide` starboard、D > 0;`scale: 0` 時 `side` none |

## 9. 已知限制與後續

- **係數未校正**(第 1、4 節):優先取得 Lataire/Vantorre 原文回歸係數或 Flanders Hydraulics 的同型船試驗,並以引水人在麥寮航道的經驗(所需舵角、偏航趨勢)檢查量級。
- **Python 參考實作尚未實作**岸壁效應(`src/Tools.Calibration` 忽略 `banks`);C#↔Python 一致性只涵蓋無岸壁情境。
- 不含:運河阻力增加(X 方向)、岸壁造成的額外 sinkage/trim(squat 仍為 ICORELS)、船間交互作用(會船/追越;第 458 行同類經驗模型,另案)、
  非定常效應(岸壁突變時的記憶效應)、岸壁對螺槳倒車流的影響、橫搖。
- 加權窗 [−L, +L] 與三角權重、前後半分配為本案選定的離散化(Lataire 2008 對不規則岸壁使用沿船長的加權),未經試驗驗證。
- 只有直線段折線;彎道岸以多段近似。水深取自船所在位置(`zones` 水深),不隨岸壁剖面變化。
- 情境 `data/scenarios/*.yaml` 尚無使用 `banks` 者(F03 類航道情境可加 `banks: { file: data/areas/mailiao-channel-banks.json }`,待示意岸壁經船長/引水人確認後啟用);
  網頁端尚未顯示 `state.bank`。
