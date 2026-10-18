# 物理第 2 階段:拖船、錨與錨鏈、碰墊與繫纜 — 設計與契約

> 規劃書依據:第 6.2 節運動方程式的 T(拖船)、A(錨/纜)項與「拖船」「錨與纜繩」兩段描述(第 459–460 行)、第 3 章錨/鏈規格
> (FSB1 AC-14 2,140 kg × 2、Φ42 mm、各 9 節;FSB2 Spek、Φ58 mm、各 10 節;起錨速率約 21.7 m/min)、R2(拖船與錨力、繫纜)、
> 第 8.1 節 E12(錨泊/走錨)、E13(拖船協助靠泊、拖纜斷裂)、第 8.3 節第 4 類(靠泊橫向接近速度、接觸能量)、第 9.1 節(泊位、碰墊、繫船柱)、第 14 章(確定性)。
> 程式:`src/SimCore/Physics/External/`(`BodyForce.cs`、`TugModel.cs`、`Catenary.cs`、`AnchorModel.cs`、`BerthModel.cs`、`ExternalForceModel.cs`)、
> `src/SimCore/Physics/IShipDynamics.cs`、`Rk4Integrator.cs`、`Mmg/MmgDynamics.cs`、`PlaceholderDynamics.cs`、`src/SimCore/Engine/SimulationEngine.cs`、
> `src/SimCore/Scenario/ScenarioExternalForces.cs`、`src/SimCore/Contracts/ExternalForceState.cs`;資料 `data/ships/*/particulars.json`(`anchors`)、`data/areas/mailiao-berth.json`;
> 測試 `tests/SimCore.Tests/TugTests.cs`、`AnchorTests.cs`、`BerthTests.cs`、`ExternalForceEngineTests.cs`。

## 1. 總覽

| 模組 | 力 | 跨步狀態(快照、雜湊) | 每階段重算 |
|---|---|---|---|
| 拖船 `TugUnit` | 固定連接點的水平力向量(方向相對船艏) | 模式、狀態、實際出力、實際/命令方向、出力令、計時、斷纜旗標 | —(整步固定) |
| 錨 `AnchorUnit` ×2 | 錨鏈孔處的懸鏈線水平張力(指向錨) | 模式、鏈長、目標鏈長、著底、錨位 (x, y)、走錨/帶上待發/鏈端/滑出/停轉旗標 | 懸鏈線(錨鏈孔位置隨船) |
| 碰墊 `BerthModel` | 非線性反力 + 黏性阻尼 + 面板摩擦,只推不拉 | 每個碰墊的接觸事件狀態(接觸中、峰值已報、最大壓縮、接近速度) | 壓縮量與接觸點 |
| 碼頭硬接觸 | 懲罰彈簧 + 阻尼(只推) | 接觸中、最大穿入 | 船體頂點穿入量 |
| 繫纜 `MooringLine` | 導纜孔→繫船柱的張力(只拉不推) | 狀態、繫船柱、未受力纜長 L₀、絞纜目標張力、待鬆長度 | 伸長與伸長速率 |

### 1.1 外力介面(MMG 外力 X/Y/N)

- `IExternalForceField.Evaluate(state, env) → BodyForce(X, Y, N)`:船體座標(x 向艏、y 向右舷、原點船舯;N 正 = 艏向右)。
  ENU 力 (F_E, F_N) 作用於船體點 (x_b, y_b):X = F_E sin ψ + F_N cos ψ、Y = F_E cos ψ − F_N sin ψ、N = x_b·Y − y_b·X。
- `IShipDynamics.SupportsExternalForces`(預設 false)與 `Derivative(t, s, env, c, external)`(預設丟 `NotSupportedException`)。
  - **MMG**(`mmg/1`):外力直接加到 X_H+X_P+X_R+X_T+X_W 等的合力,再經 `Accelerations`(含 x_G 耦合)。
  - **暫代模型**(`placeholder-nomoto/1`):以概略質量接受:u̇ += X/(1.05 m)、v̇ += Y/(1.8 m)、ṙ += N/(1.5 m (0.25 L)²),m = 排水量
    (缺資料時 ρ L B T C_b)。Nomoto 回復項仍作用,只供管線測試。
  - 其他(`SupportsExternalForces = false`):引擎不呼叫外力路徑,`tugControl`/`anchor`/`mooring` 一律 `commandRejected {reason: externalForcesUnsupported}`。
- `Rk4Integrator.Step(..., IExternalForceField)`:四個階段各以該階段狀態重算外力(錨鏈、碰墊、纜繩隨位置變化),拖船出力與錨位/鏈長/纜長整步固定。

### 1.2 每步時序(`SimulationEngine.Advance`)

陣風 → 環境漸變 → 自動舵 → 致動器 → **外力 PreStep(拖船出力一階、方向迴轉、計時;以步首對水速度計效能)** → RK4(含外力場)→ tick++ →
**外力 PostStep(錨:鏈長/著底/走錨/帶上;泊位:碰墊事件、硬接觸事件、絞/鬆纜、斷纜)→ 外力事件(source engine、visible)** → 擱淺 → 交通 → BNWAS/警報 → 評估 → 雜湊 → 廣播 → 快照。
指令(含 `injectFault tugLineBreak`)在推進前處理。

### 1.3 確定性、雜湊與快照

- 無亂數;所有計時以 tick;懸鏈線以固定 80 次對數二分(不依賴收斂判斷)。
- `ExternalForceModel.Active = 動力學支援外力 ∧ (情境有拖船 ∨ 有泊位 ∨ 錨曾被使用)`。**不 Active 時走舊積分多載、不追加雜湊位元組**:
  情境沒有 `tugs`/`berth`、錨未拋(只有 `environment.seabed` 或 `anchors{holdingFactor}` 也一樣)者雜湊與舊版逐位元相同(`ExternalForceEngineTests.UnusedSectionsKeepLegacyHashUntilAnchorIsUsed`、既有 `HashCompatibilityTests`)。
  被拒的外力指令不算使用。「錨曾被使用」為黏著旗標(進快照):被接受的 `anchor` 指令或情境初始已錨泊。
- Active 時於雜湊鏈最後追加:標記 `EXTFORCE`、AnchorsUsed、每艘拖船 8 個 int64、每個錨 6 個、泊位(硬接觸 2 + 每碰墊 3 + 每纜 5)。
- 快照 `EngineSnapshot.External`(選填;**Version 仍為 3**,既有測試以 3 為準):Active 時寫入;還原時先依情境重建外力模組(初始狀態)再套用快照,
  缺此欄位的舊快照即為初始狀態。JSON 往返位元一致(`SnapshotRestoreContinuesBitIdentical`)。
- 紀錄:三個新指令走一般 `input` 行;`berth.file`+`id` 在載入時內嵌為 `berth.definition`,header 的情境因此含完整泊位定義,重播不讀檔(`RecordReplayReproducesHashAndEvents`)。

## 2. 拖船(T 項)

### 2.1 情境 `tugs[]`

```yaml
tugs:
  - id: TG1
    name: 麥寮1號
    bollardPull_t: 40          # 繫柱拖力(t)
    attach: fwd                # fwd | aft | bow | stern | fwdPort | fwdStbd | midPort | midStbd | aftPort | aftStbd | {x, y}
    lineLength_m: 40
    modes: [push, pull]        # 可用模式(預設兩者)
    riseTime_s: 30             # 出力一階上升時間常數(20–40 s)
    turnRate_degPerSec: 3      # 方向變更速率;省略 = repositionSpeed_kn / lineLength_m(5 kn / 40 m ≈ 3.7 °/s)
    pushFullUpTo_kn: 3         # 推頂效能 100 % 的速度上限
    pushHalfAt_kn: 5           # 推頂效能 50 % 的速度
    pullFullUpTo_kn: 4
    pullHalfAt_kn: 7
    modeChange_s: 60           # 推 ↔ 拉換位(無出力)
    connect_s: 90              # free → push/pull 帶纜/靠上
    initial: { mode: standby, direction_deg: 90, power_pct: 0 }   # 初始 push/pull 時出力自穩態開始
```

具名點(L = 垂標間長、B = 型寬,估計):fwd/bow = (+0.45 L, 0)、aft/stern = (−0.45 L, 0)、fwdPort/fwdStbd = (+0.40 L, ∓0.45 B)、
midPort/midStbd = (0, ∓0.5 B)、aftPort/aftStbd = (−0.40 L, ∓0.45 B)。

### 2.2 方程式

- 目標出力 F* = BP × power × e(V)(status = working;否則 0),V = 自船對水速度 √(u² + v²)(步首)。
- 一階上升(離散解析):F_{n+1} = F_n + (F* − F_n)(1 − e^(−Δt/τ)),τ = riseTime_s。
- 方向:實際方向以 turnRate·Δt 為上限走最短弧趨近命令方向(拖船繞行)。
- 速度效能:e(V) = 1(V ≤ V_full);否則 e = 1 / (1 + ((V − V_full)/(V_half − V_full))²)。預設推頂 3 / 5 kn(5 kn 時 50 %、7 kn 時 20 %)、拖拉 4 / 7 kn。
  **假設**:依 Hensen《Tug Use in Port》的定性描述(推頂拖船在自船約 5 kn 以上難以保持位置與角度、拖纜作業可稍高),數值為估計,情境可覆寫。
- 力作用於連接點:X = F cos θ、Y = F sin θ(θ 相對船艏順時針),N = x_a·Y − y_a·X。推與拉的物理差別只在效能曲線、是否需要拖纜與換位時間(力方向由命令給定)。

### 2.3 狀態機

| status | 意義 | 進入 |
|---|---|---|
| standby | 已帶纜/靠妥待命,出力衰減到 0 | 初始、`mode: standby` |
| connecting | 帶纜/靠上中(connect_s),無出力 | free/parted 後下 push/pull/standby |
| repositioning | 推 ↔ 拉換位(modeChange_s),無出力 | 已就位(working 或 standby)且就位工作側與新命令相反 |
| working | 出力中 | push/pull(已連接) |
| free | 已解纜,出力立即 0 | `mode: free`、斷纜故障清除後 |
| parted | 拖纜斷裂(拉中或待命中) | `tugLineBreak` |

**拖纜斷裂** `injectFault {name: tugLineBreak, args: {id}}`(id 省略 = 第一艘拉中者,否則第一艘已連接者):拉中/待命者出力立即 0、`status parted`、`mode free`;
推頂者續推(推頂不需拖纜)但 `lineParted = true`。故障中 `pull` 被拒(`tugLineParted`);`clearFault` 後 parted → free,可重新帶纜。發事件 `tugLineParted`(source engine、visible)。
parted 的拖船收到只含方向/出力(mode 省略)或 `mode: free` 的指令時保持 `parted`(只更新命令值)。`args.id` 指定未定義的拖船時以 `commandRejected {command: injectFault, reason: unknownTug}` 拒絕(動力學不支援外力時 `externalForcesUnsupported`),不加入 `faults[]`、不發 `faultInjected`;id 省略時行為不變。

**就位工作側**(`TugUnit.LastWorkingMode`,進快照與雜湊):push/pull 被接受時設為該模式,`free` 與斷纜(拉中/待命者)清除為 standby(未就位)。push ↔ pull 換位以它判定,因此「推 → standby → 拉」仍需 modeChange_s;換位或帶纜中下 standby,計時照走(結束時成為 standby)。
情境 `initial.mode` 為 push/pull 時須包含在 `modes` 內(modes 省略 = 兩者皆可),否則情境驗證錯誤(與 `tugModeUnavailable` 一致)。
不產生 BAM 警報(拖船以 VHF 報告;fault-catalogue 無 `alert`)。

## 3. 錨與錨鏈(A 項)

### 3.1 資料(`particulars.json anchors`;估計欄位以 `source` 標「估計」)

| 欄位 | FSB1 | FSB2 | 來源 |
|---|---|---|---|
| 型式 / 抓著類別 | AC-14 / hhp | Spek / stockless | 交船文件 / **估計**(`holdingClass`) |
| 錨重 | 2,140 kg | 3,300 kg(`massEstimate_kg`) | 交船文件 / **估計**:IACS UR A1 舾裝數表 Φ58 U2 → 3,300 kg |
| 鏈徑、節數 | Φ42、左右各 9 節 | Φ58、各 10 節 | 交船文件 |
| 一節 | 27.5 m | 27.5 m | `shackleLength_m` |
| 鏈單位質量 | 38.6 kg/m | 73.7 kg/m | **估計**:有檔錨鏈 0.0219 d² |
| 鏈斷裂負荷 | 1,405 kN | 2,595 kN | **估計**:U2 0.0196 d² (44 − 0.08 d) kN |
| 起錨速率 | 21.7 m/min | 21.7 m/min(`heavingRateEstimate_mPerMin`) | FSB1 海報 / **估計**:沿用 No.1 |
| 絞盤拉力(過載 1.5 倍) | 75 kN | 143 kN | **估計**:UR A3 U2 42.5 d² N |
| 剎車握持力 | 632 kN | 1,168 kN | **估計**:鏈斷裂負荷 45 % |
| 錨鏈孔 (x, \|y\|, 龍骨上高) | 46.5、2.7、7.5 m | 48.9、3.0、10.6 m | **估計**:x = 0.5 L − 0.03 L(艏柱後方)、0.15 B、型深 − 0.5 m(甲板高度) |

程式缺欄位時以相同估計式補齊(`AnchorParticulars.EstimatedFields` 列出,Host 啟動時印出)。鏈水中單位重 w = m'·g·(1 − 1025/7850)。
錨鏈孔至海床垂直距離 h = 水深 + (錨鏈孔龍骨上高 − 艏吃水)。

### 3.2 準靜態懸鏈線(`Catenary.Solve`)

參數 a = H/w(H = 水平張力),X = 錨鏈孔至錨的水平距離,L = 送出鏈長:

- **鬆弛**:X ≤ L − h → H = 0(多出的鏈堆在海床)。
- **躺底**(錨端水平):s = √(h² + 2 h a)、X = (L − s) + a·asinh(s/a);躺底長 L_b = L − s;錨鏈孔張力 T = H + w h。適用到 s = L(a_max = (L² − h²)/(2h))。
- **全懸空**:√(L² − h²) = 2a·sinh(X/(2a));錨端 x₁ = a·atanh(h/L) − X/2,上拔角 θ = atan(sinh(x₁/a)),T = H cosh((x₁ + X)/a)。
- **拉直**:X ≥ √(L² − h²) → H 取上限(鏈斷裂負荷;數值安全)。
- X(a) 單調遞增 → 對數二分 80 次。兩區在 a = a_max 時連續。作用於船的力 = H(水平、指向錨),作用點 = 錨鏈孔;垂直分量不計(3 自由度)。

### 3.3 抓地力與走錨

抓地力 P = 倍率 × [m_a g λ(底質, 錨型) × max(0, 1 − θ/15°) + μ(底質) w L_b](倍率 = 情境 `anchors.holdingFactor`):

| 底質 `environment.seabed` | λ(hhp,AC-14) | λ(stockless,Spek) | μ(躺底鏈) |
|---|---|---|---|
| mud | 5 | 2.5 | 0.6 |
| sand(預設) | 7 | 3.5 | 0.75 |
| clay | 8 | 4 | 0.8 |
| rock | 1.5 | 1 | 0.5 |

(**估計**:一般船藝/錨具文獻的典型值,待以實船錨泊經驗校正。)

- H > P → 走錨(`anchorDragging`):錨沿水平方向朝錨鏈孔以 v = v_max · min(1, (H − P)/(0.25 P)) 移動(v_max = `anchors.dragSpeed_mps`,預設 0.5 m/s);
  H ≤ 0.95 P → 停止(`anchorHolding`)。
- 錨鏈孔張力 > 剎車握持力 → 鏈被拉出:ΔL ∈ (0, 1 m/s·Δt],二分使張力回到握持力(`anchorBrakeSlipping`,每次滑出一次事件)。到鏈端後不再滑出。
- 起錨中絞盤停轉(張力 > 過載拉力)時,絞盤握持上限與剎車相同:張力 > 剎車握持力時以同樣方式滑出(仍為 heaving/停轉;張力降到過載拉力以下後續收)。**假設**:停轉時馬達剎車/鏈輪剎車握持,絞盤不比剎車強(力的一致性)。

### 3.4 指令與狀態機

| action | 行為 |
|---|---|
| `letGo` | 剎車放開,鏈以 2 m/s 送出(錨落下、著底後續送)至目標長度(省略 = 鏈端)後剎車;發 `anchorLetGo`。錨已著底或仍在送出/鬆鏈中 → `anchorAlreadyOut`;懸吊中(起錨到離底後)可再拋,但目標短於目前鏈長 → `invalidTarget`;送出時鏈長只增不減 |
| `veer` | 以 0.5 m/s 鬆鏈到目標後剎車 |
| `heave` | 絞盤以起錨速率收鏈;錨鏈孔張力 > 絞盤過載拉力時停轉(`anchorWindlassStalled`);目標省略 = 收到 0(`anchorHoused`) |
| `brake` | 剎車(停在目前鏈長) |
| `release` | 放開剎車自由送出(到鏈端或下 brake) |

鏈長 > h 時錨著底(錨位 = 當時錨鏈孔的水平投影);起錨使鏈長 ≤ h 時離底(`anchorAweigh`)。letGo 後首次「剎車握持、未走錨、H > 0、對地速度 < 0.15 m/s」發 `anchorBroughtUp`。
送出到鏈端發 `anchorChainFullyOut`(之後 `veer`/`release` 被拒 `chainFullyOut`)。

`status`:stowed(收存)| running(送出中)| holding(剎車、著底、未走錨)| dragging | heaving(著底中起錨)| aweigh(錨離底:起錨中或懸吊)。

情境初始已錨泊:`anchors: { starboard: { length_shackles: 4, bearing_deg: 30 } }` → 錨在錨鏈孔 bearing 方向(預設初始航向)、水平張力 ≈ 0.25 × 錨抓力處,剎車握持。

## 4. 泊位、碰墊與繫纜

### 4.1 泊位檔(`data/areas/*.json`)與情境參照

```json
{
  "id": "mailiao-berth", "source": "示意…",
  "fenderTypes": { "cone-1000": { "height_m": 1.0, "width_m": 2.5, "ratedReaction_kN": 1000, "ratedDeflection_pct": 72, "ratedEnergy_kNm": 551.4,
                                  "curve": [[0, 0], [5, 19], …, [72, 100]], "damping_kNs_per_m": 300, "friction": 0.2 } },
  "berths": [ { "id": "MLO-OIL-3", "quay": [ {"lat": …, "lon": …}, {"lat": …, "lon": …} ], "waterSide": "right", "quayStiffness_kNpm": 20000,
                "fenders": [ { "id": "F01", "type": "cone-1000", "chainage_m": 20 }, … ],
                "bollards": [ { "id": "B1", "chainage_m": 10, "setback_m": 3, "swl_t": 50 }, … ] } ]
}
```

情境:`berth: { file: data/areas/mailiao-berth.json, id: MLO-OIL-3, lines: [...] }`(載入時內嵌為 `definition`;也可直接寫 `definition`,單元測試用本地 {x, y})。
碼頭面為折線,`waterSide` = 沿折線方向看水域在哪側;碰墊/繫船柱以沿線里程 `chainage_m` 定位,繫船柱向陸側退縮 `setback_m`。
`data/areas/mailiao-berth.json` 為**示意值**(碼頭線 085°、12 個 H 1.0 m 錐型碰墊間距 20 m、7 個 50 t 繫船柱;放在 E07 航線 BERTH 點附近),非港方資料。

### 4.2 碰墊接觸

- 船體外形:11 點多邊形(LOA × B,船舯為原點;平直段 x ∈ [−0.8, +0.5]·LOA/2,艏部收窄、艉部倒角)。
- 幾何:船體多邊形與碰墊面板帶(沿碼頭 ±width/2)交集中離碼頭面最近的距離 d → 壓縮 δ = H − d;反力沿碰墊軸線(碼頭法向、通過碰墊中心)作用。
- 力:F_n = max(0, R(δ) + c·v_n)(v_n = 接觸點對地速度朝碼頭分量;**只推不拉**),面板摩擦 F_t = −μ F_n tanh(v_t / 0.05 m/s)。
- R(δ):壓縮 % – 反力 % 分段線性曲線 × (H, R_rated);超過最後一點以觸底剛度 20·R_rated/H 外插(過載)。能量 E(δ) = ∫₀^δ R dδ(解析分段積分)。
  `ratedEnergy_kNm` 未給時 = E(額定壓縮)。阻尼 c 為以黏性近似的橡膠遲滯(示意泊位 300 kN·s/m);事件能量以靜態曲線計。
- 事件 `fenderContact`:每次接觸一次,於壓縮由增轉減(峰值)時發出,短暫接觸則在分離時發出。
  `data {fenderId, normalSpeed_mps(接觸開始時), energy_kNm = E(δ_max), maxReaction_kN = max_{[0,δ_max]} R, maxDeflection_m, deflection_pct, ratedEnergy_kNm, ratedReaction_kN, overload}`;
  overload = δ_max > 額定壓縮 或 E > 額定能量。
- 驗證(質點 + 線性碰墊,`BerthTests.FenderEnergyMatchesMassSpringAnalytic`):ΣE = ½ m v₀²、δ_max = v₀ √(m/Σk)、R_max = k δ_max(誤差 < 0.5 %)。

### 4.3 碼頭硬接觸

船體頂點穿過碼頭面(沿線段範圍內、d < 0)→ F = max(0, k_q·(−d) + c_q·v_n),k_q = `quayStiffness_kNpm`(預設 2×10⁴ kN/m)、c_q = √(k_q m)(阻尼比 0.5)。
接觸開始發 `quayContact {berthId, penetration_m, normalSpeed_mps}`;`state.berth.quayContact`。

### 4.4 繫纜(情境 `berth.lines[]`)

```yaml
lines:
  - { id: HEAD, fairlead: fwdPort, bollard: B6, mbl_t: 50, breakStrain: 0.18, exponent: 1.5, length_m: 150,
      heaveRate_mPerMin: 15, winchPull_t: 10, pretension_t: 2, initial: stowed }   # initial fast:開始時已繫(需 bollard)
```

- 張力:ε = D/L₀ − 1;ε > 0 時 T_s = MBL (ε/ε_b)^n,T = max(0, T_s + k(ε)·τ·Ḋ)(Kelvin–Voigt,τ = 1 s,k = dT_s/dD),上限 1.5 MBL;ε ≤ 0 時 0(**只拉不推**)。
  力沿導纜孔→繫船柱方向作用於導纜孔。
- `makeFast {bollard?, tension_t?}`:L₀ = D/(1 + ε_b (T_pre/MBL)^(1/n));D > length_m 拒絕(`bollardOutOfReach`)。
- `heave {tension_t}`:L₀ 以 heaveRate 縮短至 T_s ≥ min(tension_t, winchPull_t) → fast。`slack {length_m=2}`:L₀ 以同速率放長。`letGo`:解纜。
- T > MBL → 斷裂 `mooringLineParted {lineId, tension_t, mbl_t, bollardId}`,狀態 parted(不能再 makeFast)。
- 纜繩 MBL/應變預設為示意值(聚酯/聚丙烯混紡 ε_b ≈ 0.18);FSB1/FSB2 繫泊佈置與纜繩規格取得後應在情境以實際值給定。

## 5. 狀態(`state.schema.json`;C# 產生,Python 參考實作同樣輸出,見第 10 節)

```json
"tugs": [
  { "id": "TG1", "name": "麥寮1號", "mode": "push", "status": "working", "force_t": 25.3, "direction_deg": 270, "directionOrder_deg": 270,
    "power_pct": 50, "efficiency": 1.0, "bollardPull_t": 40, "lineParted": false,
    "pos": { "lat": 23.8083, "lon": 120.1702, "x": 2008.3, "y": 925.1 } }
],
"anchors": [
  { "side": "port", "status": "stowed", "length_m": 0, "length_shackles": 0, "maxLength_m": 247.5, "tension_t": 0, "horizontalTension_t": 0,
    "onBottom_m": 0, "holding_t": 0, "brake": true, "fullyOut": false },
  { "side": "starboard", "status": "holding", "length_m": 137.6, "length_shackles": 5.0, "maxLength_m": 247.5, "tension_t": 4.1,
    "horizontalTension_t": 3.6, "onBottom_m": 88.2, "holding_t": 17.3, "brake": true, "fullyOut": false,
    "position": { "lat": 23.8012, "lon": 120.1500, "x": 2.7, "y": 132.9 } }
],
"berth": {
  "id": "MLO-OIL-3", "name": "麥寮油碼頭 3 號泊位(示意)", "quayContact": false,
  "contacts": [ { "fenderId": "F06", "deflection_m": 0.21, "deflection_pct": 21.0, "reaction_kN": 694.0, "overload": false } ],
  "lines": [ { "id": "HEAD", "status": "fast", "tension_t": 2.0, "mbl_t": 50, "bollardId": "B6", "length_m": 45.2 } ]
}
```

`tugs` 只在情境有拖船時輸出;`anchors` 在 particulars 有錨資料時一律輸出(收存者無 `position`);`berth` 只在情境有泊位時輸出。
`tugs[].pos` 為示意顯示位置(拉:連接點沿力方向一個纜長;推:反方向半船寬 + 15 m),拖船本身不是交通目標,不進雷達/AIS。

## 6. 指令與事件

### 6.1 指令(`command.schema.json` `$defs.tugControlArgs/anchorArgs/mooringArgs`)

```json
{ "type": "tugControl", "args": { "id": "TG1", "mode": "push", "direction_deg": 270, "power_pct": 50 } }
{ "type": "tugControl", "args": { "id": "TG2", "mode": "standby" } }
{ "type": "anchor", "args": { "side": "starboard", "action": "letGo", "targetLength_shackles": 4 } }
{ "type": "anchor", "args": { "side": "starboard", "action": "veer", "length_m": 150 } }
{ "type": "anchor", "args": { "side": "starboard", "action": "heave" } }
{ "type": "mooring", "args": { "lineId": "HEAD", "action": "makeFast", "bollard": "B6", "tension_t": 2 } }
{ "type": "mooring", "args": { "lineId": "HEAD", "action": "heave", "tension_t": 8 } }
{ "type": "injectFault", "args": { "name": "tugLineBreak", "args": { "id": "TG2" } } }
```

`tugControl` 的 `mode`/`direction_deg`/`power_pct` 省略 = 不變。三個指令不重設 BNWAS(非駕駛台操作:拖船由 VHF、錨/纜在艏艉甲板)。時間表動作不含這三個指令
(時間表可用 `injectFault tugLineBreak`)。

### 6.2 事件(紀錄 `event` 行;`itemId` = 拖船 id / 錨側 / 碰墊 id / 纜 id)

| kind | source | data |
|---|---|---|
| `tugCommand` | 指令來源 | `{id, mode, from, status, direction_deg, power_pct}` |
| `tugLineParted` | engine | `{id, force_t, mode, reason: "fault", stillPushing}` |
| `anchorLetGo` / `anchorCommand` | 指令來源 | `{side, action, targetLength_m, targetLength_shackles, length_m, status}` |
| `anchorBroughtUp` | engine | `{side, length_m, tension_t}` |
| `anchorDragging` / `anchorHolding` | engine | `{side, tension_t, holding_t, length_m}` |
| `anchorAweigh` / `anchorHoused` | engine | `{side, length_m}` / `{side}` |
| `anchorChainFullyOut` | engine | `{side, length_m, shackles}` |
| `anchorBrakeSlipping` | engine | `{side, tension_t, brakeHolding_t, length_m}` |
| `anchorWindlassStalled` | engine | `{side, tension_t, windlassLimit_t}` |
| `fenderContact` | engine | 見第 4.2 節 |
| `quayContact` | engine | `{berthId, penetration_m, normalSpeed_mps}` |
| `mooringCommand` | 指令來源 | `{lineId, action, status, bollardId, tension_t, length_m, restLength_m}` |
| `mooringLineParted` | engine | `{lineId, tension_t, mbl_t, bollardId}` |

範例:

```json
{"kind":"fenderContact","tick":1972,"t":39.44,"source":"engine","itemId":"F06","message":"碰墊 F06 接觸","visible":true,"seq":3,
 "data":{"fenderId":"F06","normalSpeed_mps":0.21,"energy_kNm":100.2,"maxReaction_kN":751.0,"maxDeflection_m":0.241,"deflection_pct":24.1,
         "ratedEnergy_kNm":551.4,"ratedReaction_kN":1000,"overload":false}}
{"kind":"anchorDragging","tick":2210,"t":44.2,"source":"engine","itemId":"starboard","message":"starboard 走錨","visible":true,"seq":5,
 "data":{"side":"starboard","tension_t":21.4,"holding_t":6.3,"length_m":110}}
{"kind":"commandRejected","tick":3000,"t":60,"source":"instructor","message":"tugControl:tugLineParted","seq":9,
 "data":{"command":"tugControl","reason":"tugLineParted","args":{"id":"TG2","mode":"pull","direction_deg":180,"power_pct":100}}}
```

拒絕原因(`CommandRejectReasons`):`externalForcesUnsupported`、`unknownTug`、`unknownTugMode`、`tugModeUnavailable`、`tugLineParted`、`invalidPower`、
`noAnchorData`、`unknownAnchor`、`anchorAlreadyOut`、`anchorStowed`、`chainFullyOut`、`invalidTarget`、`unknownAction`、
`noBerth`、`unknownMooringLine`、`unknownBollard`、`bollardOutOfReach`、`mooringLineNotFast`、`mooringLineParted`。

## 7. 已知限制與偏離

- Python 參考實作已實作拖船、錨、碰墊與繫纜(第 10 節;2026-10-18 前 `src/Tools.Calibration` 忽略 `tugs`/`anchors`/`berth`/`environment.seabed`)。
- 3 自由度:錨鏈/纜繩的垂直分量、橫搖/縱傾、錨鏈彈性與動態(鏈的慣性、snatch)不計;鏈不會斷(拉直時張力以鏈斷裂負荷為上限);錨上拔只折減抓地力。
- 走錨以有限速率的運動學模型表示(不模擬錨的翻轉/重新入土);抓著係數與鏈摩擦為典型估計值。
- 拖船為作用於固定點的力向量,沒有拖船本身的運動學、俥流(wash)與自船的交互作用、纜角對出力的影響;推與拉不限制力方向(由教官給合理方向)。
- 碰墊:接觸點以碰墊軸線近似;船體外形為 11 點多邊形(不含外飄、舭部);碰墊在泊位檔中只有一種水平位置(無高度/潮位);碼頭硬接觸以懲罰彈簧近似。
- 繫纜:直線(無垂度)、無纜重;絞纜機無剎車滑出模型。
- 暫代模型的外力響應為概略值(第 1.1 節);定量評估以 MMG 為準。
- ~~評估模組尚未把 `fenderContact` 能量/接近速度、`anchorLetGo` 時間做成指標~~(2026-10-17 已完成,見第 9 節 `BerthingTracker`、summary.berthing / anchoring)。
- 時間表動作尚無 `tugControl`/`anchor`/`mooring`(只可注入 `tugLineBreak`)。

## 8. 測試

| 檔案 | 內容 |
|---|---|
| `TugTests` | 一階上升(30 s = 1 − e⁻¹)、方向迴轉速率與最短弧、速度效能(5 kn 50 %、7 kn 20 %)、換位/帶纜時間、MMG 中艏拖船推 → v > 0 且 r > 0、8 kn 推頂效能下降、斷纜(拉中失力、推頂續推、故障中拒拉、清除後帶纜)、拒絕原因、情境檢查與 JSON 往返 |
| `AnchorTests` | 懸鏈線解析幾何(y = a(cosh − 1)、弧長、T = H + wh)、單調性與區域連續、全懸空方程式、鬆弛/拉直;particulars 與估計;抓地/走錨門檻與遲滯、拖動速率上限;起錨 60 s = 21.7 m、絞盤停轉;剎車滑出;鏈端;MMG 流中拋錨 → 帶上 → 起錨 → 離底 → 收妥;拖船拉動造成走錨與重新抓住 |
| `BerthTests` | 碰墊曲線反力/能量/觸底;質點 + 線性碰墊的 ½ m v² 能量、δ_max、R_max;只推不拉(含阻尼);過載;硬接觸;繫纜靜平衡(張力 = 拖力、應變式)、絞纜、斷裂、拒絕;麥寮示意泊位(檔案解析、header 內嵌、MMG 拖船推靠、繫纜後靜止) |
| `ExternalForceEngineTests` | 舊情境/未使用區段雜湊與舊版相同(MMG 與暫代)、拋錨後才改變;快照 JSON 往返後續跑位元一致與事件序列;紀錄 → 重播雜湊/事件一致;暫代模型接受、不支援模型拒絕;契約欄位 |
| `BerthingAssessmentTests` | 靠泊/錨泊指標:質點 + 線性碰墊的艏/舯/艉接近速度 = v0、能量比 = ½mv²/2/額定;推靠中反彈再碰回不是新的靠上、拉離 ≥ 0.5 m 再推回才是;繫妥後的接觸不是接近接觸;繫妥時船位偏差與 `berthing.allFastPositionTolerance_m` 容許帶;容許帶引用 `berthing.approachSpeed_mps`(warn)/`approachSpeedFail_mps`(fail);碼頭硬接觸 hardFail(event 規則);斷纜張力/MBL;拖船工作時間與最大出力;拖纜斷裂次數;開始即靠在碰墊上的 initial 接觸段;快照還原續跑 summary 相同;無泊位/錨的情境 summary 不含新區塊;放錨速度、帶上鏈長/水深與 `anchoring.scopeMultiplier` 容許帶;走錨到首個反應時間;紀錄 → 重播 summary 一致 |
| `BerthingScenarioTests` | E12/E13/E14 載入、參數與示意泊位/錨地檔一致;固定腳本的關鍵事件序列、必要事件、summary.berthing / anchoring;紀錄 → 重播雜湊/事件/summary 一致;E12 鏈長 7/9 節不因帶上較晚被扣分(錨妥回報);E13 參考腳本(拖船橫向/艏向/縱向控制)allFast 前四纜皆 fast、無 bollardOutOfReach、船舯在泊位中心 ± 15 m;E14 不操船時繫泊中擺盪不是靠上、`maxLateralSpeed` 為 null、從未離開泊位;E14 參考離泊腳本的艉擺接觸不是再碰回(`maxReContactSpeed` null、`reContactSpeed` ok、100 分) |
| `BerthingAssessmentTests.ReContactAfterDepartureIsSeparateFromApproachSpeed` | 質點靠上 → 離開 > `departedDistance_m` → 推回:再碰回標記、`maxReContactSpeed_mps` = 解析速度、容許帶 warn、紀錄重播一致、舊 summary/快照不寫新欄位 |
| `EngineStartFailureTests` | engineStartFailure(主機起動失敗/延遲,E14):attempts、delay_s、持續失敗 3 次封鎖、換向點火、暫代模型(含故障未成立時停俥後才注入、清除後再注入)、mainEngine 注入/清除不解除封鎖、快照與重播 |

## 9. 靠泊與錨泊評估(`src/SimCore/Assessment/BerthingTracker.cs`;規劃書第 8.3 節第 4 類)

只讀引擎狀態與上表事件,不改物理、不用亂數;狀態進評估快照(`AssessmentSnapshot.Berthing`),**不進雜湊鏈**(與其他評估累計值相同),
故未使用泊位/拖船/錨的情境雜湊、事件序列與 summary 皆與舊版相同(summary 不輸出新區塊)。

### 9.1 量測定義

| 項目 | 定義 |
|---|---|
| 接觸段 | 船體與碰墊/碼頭由「無接觸」變為「接觸」到不再接觸;以每秒評估步取樣或 `fenderContact` / `quayContact` 事件開始,取樣不再接觸時結束 |
| 靠上(landing) | 一次靠泊接近:**第一次接觸段**,或**未繫妥且所有碰墊都已離開碰墊面 ≥ 0.5 m**(`BerthingTracker.LandingClearanceM`,每秒取樣船體外形到碰墊面的距離)之後的接觸段。靠妥後在碰墊上的反覆輕碰、拖船推靠中的反彈、繫泊中在纜繩上的擺盪都不是新的靠上(2026-10-17 審查修正:E14 不操船 600 s 原本產生 24 筆 landing) |
| 艏/舯/艉橫向接近速度 | 船體點 (+LOA/2, 0)、(0, 0)、(−LOA/2, 0) 的對地速度(含流)在最近碼頭線段法向(朝碼頭為正)的分量;取**接觸開始前最後一個無接觸的每秒取樣**(≤ 1 s 前,未受碰墊反力影響)。情境開始即接觸者 `initial: true`、速度為 null。**僅供講評**:端點含偏擺分量 r·x,比實際接觸點的法向速度大,不併入 `maxLateralSpeed` |
| 碰墊接觸 | 每個 `fenderContact` 一筆:接觸點法向接近速度(接觸開始時,m/s 與 kn)、靜態曲線能量 E(δ_max)、額定能量與能量比 E/E_rated、最大反力、壓縮 %、過載;`approach` = **接近接觸**:未繫妥,且該碰墊自上次接觸(或情境開始)以來曾離開 ≥ 0.5 m(先靠上艏端、艉端後才擺靠者,艉端碰墊的第一次接觸仍是接近接觸)。情境開始即接觸的碰墊須先離開 ≥ 0.5 m |
| 靠泊橫向接近速度 | `maxLateralSpeed` = 所有接近接觸(碰墊接觸 `approach: true` 與未繫妥時的碼頭硬接觸)**接觸點法向速度**的最大值;無接近接觸時 null。每次靠上另記 `contactSpeed`(該次靠上接近接觸的最大值)。`maxContactSpeed` 則為全部碰墊接觸(含繫泊中擺盪) |
| 繫妥(allFast) | 每秒評估:所有已設定的纜繩皆受力(fast / heaving / slacking),且本次靠泊船舯前、後的碰墊都已有接觸事件(艏艉皆已靠上,不必同時接觸)。記下 `allFastAt_s`、船舯里程 `allFastMidshipChainage_m` 與泊位中心偏差 `allFastPositionError_m`(船舯里程 − 泊位中心,正 = 沿碼頭線方向超前);泊位中心 `berthCentreChainage_m` = 碰墊列首尾中點(無碰墊時為碼頭線中點)。繫妥期間的接觸不是接近接觸;無任何纜繩受力時解除。艏艉靠上旗標在離開所有碰墊 ≥ 0.5 m、未繫妥且纜繩未全部受力時清除(先繫纜再以纜繩/拖船靠攏時,離開再靠回仍屬同一次靠泊) |
| 已離開泊位與再碰回 | 每秒評估:**曾接觸過泊位**(有 landing)後,所有碰墊壓縮為 0、無碼頭硬接觸、未繫妥,且**船體外形(艏艉含)至碼頭線(有限線段)的最近距離** > `berth.departedDistance_m`(情境選填,預設 20 m ≈ 1 B;`BerthingTracker.DefaultDepartedDistanceM`;給定時須為有限正數,0 或負值為結構錯誤 — 否則離泊中艉擺接觸會被誤判為再碰回而改變評分,`BerthTests.BerthDepartedDistanceMustBeFinitePositive`)→ 「已離開泊位」成立並鎖定(`departedAt_s` 記第一次;再次繫妥時解除)。之後的碰墊/碼頭接觸為**再碰回**(`contacts[].reContact: true`、`reContacts` 次數),接觸點法向速度最大值為 `maxReContactSpeed_mps`(與 `maxLateralSpeed` 分開)。以船體而非船舯判定:沿碼頭向前/向後駛離時船舯越過碼頭端而艉/艏仍在碰墊旁,不應視為已離開。離泊過程中的艉擺接觸、繫泊中擺盪都不是再碰回(E14 參考離泊腳本 T+269–359 s 的艉擺接觸 `maxLateralSpeed` 0.39 m/s,`departedAt_s` 486 s,其後無接觸 → 指標 null)。只用於評估,不影響物理與雜湊;新欄位為預設值時不寫出(舊 summary/快照格式不變) |
| 碼頭硬接觸 | `quayContact` 次數、最大穿入、接觸時法向速度 |
| 繫纜 | 每秒取樣的最大張力(t)與 MBL,比值 T/MBL;`mooringLineParted` 計斷纜次數(張力取事件值) |
| 拖船 | status `working` 的累計秒數(每秒取樣)、最大出力(t)、繫柱拖力;`tugLineParted` 計拖纜斷裂次數與時刻 |
| 放錨 | 每次 `anchorLetGo`(含教官/學員)時自船對地速度、目標鏈長與預定 scope(目標鏈長 / 水深) |
| 帶上 | `anchorBroughtUp` 時鏈長、節數、水深與 **scope = 鏈長 / 水深**(鏈長選擇,與參數集 `anchoring.scopeMultiplier` 比較) |
| 走錨 | `anchorDragging` 次數、首次時刻;累計走錨時間(每秒取樣 dragging);錨鏈孔最大張力(每秒取樣與事件值) |
| 走錨偵測與反應 | 首次走錨後第一次 `acknowledge`(偵測/回報)時刻;第一個學員反應(舵令、車令、轉速、側推、操舵模式,或教官/學員送出的 `anchor` 指令 `anchorCommand`/`anchorLetGo`)的時刻、方式與反應時間 |

### 9.2 指標(`MetricNames`;容許帶可引用,與 Python `scenario.py` METRIC_NAMES 一致)

`berthing.maxLateralSpeed_mps`、`berthing.maxLateralSpeed_kn`(接近接觸的接觸點法向速度最大值;見上表,不含艏/舯/艉端點速度與繫泊中擺盪)、`berthing.maxContactSpeed_mps`(全部碰墊接觸)、
`berthing.maxEnergy_kNm`、`berthing.maxEnergyRatio`、`berthing.fenderContacts`、`berthing.fenderOverloads`、`berthing.quayContacts`、`berthing.lineBreaks`、
`berthing.maxLineTensionRatio`、`berthing.tugLineBreaks`、`berthing.maxTugForce_t`、`berthing.tugWorkingTime_s`、`berthing.allFastPositionError_m`(繫妥時船舯與泊位中心的距離,絕對值;未繫妥時 null)、
`berthing.maxReContactSpeed_mps`(已離開泊位後再碰回的接觸點法向速度最大值;未再碰回時 null = 不扣分;E14 `reContactSpeed` 容許帶)、
`anchoring.letGoCount`、`anchoring.maxLetGoSog_kn`、`anchoring.dragEvents`、`anchoring.dragTime_s`、`anchoring.maxChainTension_t`、`anchoring.minScopeRatio`、
`anchoring.dragResponseTime_s`(未反應時為自首次走錨起的經過時間,容許帶可在無反應時判定)。

事件類規則以既有 hardFail `event` 規則表示(Python 已支援):`{ id: quayContact, rule: event, kind: quayContact }`、`{ rule: event, kind: mooringLineParted }`。
容許帶範例(C# 已驗證;E13 `approachSpeed`、E14 `contactEnergy`/`reContactSpeed` 已啟用):

```yaml
bands:
  - { id: approachSpeed, metric: berthing.maxLateralSpeed_mps, warn: "${params:berthing.approachSpeed_mps}", fail: "${params:berthing.approachSpeedFail_mps}", penalty: 10, failPenalty: 25 }
  - { id: contactEnergy, metric: berthing.maxEnergyRatio, warn: 0.8, fail: 1.0, penalty: 10, failPenalty: 25 }
  - { id: scope, metric: anchoring.minScopeRatio, direction: below, warn: "${params:anchoring.scopeMultiplier}", fail: 3, penalty: 10 }
  - { id: letGoSpeed, metric: anchoring.maxLetGoSog_kn, warn: "${params:anchoring.letGoSog_kn}", fail: "2x", penalty: 10 }
  - { id: berthPosition, metric: berthing.allFastPositionError_m, warn: "${params:berthing.allFastPositionTolerance_m}", fail: "2x", penalty: 10, failPenalty: 20 }
  - { id: reContactSpeed, metric: berthing.maxReContactSpeed_mps, warn: "${params:berthing.approachSpeed_mps}", fail: "${params:berthing.approachSpeedFail_mps}", penalty: 10, failPenalty: 25 }   # E14 已啟用
```

再碰回(`BerthingAssessmentTests.ReContactAfterDepartureIsSeparateFromApproachSpeed`:質點靠上 → 流轉向離開 > `departedDistance_m: 15` → 流轉回推回碰墊;節錄):

```json
"berthing": { "berthId": "TB", "maxLateralSpeed_mps": 0.163, "maxContactSpeed_mps": 0.163, "departedAt_s": 43, "reContacts": 2, "maxReContactSpeed_mps": 0.163,
  "maxEnergyRatio": 0.185, "fenderContacts": 4, …,
  "contacts": [ { "fenderId": "F1", "t_s": 6.32, "approach": true, "normalSpeed_mps": 0.150, "energyRatio": 0.156, …, "overload": false },
                { "fenderId": "F2", "t_s": 6.32, "approach": true, "normalSpeed_mps": 0.150, … },
                { "fenderId": "F1", "t_s": 474.7, "approach": true, "normalSpeed_mps": 0.163, "energyRatio": 0.185, …, "overload": false, "reContact": true },
                { "fenderId": "F2", "t_s": 474.7, "approach": true, "normalSpeed_mps": 0.163, …, "reContact": true } ] }
```

參數集(`data/parameters/company-procedures.yaml`;來源 `ASSUMED-BERTH` / `ASSUMED-E12`–`E14`,**假設值,待公司程序**):`berthing.approachSpeed_mps` 0.15
(碰墊設計值 0.10–0.20 m/s,min/max 限制)、`berthing.approachSpeedFail_mps` 0.20、`berthing.allFastPositionTolerance_m` 15、`anchoring.scopeMultiplier` 5(count)、`anchoring.letGoSog_kn` 0.5、
`anchoring.dragResponse_s` 120、`anchoring.dragReport_s` 180 等。

### 9.3 summary 區塊(JSON 範例取自 `BerthingScenarioTests.E13` 參考腳本,數值四捨五入、陣列節錄;`summary.berthing` 在情境有泊位或拖船時輸出,`summary.anchoring` 在情境有 `anchors` 區段或錨曾被使用時輸出)

```json
"berthing": {
  "berthId": "MLO-OIL-3", "maxLateralSpeed_mps": 0.135, "maxLateralSpeed_kn": 0.263, "maxContactSpeed_mps": 0.135, "maxEnergy_kNm": 55.3, "maxEnergyRatio": 0.100,
  "fenderContacts": 9, "fenderOverloads": 0, "quayContacts": 0, "lineBreaks": 0, "maxLineTensionRatio": 0.60, "tugLineBreaks": 1, "tugWorkingTime_s": 1125, "maxTugForce_t": 9.6,
  "berthCentreChainage_m": 130, "allFastAt_s": 806, "allFastMidshipChainage_m": 134.3, "allFastPositionError_m": 4.3,
  "landings": [ { "t_s": 770, "sampleAt_s": 769, "initial": false, "bow_mps": -0.03, "bow_kn": -0.06, "midship_mps": 0.17, "midship_kn": 0.34, "stern_mps": 0.38, "stern_kn": 0.73,
                  "max_mps": 0.38, "max_kn": 0.73, "contactSpeed_mps": 0.076, "contactSpeed_kn": 0.147 },
                { "t_s": 795, "sampleAt_s": 794, "initial": false, "bow_mps": 0.11, "bow_kn": 0.22, "midship_mps": 0.16, "midship_kn": 0.30, "stern_mps": 0.20, "stern_kn": 0.38,
                  "max_mps": 0.20, "max_kn": 0.38, "contactSpeed_mps": 0.135, "contactSpeed_kn": 0.263 } ],
  "contacts": [ { "fenderId": "F08", "t_s": 771.84, "approach": true, "normalSpeed_mps": 0.076, "normalSpeed_kn": 0.147, "energy_kNm": 13.4, "ratedEnergy_kNm": 551.4, "energyRatio": 0.024,
                  "maxReaction_kN": 313.4, "deflection_pct": 8.4, "overload": false },
                { "fenderId": "F08", "t_s": 797.02, "approach": true, "normalSpeed_mps": 0.135, "normalSpeed_kn": 0.263, "energy_kNm": 54.8, "ratedEnergy_kNm": 551.4, "energyRatio": 0.099,
                  "maxReaction_kN": 592.7, "deflection_pct": 17.4, "overload": false } ],
  "lines": [ { "lineId": "HEAD", "maxTension_t": 3.1, "mbl_t": 50, "maxTensionRatio": 0.06, "parted": false },
             { "lineId": "FWDSPRING", "maxTension_t": 29.8, "mbl_t": 50, "maxTensionRatio": 0.60, "parted": false } ],
  "tugs": [ { "id": "TG1", "workingTime_s": 610, "maxForce_t": 9.6, "bollardPull_t": 40, "lineParted": false },
            { "id": "TG2", "workingTime_s": 515, "maxForce_t": 8.7, "bollardPull_t": 40, "lineParted": true, "linePartedAt_s": 600 } ]
},
"anchoring": {
  "letGoCount": 1, "maxLetGoSog_kn": 1.71, "dragEvents": 4, "dragTime_s": 66, "maxChainTension_t": 12.4, "minScopeRatio": 5.5,
  "firstDragAt_s": 1160.3, "dragDetectedAt_s": 1219, "dragResponseAt_s": 1219, "dragResponseTime_s": 58.7, "dragResponseBy": "event:anchorCommand:veer",
  "anchors": [ { "side": "starboard", "letGoCount": 1, "letGoAt_s": 169, "letGoSog_mps": 0.88, "letGoSog_kn": 1.71, "maxLetGoSog_kn": 1.71, "targetLength_m": 165,
                 "plannedScopeRatio": 5.5, "waterDepth_m": 30, "broughtUpAt_s": 480.2, "broughtUpLength_m": 165, "broughtUpShackles": 6, "scopeRatio": 5.5,
                 "maxLength_m": 192.5, "maxTension_t": 12.4, "dragEvents": 4, "firstDragAt_s": 1160.3, "dragTime_s": 66, "brakeSlips": 0 } ]
}
```

### 9.4 情境

| 情境 | 位置(示意) | 重點 | 評估(Python 相容的規則) |
|---|---|---|---|
| `E12_anchoring_kaohsiung` | 高雄外錨地 A12(`data/areas/kaohsiung-anchorage.json`,水深 30 m、軟泥,3 艘錨泊船) | 東北季風 22 kn → T+`anchoring.e12.monsoonSurgeAt_s` 增至 40 kn;鏈長選擇;走錨偵測與反應;教官手動主機失效 = 緊急下錨 | hardFail 擱淺/碰撞;bands 與錨泊船最小距離;required 錨備便、放錨後帶上**或錨妥回報**(`anchorBroughtUp` 或 acknowledge report,900 s、逾時每秒 0.02 分漸進;鏈長較長而帶上較晚者不被扣滿)、走錨反應(`anchoring.dragResponse_s`)、回報 |
| `E13_tug_berthing_mailiao` | 麥寮油碼頭 MLO-OIL-3(`mailiao-berth.json`) | 2 艘 push-pull 拖船、引水人(角色)、東北吹開風;碼頭未準備 → 等待(`berthing.e13.berthReadyAfter_s`);就緒後 `tugLineBreak {id: TG2}` | hardFail 碼頭硬接觸/斷纜(event);bands 港內速限;required MPX、等待回報、拖纜斷裂反應;summary.berthing 繫妥時船位(`allFastPositionError_m`,泊位中心 = 碰墊列中點 130 m)。教官 `allFast` 為手動結束,不檢查纜繩狀態:以 summary 的 `allFastAt_s`(null = 未繫妥)與船位偏差講評 |
| `E14_unberthing_no_tug` | 麥寮小型油品碼頭 MLO-SOB-1(新 `mailiao-small-oil-berth.json`,9 個 H 0.8 m 碰墊) | 壓載右舷靠、吹攏風、4 纜已繫;`engineStartFailure {attempts: 2}`;T+`unberthing.e14.windShiftAt_s` 轉吹開風 | hardFail 碼頭硬接觸/斷纜;required 簡報、起動失敗通知機控室、風向改變反應;summary.berthing(離泊接觸、`berth.departedDistance_m: 20` 之後的再碰回 `reContactSpeed` 容許帶)、manoeuvring.thruster* |

## 10. Python 參考實作(`src/Tools.Calibration/simosa_brm/external.py`)

規劃書第 14 章「C# 核心 vs Python 參考實作(相同係數)軌跡差異 <1%」的外力部分。方程式與 C# `src/SimCore/Physics/External/` 逐式對應
(同一常數、同一二分法迭代次數、同一判斷順序),岸壁效應見 `physics-bank-effect.md` 第 10 節。

### 10.1 對應與接線

| C# | Python | 備註 |
|---|---|---|
| `TugUnit`(TugModel.cs) | `TugUnit` | 出力一階上升、方向迴轉、速度效能、換位/帶纜計時(`round` 與 C# `Math.Round` 皆為銀行家捨入)、斷纜 |
| `Catenary.Solve` | `catenary_solve` | 對數二分 80 次;全懸空區找上界最多 200 次倍增 |
| `AnchorParticulars` / `AnchorUnit` | 同名 | particulars `anchors` 缺欄估計式相同(`estimated` 列出);指令狀態機、走錨、剎車滑出 40 次二分、帶上、事件 |
| `FenderType` / `BerthModel` / `MooringLine` | 同名 | 11 點船體外形、碰墊軸線接觸點、碼頭懲罰彈簧(臨界阻尼比 0.5)、Kelvin–Voigt 纜繩阻尼 |
| `ExternalForceModel` | 同名 | `active`、指令與拒絕原因、雜湊欄位與順序(`hash_bytes`)、`tug_states`/`anchor_states`/`berth_state` |
| `Rk4Integrator.Step(..., field)` | `MMGShip.step` | 外力場在 RK4 四個階段各以該階段狀態重算後加到 X/Y/N(`_derivs(..., ext)`);環境為步首取樣 |
| `SimulationEngine.Advance` | `MMGShip.step` | 步首 `ExternalForceModel.pre_step`(拖船,以步首對水速度)→ RK4 → tick+1 → `post_step`(錨、泊位;步首環境)→ 擱淺檢查 |
| `CreateExternal`(Initialize) | `MMGShip.configure_external` / `reset` | `scenario.apply_scenario` 在設定環境後以情境初始狀態與水深建立(初始已錨泊、繫纜 initial fast) |

- 情境:`scenario.validate_scenario` 以 `external.normalise_sections` 補 C# 預設值(`ScenarioTug`、`FenderTypeDefinition`、`ScenarioMooringLine` 的預設),
  `berth.file + id` 與 `banks.file + ids` 依「專案根目錄 → 情境檔目錄 → 工作目錄」解析並內嵌,再做與 C# `CheckStructure` 相同的結構檢查;
  結果放在正規化情境的 `external` 鍵。
- 確定性:只在 `active`(有拖船/泊位、錨曾被使用)時才走含外力的積分並把外力區塊追加到 `state_hash`;未使用外力的情境逐位元與舊版相同
  (`test_unused_external_keeps_legacy_hash_and_state`)。快照(`MMGShip.snapshot`)含整個外力模組的深拷貝;舊快照還原時外力回到初始。
- state:`tugs[]`(有拖船時)、`anchors[]`(particulars 有錨資料時,與 C# 相同即使未使用也輸出)、`berth`(有泊位時)。
- 事件:接受的指令與物理事件(`anchorDragging`、`fenderContact` …)記入 `MMGShip.external_events`(kind、itemId、message、data、tick;最多 1000 筆),
  伺服器指令的 ack 附 `event`。

### 10.2 伺服器指令(`server.py`)

| 指令 | args | 接受(ack.event.kind) | 拒絕原因(與 C# `commandRejected.reason` 同名) |
|---|---|---|---|
| `tugControl` | `id`(或 value)、`mode`、`direction_deg`(或 `direction`)、`power_pct`(或 `power`) | `tugCommand` | `unknownTug`、`unknownTugMode`、`invalidPower`、`tugModeUnavailable`、`tugLineParted` |
| `anchor` | `side`(或 value)、`action`、`targetLength_shackles` / `length_m` | `anchorLetGo` / `anchorCommand` | `noAnchorData`、`unknownAnchor`、`unknownAction`、`invalidTarget`、`anchorAlreadyOut`、`anchorStowed`、`chainFullyOut` |
| `mooring` | `lineId`(或 `id`)、`action`、`bollard`、`tension_t`、`length_m` | `mooringCommand` | `noBerth`、`unknownMooringLine`、`unknownAction`、`unknownBollard`、`bollardOutOfReach`、`mooringLineNotFast`、`mooringLineParted` |
| `injectFault tugLineBreak` | `args.args.id`(只讀 `args.args` 物件,與 C# `FaultArgString` 相同;`args.id` 視同省略 → 斷第一艘拉中/待命中的拖船) | `tugLineParted` | `unknownTug`(故障不加入 faults) |
| `clearFault` | `value` 或 `args.name`(C# `SimCommand.FaultName`);皆缺或為 `all` = 全部 | —(只清指定故障;清除 `tugLineBreak` 才恢復拖纜) | — |

數值參數接受數值或數值字串(C# `ArgAsDouble`);拒絕時 ack `ok: false`、`detail` 為「拒絕(原因):…;狀態不變」。範例:

```json
{"type": "tugControl", "args": {"id": "TG1", "mode": "push", "direction_deg": 270, "power_pct": 80}}
{"type": "ack", "command": "tugControl", "ok": true, "detail": "TG1 push 270° 80%", "tick": 0,
 "event": {"kind": "tugCommand", "itemId": "TG1", "message": "TG1 push 270° 80%", "tick": 0,
           "data": {"id": "TG1", "mode": "push", "from": "standby", "status": "working", "direction_deg": 270.0, "power_pct": 80.0}}}
{"type": "ack", "command": "anchor", "ok": false, "detail": "拒絕(anchorStowed):anchor 被拒:anchorStowed;狀態不變", "tick": 120}
```

### 10.3 C# 一致性黃金檔(`tests/SimCore.Tests/Golden/ext_*`)

`export_external_reference.py`(仿 `export_reference.py`)產生情境 `ext_*.yaml` 與時間序列 `ext_*.csv`(標頭含 `# cmd: tick=<n> <JSON>` 指令序列與外力事件);
C# `ExternalForceGoldenTests` 以同一份 YAML 建立 MMG 引擎、在相同 tick 下指令,每秒比較位置/航向/速度/轉速、外力合計 X/Y/N 與各力項,並要求外力事件
(tick、kind、itemId)序列相同。Python 端 `test_golden_external_reference_is_reproducible` 重跑並比對檔案(相對 1e-9);
`test_external_golden_coverage.py` 另比對標頭 `# event` 序列(逐筆相同)與 `state_hash_end`(數值列逐位元相同時必須相同),
並要求每個項目欄至少有一個非零值、全部黃金檔合起來涵蓋 C# 比對的 16 種外力事件。C# 端擴充案例與涵蓋檢查在
`ExternalForceGoldenCoverageTests`(直接呼叫 `ExternalForceGoldenTests` 的比對方法;並檢查 `Golden/ext_*.csv` 每一份都在比對清單中)。

| 案例 | 內容 | 最大位置差 / 航程 | 力項最大相對差 |
|---|---|---|---|
| `ext_tug_push_turn` | 兩拖船推頂原地迴轉、推 → 拉換位、轉向、解纜、`tugLineBreak`(600 s、航程 1304 m) | 5.6e-14 % | ≤ 3e-14 % |
| `ext_anchor_wind_drag` | 右錨 4 節已錨泊、35 kn 風、流 0.8 kn、泥 × 0.5:走錨/抓住/剎車滑出、鬆鏈 7 節、左錨 letGo 帶上(600 s) | 0(逐位元相同) | 0 |
| `ext_berth_fender_lines` | 拖船橫推靠碼頭、非線性碰墊(阻尼、摩擦)接觸 10 次、艏艉纜 makeFast/heave/slack、吹開風(600 s;F1、F4 未接觸,不列比對欄) | 0(逐位元相同) | 0 |
| `ext_bank_suction` | 見 `physics-bank-effect.md` 第 10 節 | 1.7e-15 % | ≤ 6e-13 % |
| `ext_quay_line_part` | 無碰墊碼頭(折線、`quayStiffness_kNpm` 5000)硬接觸 `quayContact` 6 次、MBL 6 t 艏纜被拖船拉斷 `mooringLineParted`、艉纜 slack 到 `maxLength` 後回 fast、TG2 由 free 帶纜(`connect_s` 30)拉 → standby → push 換位(500 s、航程 281 m) | 0(逐位元相同) | 0 |
| `ext_anchor_heave_house` | 右錨 letGo 12 節(夾到 9 節鏈端 `anchorChainFullyOut`)、左錨 3 節後 heave:懸鏈線 hanging/slack/touchdown/suspended/taut 各區、`anchorDragging`、`anchorWindlassStalled` × 194、`anchorBrakeSlipping` × 139、`anchorAweigh`、`anchorHoused`(400 s) | 0(逐位元相同) | 0 |
| `ext_bank_astern` | 見 `physics-bank-effect.md` 第 10 節 | 1.4e-12 % | 捨入等級 |

`ext_anchor_heave_house` 的絞盤超載事件反覆切換(停轉 → 剎車滑出 → 張力降到門檻下 → 恢復絞收)是 C# `AnchorModel` 與 Python 共有的現行行為
(超載判定無遲滯),黃金檔照實記錄;兩邊同步加入遲滯後須重新產生此黃金檔。

兩實作在同一平台上達到捨入誤差等級(目標 <1%)。比對過程發現並修正一項**初始化差異**(非外力方程式):Python `apply_scenario` 先 `reset` 後設水深,
淺水情境的直航平衡轉速取到深水值(h/T 1.4、6 kn 時 70.8 vs 79.7 rpm,300 s 後位置差 5.2 % 航程);改為以情境水深、無風求平衡轉速
(C# `SteadyRpmForSpeed(u, scenario waterDepth)`)後一致。重新產生:`cd src/Tools.Calibration && uv run python ../../tests/SimCore.Tests/Golden/export_external_reference.py`。

