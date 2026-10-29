# 橫搖(解耦 1 自由度)— 設計與契約

> 規劃書依據:第 6.2 節運動方程式預留的第 4 自由度(橫搖)、環境外力 W 項(風/浪);R2「兩船自船動態」(需求追溯表:部分 — 解耦 1 自由度橫搖,不回饋 3-DOF);第 7.2 節操船台傾斜儀、第 7.3 節視景(相機隨船傾);第 14 章確定性。
> 程式:`src/SimCore/Physics/RollModel.cs`(`RollModel`、`RollSnapshot`)、`src/SimCore/Scenario/Scenario.cs`(`ScenarioRoll`、`ScenarioShip.Roll`、結構檢查)、
> `src/SimCore/Engine/SimulationEngine.cs`(接線、雜湊、快照、`state.roll`)、`src/SimCore/Engine/EngineSnapshot.cs`(`Roll`)、`src/SimCore/Contracts/OwnShipState.cs`(`RollState`);
> 契約 `src/Contracts/scenario.schema.json`(`ship.roll`)、`src/Contracts/state.schema.json`(`roll`);
> Python 參考實作 `src/Tools.Calibration/simosa_brm/roll.py`(`RollModel`、`DotNetRandom`、`normalise_roll`)、`scenario.py`(`roll_issues`)、`mmg.py`(接線);
> 測試 `tests/SimCore.Tests/RollModelTests.cs`、`src/Tools.Calibration/tests/test_roll.py`;
> 跨語言夾具 `tests/SimCore.Tests/Golden/roll_run.{yaml,csv}`(`export_roll_reference.py` 產生;φ、φ' 每秒 1e-9)。

## 1. 總覽與資料來源聲明

- 範圍:**解耦的 1 自由度線性橫搖**。核心仍以 MMG 3 自由度(surge/sway/yaw)積分運動;橫搖 φ 以單自由度線性方程式另行積分,輸入為 3-DOF 的狀態
  (對水速度、艏搖率、航向、位置)、風與浪,**不回饋**到 3-DOF(無橫搖–艏搖耦合、無橫搖對舵效/阻力的影響):啟用橫搖後 u/v/r/x/y/ψ、陣風亂數序列與
  未啟用時逐位元相同(`RollModelTests.WithoutRollOutputsAndHashAreUnchangedAndEnabledRollDoesNotFeedBack`)。
- 用途:操船台傾斜儀、視景相機(駕駛台/翼橋/艉視隨船傾)、講評的迴轉外傾/風傾/橫搖量級感;不是耐航或穩度分析工具。
- 方程式結構:φ'' + 2ζω φ' + ω² φ = ω²·(φ_turn + φ_wind) + ω²·φ_wave(t)。靜傾項以「等效靜傾角」表示(復原力矩 Δ g GM φ 與外力矩平衡),
  浪激勵以**有效波斜率**(Froude–Krylov 假設:甲板順著局部水面傾斜)表示;線性單自由度對遭遇頻率的響應即 RAO |φ|/α = 1/√((1 − r²)² + (2ζr)²),r = ω_e/ω。
- **係數為文獻量級的估計值(ASSUMED)**:兩船沒有橫搖衰減試驗或傾斜試驗以外的穩度資料,本專案離線開發;可調的 `naturalPeriod_s`、`dampingRatio` 與三個
  增益(`windHeelGain`、`turnHeelGain`、`waveGain`)集中在情境 `ship.roll`,預設值見第 4 節。沒有 `ship.roll`(或 `enabled: false`)的情境
  不建立模型:`state.roll` 省略、雜湊不追加區塊、快照無 `roll` 欄位,所有既有情境、夾具與黃金測試逐位元不變。

| 項目 | 本模型 | 狀態 |
|---|---|---|
| 方程式(線性 1-DOF、等效靜傾、有效波斜率激勵) | 第 3.1–3.3 節 | 標準教科書結構(Bhattacharyya 1978 型) |
| 自然週期 T = 2·0.4·B/√GM(Weiss 公式) | 第 4 節 | 標準估計(IMO 天氣衡準 C ≈ 0.37–0.40);情境可給定 |
| GM:particulars `loadingConditions.<loading>.gm_m`;FSB2 無資料 → 1.2 m | 第 4 節 | FSB1 依復原性資料 GH-1040;**FSB2 ASSUMED** |
| 阻尼比 ζ = 0.08 | 第 4 節 | **ASSUMED**(有舭龍骨商船 0.06–0.10) |
| 迴轉傾 −atan(v_c·r/g)·turnHeelGain | 第 3.2 節 | 離心加速度與 g 的夾角;**力臂比 (KG − T/2)/GM 未內建**(FSB1 滿載約 1.4,可由增益給) |
| 風傾:½ρ_a·C_Dt·A_L·|a|·a_y,力臂 z_w = 側面積形心高 + T/2,C_Dt = 0.7 | 第 3.2 節 | **ASSUMED**(Blendermann 正橫量級;A_L 與 Python `windage_areas` 同一估計) |
| 浪激勵:5 個浪譜成分、深水波數、地固波場、相位由種子抽取一次 | 第 3.3 節 | 標準譜離散;**無短波有效波斜率折減、無淺水色散、無方向擴散** |
| 積分:固定步長 RK4,與主迴圈同步(步首輸入) | 第 3.4 節 | 兩種語言同運算順序 |

## 2. 情境 `ship.roll`(`scenario.schema.json` `properties.ship.properties.roll`)

```yaml
ship:
  id: FSB1
  loading: full
  roll:
    enabled: true          # 預設 false(有區段但未啟用與無區段相同)
    naturalPeriod_s: 10.3  # 選填:自然橫搖週期(> 0);未給時 T = 2·0.4·B/√GM(GM 取 particulars,無則 1.2 m)
    dampingRatio: 0.08     # 選填:阻尼比 ζ(0–1 之間,不含;預設 0.08)
    windHeelGain: 1.0      # 選填:風傾倍率(≥ 0;預設 1;0 = 無風傾)
    turnHeelGain: 1.0      # 選填:迴轉傾倍率(≥ 0;預設 1;物理上約 (KG − T/2)/GM)
    waveGain: 1.0          # 選填:浪激勵倍率(≥ 0;預設 1;有效波斜率係數)
```

- 結構檢查(`Scenario.StructuralIssues` 與 Python `scenario.roll_issues`,路徑 `ship.roll.*`、訊息相同;`enabled` 與否都檢查):`naturalPeriod_s` 必須為正、
  `dampingRatio` 須在 0–1 之間(不含)、`windHeelGain`/`turnHeelGain`/`waveGain` 必須為 ≥ 0 的有限數值。Python 對非物件另回報 `ship.roll` 一條錯誤(C# 於反序列化拒絕)。
- 浪激勵取自 `environment.waves`(Hs、Tp、來向、浪譜、γ;設計文件 physics-waves.md),練習中以 `setEnvironment waves` 變更/關閉時跟著變(Hs 漸變中以每步的插值計算);
  無 `waves` 時只有靜傾與自由衰減。
- 其他情境欄位不受影響;Python 參考實作 `validate_scenario` 把正規化後的 `ship.roll` 保留在 `sc["ship"]["roll"]`(未給為 `None`)。

## 3. 模型

### 3.1 方程式與符號

- φ:橫搖角(弧度;**右傾為正**),φ' 角速度。ω = 2π/T 自然角頻率、ζ 阻尼比。
- φ'' = ω²·(φ_static + φ_wave(t) − φ) − 2ζω·φ',φ_static = φ_turn + φ_wind(整步固定,以步首狀態計算)。
- 穩態:靜傾 φ → φ_static;正弦激勵 α·sin(ω_e t) 的穩態振幅 = α·H(ω_e),H = 1/√((1 − (ω_e/ω)²)² + (2ζ·ω_e/ω)²)(共振 H = 1/(2ζ) = 6.25)。
- 自由衰減:阻尼週期 T_d = T/√(1 − ζ²),相鄰同側峰值比 exp(−2πζ/√(1 − ζ²))= 0.60(ζ 0.08)(`FreeDecayHasDampedNaturalPeriodAndLogarithmicDecrement`)。

### 3.2 靜傾:迴轉與風

- **迴轉離心傾角** φ_turn = −atan(v_c·r/g)·turnHeelGain,v_c = √(u² + v²) 對水速度、r 艏搖率(右轉正)。**右轉(r > 0)向外傾 = 左傾(負)**;左轉右傾。
  物理上穩定迴轉的外傾角 tan φ = (v_c·r/g)·(KG − T/2)/GM(離心力作用於重心、水下側向阻力中心約在半吃水);本模型把力臂比留給 `turnHeelGain`
  (FSB1 滿載 KG 5.61 m、T 5.70 m、GM 1.96 m → 1.41;壓載 KG 5.06、T 3.72、GM 4.15 → 0.77)。迴轉初期舵力造成的短暫內傾不在模型內。
- **風傾等效靜傾角** φ_wind = atan(F_y·z_w/(Δ g GM))·windHeelGain:視風向量 a = 風向量(含陣風)− 自船對地速度,船體座標分量 (a_x, a_y)
  (`EnvironmentMath.ApparentWindBody`);橫向風力 F_y = ½ρ_a·C_Dt·A_L·|a|·a_y(右舷正;風自右舷來時 a_y < 0),ρ_a = 1.225 kg/m³、C_Dt = 0.7;
  力臂 z_w = 側面積形心在水線上的高度 + T/2。**風自右舷來 → 向下風(左)傾,負**。迎艏/順艉風無橫向分量 → 0。
- A_L 與形心:particulars `windage.lateralArea_m2`(兩船目前為 null)缺值時與 Python `particulars.windage_areas` 同式估計:船體 LOA × 乾舷(max(D − T, 0.5))
  + 住艙 0.15 LOA × 11 m + 甲板設備 0.5 LOA × 3 m,形心高以各部分面積加權(乾舷/2、乾舷 + 5.5、乾舷 + 1.5)。

### 3.3 浪激勵:地固波場與有效波斜率

- 浪譜離散為 N = 5 個成分:ω_i = ω_p·ρ_i,ρ_i 在 [0.6, 2.0] 等分取中點(0.74、1.02、1.30、1.58、1.86);權重 w_i 為 PM / JONSWAP 譜形
  (與 `WaveDriftModel` 同式:ρ⁻⁵·exp(−1.25ρ⁻⁴),JONSWAP 再乘 γ^{exp(−(ρ−1)²/2σ²)}),正規化 Σw_i = 1。
- 振幅 a_i = (Hs/4)·√(2 w_i)(Σ a_i²/2 = m0 = Hs²/16);深水波數 k_i = ω_i²/g;波斜率振幅 α_i = k_i·a_i。
- **相位** ε_i = 2π·NextDouble():以情境種子建立一個獨立的 `DeterministicRandom(seed)`(.NET `Random(seed)`)依序抽 5 個,**只抽一次**(載入/重設時),
  進快照(`EngineSnapshot.Roll.Phases`);Python `roll.DotNetRandom` 為 .NET 序列的逐位元移植(`test_roll.py` 以 .NET 8 實際輸出核對),
  因此兩種語言相位相同。不用引擎本身的亂數:陣風序列與 3-DOF 運動不因啟用橫搖而改變。
- 地固波場 η(s, t) = Σ a_i·sin(ω_i t − k_i s + ε_i),s = −(x·sin D + y·cos D) 為自船位置沿浪傳播方向(來向 D + 180°)的座標。沿傳播方向的斜率
  ∂η/∂s = −Σ α_i·cos θ_i;投影到船體橫向(傳播方向與右舷夾角的 cos = −sin χ,χ = 來向 − 航向),甲板順著水面傾斜(右傾正)→
  **φ_wave = −sin χ·Hs·Σ (α_i/Hs)·cos θ_i·waveGain**。迎浪/順浪(sin χ = 0)無激勵;正橫浪最大;自右舷與自左舷來的浪在同一位置反號。
- 遭遇頻率不另外計算:船在地固波場中移動,θ_i 的時間變化率自然就是 ω_e,i = ω_i + k_i·U_w(U_w 為朝浪源的對水速度分量),迴轉中隨航向連續變化、無相位跳變。
  位置 (x, y) 與航向在一步內固定(步首值),時間 t 於 RK4 三個時刻取值。
- Hs 於每步取浪模型目前值(`WaveDriftModel.SignificantHeightM`;`setEnvironment waves` 漸變中為插值);Tp/來向/浪譜/γ 改變時重建成分表,相位不變;
  關閉浪(`waves: null` 或 Hs 0)→ 無激勵、橫搖自由衰減。

### 3.4 積分與時序

- 固定步長 RK4(`RollModel.Integrate`,純函數):k1…k4 依標準公式,φ_wave 於 t、t + dt/2、t + dt 取值,φ_static 整步固定。
- `SimulationEngine.Advance`:陣風/漸變/自動舵/致動器之後、**3-DOF RK4 之前**,以步首 `Motion`、步首環境取樣(含陣風的風、自船位置的流與水深)、
  目前 Hs 與步首時刻 t = tick·dt 呼叫 `RollModel.Step`。Python `MMGShip.step` 在 `_engine_logic` 之後、擱淺分支之前同一位置呼叫
  (擱淺後 Python 仍每步推進橫搖,與 C# 相同)。
- 對水速度用 √(u² + v²)(不用 hypot),兩種語言逐項同運算順序;只差 libm 末位(夾具實測 φ 差 ~1e-14°)。

## 4. 係數與船舶資料

| 船 / 裝載 | GM(m) | T(s) | A_L(m²) | z_w(m) | Δ(t) | 備註 |
|---|---|---|---|---|---|---|
| FSB1 滿載 | 1.96(particulars) | 10.29 | 574.9(估計) | 6.74 | 7 725 | KG 5.61 |
| FSB1 壓載 | 4.15(particulars) | 7.07 | 783.9(估計) | 6.43 | 4 473 | KG 5.06 |
| FSB2 滿載 | **1.2(ASSUMED)** | 14.61 | 774.8(估計) | 8.03 | 11 773 | 裝載手冊 GM 待數位化 |
| FSB2 壓載 | **1.2(ASSUMED)** | 14.61 | 967.1(估計) | 7.85 | 8 578(Cb·L·B·T·ρ 估計) | Cb 由滿載依吃水比估計(Python `_resolve_loading` 同式) |

- 常數(`RollModel`):`DefaultGm` 1.2 m、`DefaultDampingRatio` 0.08、`WeissCoefficient` 0.4、`AirDensity` 1.225、`WindDragCoefficient` 0.7、
  `WaterDensity` 1025、`WaveComponents` 5、`OmegaMinRatio` 0.6、`OmegaMaxRatio` 2.0;Python `roll.py` 同名常數。
- **ASSUMED 清單**:FSB2 的 GM;ζ = 0.08;C_Dt = 0.7 與側面積/形心估計;Weiss 係數 0.4;浪譜成分數與頻率範圍;有效波斜率係數 1(`waveGain`);
  迴轉傾力臂比 1(`turnHeelGain`)。取得傾斜試驗/橫搖衰減試驗或實船觀測後以 `ship.roll` 的參數校正,或更新 particulars(`gm_m`、`windage.lateralArea_m2`)。

### 4.2 FSB 尺度量級檢查(`RollModelTests.EngineReportsStaticHeelInTurnAndWindWithPlausibleMagnitudes`、`ResonantBeamSeasRollAmplitudeIsPlausibleAndLargerThanOffResonance`;數值由 Python 參考實作算出)

- **FSB1 滿載 15 kn、35° 右舵穩定迴轉**(深水):對水速度 4.5 kn、ROT 77°/min、半徑約 104 m → v_c·r/g = 0.0053 → φ_turn = **−0.31°**(向外 = 左傾;
  以物理力臂比 1.41 作 `turnHeelGain` 則 −0.43°);壓載 −0.49°(迴轉較快)。小型瀝青船迴轉外傾小於 1° 屬合理量級。
- **35 kn 正橫風(自右舷)靜傾**:視風 18 m/s → q·C_Dt = 198 Pa × 0.7,F_y = 79.9 kN、力矩 539 kN·m、Δ g GM = 148.5 MN·m → φ_wind = **−0.20°**(左傾);
  壓載 −0.20°(側面積較大、GM 亦較大)。IMO 天氣衡準以 504 Pa 的風壓(含陣風餘裕)計算約 0.75°——本模型用穩定風壓,陣風由情境 `gustiness` 的係數進來。
- **Hs 2 m 正橫浪、靜止、水深 50 m**:Tp = T(10.29 s,共振)最大橫搖 **9.9°**(最大波斜率 3.8°、峰值 RAO 6.25);Tp 7 s 7.3°;Tp 20 s(長波、斜率小)3.4°;
  Tp 4 s(短波、λ 25 m)4.8° — 波斜率達 32° 但 RAO 0.18,本模型無短波有效波斜率折減,偏保守(第 7 節)。斜後浪 45° 約 0.9 倍;10 kn 前進正橫浪 8.3°。
  壓載(T 7.07 s、Tp 7 s)最大 22°——GM 大、週期短而與常見浪週期接近,量級與小船壓載在 2 m 浪中的經驗相符但偏大,`waveGain` 可調。
- 跨語言夾具 `roll_run`(10 kn、25 kn 風、Hs 2 m / Tp 9 s 斜後浪、20° 舵迴轉):最大 |φ| 12.9°,靜傾 −0.6°(迴轉中)。

## 5. 狀態(`state.schema.json` `properties.roll`;C# 與 Python 參考實作皆輸出,只在啟用時)

```json
"roll": { "angle_deg": -3.21, "rate_degps": 1.05, "naturalPeriod_s": 10.2857, "staticHeel_deg": -0.31 }
```

- `angle_deg` 橫搖角(右傾正)、`rate_degps` 角速度、`naturalPeriod_s` 自然週期(情境值或估計)、`staticHeel_deg` 靜傾(φ_turn + φ_wind;動態橫搖 = angle − staticHeel)。
- 無 `ship.roll` 的情境省略整個欄位(`OwnShipState.Roll` null → JSON 不寫出)。

## 6. 引擎接線、快照、雜湊與確定性

- 建立:`Initialize`(載入/重設)`RollModel.Create(Ship, Loading, Scenario)` → 相位由種子抽取、φ = φ' = 0,`SetWaves(目前浪設定)`;`SetWaves`(練習中變更浪)同步成分表。
- 每步:見第 3.4 節;`state.roll` 由 `BuildState` 帶出。
- **雜湊**:啟用時於雜湊鏈最後(船間交互作用區塊之後)追加 5 個 int64:標記 `ROLLDOF1`、φ、φ'、φ_static、步首 φ_wave。無 `ship.roll` 的情境不追加
  (`WithoutRollOutputsAndHashAreUnchangedAndEnabledRollDoesNotFeedBack`)。
- **快照**:`EngineSnapshot.Roll {angle, rate, phases[5], staticHeel, waveSlope}` 只在啟用時寫出;還原時依情境重建(相位由種子重抽、浪成分依還原後的浪設定)
  再以快照覆寫 φ、φ'、相位;缺此欄位的舊快照即為初始。續跑與連續執行逐位元相同(`SnapshotRestoreContinuesBitIdentical`),紀錄/重播雜湊與事件序列一致
  (`RecordReplayReproducesHashWithRoll`)。
- 確定性:所有量只依種子、情境與輸入決定;同種子同相位、不同種子不同相位(`PhasesAreDeterministicFromSeed`)。

## 7. 適用範圍與限制

- 線性小角度模型:大角度(> 20–25°)的非線性復原力矩、黏性阻尼的角速度平方項、參數橫搖(縱浪中 GM 變化)不在模型內;壓載共振時的 20° 量級已在線性範圍邊緣。
- 不回饋 3-DOF:橫搖不改變舵效、阻力、艏搖;浪漂移力(physics-waves.md)仍只有平均二階力。
- 浪激勵:深水波數(淺水 `state.waves.wavelength_m` 仍由漂移力模型以有限水深色散求得,橫搖不用);無短波有效波斜率折減(Tp < 5 s 偏保守)、無方向擴散、
  無浪流交互作用;5 個成分的正弦和在長時間下會出現拍頻,屬預期。
- 風傾以穩定視風計算,陣風只經由每秒更新的風速係數;無動態風傾(突風的瞬時傾)。
- Python 的 `wave_number`(漂移力模型)在極深水(≥ 1000 m)與短週期會 cosh 溢位——既有限制,橫搖模型本身不用它。

## 8. 測試

- C# `RollModelTests`(22):預設週期/GM 預設/估計排水量;迴轉傾符號與大小;風傾向下風、∝ V²、視風;自由衰減週期與對數減幅;浪激勵 ∝ Hs、迎浪/順浪為 0、
  waveGain;相位確定性(含 .NET `Random(7)` 首值);無 roll 不變且啟用不回饋;靜傾量級;共振振幅;`setEnvironment waves` 變更/關閉;快照/還原(含舊快照);
  紀錄/重播;結構檢查(8 案例)與預設;跨語言夾具 `roll_run.csv`(φ、φ'、靜傾、波斜率每秒 |差| ≤ 1e-9·max(1, |值|),實測 ~1e-14;位置 < 1 %)。
- Python `test_roll.py`(27):`DotNetRandom` 對 .NET 8 實際輸出(5 個種子 × 6 值);其餘與 C# 對應;夾具可重現(1e-12、末狀態雜湊相同)。
- 重產夾具:`cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_roll_reference.py`(改模型公式時 C# 與 Python 須同時改)。

## 9. Python 參考實作(`simosa_brm/roll.py`)

- `RollModel(raw_particulars, loading, cfg, seed)`:與 C# 建構子同式(GM、排水量、側面積、力臂、週期、ζ、增益);`set_waves(tp, direction_rad, spectrum, gamma)` /
  `set_waves_from(WaveDriftModel)`;`step(kin, wind_e, wind_n, cur_e, cur_n, hs, t, dt)`;`hash_bytes()`(`<q4d`)、`state()`。
- `MMGShip`:`configure_roll(cfg)`、`roll` 屬性;`reset` 重建、`set_waves` 同步、`snapshot/restore` 深拷貝(舊快照無 `roll` 鍵 → 重建)、`state_json` 的 `roll`、`state_hash` 追加。
- `scenario.validate_scenario` → `sc["ship"]["roll"]`(`normalise_roll`);`apply_scenario` 最後呼叫 `configure_roll`。
