# 浪與平均波浪漂移力(wave drift force)— 設計與契約

> 規劃書依據:第 6.2 節運動方程式的環境外力 W 項(風/浪)與「浪:浪譜/浪漂力」;R2「風/流/潮/浪」、R3「惡劣天候」;第 7.3 節視景海面;第 14 章確定性。
> 程式:`src/SimCore/Physics/External/WaveDrift.cs`(`WaveDriftModel`、`WaveDriftCoefficients`、`WaveDriftResult`、`WaveDriftState`)、
> `src/SimCore/Scenario/Scenario.cs`(`ScenarioWaves`、`ScenarioEnvironment.Waves`、結構檢查)、`src/SimCore/Engine/SimulationEngine.cs`(接線、雜湊、狀態)、
> `src/SimCore/Contracts/OwnShipState.cs`(`waves` 欄位)、`src/Visual.Shared/Env/EnvironmentMapper.cs`(視景波高/浪向);
> Python 參考實作 `src/Tools.Calibration/simosa_brm/external.py`(`WaveDriftModel`、`normalise_waves`)、`scenario.py`(`waves_issues`)、`mmg.py`(接線);
> 測試 `tests/SimCore.Tests/WaveDriftTests.cs`、`tests/Visual.Shared.Tests/OptionsEnvironmentAndClientTests.cs`、`src/Tools.Calibration/tests/test_waves.py`;
> 跨語言夾具 `tests/SimCore.Tests/Golden/waves_force_table.csv`、`waves_drift_run.{yaml,csv}`(`export_waves_reference.py` 產生)。

## 1. 總覽與資料來源聲明

- 範圍:3 自由度(surge/sway/yaw)模型只做**平均二階波浪漂移力**(mean second-order drift force)——對操船有意義的定常分量:迎浪阻力增加(added resistance)、
  橫浪側向漂移、斜浪的艏搖力矩(靜止船趨向正橫受浪)。**不做**一階振盪運動(升沉、縱搖、橫搖)、慢變漂移振盪、波浪對舵效/螺槳的瞬時影響、浪流交互作用。
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
- setEnvironment: { waves: { direction_deg: 180 } }      # 部分更新:缺的欄位沿用目前值(Hs 取模型目前值)
- setEnvironment: { waves: null }                        # 關閉(significantHeight_m: 0 亦為關閉);ramp_s > 0 時 Hs 先漸變到 0 再移除
```

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
| 自由度 | 3(平均力) | 無橫搖/升沉/縱搖,無一階振盪;儀表不顯示搖晃 |

## 8. 測試

- C# `tests/SimCore.Tests/WaveDriftTests.cs`(29 項):鏡像對稱、迎浪/順浪 sway = 0、量級與 Hs²/長波/壓載、斜浪力矩方向與遭遇頻率修正、JONSWAP/PM、
  淺水波長(色散殘差 < 1e-9、深水收斂回 k0)、Bessel/Ursell 表值、無浪不變(含 Hs = 0 位元相同)、引擎施力與 `state.waves` JSON 往返、與拖船相加、
  快照還原位元相同、紀錄/重播、結構檢查 9 例與預設值、跨語言夾具。
- Visual.Shared `OptionsEnvironmentAndClientTests.StateWavesOverrideWindDerivedWaveHeightAndDirection`。
- Python `src/Tools.Calibration/tests/test_waves.py`(40 項):同上各項的 Python 版、`normalise_sections`/`validate_scenario` 帶出 `waves`、快照/還原/`reset`/`configure_external(None)`、夾具可重現;
  練習中變更(11 項):指令加浪與情境浪逐位元相同、部分更新/關閉/不合法、快照/還原/`reset`、伺服器 ack 與拒絕、時間表結構檢查(與 C# 同路徑/訊息)。
- C# `tests/SimCore.Tests/WaveEnvironmentCommandTests.cs`(21 項;第 6.1 節):指令加浪逐位元相同、部分更新與關閉、不合法 7 例(其他欄位照常)、Hs 漸變與漸變到 0 關閉、
  快照(變更後才寫、漸變中途還原、倒帶恢復情境浪、關閉快照、`reset`)、紀錄/重播、時間表動作、結構檢查 7 例、不接受外力的動力學。
- Sensors.Radar `WaveSeaStateTests`(17 項):Douglas 分界表、`RadarScene.WaveHeightM`、浪驅動海雜波與練習中變更/關閉、明示海況優先。
- vitest `src/Instruments.Web/tests/waves-sea.test.ts`(7 項):`state.waves` / `shipInteraction` 解析、指令形狀、SEA 方塊與 conning 條、教官站環境面板、事件列。

## 9. 已知限制與後續

1. 練習中變更只有 Hs 漸變,Tp / 來向 / 浪譜為階躍(來向可比照風向以最短弧漸變,但模型需每步重算譜權重;後續視需要加入);Python 參考實作無漸變。
2. 無一階振盪與橫搖(規劃書第 6.2 節預留 4 自由度);儀表/視景不呈現搖晃,只有平均漂移。
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
