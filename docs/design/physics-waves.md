# 浪與平均波浪漂移力(wave drift force)— 設計與契約

> 規劃書依據:第 6.2 節運動方程式的環境外力 W 項(風/浪)與「浪:浪譜/浪漂力」;R2「風/流/潮/浪」、R3「惡劣天候」;第 7.3 節視景海面;第 14 章確定性。
> 程式:`src/SimCore/Physics/External/WaveDrift.cs`(`WaveDriftModel`、`WaveDriftCoefficients`、`WaveDriftResult`、`WaveDriftState`)、
> `src/SimCore/Scenario/Scenario.cs`(`ScenarioWaves`、`ScenarioEnvironment.Waves`、結構檢查)、`src/SimCore/Engine/SimulationEngine.cs`(接線、雜湊、狀態)、
> `src/SimCore/Contracts/OwnShipState.cs`(`waves` 欄位)、`src/Visual.Shared/Env/EnvironmentMapper.cs`(視景波高/浪向);
> Python 參考實作 `src/Tools.Calibration/simosa_brm/external.py`(`WaveDriftModel`、`normalise_waves`)、`scenario.py`(`waves_issues`)、`mmg.py`(接線);
> 測試 `tests/SimCore.Tests/WaveDriftTests.cs`、`tests/Visual.Shared.Tests/OptionsEnvironmentAndClientTests.cs`、`src/Tools.Calibration/tests/test_waves.py`;
> 跨語言夾具 `tests/SimCore.Tests/Golden/waves_force_table.csv`、`waves_drift_run.{yaml,csv}`(`export_waves_reference.py` 產生)。
> 一階運動(升沉/縱搖;第 11 節,2026-11-11):`src/SimCore/Physics/SeakeepingModel.cs`、`Scenario.cs`(`ScenarioSeakeeping`)、`OwnShipState.cs`(`waveMotion`);
> Python `simosa_brm/seakeeping.py`;測試 `tests/SimCore.Tests/SeakeepingModelTests.cs`、`src/Tools.Calibration/tests/test_seakeeping.py`;
> 夾具 `tests/SimCore.Tests/Golden/seakeeping_table.csv`、`seakeeping_run.{yaml,csv}`(`export_seakeeping_reference.py` 產生)。

## 1. 總覽與資料來源聲明

- 範圍:3 自由度(surge/sway/yaw)模型只做**平均二階波浪漂移力**(mean second-order drift force)——對操船有意義的定常分量:迎浪阻力增加(added resistance)、
  橫浪側向漂移、斜浪的艏搖力矩(靜止船趨向正橫受浪)。**不做**慢變漂移振盪、波浪對舵效/螺槳的瞬時影響、浪流交互作用;一階振盪運動不進 3 自由度方程式——
  橫搖另由解耦模型輸出(`ship.roll`,physics-roll.md),升沉與縱搖另由線性、只輸出的模型輸出(`ship.seakeeping`,**第 11 節**),兩者皆不回饋本節的漂移力與 3 自由度運動。
- 公式結構:Faltinsen 等(1980)短波漸近式——沿水線「迎浪側」積分反射壓力 ∝ ½ρgζa²·sin²(入射角),加上遭遇頻率的速度修正——推廣到不規則海:
  以 Hs 的譜零階矩 m0 = Hs²/16 代替 ζa²/2,每個浪譜成分再乘以兩個衰減:
  (1) **Ursell(1947)垂直障板反射係數平方** α_d(x) = π²I1²(x)/(π²I1²(x) + K1²(x)),x = 1.5·k·T(Fujii & Takahashi 1975 的吃水修正;NMRI/ITTC 的
  added resistance 反射項亦用此式):短波全反射,長波自吃水下方通過而漂移力趨近零;
  (2) **λ/L 截止** D_L = 1/(1 + (λ/2L)⁴):短波漸近式在 λ ≳ 2L 不再適用,長波時力趨近零。
  速度修正以遭遇頻率比 (ω_e/ω)² = (1 + U_w/c)²(U_w 為朝浪源的對水速度分量、c 為該成分的相速度)表示,深水小速度時即 Faltinsen 的 1 + 2ωU/g。
- **數值係數與水線形狀為文獻量級的估計值**:兩船沒有耐航/漂移力模型試驗,本專案離線開發(CLAUDE.md:不連網際網路取資料),係數集中於
  `WaveDriftCoefficients`(第 4 節);取得模型試驗或實船漂移觀測後應校正。沒有 `environment.waves` 的情境零力、狀態與雜湊與舊版逐位元相同。

| 項目 | 本模型 | 狀態 |
|---|---|---|
| 方程式結構(水線反射積分、∝ ρ g Hs²、譜加權) | Faltinsen 1980 短波漸近 + 不規則海譜積分 | 依文獻結構 |
| 吃水/波長衰減 α_d(1.5·k·T) | Ursell 1947 / Fujii–Takahashi 1975 | 依文獻公式(A&S 多項式近似 Bessel) |
| λ/L 截止 D_L、截止長度 2L | 第 3.3 節 | **假設**(使長波趨近零) |
| 遭遇頻率修正 (1 + U_w/c)² | 第 3.4 節 | 簡化(與 Faltinsen 的 1 + 2ωU/g 一致到一階) |
| 水線形狀(平行中體 + 拋物線端 + 寬 0.3B 的艏艉平面) | 第 3.2 節 | **假設**(鈍度係數 B_f ≈ 0.34,落在商船 0.3–0.7 區間) |
| 浪譜(PM / JONSWAP)離散 40 成分 | 第 3.1 節 | 標準譜形;只影響加權 |
| 淺水波長(有限水深色散關係) | 第 3.1 節 | 標準公式;未含淺水波高變化(shoaling)與碎波 |

## 2. 情境 `environment.waves`(`scenario.schema.json` `properties.environment.properties.waves`)

```yaml
environment:
  waterDepth: 20
  waves:
    significantHeight_m: 2.0      # 有義波高 Hs(≥ 0;0 = 有區段但零力,state.waves 仍輸出)
    peakPeriod_s: 7.0             # 譜峰週期 Tp(> 0)
    direction_deg: 45             # 浪來向(0–360,氣象慣例,與風相同;45 = 自東北來)
    spectrum: jonswap             # 選填:pm(預設)| jonswap
    gamma: 3.3                    # 選填:JONSWAP 峰度係數(≥ 1;預設 3.3;pm 時忽略)
```

- 結構檢查(`Scenario.StructuralIssues` 與 Python `scenario.waves_issues`,路徑 `environment.waves.*`、訊息相同):`significantHeight_m` 為 ≥ 0 的有限數值、
  `peakPeriod_s > 0`、`direction_deg` 在 0–360、`spectrum` 為 `pm | jonswap`(不分大小寫)、`gamma ≥ 1`。缺 `significantHeight_m` / `peakPeriod_s` / `direction_deg` 皆為錯誤。
- 無 `waves` 區段:不建立模型,零力、`state.waves` 省略、雜湊不追加區塊(所有既有情境、夾具與黃金測試不變)。
- 練習中可由 `setEnvironment` 的 `waves`(教官指令或時間表動作)變更或關閉,Hs 可漸變(第 6.1 節);`reset` / 載入情境回到情境設定。

## 3. 模型

### 3.1 浪譜成分、波長與相速度

- 譜峰角頻率 ω_p = 2π/Tp。N = 40 個成分 ω_i = ω_p·ρ_i,ρ_i 在 [0.5, 3.0] 等分取中點。權重為譜形:
  PM:f(ρ) = ρ⁻⁵·exp(−1.25·ρ⁻⁴);JONSWAP:再乘 γ^{exp(−(ρ−1)²/(2σ²))},σ = 0.07(ρ ≤ 1)/ 0.09;正規化 Σw_i = 1(Hs 的能量由 m0 = Hs²/16 給,譜形只決定加權)。
- 波數 k_i:深水 k0 = ω²/g;以自船位置的**有效水深** h(基底 + 潮高)一律用有限水深色散關係 ω² = g·k·tanh(k·h) 求解(深水時收斂回 k0;不以 h = λ0/2 切換分支,避免越過該等深線時 0.37 % 的跳變)——自 k0 起固定 12 次 Newton
  迭代(無收斂判斷,兩種語言逐位元相同;殘差 < 1e-9),h ≤ 0 或無限大視為深水。λ_i = 2π/k_i、相速度 c_i = ω_i/k_i。
- `state.waves.wavelength_m` = 峰值成分 ω_p 在目前水深的波長。

### 3.2 水線幾何與反射積分

船體座標(x 向艏、y 向右舷、原點船舯)。半寬 y(x):|x| ≤ Lp/2 為 B/2;進流/去流段 (B/2)·(e + (1 − e)(1 − s²)),s = (|x| − Lp/2)/L_e;艏艉另有寬 e·B 的平面。
e = `EndFullness` = 0.3;L_e = 1.5(1 − Cw)·L/(1 − e) 夾在 [0.1L, 0.5L],Cw = (1 + 2Cb)/3(Cb 取裝載狀態的方塊係數,無資料時 0.75);Lp = L − 2L_e。
每舷 40 站離散為直線段。

浪的相對來向 χ = wrap(來向 − 航向)(0 = 迎浪、+90° = 自右舷來),d = (cos χ, sin χ) 為指向浪源的單位向量。對每個外法向 n、長度 dl、中點 (x_m, y_m) 的線段,
**只取迎浪側(n·d > 0)**:

- 力 f = (n·d)²·(−n)·dl(反射壓力把船推離浪源),力矩 x_m·f_y − y_m·f_x;
- G = Σ f = (G_x, G_y),G_n = Σ 力矩。艏艉平面只貢獻 G_x。

橫浪時 |G_y| ≈ 0.97 L(接近方箱的 L);迎浪時 |G_x|/B ≈ 0.34(Fujii–Takahashi 鈍度係數 B_f 的定義 (1/B)∫sin³β_w dl 即此積分)。

### 3.3 衰減與遭遇頻率:係數 K

K = Σ_i w_i · α_d(1.5·k_i·T) · D_L(λ_i/L) · max(0, 1 + U_w·k_i/ω_i)²

- α_d:Ursell 垂直障板反射係數平方,T 為裝載狀態的平均吃水(`draftMean_m`,無則最大吃水)。I1、K1 以 Abramowitz & Stegun 9.8.3/9.8.4、9.8.7/9.8.8 多項式近似
  (|誤差| ≲ 1e-7);x ≥ 3.75 以漸近式的比值 K1/(πI1) 計算,避免 e^x 溢位。
- D_L = 1/(1 + (λ/(2L))⁴):λ = L 時 0.94、λ = 2L 時 0.5、λ = 3L 時 0.16。
- U_w = u·cos χ + v·sin χ:朝浪源的對水速度分量(浪相對水;流不改變此項)。(1 + U_w/c)² = (ω_e/ω)²:迎浪前進遭遇頻率升高、力增大;順浪減小;
  順浪快於相速度時夾到 0。

### 3.4 力

F = ρ·g·Hs²/16 · K · (G_x, G_y, G_n),ρ = 1025 kg/m³、g = 9.80665 m/s²。單位 N、N·m,船體座標,直接加入 MMG 運動方程式右側(與拖船/岸壁相同的
`IExternalForceField` 路徑,RK4 每階段以該階段的狀態重算;純函數、無跨步狀態、無相位、無亂數)。

## 4. 係數(`WaveDriftCoefficients`;Python `WAVE_DEFAULTS`)

| 係數 | 值 | 意義 | 來源/狀態 |
|---|---|---|---|
| `EndFullness` e | 0.3 | 艏艉平面寬 e·B;水線端部半寬 e·B/2 | **估計**(球艏/方艉;使 B_f ≈ 0.34) |
| `DraftFactor` | 1.5 | α_d 的引數 1.5·k·T | Fujii & Takahashi 1975 |
| `CutoffWavelengthL` | 2.0 | D_L 的截止長度 2L | **假設** |
| `Components` / `OmegaMinRatio` / `OmegaMaxRatio` | 40 / 0.5 / 3.0 | 浪譜離散 | 數值設定(PM 在 [0.5, 3]ω_p 外能量 < 1 %) |
| `Stations` | 40 | 每舷水線站數 | 數值設定(80 站與 40 站差 < 0.1 %) |
| `DispersionIterations` | 12 | 色散關係 Newton 次數 | 數值設定(殘差 < 1e-9) |
| `Rho` | 1025 | 海水密度 | 與外力/岸壁相同 |

### 4.2 FSB 尺度量級檢查(FSB1 滿載 L 99 m、B 18 m、T 5.70 m、Cb 0.738;`WaveDriftTests.BeamSeasMagnitudeIsComparableToStrongWindAndScalesWithHs`)

| 浪況 | 狀態 | X(surge) | Y(sway) | N(yaw) | K |
|---|---|---|---|---|---|
| Hs 2 m、Tp 7 s(λ 76 m)自右舷正橫,深水 | 靜止 | 0 | **−17.6 t**(向左舷) | 0 | 0.71 |
| 同上,Tp 4 s(短波) | 靜止 | 0 | −24.6 t | 0 | 1.00 |
| 同上,Tp 12 s(深水 λ 225 m;50 m 水深 205 m ≈ 2.1 L) | 靜止 | 0 | −4.0 t | 0 | 0.16 |
| 同上,Tp 7 s,壓載(T 3.72 m) | 靜止 | 0 | −11.4 t | 0 | 0.46 |
| Hs 2 m、Tp 7 s 自 045° | 靜止 | −1.0 t | −9.1 t | −764 kN·m(艏向左,趨向正橫) | 0.71 |
| Hs 2 m、Tp 7 s 迎浪 | 靜止 / 10 kn | −1.1 t / −3.1 t | 0 | 0 | 0.71 / 1.97 |
| Hs 2 m、Tp 7 s 順浪 | 10 kn | +0.2 t | 0 | 0 | 0.11 |
| Hs 2 m、Tp 7 s 正橫,水深 8 m(λ 47 m) | 靜止 | 0 | −22 t | 0 | 0.90 |

對照風力(Blendermann,A_L 575 m²、C_Dt 0.7):正橫 25 kn 約 4.2 t、35 kn 約 8.1 t、45 kn 約 13 t。Hs 2 m 的風浪(約蒲福 7–8 級)橫向漂移力與
產生它的強風同量級、略大;迎浪阻力增加(1–3 t)與 25 kn 頂風阻力(約 2 t)同量級;045° 斜浪的艏搖力矩約為 6 kn 時 10° 舵力矩的 1/3。
文獻中橫浪漂移力無因次係數 F_y/(½ρgζa²L) 在 λ/L ≈ 0.5–0.8 常見 0.5–0.9,本模型 0.7(含水線形狀 0.97)落在其中;
迎浪 added resistance 的 B_f 0.34 介於貨櫃船(≈ 0.3)與油輪(≈ 0.6)之間。

## 5. 狀態(`state.schema.json` `properties.waves`;C# 與 Python 參考實作皆輸出)

只在情境有 `environment.waves` 且動力學接受外力時輸出:

| 欄位 | 單位 | 說明 |
|---|---|---|
| `significantHeight_m` | m | Hs(情境值,或 `setEnvironment waves` 變更後的目前值;Hs 漸變中為當下插值) |
| `peakPeriod_s` | s | Tp(情境值) |
| `direction_deg` | 度 | 來向(0–360;360 正規化為 0) |
| `wavelength_m` | m | 峰值波長(自船位置的有效水深;淺水時較短) |
| `spectrum` | — | `pm` / `jonswap` |
| `X_kN`、`Y_kN`、`N_kNm` | kN、kN·m | 步末狀態的平均漂移力(船體座標,X 向艏、Y 右舷、N 艏向右正) |

視景(`EnvironmentMapper`):有 `state.waves` 時海面波高取 `significantHeight_m`、浪向 = `direction_deg` + 180°(去向),取代由風速推得的海況波高與風向推得的浪向;
海況等級(Douglas)仍由風速/雷達環境決定。無 `waves` 時行為與舊版相同(Python/舊核心狀態亦然)。

雷達(`Sensors.Radar`;`radar-arpa.md` 第 2.1 節):情境 `environment.radar.seaState` 或教官指令未明示海況時,海雜波的海況由 `state.waves.significantHeight_m` 依 Douglas 海況表推估
(`VideoGrid.SeaStateFromWaveHeight`:0.1 / 0.5 / 1.25 / 2.5 / 4 / 6 / 9 m 分界),無 `waves` 時仍依真風速;明示時維持明示值。

儀表(`src/Instruments.Web`):操船台 conning 條在 `state.waves` 或 `state.shipInteraction` 存在時加一格 SEA 方塊(`components/sea-readout.ts`):Hs / Tp、來向與相對來向(艏向上的箭頭,與風相同慣例)、浪譜;
船間交互作用啟用時另列主要目標與側向力 / 艏搖力矩(t、t·m),`event` 為 true(超過門檻)時以警示色。`types/state.ts` `WaveState` / `ShipInteractionState`,`lib/state-parser.ts` 解析(型別錯誤記 issues;關閉浪後省略、不繼承前一幀)。

## 6. 引擎接線與確定性

- `WaveDriftModel : IExternalForceField`,RK4 每階段以該階段的狀態(航向、u、v)與步首環境取樣的水深計算;同一步四個階段水深相同,波數/衰減係數只在
  水深改變時重算(快取;潮汐使水深每步變動時每步重算一次)。
- 與其他外力的組合(`SimulationEngine.Step`):無浪時路徑與舊版完全相同(只有外力模組 → `_external`;只有岸壁 → `_bank`;兩者 → `SumForceField`);
  有浪時 (前者) + 浪,只有浪時直接取浪。Python `mmg.step` 的 `field` 同一順序相加。
- 動力學不接受外力(`SupportsExternalForces = false`)時不啟用(`Active = false`:不施力、不輸出 `state.waves`、不追加雜湊)。
- 每步時序:… → RK4(含外力場)→ tick++ → 潮高/流更新 → 外力 PostStep → 岸壁 UpdateLast → **浪 UpdateLast**(步末狀態與有效水深)→ 擱淺 → … → 雜湊。
- **雜湊**:啟用時於雜湊鏈最後(岸壁區塊之後)追加 8 個 int64:標記 `WAVEDRFT`、Hs、Tp、來向(弧度)、峰值波長、步末 X、Y、N。無 `waves` 的情境不追加
  (`WaveDriftTests.WithoutWavesOutputsAndMotionAreUnchanged`:Hs = 0 的區段力恰為 0、運動與無浪逐位元相同,但雜湊因區塊而不同)。
- **快照**:純函數、無跨步狀態,`EngineSnapshot` 不需新欄位;還原時依情境重建並以還原後的狀態重算步末結果,續跑雜湊與連續執行逐位元相同
  (`SnapshotRestoreContinuesBitIdentical`);紀錄/重播雜湊與事件序列一致(`RecordReplayReproducesHashWithWaves`)。
- 對稱性(`PortStarboardMirrorSymmetry`、`HeadAndFollowingSeasGiveNoSwayOrYaw`):來向鏡像且 v 反號時 X 相同、Y/N 反號;迎浪/順浪時 Y = N = 0。

### 6.1 練習中變更(`setEnvironment` args.waves;教官指令與時間表動作共用)

`command.schema.json` / `scenario.schema.json`(`timelineAction.setEnvironment`):

```yaml
- setEnvironment: { waves: { significantHeight_m: 2.5, peakPeriod_s: 8, direction_deg: 45, spectrum: jonswap }, ramp_s: 120 }   # 建立或更新
- setEnvironment: { waves: { direction_deg: 180 } }      # 部分更新:缺的欄位沿用目前值(Hs 取模型目前值);進行中的 Hs 漸變保留,附 ramp_s 也不建立漸變
- setEnvironment: { waves: null }                        # 關閉(significantHeight_m: 0 亦為關閉);ramp_s > 0 時 Hs 先漸變到 0 再移除
```

欄位語意:屬性**不存在**→ 沿用目前值;屬性存在但為 `null`、布林或非數值字串 → 整個 `waves` 拒絕(`commandRejected invalidWaves`;Python `invalidWaves`;時間表結構檢查同路徑),其他環境欄位照常套用。

- 語意(`SimulationEngine.ParseWavesArg` / `ApplyWaves`;Python `MMGShip.set_waves`):`waves` 缺省 = 不變;`null` 或 Hs = 0 = 關閉(模型移除:零力、`state.waves` 省略、
  雜湊不再追加區塊);物件 = 與目前設定合併後檢查(Hs ≥ 0 有限、Tp > 0、來向有限並正規化到 0–360、`spectrum` pm|jonswap、`gamma` ≥ 1;無浪時 `peakPeriod_s` 與
  `direction_deg` 必填),不合法 → `commandRejected {command: setEnvironment, reason: invalidWaves}`、浪不變,其他環境欄位照常套用。
- 漸變:`ramp_s > 0` 時只有 Hs 走線性漸變(通道 `RampChannel.WaveHeight`,`RampSet` 把插值寫在 `EnvironmentConditions.WaveHeightM`,引擎每步於漸變套用後
  `WaveDriftModel.SetSignificantHeight`;Tp / 來向 / 浪譜 / γ 立即生效 —— 模型以目前 Hs 重建,譜權重與水線幾何重算);完成發 `environmentRampDone {channel: waveHeight}`。
  自無浪加浪時自 0 起算;關閉時漸變到 0 完成後移除模型。Hs 只是力的強度 ∝ Hs²:以目前 Hs 重建模型與 `SetSignificantHeight` 逐位元相同。
- 事件:`environmentChange` 的 data 加 `waves`(原 args)與 `changes.waves {from, to}`(各為 `{significantHeight_m, peakPeriod_s, direction_deg, spectrum, gamma}` 或 null)。
- 確定性與紀錄:指令寫 `input` 行即可重播(`WaveEnvironmentCommandTests.RecordReplayReproducesHashWithWaveCommands`);雜湊透過既有浪區塊(Hs/Tp/來向)自然改變,
  無浪情境以指令在 tick 0 設定與情境相同的浪 → 運動與雜湊與情境自帶浪逐位元相同(`CommandAddsWavesToScenarioWithoutWavesBitIdenticalToScenarioWaves`)。
- 快照:`EngineSnapshot.Waves`(`WaveSnapshot {enabled, significantHeightM(當下值), peakPeriodS, directionDeg, spectrum, gamma}`)**只在變更後寫出**(情境原設定不寫,
  既有快照位元不變);還原時有此欄位依它重建(`enabled = false` = 已關閉),沒有則回到情境 `environment.waves`(倒帶到變更前即恢復情境浪);進行中的 Hs 漸變在
  `Timeline.Ramps`,漸變中途還原續跑逐位元相同(`SnapshotMidRampRestoresBitIdentical…`)。`Reset` / `loadScenario` 清除變更。
- 動力學不接受外力時(`Active = false`):設定照常記錄(快照/事件)但不施力、不輸出 `state.waves`、雜湊不追加(`UnsupportedDynamicsIgnoreWaveCommands…`)。
- 結構檢查(時間表動作;`Scenario.CheckSetEnvironmentAction` 與 Python `set_environment_action_issues`,路徑 `timeline[i].actions[j].setEnvironment.waves.*`、訊息與
  `environment.waves` 相同):`setEnvironment` 須為物件;`waves` 須為物件或 null;給了的欄位 Hs ≥ 0、Tp > 0、來向 0–360、spectrum pm|jonswap、gamma ≥ 1。
- Python 參考實作(`MMGShip.set_environment(waves=…)` / `set_waves`、`server.py` setEnvironment):同語意、立即生效(無漸變,`ramp_s` 忽略;ack detail 註明);不合法時
  ack `ok=false`、detail `拒絕(invalidWaves)`,其他欄位已套用。
- 教官站環境面板(`components/instructor/environment-panel.ts`):標題顯示目前 Hs / Tp / 來向(浪譜),Hs / Tp / 來向 / 浪譜「套用」送 `setEnvironment { waves }`(漸變秒數附
  `ramp_s`),「關閉」送 `{ waves: null }`;`lib/instructor-command.ts` `setWavesCommand` / `clearWavesCommand` / `waveArgs`。事件列(`lib/sim-event.ts`)顯示「浪 Hs … / 浪關閉」。

## 7. 適用範圍與外推限制

| 項目 | 適用範圍 | 超出時 |
|---|---|---|
| 波長 | 短波到 λ ≈ L(反射/繞射主導) | λ ≳ L 時實船的一階運動(縱搖/橫搖)會使漂移力出現峰值,本模型只單調衰減(低估 λ/L ≈ 1–1.5 的力) |
| Hs | 線性(∝ Hs²);無碎波、無甲板上浪 | 極端海況不在訓練範圍 |
| 速度 | |U_w| < c(遭遇頻率修正為二次) | 順浪快於相速度時力為 0;迎浪高速時修正偏大(Faltinsen 式本身為一階) |
| 水深 | 深水到 h ≈ T(波長以有限水深色散求得) | 不含淺水波高變化(shoaling/折射)與碎波;h → T 時反射係數以 kT 計(較保守) |
| 船型 | Cb 0.7–0.85、有球艏/方艉的商船 | 水線形狀為假設;細長船艏的迎浪力偏大 |
| 自由度 | 3(平均力) | 漂移力本身不含一階振盪;橫搖(`ship.roll`)與升沉/縱搖(`ship.seakeeping`,第 11 節)為另外的只輸出模型,不回饋 3 自由度 |

## 8. 測試

- C# `tests/SimCore.Tests/WaveDriftTests.cs`(29 項):鏡像對稱、迎浪/順浪 sway = 0、量級與 Hs²/長波/壓載、斜浪力矩方向與遭遇頻率修正、JONSWAP/PM、
  淺水波長(色散殘差 < 1e-9、深水收斂回 k0)、Bessel/Ursell 表值、無浪不變(含 Hs = 0 位元相同)、引擎施力與 `state.waves` JSON 往返、與拖船相加、
  快照還原位元相同、紀錄/重播、結構檢查 9 例與預設值、跨語言夾具。
- Visual.Shared `OptionsEnvironmentAndClientTests.StateWavesOverrideWindDerivedWaveHeightAndDirection`。
- Python `src/Tools.Calibration/tests/test_waves.py`(40 項):同上各項的 Python 版、`normalise_sections`/`validate_scenario` 帶出 `waves`、快照/還原/`reset`/`configure_external(None)`、夾具可重現;
  練習中變更(11 項):指令加浪與情境浪逐位元相同、部分更新/關閉/不合法、快照/還原/`reset`、伺服器 ack 與拒絕、時間表結構檢查(與 C# 同路徑/訊息)。
- C# `tests/SimCore.Tests/WaveEnvironmentCommandTests.cs`(22 項;第 6.1 節):指令加浪逐位元相同、部分更新與關閉、不合法 7 例(其他欄位照常)、Hs 漸變與漸變到 0 關閉、
  快照(變更後才寫、漸變中途還原、倒帶恢復情境浪、關閉快照、`reset`)、紀錄/重播、時間表動作、結構檢查 7 例、`command.schema.json` 範例、不接受外力的動力學。
- Sensors.Radar `WaveSeaStateTests`(17 項):Douglas 分界表、`RadarScene.WaveHeightM`、浪驅動海雜波與練習中變更/關閉、明示海況優先。
- vitest `src/Instruments.Web/tests/waves-sea.test.ts`(7 項):`state.waves` / `shipInteraction` 解析、指令形狀、SEA 方塊與 conning 條、教官站環境面板、事件列。

## 9. 已知限制與後續

1. 練習中變更只有 Hs 漸變,Tp / 來向 / 浪譜為階躍(來向可比照風向以最短弧漸變,但模型需每步重算譜權重;後續視需要加入);Python 參考實作無漸變。
2. 一階振盪不回饋 3 自由度(規劃書第 6.2 節預留 4 自由度):橫搖為解耦 1 自由度(`ship.roll`)、升沉/縱搖為線性只輸出模型(`ship.seakeeping`,第 11 節),
   皆需情境啟用;縱盪/橫盪/艏搖的一階運動未實作,漂移力不因船體運動而改變(第 7 節 λ ≳ L 的低估仍在)。
3. 係數為估計;待模型試驗或實船漂移紀錄校正(`EndFullness`、`CutoffWavelengthL`)。
4. 浪與流、浪與淺水的波高變化、方向擴散(directional spreading)未納入;雷達海雜波只取 Hs 推 Douglas 海況(不含 Tp / 來向的方位調制;教官/情境明示海況優先)。
5. 視景海況等級(Douglas)仍由風速/雷達環境決定,只有波高/浪向取自 `state.waves`。

## 10. Python 參考實作(`simosa_brm/external.py` `WaveDriftModel`)

- 與 C# 逐式對應、同運算順序:譜權重、水線站、`wave_number`(固定 12 次 Newton)、`ursell_reflection`/`bessel_i1`/`bessel_k1`(同一組 A&S 多項式)、
  `compute`(K 與 G 的累加順序相同)、`hash_bytes`(`struct.pack("<qddddddd", …)`)、`state()`。
- 練習中變更:`MMGShip.set_waves(waves)`(`set_environment(waves=…)`;第 6.1 節):None / Hs 0 關閉、dict 合併目前值後檢查並重建模型(來向正規化),不合法 `ValueError("invalidWaves")`;
  立即生效(無漸變);`snapshot()` 深拷貝模型、`reset` / `apply_scenario` 回到情境設定。
- 接線:`scenario.validate_scenario` → `environment.waves` 正規化(`external.normalise_waves`),同時由 `normalise_sections` 放進 `external["waves"]`,
  `MMGShip.configure_external` / `_build_external` 建立 `ship.waves`;`step` 的外力場相加順序與 C# 相同;步末 `update_last`;`state_hash` 追加區塊;
  `state_json` 輸出 `waves`;快照/還原含 `waves`。
- 跨語言夾具(`tests/SimCore.Tests/Golden/export_waves_reference.py`):
  - `waves_force_table.csv`:3 組船舶/裝載 × 5 種浪況(PM/JONSWAP、不同 Hs/Tp/來向/γ)× 2 水深(50 m 深水、12 m 淺水)× 4 狀態(靜止、斜航前進、倒退、高速)
    共 120 列的純函數取樣;C# `GoldenForceTableMatchesPythonReference` 比對波長、K、X、Y、N 相對差 ≤ 1e-12(實測 0:逐位元相同)。
  - `waves_drift_run.{yaml,csv}`:FSB1 滿載 6 kn、Hs 2 m / Tp 7 s 自 045° JONSWAP、水深 20 m(走有限水深色散),100 s 右舵 10°,300 s;
    C# `GoldenRunMatchesPythonWithinOnePercent` 每 1 s 比對位置/航向/速度/漂移力/波長(規劃書第 14 章 < 1%;實測位置差 5e-16 % 航程、
    漂移力相對差 ≤ 5e-16)。Python `test_waves.py` 以同一路徑重算並比對末狀態雜湊。

## 11. 一階運動:升沉(heave)與縱搖(pitch)(`ship.seakeeping` → `state.waveMotion`;2026-11-11)

### 11.1 總覽與資料來源聲明

- 範圍:浪的**一階振盪運動**中的升沉與縱搖,供視景(相機/船模)與儀表呈現船體隨浪起伏。**線性、只輸出、不回饋 3 自由度運動**(與解耦橫搖 `ship.roll` 同原則):
  啟用後 u/v/r/x/y/ψ、陣風亂數序列與未啟用時逐位元相同;不改變漂移力、舵效、阻力、UKC(`depthBelowKeel` 不含升沉/縱搖)。
- 做法:沿用橫搖浪激勵的**同一個地固波場**(同一組浪譜成分、同一組相位、同一個種子,不另造亂數),每個成分以遭遇頻率的線性單自由度穩態響應(RAO 與相位滯後)
  **閉式合成**——不積分、無跨步狀態,輸出是步末狀態、時刻、目前 Hs、浪設定與種子的純函數。
- **所有係數為 ASSUMED**(兩船沒有耐航模型試驗或實船運動紀錄;本專案離線開發):附加質量/慣量、阻尼比、水線面係數、縱搖迴轉半徑、GM_L 估計式集中於
  `SeakeepingModel` 常數與情境 `ship.seakeeping`(第 11.4 節);取得耐航計算(strip theory)或實船紀錄後應校正。未啟用、無浪或 Hs = 0 的情境:無輸出,
  狀態、雜湊、既有夾具與黃金測試逐位元不變。

| 項目 | 本模型 | 狀態 |
|---|---|---|
| 波場(成分、振幅、相位、深水波數) | 與 `RollModel` 相同:5 個成分 ω_i = ω_p·ρ_i(ρ_i 在 [0.6, 2.0] 等分取中點)、PM/JONSWAP 權重、相位由情境種子 | 與橫搖一致(測試以波斜率核對) |
| 遭遇頻率 ω_e = ω + k·V_D | 對地速度朝浪源的分量 | 標準(深水) |
| 升沉自然頻率 ω_h² = g·A_wp/(∇ + A_33) | A_33 = ∇、A_wp = C_wp·L·B、C_wp = (1 + 2C_b)/3 | 結構標準;**A_33、C_wp 為 ASSUMED** |
| 縱搖自然頻率 ω_p² = g·∇·GM_L/I_55 | I_55 = ρ∇(k_yy·L)²·1.2、k_yy/L = 0.25、GM_L ≈ BM_L = 3·C_wp²·L²/(40·C_b·T) | 結構標準;**附加慣量 1.2、k_yy、GM_L 估計式為 ASSUMED** |
| 激勵(Froude–Krylov 型):沿船長的長度濾波 × Smith 修正 exp(−k·T/2) | 均勻(矩形)水線面的波面平均(升沉)與一次矩(縱搖) | 簡化(無繞射、無船型分佈) |
| 響應 RAO = 1/√((1 − r²)² + (2ζr)²)、滯後 atan2(2ζr, 1 − r²) | 阻尼比 ζ_h = ζ_p = 0.35 | 線性單自由度;**ζ 為 ASSUMED** |
| 升沉–縱搖耦合、與橫搖耦合、縱盪/橫盪/艏搖一階運動 | 無 | **未實作**(第 11.6 節) |

### 11.2 情境 `ship.seakeeping`(`scenario.schema.json` `properties.ship.properties.seakeeping`)

```yaml
ship:
  id: FSB2
  loading: full
  seakeeping:
    enabled: true              # 預設 false
    heaveDampingRatio: 0.35    # 選填 0–1(不含);預設 0.35(ASSUMED)
    pitchDampingRatio: 0.35    # 選填 0–1(不含);預設 0.35(ASSUMED)
    pitchGyradiusRatio: 0.25   # 選填 k_yy/L,> 0 且 ≤ 0.5;預設 0.25(ASSUMED)
    gmL_m: 104.7               # 選填 > 0;未給時由 particulars 估 3·C_wp²·L²/(40·C_b·T)(滿載約 1.0–1.2 L)
environment:
  waves: { significantHeight_m: 2.5, peakPeriod_s: 8, direction_deg: 45, spectrum: jonswap }   # 沒有浪(或 Hs = 0)就沒有輸出
```

- 結構檢查(`Scenario.StructuralIssues` 與 Python `scenario.seakeeping_issues`,路徑 `ship.seakeeping.*`、訊息相同;`enabled` 與否都檢查):
  `heaveDampingRatio` / `pitchDampingRatio` 須在 0–1 之間(不含)、`pitchGyradiusRatio` 須大於 0 且不超過 0.5、`gmL_m` 必須為正(皆有限)。
- 無區段或 `enabled: false`:不建立模型(`SimulationEngine.Seakeeping` 為 null),`state.waveMotion` 省略、雜湊不追加區塊。
- 可與 `ship.roll` 同時啟用(兩者互不影響);浪可來自情境 `environment.waves` 或練習中的 `setEnvironment waves`(第 6.1 節;Hs 漸變中以當下插值計算)。

### 11.3 模型

**波場**(與 physics-roll.md 第 3.3 節相同):成分 ω_i = ω_p·ρ_i、權重 w_i(PM / JONSWAP,Σw_i = 1)、振幅 a_i = (Hs/4)·√(2w_i)、深水波數 k_i = ω_i²/g、
相位 ε_i = 2π·`DeterministicRandom(seed).NextDouble()`(`RollModel.DrawPhases`,同一序列)。地固波面
η(x, y, t) = Σ a_i·sin θ_i,θ_i = ω_i·t − k_i·s + ε_i,s = −(x·sin D + y·cos D) 為沿浪傳播方向的座標(D = 來向)。

**幾何與遭遇頻率**:χ = D − ψ(0 = 頂浪、±90° = 橫浪、180° = 順浪);沿艏向座標 ξ 的相位為 θ_i + k_i·cos χ·ξ,令 x_i = k_i·L_pp·cos χ/2。
V_D = v_E·sin D + v_N·cos D 為**對地**速度(對水速度 + 流)朝浪源的分量,遭遇頻率 ω_e,i = dθ_i/dt = ω_i + k_i·V_D(頂浪前進升高;順浪且船速超過波速時為負)。

**升沉**(向上為正):

- 自然頻率 ω_h² = ρ·g·A_wp/(ρ∇ + A_33) = g·A_wp/(2∇)(A_33 = ρ∇)。
- 激勵 = 沿船長平均的波面 × Smith 修正:a_i·sinc(x_i)·exp(−k_i·T/2),sinc(x) = sin x/x(沿船長正好一個波長時為零;可為負 = 反相)。
- z = Σ a_i·sinc(x_i)·e^{−k_i T/2}·RAO(r_h,i)·sin(θ_i − δ_h,i),r_h,i = ω_e,i/ω_h;ż = Σ(同振幅)·ω_e,i·cos(θ_i − δ_h,i)。

**縱搖**(艏仰為正):

- 自然頻率 ω_p² = ρ·g·∇·GM_L/I_55 = g·GM_L/(1.2·(k_yy·L)²)。
- 激勵 = 均勻水線面上波面的一次矩(靜水壓力矩)÷ 縱向復原力矩 × Smith 修正:
  (12/L³)·∫ξ·η(ξ)dξ = a_i·k_i·cos χ·F_p(x_i)·cos θ_i,**F_p(x) = 3(sin x − x cos x)/x³**(x → 0 時 1,即沿艏向的波斜率 a·k·cos χ;第一個零點 x ≈ 4.49;|x| < 0.05 用級數 1 − x²/10 + x⁴/280)。
- θ = Σ a_i·k_i·cos χ·F_p(x_i)·e^{−k_i T/2}·RAO(r_p,i)·cos(θ_i − δ_p,i);θ̇ = −Σ(同振幅)·ω_e,i·sin(θ_i − δ_p,i)。
- 趨勢:橫浪 cos χ = 0 → 縱搖恰為零;長波(λ ≫ L)時頂浪/順浪最大、隨 cos χ 遞減;λ ≈ L 的短波在頂浪時沿船長相消(F_p 小),斜浪的等效波長 λ/cos χ 較長,
  縱搖可略大於頂浪(實船亦然)。
- 註:規劃時的近似式「a·k·|2(sin x/x − cos x)/x|」與上式只差無因次因子 k·L/3(且取絕對值);上式是同一個一次矩積分以縱向復原力矩 ρg·B·L³/12 正規化的結果,
  長波極限才會回到波斜率 a·k·cos χ,並保留正負號(x 超過零點時反相),故採用上式。

**響應**:RAO(r) = 1/√((1 − r²)² + (2ζr)²)、相位滯後 δ = atan2(2ζr, 1 − r²)(r → 0:1 與 0;r = 1:1/(2ζ) 與 π/2;r → ∞:0 與 π;r < 0 時 RAO 相同、滯後變號);
最大值在 r = √(1 − 2ζ²)(ζ = 0.35 時 r = 0.87、RAO 1.53)。速率為解析微分(把 RAO、δ、χ 視為緩變)。

**船速/航向改變**:θ_i 以自船位置取值,波的相位連續;RAO 與 δ 直接代入當下的 ω_e、長度濾波代入當下的 χ ——不模擬暫態,船速或航向急變時各成分的振幅與相位滯後
立刻跳到新的穩態值(輸出可能出現小的不連續;以 50 Hz 計算、操船的 ω_e 變化緩慢,可接受)。

### 11.4 係數(ASSUMED)與 FSB 量級

| 常數 / 設定 | 值 | 說明 |
|---|---|---|
| `HeaveAddedMassRatio` | 1.0 | A_33/(ρ∇);寬淺船型的升沉附加質量與排水質量同量級 |
| C_wp | (1 + 2C_b)/3 | 與漂移力水線幾何同式(FSB 0.80–0.84) |
| `PitchAddedInertiaFactor` | 1.2 | I_55 = ρ∇(k_yy·L)² × 1.2 |
| `pitchGyradiusRatio`(k_yy/L) | 0.25 | 商船 0.24–0.26 |
| `gmL_m` 估計 | 3·C_wp²·L²/(40·C_b·T) | BM_L 的經驗式(= 3·A_wp²·L/(40·B·∇));忽略 KB − KG |
| `heaveDampingRatio` / `pitchDampingRatio` | 0.35 | 興波阻尼大,文獻量級 0.2–0.5 |

| 船/裝載 | C_wp | A_wp (m²) | ∇ (m³) | T_h (s) | GM_L (m) | GM_L/L | T_p (s) |
|---|---|---|---|---|---|---|---|
| FSB1 滿載 | 0.825 | 1,471 | 7,536 | 6.42 | 119.0 | 1.20 | 4.99 |
| FSB1 壓載 | 0.800 | 1,426 | 4,364 | 4.96 | 180.7 | 1.82 | 4.05 |
| FSB2 滿載 | 0.843 | 1,754 | 11,486 | 7.26 | 104.7 | 1.01 | 5.58 |
| FSB2 壓載(C_b、Δ 為估計) | 0.826 | 1,717 | 8,369 | 6.26 | 137.4 | 1.32 | 4.88 |

量級(FSB2 滿載、Hs 2.5 m、PM、靜止,1,200 s 內的最大值;`SeakeepingModelTests.BeamSeasGiveNoPitchWhileHeadAndFollowingSeasGiveMost`):

| Tp | 頂浪 升沉 / 縱搖 | 45° 斜浪 | 橫浪 |
|---|---|---|---|
| 8 s(λ_p ≈ L) | 0.30 m / 1.15° | 0.51 m / 1.36° | 1.34 m / 0° |
| 14 s(λ_p ≈ 3 L) | 1.09 m / 1.60° | 1.27 m / 1.43° | 1.56 m / 0° |

- 12 kn、Hs 2.5 m / Tp 8 s 自 045°(JONSWAP;黃金案例):最大升沉 0.36–0.38 m、縱搖 1.5–1.6°、升沉速率 0.35 m/s、縱搖角速度 1.5°/s。
- 12 kn 頂浪 Tp 14 s:升沉 1.25 m、縱搖 2.14°(遭遇頻率升高,接近縱搖共振)。
- E17b(FSB1 壓載、泊位上靜止、湧浪自左舷艉 225°、Tp 10 s):Hs 0.5 m 時升沉 ≤ 0.17 m、縱搖 ≤ 0.38°;Hs 0.8 m 時 0.27 m、0.61°。

### 11.5 狀態、引擎接線、雜湊與確定性

```json
"waveMotion": { "heave_m": 0.132, "pitch_deg": 1.256, "heaveRate_mps": 0.227, "pitchRate_dps": -0.495 }
```

- `state.waveMotion`(`state.schema.json` `properties.waveMotion`;`OwnShipState.WaveMotion`):`heave_m` 升沉(向上為正)、`pitch_deg` 縱搖(艏仰為正)、
  `heaveRate_mps`、`pitchRate_dps`。**只在 `ship.seakeeping.enabled` 且有浪(Hs > 0)時輸出**;其餘情況省略,其他欄位不變。
- 建立:`Initialize`(載入/重設)與 `Restore` 以 `SeakeepingModel.Create(Ship, Loading, Scenario)` 重建(相位由種子)、`SetWaves(目前浪設定)`,最後 `RefreshSeakeeping`
  以目前狀態重算(tick 0 即有輸出)。練習中變更浪(`SetWaves`)同步成分並重算;關閉浪 → 輸出消失。`setEnvironment`(`ApplyEnvironment`)結尾也重算一次:
  立即改流(無 ramp)會改變對地速度 → 遭遇頻率,凍結中下指令或時間表在評估步同 tick 改流時,`state.waveMotion` 當下即為新流的值,
  與此時取快照再還原的結果一致(`ImmediateCurrentChangeWhileFrozenRefreshesOutputAndSurvivesRestore`)。
- 每步(`Advance`):… → RK4 → tick++ → 潮高/流更新 → … → 浪 UpdateLast → **`RefreshSeakeeping`**(步末狀態、自船位置的流 → 對地速度、目前 Hs、步末時刻 tick·dt)→ 擱淺 → … → 雜湊。
  不在 RK4 內、不產生任何力。動力學不接受外力(`SupportsExternalForces = false`)時漂移力停用、`state.waves` 省略,但升沉/縱搖只需浪設定與運動狀態,照常輸出
  (`DynamicsWithoutExternalForceSupportStillOutputWaveMotion`;與橫搖同原則)。
- **雜湊**:有輸出時於雜湊鏈最後(橫搖區塊之後)追加 5 個 int64:標記 `SEAKEEP1`、升沉(m)、縱搖(rad)、升沉速率(m/s)、縱搖角速度(rad/s)。未啟用、無浪或 Hs = 0 不追加
  (`WithoutSeakeepingOutputsAndHashAreUnchangedAndEnabledDoesNotFeedBack`、`EnabledWithoutWavesOrWithZeroHsProducesNoOutputAndLeavesHashUnchanged`)。
- **快照**:純函數、無跨步狀態,`EngineSnapshot` **不需新欄位**(啟用與否快照 JSON 除雜湊外相同);還原後以還原的狀態、時刻與浪設定重算,
  與快照當時逐位元相同,續跑雜湊與連續執行相同(含 Hs 漸變中途;`SnapshotRestoreReproducesOutputAndHash`);紀錄/重播一致(`RecordReplayReproducesHashWithSeakeeping`)。
- 確定性:只依種子、情境與輸入;C# 與 Python 逐項同運算順序(取樣表逐位元相同,第 11.8 節)。
- 顯示端(T4;`Visual.Shared` 內插與相機/船模、網頁 `state-parser` / conning 海況方塊 / 教官站自船面板)依本節欄位名稱與正負號;與橫搖的合成順序見 `docs/design/visual-client.md`。

### 11.6 適用範圍與限制

- **線性**:振幅 ∝ Hs;無甲板上浪、艏底拍擊(slamming)、螺槳出水與大振幅非線性。**無升沉–縱搖耦合**(各為獨立的單自由度)、**無橫搖耦合**(橫搖見 `ship.roll`)、
  **無縱盪/橫盪/艏搖的一階運動**;不回饋 3 自由度(失速、舵效、漂移力不因船體運動改變),UKC/擱淺判定不含升沉與縱搖。
- 激勵為矩形水線面的 Froude–Krylov 近似 + Smith 修正:無繞射/輻射的頻率相依(附加質量與阻尼為常數)、無船型沿船長的分佈、無前進速度對激勵與阻尼的影響
  (只有遭遇頻率)。RAO 的峰值位置與高度取決於 ASSUMED 的 ζ、A_33、k_yy、GM_L。
- **深水波數**(與橫搖相同):淺水(例:E17b 泊位水深 12 m、Tp 10 s)實際波長較短,長度濾波與波斜率偏離;港內遮蔽、反射與繞射不在模型內(情境的港內波高為示意值)。
- 5 個成分的正弦和:長時間下有拍頻;不是連續譜的統計(最大值與有義值的比例和實海不同)、無方向擴散。
- 不模擬暫態(第 11.3 節):船速/航向急變、`setEnvironment waves` 改 Tp/來向時輸出階躍。
- 遭遇頻率為零或負(順浪、船速 ≥ 波速)時以準靜態/反向相位處理,實際上此時線性頻域模型本身不適用(衝浪、打橫不在模型內)。

### 11.7 Python 參考實作(`simosa_brm/seakeeping.py`)

- `SeakeepingModel(raw_particulars, loading, cfg, seed)`:與 C# 建構子同式;`set_waves(tp, direction_rad, spectrum, gamma)` / `set_waves_from(WaveDriftModel)`;
  純函數 `sinc`、`pitch_filter`、`response`、`compute`;`evaluate(kin, cur_e, cur_n, hs, t)` / `update_last`;`hash_bytes()`(`<q4d`)、`state()`;相位用 `roll.draw_phases`。
- `MMGShip`:`configure_seakeeping(cfg)`、`seakeeping` 屬性;`reset` 重建、`set_waves` 同步並重算、`step` 步末(含擱淺分支)`_refresh_seakeeping`、
  `snapshot/restore` 深拷貝(舊快照無此鍵 → 重建)、`state_json` 的 `waveMotion`、`state_hash` 追加(只在有輸出時)。Python 無 Hs 漸變(同第 6.1 節)。
- `scenario.validate_scenario` → `sc["ship"]["seakeeping"]`(`normalise_seakeeping`)、`seakeeping_issues`;`apply_scenario` 最後呼叫 `configure_seakeeping`。

### 11.8 測試與跨語言夾具

- C# `tests/SimCore.Tests/SeakeepingModelTests.cs`(31 項):自然週期與預設/覆寫;RAO 極限與共振峰;長度濾波極限;長波極限(升沉 → 波面、縱搖 → 波斜率,殘差 < 8 %);
  橫浪縱搖為零、頂浪 = 順浪 > 斜浪(長波)、橫浪升沉最大;低阻尼時 Tp ≈ T_h 放大最大;速率 = 沿航跡的數值微分(含流)與遭遇頻率;與橫搖同一波場/相位;Hs 線性、
  無浪/Hs = 0 無輸出;未啟用位元不變且啟用不回饋(狀態 JSON 去掉 `waveMotion` 後相同、快照相同);啟用但無浪雜湊不變;不接受外力的動力學仍輸出;量級;`setEnvironment waves` 開/漸變/關;
  快照/還原;紀錄/重播;結構檢查 8 例與情境 JSON 往返;schema 欄位名稱/預設值與契約類別一致;跨語言夾具 2 項;E17b 2 項。
- Python `src/Tools.Calibration/tests/test_seakeeping.py`(26 項):同上各項的 Python 版、`MMGShip` 接線(`reset`、舊快照)、夾具可重現、E17b。
- 跨語言夾具(`tests/SimCore.Tests/Golden/export_seakeeping_reference.py`;重產:`cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_seakeeping_reference.py`):
  - `seakeeping_table.csv`:4 組船舶/裝載 ×(預設 2 個種子 + 覆寫設定 1 個種子)× 5 種浪況 × 7 個狀態(靜止、頂浪/斜浪前進、倒退、順浪超過短波波速、有流、遠離原點與長時間)
    共 420 列的純函數取樣;C# `GoldenTableMatchesPythonReference` 四個輸出 |差| ≤ 1e-12·max(1, |值|)(實測 0:逐位元相同)。
  - `seakeeping_run.{yaml,csv}`:FSB2 滿載 12 kn(未指定車鐘 → 直航平衡轉速;無舵令,浪漂移使艏向在 120 s 內緩慢偏轉約 18°)、Hs 2.5 m / Tp 8 s 自 045° JONSWAP、深水,120 s 每 1 s 一列;
    C# `GoldenSeakeepingRunMatchesPythonReference` 升沉/縱搖/速率/波面高每列 |差| ≤ 1e-9·max(1, |值|)(與橫搖夾具相同容差;Linux 實測逐位元相同)、位置差 / 航程 < 1 %。
- 情境 `data/scenarios/E17b_typhoon_seakeeping.yaml`(E17 的變體;scenario-library.md 第 11 節):`E17b_SharesE17GeometryAndAddsSwellWithSeakeeping`、
  `E17b_NoOperatorHeaveAndPitchGrowWithSwellAndRecordReplays`;核心紀錄夾具 `record-E17b_typhoon_seakeeping-core.jsonl`。
