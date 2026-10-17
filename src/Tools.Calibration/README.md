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
uv run simosa-brm estimate --ship FSB2 --loading ballast --transfer-from full --out ..\..\data\ships\FSB2\coefficients.ballast.json --note …   # 壓載估計 + 滿載識別修正轉移(見「壓載係數」)
uv run simosa-brm identify --ship FSB1 --loading full    # 參數識別   → data\ships\FSB1\coefficients.full.json
uv run simosa-brm validate --ship FSB1 --loading full    # 驗證報告   → build\calibration\FSB1\validation.full.md + png
uv run simosa-brm manoeuvre turning --ship FSB1 --rudder 35 --side port --speed 14.5
uv run simosa-brm manoeuvre zigzag  --ship FSB1 --rudder 10 --side port --speed 14.5 --csv ..\..\build\zz.csv
uv run simosa-brm manoeuvre crashstop --ship FSB2 --speed 12.8 --depth 15
uv run simosa-brm serve --ship FSB1 --port 8765          # WebSocket:每 40 ms 送 state JSON
uv run simosa-brm serve --ship FSB1 --scenario E01_baseline   # 啟動即載入情境(data\scenarios\E01_baseline.yaml)
uv run pytest -q                                         # 測試(黃金測試讀 data\ships\*\coefficients.*.json,不重跑識別)
```

子命令摘要:

| 命令 | 說明 |
|---|---|
| `estimate` | 由 particulars 估計全部係數(不覆蓋已識別檔;`--out` 可指定路徑) |
| `identify` | 以 `trial_targets.identification` 區擬合 8–12 個參數(`--params`、`--dt`、`--max-nfev`、`--prior-weight`、`--workers`);validation 區不用於擬合 |
| `validate` | 執行全部標準操縱(識別區 + 驗證區),與目標比對並標示是否在 `tolerances` 內,寫 markdown/png/json |
| `manoeuvre` | `turning` / `zigzag` / `crashstop` / `inertia` / `thruster` / `speed`,可加 `--depth`、`--wind kn,deg`、`--csv` |
| `serve` | WebSocket 服務;接收 `command.schema.json` 的命令(支援矩陣見下);`--scenario` 啟動時載入情境 |

WebSocket 範例(JavaScript):

```js
const ws = new WebSocket("ws://127.0.0.1:8765");
ws.onmessage = (e) => { const s = JSON.parse(e.data); if (s.tick !== undefined) console.log(s.heading, s.rot, s.rudder); };
ws.send(JSON.stringify({ type: "loadScenario", value: "E01_baseline" }));
ws.send(JSON.stringify({ type: "telegraph", value: "HAH" }));
ws.send(JSON.stringify({ type: "rudder", value: -20 }));
ws.send(JSON.stringify({ type: "autopilot", args: { enabled: true, heading: 275, rotLimit: 15 } }));
ws.send(JSON.stringify({ type: "injectFault", value: "steeringGear" }));
```

### WebSocket 服務命令支援(`server.py`,2026-10)

每筆命令回 `{"type":"ack","command":…,"ok":…,"detail":…,"tick":…}`;`ok=false` 時模型狀態不變、`detail` 說明原因。
語意與 C# `SimCore.Host` 一致之處已註明;狀態 JSON 一律依 `state.schema.json`,不新增欄位。

| 命令 | 讀取欄位 | 語意 | 狀態 |
|---|---|---|---|
| `rudder` | `value` 度(右正) | 舵令,夾到 ±`rudder.maxAngle_deg`;關閉自動舵;autopilot / nfu 模式切回 fu(emergency 保持,C# 相同) | 實作 |
| `telegraph` | `value` EFAS…NAVF | 車鐘 → 轉速令(係數檔 `engine.telegraph`);未知車令 `ok=false` | 實作 |
| `rpm` | `value` rpm(倒車負) | 直接轉速令,幅度夾到 [minRpm, maxRpm];車鐘以最接近者標示 | 實作 |
| `thruster` | `value` −1…1 | 側推令;無側推的船固定 0 | 實作 |
| `autopilot` | `args.enabled/heading/rotLimit/rudderLimit` | 簡單 PID 航向控制;啟用 → `steering.mode = autopilot`、解除 → fu(原為 autopilot 時)。`enabled` 只認布林,**省略(或非布林)時只更新航向/rotLimit/rudderLimit、不改啟用狀態與操舵模式**(與 C# 相同;fu/nfu 中送 `{heading}` 不會切到自動舵);`rotLimit` 取 max(1, \|值\|) 度/分。`enabled: true` 在 emergency 模式中拒絕(`ok=false`,detail「拒絕(emergencySteering)」)、在 `autopilotFailure` / `gyroFailure` 時拒絕(「拒絕(autopilotUnavailable)」),狀態不變 | 實作 |
| `steeringMode` | `value`(或 `args.mode`)fu / nfu / autopilot / emergency(不分大小寫);`args.pump` 1 / 2 / both(數值 1、2 與 "all"、"1+2" 亦可,不明值不變);`args.direction` port / starboard / stop(nfu);`args.heading`、`args.rotLimit`(autopilot) | 情境引擎設計 D32、D35,語意同 C# `SimulationEngine`:**nfu** 推桿 port/starboard 時舵令為該側滿舵、舵以舵機速率移動,stop 舵停在原處,省略方向維持、不明方向視同 stop;**進出 nfu 時舵令先同步到目前舵角**(推桿的滿舵舵令或先前隨動舵令失效);**autopilot** 同 `autopilot {enabled: true}`,未給航向且原本未啟用時保持目前航向、rotLimit 取 max(1, \|值\|) 度/分;**emergency** 應急操舵:`steeringGear` 故障下舵仍可動,速率為 `steeringGear.emergencyHardOverTime35_s`(35°/t)或係數檔速率的一半,emergency 中 `steeringMode autopilot` 拒絕(emergencySteering,需先 fu);`autopilotFailure` / `gyroFailure` 時 `steeringMode autopilot` 拒絕(autopilotUnavailable,emergency 優先),自動舵啟用中注入這兩種故障則跳脫:舵停在目前舵角、模式回 fu(C# TripAutopilot);**泵**:速率 = 係數檔速率 × hardOverTime35(無則雙泵、單泵)/ 所選泵的轉舵時間(both 用雙泵、1/2 用單泵;No.2 預設泵 1 = 28 s,both = 14 s;No.1 無泵資料不換算)。`steeringPumpFailure` 使所有模式速率減半、`steeringFuOnly` 使 fu/autopilot 動不了舵。state 輸出 `steering {mode, pump, nfuActive?}`(nfuActive 只在 nfu);未知模式 `ok=false`。非預設(nfu / emergency / 泵非 1)才加入狀態雜湊(3 個 int64,與 C# 相同規則),快照/還原含操舵模式。Python 不發事件(steeringModeChanged / commandRejected),拒絕原因只在 ack | 實作 |
| `freeze` / `resume` | — | 凍結/恢復(時間不前進) | 實作 |
| `reset` | 無 `args` 且已載入情境:回到情境初始狀態(C# 語意)。有 `args`:自船覆寫 `x/y` 或 `lat/lon`(經 LTP 轉本地)、`heading`、`speed`、`rpm`、`telegraph`,未給者為 0 / 停俥;`loading`、`tugs` 忽略並在 detail 註明 | 直航重設,tick 歸零、故障清除 | 實作 |
| `timeScale` | `value` ≥ 0 | 即時倍率(0 = 暫停推進) | 實作 |
| `loadScenario` | `value`(代號或路徑),否則 `args.path`、`args.id`;依序嘗試 | 代號解析為 `data/scenarios/<id>.yaml`(也接受絕對路徑或相對工作目錄/專案根目錄的路徑);先解析 `${params:…}` 參數引用(見下節)並做時間表/航線/區域/評估/簡報的結構檢查,再依 `ship.id/loading` 重建模型、套初始位置(經緯度經 `LocalTangentPlane` 轉 ENU,與 C# 同式)/航向/航速/車鐘或轉速/舵角、環境(風、陣風、流、水深、能見度)、種子與 `timeScale`;tick 與時間歸零、快照清空。檔案不存在、YAML 錯誤、未解析的參數鍵、結構錯誤、裝載不存在等皆 `ok=false` 且原模型不變。`detail` 回報情境代號、名稱與參數集版次/引用數;之後 state JSON 含 `scenario {id, phase: "normal", parameterSetVersion?}` | 實作 |
| `setEnvironment` | `args.wind{trueSpeed,trueDir,gustiness}`、`args.current{set,drift}`、`args.waterDepth`、`args.visibility_nm`(或 `visibility`) | 部分更新;速度不為負、角度正規化、陣風夾到 [0,1]、非正水深忽略(與 C# 相同)。`gustiness`:每秒以種子亂數更新風速係數 1±gustiness(`state.wind.trueSpeed` 回報有效風速,與 C# 相同;亂數為 Python `random.Random(seed)`,序列與 .NET `Random` 不同,故含陣風時兩核心狀態雜湊不同)。`visibility_nm`:只記錄(`command.schema.json` 的 `args` 未列欄位、`state.schema.json` 無此欄位、運動模型不使用),ack detail 註明 | 實作(能見度僅記錄) |
| `snapshot` / `restore` | `value` 名稱(預設 `default`) | 記憶體快照:運動狀態、控制、環境、主機狀態機、故障、擱淺、陣風係數與亂數狀態;`restore` 無此名稱 `ok=false`。`loadScenario` 後快照清空 | 實作 |
| `injectFault` | `value` 名稱 | 加入故障(同名不重複)並在 `state.faults` 廣播。會改變行為的五種(`src/SimCore/Engine/FaultNames.cs`):`steeringGear` 舵角停在目前位置(舵令與自動舵照記但不作用;emergency 操舵可動);`steeringFuOnly` fu 與自動舵動不了舵(nfu、emergency 可動);`steeringPumpFailure` 舵機速率減半;`mainEngine` 轉速令視為 0、軸轉速以停俥時間常數衰減、`engine.state = failed`,車鐘改令無效;`bowThruster` 側推令視為 0、實際推力依延遲衰減。`autopilotFailure` / `gyroFailure`:自動舵不可用(啟用被拒 autopilotUnavailable;啟用中注入則跳脫回 fu,detail 註明),感測器層不模擬。其餘名稱(如 `gyroDrift`、`gpsJump`、`radarFailure`、`blackout`)僅記錄並廣播,由儀器端自行反應,detail 註明(C# 另有 `engineStartFailure`,Python 尚未實作,僅記錄) | 實作(五種行為 + 其餘記錄) |
| `clearFault` | `value` 名稱;省略 / null / 空字串 = 全部 | 清除指定故障(不在清單中仍 `ok=true`,detail 註明)或全部(C# 語意) | 實作 |
| `triggerTimeline` / `acknowledge` / `getAssessment` / `endExercise` | — | 情境引擎指令(C# SimCore 實作);Python 參考實作不執行時間表、不評估,固定 `ok=false`、`detail` 說明未實作,模型不變 | 未實作(回 ok=false) |
| `targetControl` | — | 目標船指令(C# SimCore 實作);Python 只模擬自船(無目標船、AIS、COLREG 行為),固定 `ok=false`、`detail` 說明「Python 參考實作無目標船」,模型不變 | 未實作(回 ok=false) |
| `alertAcknowledge` / `alertSilence` / `bnwasMode` / `bnwasReset` | — | 警報管理(BAM)與 BNWAS 指令(C# SimCore 實作;docs/design/sensors-alerts.md);Python 不實作感測器層/警報/BNWAS(不輸出 `state.truth/sensors/alerts/bnwas`),固定 `ok=false`、模型不變 | 未實作(回 ok=false) |

未在 `command.schema.json` 的命令回 `ok=false`、`detail` 為「未知命令」。

### 公司程序參數集與情境結構檢查(`parameters.py`、`scenario.py`,2026-10)

情境引擎設計第 2 節的 Python 對應(語意與 C# `SimCore.Parameters` / `ScenarioLoader` / `YamlJson` 相同;跨語言一致性測試 `tests/test_scenario_params.py`):

- **YAML 讀取** `parameters.yaml_to_tree`:純量型別推斷與 C# `YamlJson` 相同 — 加引號者為字串;純量 `true`/`false` → 布林、十進位整數/小數(含指數)→ 數值、`null`/`~`/空 → None;其餘(日期、`yes`/`no`、`0x1F`、`1_000`、`.inf`)一律字串。情境與參數集都走這條路。
- **參數集** `ParameterSet.load(path)`(預設 `data/parameters/company-procedures.yaml`,schema `src/Contracts/parameter-set.schema.json`):載入即結構檢查(版次 `YYYY.M.n`、單位列舉、來源文件存在、鍵名單位後綴與 `unit` 一致、`min`/`max`),並記錄 `path` 與檔案位元組的 `sha256`(hex 大寫,與 C# 相同)。
- **引用解析** `parameters.resolve_references(tree, pset)`:整個字串恰為 `"${params:<key>}"`(**必須加引號**,否則 YAML 層已擋)者以參數 `value` 就地取代;未知鍵、欄位後綴(`_nm`、`_m`、`_s`、`_kn`、`_deg`、`_degPerMin`、`_mps`、`_fraction`;慣用欄位 `metres`/`fractionOfDraft`/`passMark`)與參數 `unit` 不符、引用於 `id`/`name`/`seed`/`type`/`rule`/`metric` 等欄位、來源文件缺漏皆 `ValueError`。回傳解析清單 `[{path, key, value, unit, source, revision, clause}]`。
- **自動載入**:`load_scenario(ref, parameters=None)` 在情境含引用而未給參數集時,依 `assessment.parameterSet`(絕對路徑;否則依序專案根目錄、情境檔目錄、工作目錄;未宣告時預設 `data/parameters/company-procedures.yaml`)尋找,找不到即 `ValueError` 並提示 `--parameters`。無引用的情境不需要參數集。
- **載入結果**:正規化 dict 新增 `parameterSet`(`{id, version, path, sha256, resolved}`,同紀錄標頭 `header.parameterSet`;無引用時 `None`)與數值已代入的 `timeline` / `route` / `zones` / `assessment` / `briefing` 子樹(原樣保留,**不模擬**),以及補上 C# 預設值(`ScenarioBridge.cs`)的 `sensors` / `alerts` / `bnwas`(結構檢查同 C# `Scenario.StructuralIssues`:秒數/門檻為正、`offHeadingDelay_s` ≥ 0、`dormant_s` 180–720;未設的門檻省略 = 關閉;數值欄位可引用參數,`mode` 不可)。伺服器 ack 與 `state.scenario.parameterSetVersion` 由此取得。
- **結構檢查** `scenario.structural_issues(raw)`(C# `Scenario.StructuralIssues` 的便宜規則,第一個錯誤即 `ValueError`):時間表 `id` 唯一、`when` 條件型別/必要欄位/參照(區域、航點、目標)、`repeat`、動作名稱在允許清單(`loadScenario`/`reset`/`restore`/`snapshot` 不允許)與各動作必要欄位;航線航點 ≥ 2、`name` 唯一、位置、`startLeg`、相鄰航點不重合;區域 `id` 唯一、`type`、多邊形 3–64 點、水深為正;評估規則 `id` 唯一、`rule`/`metric`/`expect` 名稱、`lessThan`、`zoneId`/`itemId`/`waypoint` 參照、`within_s`;簡報 `roles[].role`、`minParticipants`、`duration_s`。不做初始狀態相關檢查(初始 UKC、禁區、多邊形自交等由 C# `ScenarioValidator` 負責)。
- **跨語言一致性測試**:對 `data/scenarios/*.yaml` 以 Python 載入+解析,與 C# 紀錄夾具(`src/Instruments.Web/tests/fixtures/record-*-core.jsonl` 第一行 `header.scenario` / `header.parameterSet`)比對解析路徑集合、鍵集合、版次、各已解析數值(1e-9)與 route/zones/assessment/timeline/briefing 的全部葉值(Python 為 C# 輸出的子集)。E07 夾具錄製後 YAML 與參數集又改過(CPA 門檻改用 `navigation.cpa.channel_nm`、ecrPrompt 條件),差異列在測試的 `KNOWN_FIXTURE_DRIFT`,重新錄製夾具後可清空。

## 套件結構

| 模組 | 內容 |
|---|---|
| `particulars.py` | 讀 particulars.json;`loading`(full/ballast)決定排水量、吃水、Cb;`null` 欄位以經驗式補並記在 `estimated` |
| `propeller.py` | Wageningen B 系列 K_T、K_Q 多項式(Oosterveld & van Oossanen 1975);簡化四象限推力;鎖定螺槳阻力 |
| `coefficients.py` | 係數估計(見下)、係數檔讀寫、KVLCC2 基準係數 |
| `mmg.py` | 3 自由度 MMG + 致動器 + 環境(含種子陣風)+ 故障行為,固定步長 RK4,狀態 JSON(state.schema.json),狀態雜湊,`LocalTangentPlane`(ENU ↔ 經緯度,與 C# 同式),快照/還原 |
| `parameters.py` | 公司程序參數集(`data/parameters/company-procedures.yaml`、`parameter-set.schema.json`)載入/檢查、`${params:…}` 引用解析、與 C# `YamlJson` 相同的 YAML 純量規則 |
| `scenario.py` | 情境 YAML(`data/scenarios/*.yaml`、`scenario.schema.json`)解析/參數替換/結構檢查/初始化,語意與 C# `ScenarioLoader` / `Scenario.Validate` / `SimulationEngine.Initialize` 相同;時間表/航線/區域/評估只檢查不模擬 |
| `manoeuvres.py` | 迴旋、Z 形、緊急停船、慣性停船、倒車→進車、側推迴轉、速度-轉速 |
| `identify.py` | scipy `least_squares`(trf,有界)+ 平行有限差分 Jacobian + 先驗殘差 |
| `report.py` | 驗證報告(markdown、matplotlib png、json) |
| `server.py` | websockets 即時模擬(25 Hz 廣播、自船命令、操舵模式、情境載入、故障、快照;情境引擎 4 指令、警報/BNWAS 4 指令與 targetControl 回 ok=false) |
| `cli.py` | `simosa-brm` 入口 |
| `tests/` | 單位/正負慣例、確定性、B 系列與 KVLCC2 回歸、速度-轉速、FSB1 迴旋/Z 形/停船黃金測試、FSB2 海報比對、WebSocket、情境載入/故障/陣風/快照(`test_server_scenario_faults.py`)、參數集/引用解析/結構檢查/與 C# 紀錄標頭的跨語言一致性(`test_scenario_params.py`) |

## 模型與來源

座標與無因次化依 Yasukawa & Yoshimura (2015):船舯座標,u、v_m、r;X、Y 以 ½ρLdU²、N 以 ½ρL²dU²、質量以 ½ρL²d、慣性矩以 ½ρL⁴d,L = LPP,d = 平均吃水。
航向 ψ 自北順時針,位置為本地 ENU(x 東、y 北),舵角與迴轉率右正(state.schema.json)。

| 項目 | 方法 | 來源/備註 |
|---|---|---|
| 運動方程式 | (m+m_x)u̇ − (m+m_y)v_m r − x_G m r² = X 等三式;x_G 支援但目前設 0(LCG 未知) | Yasukawa & Yoshimura 2015 式 (1)–(3) |
| 船體力 | 多項式 X_vv…X_vvvv、Y_v…Y_rrr、N_v…N_rrr(½ρLdU²、½ρL²dU²,v' = v_m/U、r' = rL/U);大漂角與低速時與截面橫流阻力(C_D=1.0,10 條帶 ∫(v+xr)\|v+xr\|dx)混合,權重取「船舯漂角 20–40°」與「艏搖局部流向角 atan(½L\|r\|/\|u\|) 30–45°(r' ≈ 1.15–2)」兩通道的最大值;橫流區線性項乘 \|cos β\|(Munk 力矩 ∝ sin 2β)、多項式 X 項淡出、阻力改為 −R0'·½ρLd·u\|u\|(倒退時向前);無因次化速度下限 0.5 m/s | 規劃書 6.2;Yasukawa & Yoshimura 2015 式 (6)–(8) |
| 線性導數 | Y_β = ½πk + 1.4 Cb B/L、Y_r − m_x' = ¼πk(故 Y_r = ¼πk + m_x')、N_β = k、N_r = −0.54k + k²(k = 2d/L) | Kijima et al. 1990 / Inoue 1981 |
| 非線性導數 | 以 KVLCC2 基準值為起點(本船 L/B、d/B 與 KVLCC2 相近),再由識別調整 | Yasukawa & Yoshimura 2015 Table 3;規劃書 6.1 |
| 附加質量 | m_x、m_y、J_zz 的 Zhou 經驗式(Motora 圖表回歸);I_zz 以 k = 0.25 L | 周昭明 |
| 阻力 R0'(U) | ITTC-57 摩擦 × 形狀因子 × S/(Ld)(Mumford 濕面積)+ c_w(Fn/Fn_ref)^q;由 speedTrial(或海報車鐘表)以推力恆等反推、三參數擬合;低速不低於最低資料點 | 規劃書 6.3 識別順序第一步 |
| 螺槳 | B 系列 K_T、K_Q 在 0≤J≤J0 的二次擬合;w_P = w_P0·exp(−4β_P²)(w_P0 = 0.5Cb − 0.05,t_P = 0.6 w_P0);倒車:多項式負 J 外推 × 0.85;軸停止:鎖定圓盤 C_D 0.5;倒車橫向力 8% 推力(右旋槳艉向左) | Oosterveld & van Oossanen 1975;規劃書 6.2 |
| 舵 | F_N = ½ρA_R U_R² f_α sin α_R,Fujii f_α = 6.13Λ/(Λ+2.25);u_R = ε u_P √(η(1+κ(√(1+8K_T/πJ²)−1))² + 1−η)(流向依舵面積加權平均流速:倒退中正車時仍為正)、v_R = Uγ_Rβ_R(γ_R 依 β_R 正負)、l_R、ε;X_R = −(1−t_R)F_N sinδ、Y_R = −(1+a_H)F_N cosδ、N_R = −(x_R + a_H x_H)F_N cosδ;a_H = 0.627Cb − 0.153、1 − t_R = 0.28Cb + 0.55、ε = −156.2(CbB/L)² + 41.6(CbB/L) − 1.76 | Kijima 1990;KVLCC2 的 γ_R、l_R、κ |
| Schilling 舵 | `rudder.type` 含 "Schilling":有效展弦比 ×1.5(端板),\|α\| ≤ 35° 用 f_α sin α,35–70° 以 `highLiftSlopeFactor`(0.5)× f_α 的斜率延伸,逾 70° 保持;`swirlAngle_deg` 為滑流旋轉造成的有效舵角偏移(× 滑流加速因子,上限 5),識別用 | 無實測升力曲線,係數可調 |
| 舵機 | 速率 = 70° / `hardOverTime35_s`(解讀為 35° 一舷至 35° 另一舷);No.2 用兩泵值 | particulars |
| 主機 | 一階滯後(6 s)+ 速率限制(3 rpm/s);STOP:軸轉速以 20 s 時間常數衰減;換向:燃油切斷→軸轉速降到 5% MCR 且逾 `reversalDelay_s` 後反向點火;起動延遲 5 s。No.1 的換向延遲取自試俥緊急停船紀錄(機械特性,非水動力擬合);No.2 無資料,假設 90 s | 規劃書 6.2 |
| 艏側推 | 推力 = order × 額定 × effectiveness × exp(−ln2 (u/2.5 kn)²);一階 + 速率限制(`fullThrustDelay_s`);作用點 0.43 L 前 | 規劃書 6.2 |
| 風 | Blendermann (1994) 參數式(油輪:C_Dt 0.70、C_Dl,AF 0.90/0.55、δ 0.40;分母 1 − δ/2(1 − C_Dl/C_Dt)sin²2ε 的 C_Dl = C_Dl,AF·A_F/A_L 以側面積為基準,與 MSS blendermann94 相同);受風面積 `null` 時以 LOA×乾舷 + 住艙(11 m × 0.15 LOA)+ 甲板設備(3 m × 0.5 LOA)估計並標註 | Fujiwara 1998 / Isherwood 1972 回歸表尚未數位化 |
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
6. 資料分工:`identification` 區(35° 迴旋、10/10 Z 形)只用於擬合;`validation` 區(20/20 Z 形、緊急/慣性停船、倒車→進車、側推、速度-功率)不進入殘差。兩個例外已在報告中標示為非獨立驗證:主機換向延遲 `engine.reversalDelay_s` 取自 validation.crashStop.asternStart_s(機械特性,寫入 `source.notes`);速度-轉速曲線與阻力擬合用的是同一組 speedTrial(規劃書 6.3 識別順序第一步)。

## 結果摘要(2026-10-08,物理審查修正後重跑識別;詳見 `build/calibration/<ID>/validation.full.md`)

- FSB1 滿載(試俥實測):識別區 13/14 在公差內(右迴旋橫距 142.8 m vs 175.5 m 超出,其餘前進距離/戰術直徑偏差 ≤ 8.3%、10/10 超越角偏差 ≤ 1.9°);驗證區 20/20 Z 形 4/4、緊急停船時間 288 s(試俥 259 s,+11%)/航跡 1285 m(1191 m,+8%)2/2、速度-轉速 4/4;正式驗收項目 23/26。慣性停船(試俥航跡彎曲)、倒車→進車(換向延遲以進→退值假設、無風車/壓縮制動模型)、艏側推 90°(左 −17%、右 −9%,參考項)仍超出。
- FSB2 滿載(海報,8 參數粗識別,航向穩定性導數留經驗值):迴旋識別區 17/22 在公差內(左/右戰術直徑 282/300 m vs 海報 208/245 m、90° 速度偏高 1.4–1.8 kn、右 270° 速度偏低 0.8 kn);緊急停船超出(停船時間 +74%、航跡 +36%,海報讀值且換向延遲未知);EEDI 13.06 kn/110 rpm 點與海報 12.8 kn/133.6 rpm 互相矛盾(trial_targets 已註記)。放開 Y'_r(9 參數)可達 19/22,但 Y'_r 跑到物理下界 0.02(船體橫向力幾乎消失),與 12 參數時 Y'_v、Y'_r 跑到下界同一現象,故不採用;海報戰術直徑 2.0 L 對 104 m 油輪偏小,待試俥報告核對。

## 壓載係數(估計、未識別;2026-10-17)

| 檔案 | 產生方式 | 資料 |
|---|---|---|
| `data/ships/FSB1/coefficients.ballast.json` | `estimate --loading ballast`(純經驗式) | 吃水 F 2.03 / A 5.41 m(裝載手冊 COND.3);阻力由海報車鐘壓載航速擬合;海報壓載迴旋/停船估計值只供趨勢參考 |
| `data/ships/FSB2/coefficients.ballast.json` | `estimate --loading ballast --transfer-from full`(經驗式 + 滿載識別修正轉移) | 吃水 F 4.4 / A 6.5 m(2026-09 實際航次;海報 5.80 m 平吃水);Cb 0.738、排水量 8,578 t 依吃水比估計;阻力由海報 `speedBallast_kn`(NAVF 13.3 kn … DSAH 6.0 kn)擬合;海報無壓載操縱資料 |

- **滿載識別修正轉移**(`identify.transfer_identification`):對滿載識別過的參數,初值不為 0 者 p_壓載 = p_壓載經驗 × p_滿載識別 / p_滿載經驗,
  初值為 0 者(swirlAngle)加上差值,夾在 `PARAM_SPECS` 範圍內;明細寫入 `source.identificationTransfer`,`source.method` 仍為 `estimate`、
  `identifiedParameters` 為空。理由:No.2 滿載的經驗式戰術直徑 3.7–4.0 L,識別後 2.7–2.9 L(−26 至 −29%,主要是舵-船體交互作用與 KVLCC2
  代用非線性導數的偏差,與吃水無關);純經驗壓載得 3.2–3.6 L,反而大於滿載識別結果,與壓載迴旋圈較小的常識相反。No.1 滿載識別修正只有
  −8 至 −12%,壓載維持純經驗(以 `--transfer-from full` 重產可得戰術直徑 2.75/2.94 L,與現檔 2.73/3.13 L 差異不大;未改動)。
- **舵面積**:particulars 為 null 時改以滿載吃水估計(舵為固定幾何;先前壓載依壓載吃水縮小為 10.3 m²,現與滿載同為 13.61 m²);No.1 有實測面積不受影響。
- **驗證**:`validate --loading ballast` 對目標裝載(trial_targets.condition = full)以外的裝載,把迴旋/Z 形/停船比對改列「參考」,正式項目只剩本裝載海報車鐘航速
  (5/5;阻力即由此擬合,非獨立驗證),另加「裝載趨勢」節:以各裝載 NAVF 轉速的直航速度執行同一組標準操縱,與滿載係數檔比較方向
  (`manoeuvres.loading_trend_metrics` / `loading_trends`;測試 `tests/test_fsb2_ballast.py`)。報告 `build/calibration/<ID>/validation.ballast.md`。

裝載趨勢(dt 0.02 s;壓載 / 滿載,括號為比值;受風項為 25 kn 正橫風):

| 項目 | FSB2 滿載 → 壓載 | FSB1 滿載 → 壓載 | 預期 |
|---|---|---|---|
| 進入速度(NAVF) | 12.8 → 13.3 kn | 14.5 → 14.7 kn | — |
| 左/右 35° 戰術直徑 / L | 2.71 → 2.33(0.86)/ 2.89 → 2.52(0.87) | 3.00 → 2.72(0.91)/ 3.18 → 3.13(0.98) | 較小 ✔ |
| 左/右 前進距離 / L | 2.89 → 2.53(0.87)/ 2.99 → 2.63(0.88) | 2.90 → 2.74(0.95)/ 2.99 → 2.95(0.99) | 較小 ✔ |
| 10/10 Z 形 第 1/2 超越角 | 5.0/6.6° → 5.5/6.2° | 7.0/8.8° → 5.5/4.4° | 參考 |
| 緊急停船 時間 / 航跡 | 355 s / 1,254 m → 283 s / 1,080 m(0.80 / 0.86) | 285 s / 1,230 m → 208 s / 894 m(0.73 / 0.73) | 較短 ✔ |
| 停俥慣性減至 5 kn 距離 | 2,605 → 2,117 m(0.81) | 2,699 → 1,335 m(0.49) | 較短 ✔ |
| 側面積 / (L·d) | 1.03 → 1.71 | 1.02 → 2.13 | 較大 ✔ |
| 3 kn 自動舵保持航向的漂角 | 5.5 → 11.1°(2.0) | 6.4 → 16.4°(2.6) | 較大 ✔ |
| 零速 300 s 漂移速度 | 0.84 → 1.00 kn | 0.88 → 1.04 kn | 較大 ✔ |

FSB2 壓載海報車鐘航速 5 點重現在 0.01 kn 內。限制:目標值只有航速;迴旋/停船為估計,F01_FSB2_ballast 的量測 `dataQuality` 為 none(無比較);
艉傾 2.1 m 未納入導數(實船壓載航向穩定性可能較高、迴旋圈可能較模擬大);受風面積、換向延遲 90 s 為假設;滿載重新識別後須重產壓載檔
(`test_ballast_file_matches_tool_output` 會提示)。

## 物理模型審查(2026-10-08,對抗式)與修正

逐項核對 Yasukawa & Yoshimura (2015) 的運動方程式、船體/螺槳/舵模型、正負慣例、RK4、風與淺水、MSC.137(76) 量測定義與識別資料分工,
回歸測試在 `tests/test_physics_review.py`。結論與修正:

| 項目 | 結論 |
|---|---|
| (a) 運動方程式、x_G、無因次化 | 正確(式 (1)–(3) 含 x_G 耦合項、2×2 聯立;½ρLdU²、½ρL²dU²、½ρL²d、½ρL⁴d)。KVLCC2 回歸測試改用論文 x_G = 11.2 m。 |
| (b) 船體多項式 | 項與符號正確。**修正**:阻力 −R0'·½ρLdU² 與 u 的方向無關,倒退時把船往後推(愈退愈快)→ 改為依縱向速度方向(橫流區 −R0'·½ρLd·u\|u\|);純橫移(β = 90°)時 X'_vvvv v'⁴ 給出向前推力(1 m/s 時 +208 kN)、N'_v v' 給出虛假 Munk 力矩(約為艏側推力矩 2 倍)→ 橫流區多項式 X 項淡出、線性項乘 \|cos β\|;零速純艏搖完全無阻尼(多項式以 U 無因次化歸零、橫流權重只看漂角)→ 加入艏搖通道 atan(½L\|r\|/\|u\|) 30–45°。正常操縱區(\|β\| < 20°、r' ≲ 1.15)數值完全不變(35° 迴旋結果逐位相同)。 |
| (c) 螺槳 | (1−t_P)T、J_P = u(1−w_P)/(nD)、w_P = w_P0 e^{−4β_P²}、B 系列 39/47 項係數表核對無誤;四象限為簡化(負 J 外推 × 0.85、jClip、鎖定圓盤),倒車橫向力方向正確(右旋槳艉向左)。未修正:無 C_T*(β)、C_Q*(β) 表(限制 2)。 |
| (d) 舵 | u_R(ε、κ、η)、v_R = Uγ_Rβ_R(β_R 正負取不同 γ_R)、α_R = δ − atan(v_R/u_R)、F_N、X_R/Y_R/N_R 與 t_R、a_H、x_H 的位置與符號正確。**修正**:倒退中正車(u<0、n>0)時 u_R 被強制取負,滑流實際仍由前向後流過舵 → 改依舵面積加權平均流速定號。 |
| (e) 正負慣例 | 右舵 → F_N>0 → N_R>0 → r>0、航向增加、v_m<0、β = atan(−v/u) > 0;ROT = r·180/π·60 度/分。正確。 |
| (f) RK4 | 古典四階、固定步長、致動器一併積分、純浮點無亂數;狀態雜湊測試通過。識別用 dt 0.1、驗證用 0.02(文件已載)。 |
| (g) 風、淺水 | **修正**:Blendermann 分母 1 − δ/2(1 − C_Dl/C_Dt)sin²2ε 誤用以正面積為基準的 C_Dl,AF(0.90),應為 C_Dl = C_Dl,AF·A_F/A_L(與 MSS `blendermann94` 相同);修正後 45° 側向力增大約 17%。單位(kn→m/s、ρ_a 1.225、N 以 A_L·L_oa)正確。淺水倍率為趨勢用,來源(Kijima & Nakiri 型)與單位無誤但未驗證(限制 7)。 |
| (h) 迴旋幾何 | 前進距離/橫距在航向變化 90°、戰術直徑在 180°,皆以下舵令時的船舯位置與原航向為基準並線性內插,符合 MSC.137(76)/MSC/Circ.1053。 |
| (i) Z 形超越角 | 航向變化達 ±ψ 時反舵,超越角 = 反舵後最大航向偏離 − ψ,符合標準定義。 |
| (j) 識別資料 | `build_targets`/`simulate_targets` 只讀 `identification` 區;validation 區未進殘差。兩處間接使用已標示(見「識別流程」第 6 點)。 |
| 經驗式 | **修正**:Kijima/Inoue 回歸式為 Y'_r − m'_x = ¼πk,原實作漏加 m'_x(FSB1 約 +19%);Y'_r 在 No.1 為識別參數,影響僅在初值/先驗,No.2 固定為經驗值故重跑識別。 |

修正後重跑 `identify`(FSB1 12 參數、FSB2 8 參數)並以 `validate` 重新產生報告。

## 已知限制

1. **非線性船體導數**以 KVLCC2 基準值代用再識別;Yoshimura & Masumoto (2012)/Kijima (1990) 的非線性回歸常數尚待依原文核對後納入(附錄 C 事項)。線性導數經驗式誤差 ±20–30%(規劃書 6.3)。
2. **四象限螺槳**為簡化(負 J 外推、倒車折減、鎖定圓盤);無 Wageningen C_T*(β)、C_Q*(β) 表;風車/主機壓縮制動未建模。倒車→進車與慣性停船偏差主要來自此處與換向延遲假設。
3. **慣性停船**試俥航跡彎曲(head reach 1098 m、side reach 402 m),直線滑行模擬的 5 kn 時間/距離偏大約 2 倍;屬資料條件差異,未調整阻力。
4. **試俥功率**:以 B 系列 K_Q 估計的軸功率比試俥低約 17–20%(實際螺槳非 B 系列、PBCF、η_R 等);速度-轉速以推力恆等校準,不受影響,但功率/負荷顯示僅供參考。
5. **Schilling 舵** 35° 以上升力曲線與滑流不對稱為假設模型;No.1 右迴旋橫距(初期迴轉快、定常圓大的試俥特徵)無法完全重現。
6. **No.2** 僅海報圖面讀值;舵面積、受風面積、側推推力、換向延遲為估計;取得操縱試驗報告後重跑 `identify`。壓載狀態(兩船)僅有海報車鐘速度,係數為估計(`loading=ballast` 可用,未驗證;見「壓載係數」);艏艉吃水差對導數的影響未納入。
7. 風為 Blendermann 參數式與估計面積,陣風只是每秒更新的風速係數(無空間/頻譜模型);淺水修正為趨勢用倍率;岸壁效應、浪、拖船、錨纜、4 自由度橫搖未實作(規劃書 6.2 其餘項目)。故障只有 `steeringGear`、`mainEngine`、`bowThruster`、`steeringFuOnly`、`steeringPumpFailure` 五種改變行為(與 C# 相同;C# 的 `engineStartFailure` 尚未移植);`autopilotFailure` / `gyroFailure` 只模擬「自動舵不可用」(啟用被拒、啟用中跳脫,與 C# 相同),電羅經指示值等感測器層不模擬;其餘名稱僅記錄;能見度只記錄不用。情境的時間表/航線/區域/評估與 sensors/alerts/bnwas 設定只做參數解析與結構檢查(後者補 C# 預設值後保留),不執行(情境引擎、感測器故障層、警報與 BNWAS 由 C# SimCore 負責)。
8. 兩船 `x_G` 設 0(LCG 未知);KVLCC2 回歸測試已用論文值 x_G = 11.2 m,x_G 對迴旋直徑影響約 8–10%,取得兩船 LCG 後應填入 `reference.xG_m`。
10. **低速/零速艏搖阻尼**只有橫流阻力(C_D = 1.0,未校準)與多項式外推;零速側推的穩態迴轉率約 40–45°/min(海報 20°/min),艏側推 90° 迴轉時間偏短約 16%;側推有效推力(`thruster.effectiveness`)與 C_D 待船長/引水人評價或 VDR 資料校準(規劃書 6.4 將側推列為趨勢項)。側推時船體因「旋轉力的運動學」((m+m_y)v r 耦合)會逐漸獲得前進速度(上限約 推力/((m+m_x)r)),與試俥記錄「約 1 kn」一致。
11. **倒退(u<0)**:多項式 Y、N 導數沿用前進值(MMG 標準模型未定義倒退),僅阻力方向與舵流向已處理;倒退操縱性未驗證。
9. 本工具只讀寫專案資料夾;識別結果具確定性(同一機器、同一套件版本);不同 CPU/BLAS 可能使最佳化路徑略有差異,但係數檔為最終依據。
