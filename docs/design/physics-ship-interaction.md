# 船間交互作用(ship–ship interaction)— 設計與契約

> 規劃書依據:第 6.2 節運動方程式的外力項、R2「船間交互作用」;第 5.2 節 Traffic 模組(目標船為運動學目標);第 14 章確定性與 C#↔Python 一致性。
> 程式:`src/SimCore/Physics/External/ShipInteraction.cs`(`ShipInteractionModel`、`ShipInteractionCoefficients`、`InteractionTarget`、`TargetHullDefaults`、
> `HullProfile`、`ShipInteractionResult`、`ShipInteractionState`、`ShipInteractionSnapshot`)、`src/SimCore/Scenario/Scenario.cs`(`ScenarioShipInteraction`、
> `ScenarioEnvironment.ShipInteraction`、結構檢查)、`src/SimCore/Scenario/ScenarioTarget.cs`(`displacement_t`)、`src/SimCore/Engine/SimulationEngine.cs`(接線、事件、雜湊、快照、狀態)、
> `src/SimCore/Engine/EngineSnapshot.cs`(`ShipInteraction`)、`src/SimCore/Engine/SimEvent.cs`(`SimEventKinds.ShipInteraction`)、`src/SimCore/Contracts/OwnShipState.cs`(`shipInteraction`);
> Python 參考實作 `src/Tools.Calibration/simosa_brm/external.py`(`ShipInteractionModel`、`KinematicTarget`、`hull_profile`、`interaction_kernel`、`normalise_ship_interaction`)、
> `scenario.py`(`ship_interaction_issues`)、`mmg.py`(接線);測試 `tests/SimCore.Tests/ShipInteractionTests.cs`、`src/Tools.Calibration/tests/test_ship_interaction.py`;
> 跨語言夾具 `tests/SimCore.Tests/Golden/interaction_force_table.csv`、`interaction_overtaking_run.{yaml,csv}`(`export_interaction_reference.py` 產生)。

## 1. 總覽與資料來源聲明

- 範圍:目標船(`targets[]`,交通模組的運動學目標)通過、追越或對遇自船時,自船受到的流體交互作用力——surge、sway(吸引/排斥)、yaw 力矩。
  **單向**:目標船為運動學目標(航點/腳本/COLREG 由 `TrafficManager` 決定),不受自船影響;自船的力加入 MMG 運動方程式右側(與拖船/岸壁/浪相同的 `IExternalForceField` 路徑)。
- 公式結構:**Tuck & Newman(1974)型細長體理論的經驗化**。兩船各以沿中心線的線源 σ(x) = −U·S′(x) 代表(S 為剖面積曲線、U 為對水航速;艏為源、艉為匯),
  自由面以剛性蓋(雙體)處理,有限水深以海底/自由面鏡像級數(Yeung 1978 淺水理論的鏡像形式)求目標船在自船中心線上的誘導速度場;自船受力為兩項之和:
  (1) **Lagally 源項** f = −ρ σ_o(x)·u_ext(x)(∝ U_o·U_t):自船源分佈在目標船速度場中的力;
  (2) **壓力梯度/附加質量項** f = ρ(1 + k_a)·S_o(x)·a_ext(x)(∝ U_t²):目標船定常壓力場的流體加速度作用於自船排水體積(Froude–Krylov)加橫向附加質量(Morison 慣性項的 C_M = 1 + C_a)。
  第 (2) 項在自船靜止(繫泊)時仍存在,即「過往船對繫泊船的作用」;第 (1) 項在目標船靜止時為零。
- **係數為文獻量級的估計值**:兩船沒有船間交互作用模型試驗,本專案離線開發(CLAUDE.md:不連網際網路取資料),係數集中於 `ShipInteractionCoefficients`(第 4 節);
  並行吸力的無因次係數 C_Y = |Y| / (½ρ U_t² L T) 在深水 η = 0.5 為 0.012、淺水 h/T 2.1 為 0.046,落在 Vantorre 等(2002)追越模型試驗報告的量級(淺水 0.02–0.05)。
  取得模型試驗或實船觀測後應以 `coefficientScale` 或第 4 節係數校正。**未啟用的情境零力、狀態與雜湊與舊版逐位元相同。**

| 項目 | 本模型 | 狀態 |
|---|---|---|
| 線源 σ = −U S′、Lagally 源項、雙體(鏡像自由面) | Tuck & Newman 1974 深水細長體 | 依文獻結構 |
| 壓力梯度項 ρ(1 + k_a) S a_ext | Froude–Krylov + 附加質量(Morison 慣性項;Tuck–Newman 的非定常項) | 依文獻結構;k_a 為**假設** |
| 有限水深:海底鏡像級數 z = ±2kh + 積分尾項 | Yeung 1978 淺水理論的鏡像形式 | 標準方法;6 個鏡像 + 尾項(誤差 ~2e-4) |
| 剖面積曲線(平行中體 + 拋物線端,A_m 由排水體積反推) | 第 3.2 節 | **假設** |
| 核距離 δ = 0.25(B_o + B_t) | 第 3.3 節 | **假設**(避免船體相觸時發散) |
| 作用範圍 3 L、2(B_o + B_t) 與漸減帶 | 第 3.5 節 | 任務規格 + **假設**(平滑邊界) |
| 目標船排水量預設(依船型 Cb) | 第 2.2 節 | **假設** |

## 2. 情境

### 2.1 `environment.shipInteraction`(`scenario.schema.json` `properties.environment.properties.shipInteraction`)

```yaml
environment:
  waterDepth: 20
  shipInteraction:
    enabled: true           # 預設 false(有區段但未啟用與無區段相同)
    coefficientScale: 1.0   # 選填:整體倍率(≥ 0;0 = 只顯示不施力)
    eventThreshold_t: 3.0   # 選填:事件門檻(t,≥ 0;0 = 不發事件)
targets:
  - id: OVT
    shipType: cargo
    loa: 130
    beam: 22
    draft: 7.5
    displacement_t: 16490   # 選填:只供船間交互作用
    initial: { position: { x: -50, y: -200 }, heading: 0, speed: 12 }
    behaviour: { mode: hold }
```

- 結構檢查(`Scenario.StructuralIssues` 與 Python `scenario.ship_interaction_issues`,路徑 `environment.shipInteraction.*`、訊息相同):`coefficientScale`、`eventThreshold_t`
  為 ≥ 0 的有限數值(未啟用時同樣檢查)。`enabled` 非 true 視為未啟用。
- 無區段或 `enabled: false`:不建立模型,零力、`state.shipInteraction` 省略、雜湊不追加區塊(所有既有情境、夾具與黃金測試不變)。
- 動力學不接受外力(`SupportsExternalForces = false`)時不啟用。

### 2.2 目標船資料(`targets[]`;`TargetHullDefaults`、Python `target_block_coefficient`)

模型需要每艘目標的船長、船寬、吃水與排水量(方塊係數)。情境 `targets[]` 既有 `loa`(預設 100 m)、`beam`(16 m)、`draft`(5 m)、`shipType`,本輪新增選填 `displacement_t`:

| 資料 | 來源 | 無資料時 |
|---|---|---|
| 船長 L_t | `loa` | 100 m(既有預設) |
| 船寬 B_t | `beam` | 16 m |
| 吃水 T_t | `draft`(> 0) | `draft` ≤ 0 時 0.3·B_t(最少 0.5 m) |
| 方塊係數 Cb_t | `displacement_t` → Δ/(ρ·L_t·B_t·T_t) 夾在 [0.35, 0.95] | 依 `shipType`:tanker 0.82、cargo 0.72、passenger 0.62、fishing 0.55、tug 0.55、pilot 0.50、sar 0.50、pleasure 0.45、hsc 0.45、sailing 0.40、其他 0.65 |

納入計算的目標:已出現(觸發條件成立)且**不是跟隨自船的護航目標**(`follow` 模式、leader = own 的引水船/拖船;拖船力由拖船模組計算)。錨泊目標(航速 0)在範圍內但零力(第 3.4 節)。

## 3. 模型

### 3.1 幾何

自船船體座標(x 向艏、y 向右舷、原點船舯)。目標船舯 (x_t, y_t) 換算到自船座標為 (dx, dy)(`BodyFrame.FromEnu`),縱向錯位 ξ = dx/L_o、橫向間距 η = dy/L_o(正 = 目標在右舷),
相對航向 β = ψ_t − ψ_o,目標船軸向單位向量 t̂ = (cos β, sin β)。目標船站 s_j(沿其軸向,艏為正)位於 (dx + s_j cos β, dy + s_j sin β)。

### 3.2 剖面積曲線與線源(`HullProfile`)

S(x) = A_m 於平行中體 |x| ≤ L_p/2;進流/去流段 S = A_m(1 − s²),s = (|x| − L_p/2)/L_e。L_e = 1.5(1 − C_p)·L 夾在 [0.1L, 0.5L],C_p = min(Cb/C_m, 0.98)、C_m = 0.98;
A_m = Cb·L·B·T / (L − 2L_e/3)(使 ∫S dx 等於排水體積,排水量比由此進入)。沿船長等分 N = 16 站取中點,
a_i = −S′(x_i)·Δx(艏為正 = 源、艉為負 = 匯,Σa_i = 0)、S_i = S(x_i)·Δx。自船與目標船皆以此建立(自船 L = Lpp、B、T = 裝載平均吃水、Cb = 裝載方塊係數)。

### 3.3 線源核與誘導速度

目標船站 j 的雙體源強度 σ_j = U_t·a_j 在自船站 i 誘導的速度 u⃗_ij = (σ_j/2π)·r⃗_ij·G(r_ij²),r⃗_ij = 站 i − 站 j 的水平向量,
r² 以核距離 δ = 0.25(B_o + B_t) 正則化(r² → r² + δ²)。

- 深水(水深 ≥ 1000 m、≤ 0 或無限大):G = (r²)^{−3/2}。
- 有限水深 h:G = Σ_{k=−K}^{K} (r² + (2kh)²)^{−3/2} + (1 − s₀/√(r² + s₀²))/(h·r²),K = 6、s₀ = (2K + 1)h(|k| > K 的積分尾項);r ≫ h 時趨近淺水二維核 1/(h r²)。
- 壓力梯度項需要場點沿目標船軸向的導數:∂/∂s (r⃗ G) = t̂·G + r⃗·G′·2(r⃗·t̂),G′ = dG/d(r²) 同樣以級數與尾項計算。

### 3.4 力

對每艘在範圍內的目標,U_o = u(自船 surge 對水速度,倒退為負)、U_t = 目標對水速度(沿其航向):

- 誘導速度與加速度(站 i):u⃗_i = Σ_j a_j r⃗_ij G_ij;a⃗_i = −U_t Σ_j a_j ∂_s(r⃗_ij G_ij)(目標船定常場在其框架 (−U_t t̂·∇) 的流體加速度);
- f_x,i = −ρ U_o U_t/(2π)·a_o,i·u_x,i − ρ U_t²/(2π)·S_o,i·a_x,i;
  f_y,i = −ρ U_o U_t/(2π)·a_o,i·u_y,i − ρ U_t²/(2π)·(1 + k_a)·S_o,i·a_y,i(k_a = 0.8 橫向附加質量比;surge 不計附加質量);
- X = Σ f_x,i、Y = Σ f_y,i、N = Σ x_i f_y,i;再乘範圍漸減權重 w(第 3.5 節)與 `coefficientScale`;多目標相加。

物理意義:Lagally 項使同號奇點相吸(艏–艏、艉–艉)、異號相斥(艏–艉);壓力梯度項為對方艏艉高壓區推開、中體低壓區吸引。兩者合成追越的經典三階段
(對方艏到自船艉:艉被推離、艏轉向對方 → 並行:整體吸引 → 對方艏越過自船艏:艏被推離、艉被吸向對方)與對遇的艏墊(bow cushion)/並行吸引/艉吸。

### 3.5 作用範圍

|dx| < 3 L_o 且 |dy| < 2(B_o + B_t) 的目標才計算;兩個方向都自 0.8 倍限值起以 smoothstep 漸減到限值處為 0(避免力在邊界跳變)。範圍外的目標不計入 `active`。

### 3.6 事件

等效側向力 F_eq = max(|Y|, 4|N|/L_o)。F_eq ≥ `eventThreshold_t`(預設 3 t)時發 `shipInteraction` 事件(data.phase = enter、targetId = 主要目標),之後 F_eq < 0.5 × 門檻時發
離開事件(phase = exit)。進入/離開各一次(遲滯避免抖動);旗標與目標 id 進快照與雜湊。門檻 0 = 不發事件。

## 4. 係數(`ShipInteractionCoefficients`;Python `SHIP_INTERACTION_DEFAULTS`)

| 係數 | 值 | 意義 | 來源/狀態 |
|---|---|---|---|
| `Stations` | 16 | 每船站數 | 數值設定(48 站差 < 1 %) |
| `Images` | 6 | 有限水深鏡像數(每側)+ 尾項 | 數值設定(60 個鏡像差 ~2e-4) |
| `DeepDepthM` | 1000 | 視為深水的水深 | 數值設定(鏡像項 < 1e-4) |
| `Rho` | 1025 | 海水密度 | 與外力/岸壁相同 |
| `MidshipCoefficient` C_m | 0.98 | C_p = Cb/C_m | 商船典型值 |
| `SwayAddedMassRatio` k_a | 0.8 | 壓力梯度項的 (1 + k_a) | **假設**(Lewis 剖面橫向附加質量 0.6–1.0 ρS) |
| `CoreFactor` | 0.25 | δ = 0.25(B_o + B_t) | **假設** |
| `RangeLengths` / `LateralBeams` | 3 / 2 | 作用範圍 3 L_o、2(B_o + B_t) | 任務規格 |
| `TaperStart` | 0.8 | 漸減帶起點 | **假設** |
| `EventHysteresis` | 0.5 | 離開門檻 = 0.5 × 進入門檻 | **假設** |
| `MinCb` / `MaxCb` | 0.35 / 0.95 | 由排水量反推 Cb 的夾限 | 船型範圍 |
| 情境 `coefficientScale` / `eventThreshold_t` | 1 / 3 t | 整體倍率 / 事件門檻 | 教官可調 |

### 4.2 FSB 尺度量級檢查(`ShipInteractionTests.OvertakingStagesHaveClassicSignsAndMagnitude`、`HeadOnEncounterRepelsApproachingBowsAndAttractsAbreast`)

自船 FSB1 滿載(L 99 m、B 18 m、T 5.70 m、Cb 0.738、Δ 7 725 t)以 8 kn 航行;追越船 OVT:L 130 m、B 22 m、T 7.5 m、Δ 16 490 t(2.1 倍)、12 kn,左舷橫向間距 50 m(η = −0.51,舷間 30 m):

| 水深 | ξ(目標相對自船) | X(surge) | Y(sway) | N(yaw) |
|---|---|---|---|---|
| 深水 | −1.0(對方艏到自船艉) | −1.0 t | **+6.4 t**(艉被推離) | **−1 150 kN·m**(艏轉向對方) |
| 深水 | −0.4 | −5.7 t | −5.5 t | +900 kN·m |
| 深水 | 0(並行) | 0 | **−13.6 t**(吸向對方) | 0 |
| 深水 | +1.0(對方艏越過自船艏) | +1.0 t | +6.4 t | **+1 150 kN·m**(艏被推離) |
| 50 m(h/T 8.8) | 0 | 0 | −15.5 t | 0 |
| 20 m(h/T 3.5) | 0 / −1.0 | — | −30.8 t / +12.9 t | 0 / −2 460 kN·m |
| 12 m(h/T 2.1) | 0 / −1.0 | — | **−51.3 t** / +21.5 t | 0 / −4 070 kN·m |
| 9 m(h/T 1.6) | 0 | — | −68.4 t | — |

- 深水並行吸力隨橫向間距:30 m −32.6 t、40 m −20.7 t、50 m −13.6 t、70 m −4.5 t、80 m(範圍邊界)0。
- 對照舵力:FSB1 8 kn 10° 舵約 6.5 t、力矩約 3 MN·m;深水 50 m 追越的吸力約 2 倍舵力、力矩約 1/3;淺水 12 m 時吸力 8 倍、力矩 1.3 倍——與操船經驗「淺水窄距追越需提早用大舵角」一致。
- 繫泊(自船 U_o = 0)被同一船以 12 kn 通過:深水並行吸力 −10.0 t、ξ −0.6 時艏搖 +1 370 kN·m;12 m 水深 −37.5 t。
- 同尺寸船(FSB1)以 10 kn 追越:並行 −6.5 t;對遇(10 kn、50 m):艏對艏接近(ξ 1.3)推開 +0.4 t,對方中體到自船艏旁(ξ 0.4)艏吸向對方 −1 000 kN·m,並行吸引 −2.5 t。
- 引水船(20 m、15 kn、25 m 外)−0.9 t:小於事件門檻。
- 無因次並行吸力 C_Y = |Y|/(½ρU_t²LT):深水 0.012、12 m 水深 0.046。

## 5. 狀態與事件(`state.schema.json` `properties.shipInteraction`;C# 與 Python 參考實作皆輸出)

只在情境啟用 `environment.shipInteraction` 且動力學接受外力時輸出(無目標在範圍內時 `active: false`、力為 0):

| 欄位 | 單位 | 說明 |
|---|---|---|
| `active` | — | 有目標船在作用範圍內 |
| `targetId` | — | 主要目標(等效側向力最大者;無則省略) |
| `forceSurge_t`、`forceSway_t`、`momentYaw_tm` | t、t、t·m | 步末合力(船體座標;sway 向目標為吸引) |
| `xi`、`eta` | — | 主要目標的縱向錯位/橫向間距(以 L_o 無因次;無目標時省略) |
| `targets` | — | 範圍內目標數 |
| `event` | — | 已超過事件門檻(進入事件已發) |

事件 `shipInteraction`(source engine、targetId、學員站可見):data `{phase: enter|exit, targetId, forceSurge_t, forceSway_t, momentYaw_tm, xi, eta, threshold_t}`;
講評可由進入/離開時刻與力的大小檢視學員是否提早用舵、保持距離。

## 6. 引擎接線與確定性

- `ShipInteractionModel : IExternalForceField`。每步:`PreStep` 以步首的目標船狀態(上一步末推進完成)組出本步四個 RK4 階段共用的目標清單;RK4 各階段以該階段的自船狀態與步首環境取樣的有效水深計算。
- 與其他外力的組合(`SimulationEngine.Advance`):無此模組時路徑與舊版完全相同(外力模組 / 岸壁 / 和 / + 浪);有此模組時最後再相加(只有它時直接取它)。Python `mmg.step` 的 `field` 同一順序相加。
- 每步時序:… → RK4(含外力場)→ tick++ → 潮高/流更新 → 外力 PostStep → 岸壁/浪 UpdateLast → 擱淺 → **目標船推進(TrafficManager.Step)→ 交互作用 UpdateLast(自船與目標皆為步末幾何)→ 門檻事件** → BNWAS/警報 → 評估 → 雜湊。
- **雜湊**:啟用時於雜湊鏈最後(浪區塊之後)追加 7 個 int64:標記 `SHIPINTR`、事件旗標、步末 X、Y、N、ξ、η。未啟用的情境不追加(`WithoutInteractionOutputsMotionAndHashAreUnchanged`:
  `enabled: false` 與無區段逐位元相同;啟用但目標在範圍外時運動相同、雜湊因區塊而不同)。
- **快照**:`EngineSnapshot.ShipInteraction {EventActive, EventTargetId}` 只在啟用時寫入;力為純函數,還原時依情境重建、以還原後的自船與目標船重算步末結果,續跑雜湊與連續執行逐位元相同;
  進入事件之後的快照還原不重發事件,之前的快照還原後重跑會再發(`SnapshotRestoreContinuesBitIdentical`);紀錄/重播雜湊與事件序列一致(`RecordReplayReproducesHashAndEvents`)。
- 確定性:純函數、無亂數;目標船狀態本就在雜湊內(x、y、ψ、V、r)。
- 對稱性(`PortStarboardMirrorSymmetry`):目標鏡像到另一舷且 v 反號時 X 相同、Y/N 反號。

## 7. 適用範圍與外推限制

| 項目 | 適用範圍 | 超出時 |
|---|---|---|
| 間距 | 舷間 ≳ 0.5 B 到 2(B_o + B_t) | 近距以核距離 δ 正則化(線源模型在舷間 < 0.5 B 不準);範圍外零力 |
| 速度 | 兩船對水速度同量級、Fn < 0.3 | 無造波(雙體);高速船波浪交互作用不在範圍 |
| 水深 | 深水到 h/T ≈ 1.3 | 鏡像級數為勢流;h/T < 1.3 的黏性/squat 耦合未納入,力偏大 |
| 船型 | Cb 0.45–0.95、長寬比 4–8 | 剖面積曲線為假設 |
| 目標 | 運動學目標(交通模組);單向 | 目標船不受自船影響(不做雙向耦合);錨泊/靜止目標對航行中的自船零力(定常線源模型無此項) |
| 自由度 | 3(surge/sway/yaw) | 無升沉/縱搖交互(squat 互作用)、無橫搖 |
| 多船 | 線性相加 | 三船以上的二次交互不計 |

## 8. 測試

- C# `tests/SimCore.Tests/ShipInteractionTests.cs`(21 項):鏡像對稱 4 例、追越三階段方向與量級(含淺水放大、靜止目標零力、繫泊、倍率)、對遇、距離衰減/作用範圍/漸減帶/多目標相加、
  核函數鏡像級數與導數、剖面積曲線與目標船體預設、未啟用不變(無區段 = enabled false 位元相同;啟用無目標雜湊不同)、引擎施力與 `state.shipInteraction` JSON 往返、
  事件進入/離開各一次(門檻 0 / 倍率 0 不發)、護航與未出現目標排除、快照還原位元相同與事件旗標、紀錄/重播、結構檢查 3 例與預設值、跨語言夾具 2 項。
- Python `src/Tools.Calibration/tests/test_ship_interaction.py`(20 項):同上各項的 Python 版、`normalise_sections`/`validate_scenario` 帶出 `shipInteraction` 與直線等速目標(非 hold/有觸發者略過)、
  快照/還原/`reset`/`configure_external(None)`、夾具可重現(數值逐位元、事件、末狀態雜湊)。

## 9. 已知限制與後續

- Python 參考實作的目標船為直線等速;C# `TargetShip` 以 `behaviour.maxSpeed`(未給時 loa ≥ 50 m 為 20 kn、否則 30 kn)夾限航速令,初速超過上限者會指數衰減到上限。`normalise_ship_interaction` 因此略過初速超過該上限的目標(不在跨語言比對範圍),夾具情境請把目標初速設在上限內。

1. 單向:目標船不受自船影響(規格如此;雙向需交通模組的目標也走動力學)。
2. 靜止/錨泊目標對航行中自船零力(定常線源模型的二階效應未納入);繫泊自船受過往船的力已含(壓力梯度項)。
3. 係數為估計(k_a、剖面積曲線、核距離);待模型試驗或實船觀測校正,先以 `coefficientScale` 調整。
4. Python 參考實作的目標船只有直線等速(hold、無觸發);航點/腳本/錨泊/跟隨/COLREG 目標與 C# 交通模組的行為不在參考實作內(跨語言夾具用 hold 目標)。
5. 教官站/儀表尚未顯示 `state.shipInteraction`(後續可在操船台顯示力箭頭、講評列出事件)。

## 10. Python 參考實作(`simosa_brm/external.py` `ShipInteractionModel`)

- 與 C# 逐式對應、同運算順序:`hull_profile`、`interaction_kernel`(級數與尾項、導數)、`interaction_taper`、`compute`(站迴圈與累加順序相同)、`hash_bytes`(`struct.pack("<qqddddd", …)`)、`state()`、事件 data。
- 目標船 `KinematicTarget`:C# `TargetShip.Integrate` 的 hold 路徑(航向/航速指令 = 初始 → 迴轉率與航速不變、位置含均勻流),於 `post_step` 推進(自船 RK4 之後、`update_last` 之前)。
- 接線:`scenario.validate_scenario` → `environment.shipInteraction` 正規化(`external.normalise_ship_interaction`,含 hold 目標清單),同時由 `normalise_sections` 放進 `external["shipInteraction"]`,
  `MMGShip.configure_external` / `_build_external` 建立 `ship.interaction`;`step` 的外力場最後相加;步末 `post_step`(推進目標、`update_last`、事件進 `external_events`);`state_hash` 追加區塊;`state_json` 輸出 `shipInteraction`;快照/還原含 `interaction`。
- 跨語言夾具(`tests/SimCore.Tests/Golden/export_interaction_reference.py`):
  - `interaction_force_table.csv`:3 組船舶/裝載/倍率 × 3 水深(1000 m 深水、50 m、12 m)× 4 自船狀態(8 kn、斜航 6 kn、倒退 2 kn、靜止)× 12 目標(追越五個 ξ、對遇、交叉、漸減帶、範圍外、靜止、小艇)
    共 432 列;C# `GoldenForceTableMatchesPythonReference` 比對 active、ξ、η、X、Y、N 相對差 ≤ 1e-12(實測 0:逐位元相同)。
  - `interaction_overtaking_run.{yaml,csv}`:FSB1 滿載 8 kn 被 OVT 以 12 kn 自左舷 50 m 追越、深水 1000 m,80 s 右舵 5°,210 s;C# `GoldenRunMatchesPythonWithinOnePercent` 每 1 s 比對
    位置/航向/速度/交互作用力/ξ/η/目標位置(規劃書第 14 章 < 1%;實測位置差 3e-15 % 航程、力相對差 ≤ 7e-16),事件 tick/phase 相同(enter tick 1562、exit tick 8699)。
    Python `test_ship_interaction.py` 以同一路徑重算並比對末狀態雜湊。
