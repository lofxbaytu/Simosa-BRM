# Tools.Calibration — MMG 船舶運動模型參考實作與校正工具(`simosa-brm`)

規劃書第 6 章(船舶運動數學模型與校正)的 Python 參考實作:由 `data/ships/<ID>/particulars.json` 以經驗公式估計 MMG 係數、以
`trial_targets.json` 的試俥/海報資料做參數識別、產生 C# SimCore 與 Python 共用的係數檔 `coefficients.<loading>.json`
(格式:`src/Contracts/coefficients.schema.json`),並輸出驗證報告。另附 WebSocket 即時模擬服務供網頁儀器原型使用。

## 用法

需要 Python 3.12+ 與 [uv](https://github.com/astral-sh/uv)。所有快取與輸出留在專案資料夾內(`.cache/uv`、`.venv`、`build/`;CLAUDE.md)。

```powershell
cd "D:\Simosa BRM\src\Tools.Calibration"
uv sync --all-extras                                     # 建立 .venv(快取在 ..\..\.cache\uv)
uv run simosa-brm estimate --ship FSB1 --loading full    # 經驗公式估計 → build\calibration\FSB1\coefficients.full.estimate.json
uv run simosa-brm identify --ship FSB1 --loading full    # 參數識別   → data\ships\FSB1\coefficients.full.json
uv run simosa-brm validate --ship FSB1 --loading full    # 驗證報告   → build\calibration\FSB1\validation.full.md + png
uv run simosa-brm manoeuvre turning --ship FSB1 --rudder 35 --side port --speed 14.5
uv run simosa-brm manoeuvre zigzag  --ship FSB1 --rudder 10 --side port --speed 14.5 --csv ..\..\build\zz.csv
uv run simosa-brm manoeuvre crashstop --ship FSB2 --speed 12.8 --depth 15
uv run simosa-brm serve --ship FSB1 --port 8765          # WebSocket:每 40 ms 送 state JSON
uv run pytest -q                                         # 測試(黃金測試讀 data\ships\*\coefficients.*.json,不重跑識別)
```

子命令摘要:

| 命令 | 說明 |
|---|---|
| `estimate` | 由 particulars 估計全部係數(不覆蓋已識別檔;`--out` 可指定路徑) |
| `identify` | 以 `trial_targets.identification` 區擬合 8–12 個參數(`--params`、`--dt`、`--max-nfev`、`--prior-weight`、`--workers`);validation 區不用於擬合 |
| `validate` | 執行全部標準操縱(識別區 + 驗證區),與目標比對並標示是否在 `tolerances` 內,寫 markdown/png/json |
| `manoeuvre` | `turning` / `zigzag` / `crashstop` / `inertia` / `thruster` / `speed`,可加 `--depth`、`--wind kn,deg`、`--csv` |
| `serve` | WebSocket 服務;接收 `command.schema.json` 的 `rudder`/`telegraph`/`rpm`/`thruster`/`autopilot`/`freeze`/`resume`/`reset`/`setEnvironment`/`timeScale`/`snapshot`/`restore` |

WebSocket 範例(JavaScript):

```js
const ws = new WebSocket("ws://127.0.0.1:8765");
ws.onmessage = (e) => { const s = JSON.parse(e.data); if (s.tick !== undefined) console.log(s.heading, s.rot, s.rudder); };
ws.send(JSON.stringify({ type: "telegraph", value: "HAH" }));
ws.send(JSON.stringify({ type: "rudder", value: -20 }));
ws.send(JSON.stringify({ type: "autopilot", args: { enabled: true, heading: 275, rotLimit: 15 } }));
```

## 套件結構

| 模組 | 內容 |
|---|---|
| `particulars.py` | 讀 particulars.json;`loading`(full/ballast)決定排水量、吃水、Cb;`null` 欄位以經驗式補並記在 `estimated` |
| `propeller.py` | Wageningen B 系列 K_T、K_Q 多項式(Oosterveld & van Oossanen 1975);簡化四象限推力;鎖定螺槳阻力 |
| `coefficients.py` | 係數估計(見下)、係數檔讀寫、KVLCC2 基準係數 |
| `mmg.py` | 3 自由度 MMG + 致動器 + 環境,固定步長 RK4,狀態 JSON(state.schema.json),狀態雜湊 |
| `manoeuvres.py` | 迴旋、Z 形、緊急停船、慣性停船、倒車→進車、側推迴轉、速度-轉速 |
| `identify.py` | scipy `least_squares`(trf,有界)+ 平行有限差分 Jacobian + 先驗殘差 |
| `report.py` | 驗證報告(markdown、matplotlib png、json) |
| `server.py` | websockets 即時模擬(25 Hz 廣播、命令處理、簡單 PID 自動舵) |
| `cli.py` | `simosa-brm` 入口 |
| `tests/` | 單位/正負慣例、確定性、B 系列與 KVLCC2 回歸、速度-轉速、FSB1 迴旋/Z 形/停船黃金測試、FSB2 海報比對、WebSocket |

## 模型與來源

座標與無因次化依 Yasukawa & Yoshimura (2015):船舯座標,u、v_m、r;X、Y 以 ½ρLdU²、N 以 ½ρL²dU²、質量以 ½ρL²d、慣性矩以 ½ρL⁴d,L = LPP,d = 平均吃水。
航向 ψ 自北順時針,位置為本地 ENU(x 東、y 北),舵角與迴轉率右正(state.schema.json)。

| 項目 | 方法 | 來源/備註 |
|---|---|---|
| 運動方程式 | (m+m_x)u̇ − (m+m_y)v_m r − x_G m r² = X 等三式;x_G 支援但目前設 0(LCG 未知) | Yasukawa & Yoshimura 2015 式 (1)–(3) |
| 船體力 | 多項式 X_vv…X_vvvv、Y_v…Y_rrr、N_v…N_rrr;大漂角(>20–40°)與截面橫流阻力(C_D=1.0)混合;無因次化速度下限 0.5 m/s | 規劃書 6.2 |
| 線性導數 | Y_β = ½πk + 1.4 Cb B/L、Y_r = ¼πk、N_β = k、N_r = −0.54k + k²(k = 2d/L) | Kijima et al. 1990 / Inoue 1981 |
| 非線性導數 | 以 KVLCC2 基準值為起點(本船 L/B、d/B 與 KVLCC2 相近),再由識別調整 | Yasukawa & Yoshimura 2015 Table 3;規劃書 6.1 |
| 附加質量 | m_x、m_y、J_zz 的 Zhou 經驗式(Motora 圖表回歸);I_zz 以 k = 0.25 L | 周昭明 |
| 阻力 R0'(U) | ITTC-57 摩擦 × 形狀因子 × S/(Ld)(Mumford 濕面積)+ c_w(Fn/Fn_ref)^q;由 speedTrial(或海報車鐘表)以推力恆等反推、三參數擬合;低速不低於最低資料點 | 規劃書 6.3 識別順序第一步 |
| 螺槳 | B 系列 K_T、K_Q 在 0≤J≤J0 的二次擬合;w_P = w_P0·exp(−4β_P²)(w_P0 = 0.5Cb − 0.05,t_P = 0.6 w_P0);倒車:多項式負 J 外推 × 0.85;軸停止:鎖定圓盤 C_D 0.5;倒車橫向力 8% 推力(右旋槳艉向左) | Oosterveld & van Oossanen 1975;規劃書 6.2 |
| 舵 | F_N = ½ρA_R U_R² f_α sin α_R,Fujii f_α = 6.13Λ/(Λ+2.25);u_R 含滑流加速(κ、η)、v_R = Uγ_Rβ_R(γ_R 依 β_R 正負)、l_R、ε;t_R、a_H、x_H 船體交互;a_H = 0.627Cb − 0.153、1 − t_R = 0.28Cb + 0.55、ε = −156.2(CbB/L)² + 41.6(CbB/L) − 1.76 | Kijima 1990;KVLCC2 的 γ_R、l_R、κ |
| Schilling 舵 | `rudder.type` 含 "Schilling":有效展弦比 ×1.5(端板),\|α\| ≤ 35° 用 f_α sin α,35–70° 以 `highLiftSlopeFactor`(0.5)× f_α 的斜率延伸,逾 70° 保持;`swirlAngle_deg` 為滑流旋轉造成的有效舵角偏移(× 滑流加速因子,上限 5),識別用 | 無實測升力曲線,係數可調 |
| 舵機 | 速率 = 70° / `hardOverTime35_s`(解讀為 35° 一舷至 35° 另一舷);No.2 用兩泵值 | particulars |
| 主機 | 一階滯後(6 s)+ 速率限制(3 rpm/s);STOP:軸轉速以 20 s 時間常數衰減;換向:燃油切斷→軸轉速降到 5% MCR 且逾 `reversalDelay_s` 後反向點火;起動延遲 5 s。No.1 的換向延遲取自試俥緊急停船紀錄(機械特性,非水動力擬合);No.2 無資料,假設 90 s | 規劃書 6.2 |
| 艏側推 | 推力 = order × 額定 × effectiveness × exp(−ln2 (u/2.5 kn)²);一階 + 速率限制(`fullThrustDelay_s`);作用點 0.43 L 前 | 規劃書 6.2 |
| 風 | Blendermann (1994) 參數式(油輪:C_Dt 0.70、C_Dl 0.90/0.55、δ 0.40);受風面積 `null` 時以 LOA×乾舷 + 住艙(11 m × 0.15 LOA)+ 甲板設備(3 m × 0.5 LOA)估計並標註 | Fujiwara 1998 / Isherwood 1972 回歸表尚未數位化 |
| 均勻流 | 以相對水速進運動方程式,對地位置積分加流速向量 | 規劃書 6.2 |
| 淺水 | 線性/非線性導數與附加質量乘 1 + a·(T/(h−T) − 0.1)^n(h/T ≥ 11 無修正);擱淺:龍骨下水深 ≤ 0 時停止 | Kijima & Nakiri 型(趨勢用,未驗證) |
| Squat | ICORELS:S = C_s ∇/L² Fnh²/√(1−Fnh²),C_s 由 particulars.squatTable 擬合 | PIANC 2014 |
| 自動舵 | 舵角 = 3·航向誤差 − 60·(r − r_cmd),r_cmd 受 ROT 限制,舵角限制 | 規劃書 6.2(PID 簡化) |

### 識別流程(`identify`)

1. `estimate_coefficients` 產生經驗初值(含阻力/推進由速度曲線校準)。
2. 目標:`trial_targets.identification`(No.1:左右 35° 迴旋的前進距離、橫距、戰術直徑、定常速度 + 10/10 Z 形三個超越角;No.2:海報左右迴旋含 90/180/270/360° 歷時與速度)。
3. 殘差 = (模擬 − 目標)/公差(迴旋 max(10%, 0.3 L);Z 形 max(3°, 25%);速度 0.5 kn;No.2 公差 1.5 倍),外加弱先驗 λ(p − p₀)/σ。
4. 預設 12 個參數:`Yv, Yr, Nv, Nr, Yvvv, Nvvr, Nrrr, aH, epsilon, gammaRMinus, gammaRPlus, swirlAngle`,各有物理合理範圍(`identify.PARAM_SPECS`);No.2 建議用 8 個(`Nr, Nvvr, Nrrr, aH, epsilon, gammaRMinus, gammaRPlus, swirlAngle`),航向穩定性導數留經驗值(規劃書 6.3)。
5. 識別用 dt 0.1 s(與 0.02 s 的差異 < 0.1%),驗證用 0.02 s。結果與每個目標的偏差寫入係數檔 `source.identification`。

## 結果摘要(2026-10-08,詳見 `build/calibration/<ID>/validation.full.md`)

- FSB1 滿載(試俥實測):識別區 13/14 在公差內(右迴旋橫距 141 m vs 175.5 m 超出);驗證區 20/20 Z 形 4/4、緊急停船時間/航跡 2/2、速度-轉速 4/4 在公差內;慣性停船、倒車→進車、側推迴轉偏差見下。
- FSB2 滿載(海報,8 參數粗識別,航向穩定性導數留經驗值):迴旋識別區 18/22 在公差內(左迴旋戰術直徑 266 m vs 海報 208 m、90° 速度偏高約 1–1.5 kn、270° 速度偏低 0.9 kn);緊急停船超出(停船時間 +74%、航跡 +36%,海報讀值且換向延遲未知);EEDI 13.06 kn/110 rpm 點與海報 12.8 kn/133.6 rpm 互相矛盾,無法同時滿足(trial_targets 已註記)。以 12 參數識別可達 21/22,但 Y_v、Y_r 會跑到物理下界(船體橫向力幾乎消失、倒車艏向偏轉方向錯誤),故不採用。

## 已知限制

1. **非線性船體導數**以 KVLCC2 基準值代用再識別;Yoshimura & Masumoto (2012)/Kijima (1990) 的非線性回歸常數尚待依原文核對後納入(附錄 C 事項)。線性導數經驗式誤差 ±20–30%(規劃書 6.3)。
2. **四象限螺槳**為簡化(負 J 外推、倒車折減、鎖定圓盤);無 Wageningen C_T*(β)、C_Q*(β) 表;風車/主機壓縮制動未建模。倒車→進車與慣性停船偏差主要來自此處與換向延遲假設。
3. **慣性停船**試俥航跡彎曲(head reach 1098 m、side reach 402 m),直線滑行模擬的 5 kn 時間/距離偏大約 2 倍;屬資料條件差異,未調整阻力。
4. **試俥功率**:以 B 系列 K_Q 估計的軸功率比試俥低約 17–20%(實際螺槳非 B 系列、PBCF、η_R 等);速度-轉速以推力恆等校準,不受影響,但功率/負荷顯示僅供參考。
5. **Schilling 舵** 35° 以上升力曲線與滑流不對稱為假設模型;No.1 右迴旋橫距(初期迴轉快、定常圓大的試俥特徵)無法完全重現。
6. **No.2** 僅海報圖面讀值;舵面積、受風面積、側推推力、換向延遲為估計;取得操縱試驗報告後重跑 `identify`。壓載狀態(兩船)僅有海報車鐘速度,係數為估計(`loading=ballast` 可用,未驗證);艏艉吃水差對導數的影響未納入。
7. 風為 Blendermann 參數式與估計面積;淺水修正為趨勢用倍率;岸壁效應、浪、拖船、錨纜、4 自由度橫搖、故障行為未實作(規劃書 6.2 其餘項目)。
8. `x_G` 設 0;KVLCC2 回歸測試顯示 x_G 對迴旋直徑影響約 8–10%,取得兩船 LCG 後應填入 `reference.xG_m`。
9. 本工具只讀寫專案資料夾;識別結果具確定性(同一機器、同一套件版本);不同 CPU/BLAS 可能使最佳化路徑略有差異,但係數檔為最終依據。
