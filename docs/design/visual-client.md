# 3D 視景客戶端設計(Visual.Shared + Visual.Godot)

> 對應規劃書:第 5.2 節 Visual(只做顯示、不做物理)、第 5.4 節硬體基準(內顯文書筆電)、第 7.3 節視覺設計與品質分級(D6)、第 10.1 節引擎選型(D7:Godot 4.5 .NET,零授權費)、第 14 章效能驗收。
> 版本:第一版(2026-10)。程式位置:`src/Visual.Shared/`(純 .NET 類別庫,可單元測試)、`src/Visual.Godot/`(Godot 專案)、`tests/Visual.Shared.Tests/`、`tools/visual-*.{sh,ps1}`、`data/visual/scenario-areas.json`。

## 1. 範圍與原則

- 視景是狀態匯流排的一個訂閱者(規劃書第 5.1 節原則 2):只讀 `ws://127.0.0.1:8765` 的 25 Hz 狀態幀,**不送指令、不做物理**;所有幾何(港口、船)只影響畫面,不影響雷達、ECDIS、儀器與評估。
- 三級品質共用同一組幾何與狀態,只差渲染設定(第 7.3 節);以內顯文書筆電為效能基準機,低/中品質 1080p ≥ 30 fps,幀率連續低於 25 fps 自動降一級並記錄。
- 多螢幕/多機:**每台顯示器一個程序訂閱同一狀態流**(規劃書第 10.1 節建議),以啟動參數指定視角與螢幕。
- 所有輸出與快取留在專案內(CLAUDE.md):Godot 匯入快取 `src/Visual.Godot/.godot/`、.NET 輸出 `.godot/mono/temp/`、NuGet `.cache/nuget/`、Godot 編輯器/使用者資料 `.cache/godot/`、品質設定與紀錄 `build/visual/`。
- 第一版為示意等級:**無實船 3D 模型**(FSB1/FSB2 以 particulars 參數化船體代表,標註待替換)、**港口為示意低面數幾何**(來源 `data/areas/*.json` 手繪輪廓,非 ENC)。

## 2. 架構

```
SimCore.Host ──ws 8765 (state 25 Hz, event, assessment)──▶ Visual.Shared.Net.StateClient
                                                              │ Push(state, 牆鐘)
                                                              ▼
                                                   StateInterpolator(速率估計、插值/外推)
                                                              │ Sample(牆鐘) → VisualSample
                                                              ▼
   Visual.Godot.Main._Process ──▶ OwnShip / Targets / TugsAnchors / Harbour / Ocean / Weather / CameraRig / HUD
                                   ▲
   AreaLoader(data/areas → ENU → MeshData)、HullParameters + ProceduralHullBuilder(particulars → MeshData)
   QualityDetector + AdaptiveQualityController + QualityPersistence(build/visual/quality.json)
```

| 元件 | 位置 | 職責 |
|---|---|---|
| `Net/StateClient` | Visual.Shared | ClientWebSocket 訂閱;斷線以 0.5 → 5 s 退避自動重連;不走系統代理;事件框只記 kind 供 HUD |
| `State/StateFrameParser` | Visual.Shared | 分辨 state / event / assessment 框;以 `ContractJson` 反序列化 `OwnShipState`(含 targets/tugs/anchors/berth/truth/sensors/alerts/radarEnvironment);壞框不丟例外 |
| `State/StateInterpolator` | Visual.Shared | 第 4 節 |
| `State/OriginSolver` | Visual.Shared | 由狀態幀的 lat/lon 與 ENU x/y 反推情境原點(迭代 4 次,1e-8°),區域資料據此投影 |
| `Scene/SceneCoordinates` | Visual.Shared | 第 3 節座標換算 |
| `Quality/*` | Visual.Shared | 等級參數、硬體偵測、自動降級、持久化 |
| `Area/*` | Visual.Shared | 區域檔載入、擠出幾何(耳切法三角化)、情境 → 區域對照 |
| `Ship/*` | Visual.Shared | particulars → 船體參數、程序化船體/上層建築/艏樓/桅網格(完整與低面數) |
| `View/*` | Visual.Shared | 視角定義(眼位、偏航、FOV)、啟動參數解析 |
| `Env/EnvironmentMapper` | Visual.Shared | 能見度 → 霧、風 → 海況/波高、雨區 → 雨、日夜/太陽位置、月光與夜空亮度(`Env/MoonModel`:月球位置/月相/照度) |
| `Smoke/*` | Visual.Shared | 無頭煙霧測試的假狀態序列與報告 |
| `scripts/Main.cs` | Visual.Godot | 啟動流程、每幀更新、品質套用、煙霧模式 |
| `scripts/OceanNode.cs` + `shaders/ocean.gdshader` | Visual.Godot | 海面 |
| `scripts/WeatherNode.cs` | Visual.Godot | 天空、太陽、霧、雨粒子 |
| `scripts/ShipNode.cs`、`NavLights.cs`、`TargetShipsNode.cs`、`TugsAndAnchorsNode.cs` | Visual.Godot | 自船、目標船(含 LOD)、航行燈、拖船與纜線、錨鏈 |
| `scripts/HarbourNode.cs` | Visual.Godot | 港口幾何、碰墊/繫船柱、導燈、水域示意線 |
| `scripts/CameraRig.cs`、`HudLayer.cs` | Visual.Godot | 視角切換與環視、HUD 與品質 UI |

## 3. 座標系

| 系統 | 軸 | 備註 |
|---|---|---|
| 核心 ENU(契約 `pos.x/y`) | x 東、y 北、z 上(m) | 原點為情境 `origin`;視景以 `OriginSolver` 反推 |
| Godot 場景 | x 東、y 上、z 南(右手系;節點前方 −z) | `SceneCoordinates.FromEnu(e, n, u) = (e, u, −n)` |
| 航向 → 節點 yaw | `yaw = −heading(弧度)` | 航向 090 → 節點前方指 +x(東);測試 `SceneCoordinatesTests` |
| 船體(MMG) | x 向艏、y 向右舷、z 上 | 船節點區域座標 `(y, z, −x)`;水線在節點 y = 0(船體以 −吃水 下沉) |
| 正面(背面剔除) | 從法線方向看為逆時針 | 擠出頂面:ENU 逆時針索引直接使用;側壁 a0 → b0 → b1 → a1;測試 `HarbourMeshBuilderTests`、`ShipModelTests.WindingIsConsistentWithNormals` |

## 4. 狀態契約與時間平滑

- 狀態幀:`src/Contracts/state.schema.json`(C# 型別 `SimCore.Contracts.OwnShipState`)。視景使用:`t`、`utc`(選填;日夜,見下)、`pos`、`heading`、`cog`、`sog`、`rot`、`rudder`、`rpm`、`telegraph`、`wind`、`visibility_nm`、`flags`、`targets[]`(`pos/heading/cog/sog/rot/loa/beam/draft/shipType/lights/aisOn`;**AIS 關閉者仍畫**,視景是「眼睛看到的」)、`tugs[]`(`pos/mode/status/direction_deg/lineParted`)、`anchors[]`(`side/status/position`)、`berth`(`id`、`lines[]{id, status, tension_t, mbl_t, bollardId}` → 繫纜;碰墊/繫船柱位置來自區域檔)、`radarEnvironment`(雨區;**只在關鍵幀帶出**,插值器保留最後一次、時間倒退時清除)、`scenario.id`(查區域對照)。`truth/sensors/alerts` 可解析但視景不顯示(學員端不應看到真值)。
- 事件框 `{"type":"event", "event":{"kind":…}}` 與 `{"type":"assessment"}` 只記最近事件 kind 於 HUD。
- **插值/外推**(`StateInterpolator`):保留最近 8 幀的 (t, 到達牆鐘);速率 = Δt/Δ牆鐘(即時 1、凍結 0、timescale 10 → 10);顯示時間 = 最新 t + 速率 × (now − 到達 − 0.08 s 緩衝);落在兩幀間線性插值(航向走最短弧);超出最新幀以 COG/SOG/ROT 外推最多 0.25 s,之後凍結並標示 `Stalled`;t 倒退(重設/倒帶/重播)或前跳 > 5 s(快照還原)清空歷史。目標船以 id 配對插值,只在單側者取最新幀。
  - **成批到達**(啟動第一幀、網路停頓後 TCP 一次送達數幀、GC 卡頓):同牆鐘(間隔 ≤ 2 ms)到達的幀不含速率資訊,速率改以「有效間隔的中位數 × 間隔數」作 Δ牆鐘下限(有效間隔不到一半時沿用前值;沒有成批幀時直接用最舊/最新兩幀),避免估到 2–3 倍;顯示時間另外保證單調不減(暫停而非倒退;Reset 例外)。凍結(Δt = 0)直接判為速率 0。測試:`StateInterpolatorTests`、`StateInterpolatorBurstTests`。
- **UTC 與日夜**:核心 `state.utc`(ISO 8601,毫秒,Z)= 練習開始 UTC + `t`;開始時刻為情境 `startTimeUtc`,未給時為主控程序載入情境的實際時鐘(`EngineOptions.Clock`;引擎寫回情境,紀錄標頭因此帶有它、重播沿用而非現在時間;不進雜湊)。`--daynight=auto` 時 `EnvironmentMapper` 優先用 `state.utc` 算太陽高度,無(舊核心、未設時鐘)或不可解析時退回 `--start-utc` + 模擬秒數,兩者皆無為白天;day/dusk/night 固定。測試:`EnvironmentUtcTests`;核心端 `StateUtcTests`(含重播一致、雜湊不變)。
- **月光與夜間光照**(`MoonModel` 純函數、`EnvironmentMapper.MoonLookFor`;Godot `WeatherNode` 套用):
  - 月球位置:Schlyter「How to compute planetary positions」的月球軌道根數 + 主要攝動(黃經 12 項、黃緯 5 項、距離 2 項),黃道 → 赤道 → 地平,含視差修正;高度/方位誤差約 ±0.5°(需求 ±1°,只作光照方向與是否在地平線上)。月相 = 日月真黃經離角 ε 的照明比例 (1 − cos ε)/2;月齡由黃經差推得。只依 UTC 與自船經緯度,確定性。
  - 月光相對照度 `MoonModel.RelativeIlluminance(仰角, 照明比例)` 0–1:滿月天頂晴空 = 1(≈ 0.25 lux,`FullMoonZenithLux`);仰角 ≤ 0 → 0;相位項依 Krisciunas & Schaefer (1991) 視星等 0.026·α + 4×10⁻⁹·α⁴(上弦約 0.09)、乘 sin(仰角) 與大氣消光(0.2 mag/氣團)。
  - `EnvironmentLook.Moon`(`MoonLook`:`ElevationDeg`、`AzimuthDeg`、`Phase`、`Intensity` 晴空、`Light` 經天候衰減)與 `NightSkyBrightness`:`Light = Intensity × MoonlightWeatherFactor(能見度, 雨)`(能見度 ≥ 5 nm 不衰減,線性降到 0.5 nm 的 0.1;雨強度 1 再乘 0.4);`NightSkyBrightness = (1 − Daylight) × (0.08 星光底值 + 0.92 × Light)`,白天為 0。
  - 日夜模式:`auto` 依顯示幀 UTC(同太陽)算真實月球,無 UTC(白天)無月光;`night` 固定模式月球固定高度 45°、方位 135°,月相由顯示幀 UTC(`state.utc` → `--start-utc` + t)推得、皆無時 0.5;`day`/`dusk` 月球在地平線下。
  - Godot:`WeatherNode` 月光 `DirectionalLight3D`(冷色 (0.72, 0.80, 1.0),能量 0.35 × `Moon.Light` × (1 − Daylight),低於 0.002 時隱藏燈;陰影依品質 `MoonShadows`,低品質關閉),夜間環境光 `AmbientLightEnergy = 0.85 × Daylight + 0.05 + 0.25 × NightSkyBrightness`(無月夜 ≈ 0.07、滿月晴夜 0.30),天空/霧底色加入夜空亮度項;HUD 顯示月球高度/方位、相位、月光 %。**亮度係數需實機確認**(第 10 節)。
  - 測試:`MoonModelTests`(2026-01-03 滿月、2026-01-18 新月,及日食/月食日 2026-02-17、2026-08-12、2026-03-03、2026-08-28 必為新月/滿月;上/下弦與漸盈旗標;高雄滿月午夜近中天、正午在地平線下、傍晚東方升起、黎明前西方;期望值為演算法自身的月齡計算,非外部星曆)、`EnvironmentMoonlightTests`(滿月夜比新月夜亮、月球地平線下無月光、霧/雨衰減、night 固定模式月相來源、day/dusk 無月光、夜空亮度混合、低品質無月光陰影)。
- **繫纜**(`MooringLinePresentation`,純計算;Godot `MooringLinesNode`):`berth.lines[]` 中 fast/heaving/slacking 者自導纜孔畫到區域檔泊位(`state.berth.id` 對應的 `BerthGeometry`,找不到時搜所有泊位)的繫船柱 `bollardId`(碼頭面 3 m + 0.4 m);stowed/letGo 不畫;**parted 不畫線**,在導纜孔處紅球閃爍 3 s(4 Hz)。張力比 = `tension_t / mbl_t`:< 50 % 纜繩本色、50–80 % 黃、> 80 % 紅。契約無導纜孔座標,**導纜孔為估計**:艏/艉依纜名(`head/bow/fwd*/fore*` → 艏、`stern/aft*` → 艉;情境庫慣例 HEAD/FWDSPRING/AFTSPRING/STERN;無法判斷時依繫船柱在船體座標的前後),舷側依繫船柱所在側,位置與 SimCore `ScenarioBodyPoint` 具名點相同(±0.40 Lpp、∓0.45 B),高度為上甲板(型深 − 吃水)。繫船柱找不到者不畫(HUD 顯示「繫纜 畫出/受力」)。測試:`MooringLinePresentationTests`。
- **拖船姿態**(`TugPresentation`):`direction_deg` 為力方向(相對船艏),核心把 push 拖船放在力方向反側、pull 放在力方向一個纜長處,所以 push/pull 艏向都 = 自船艏向 + 力方向(推頂艏頂自船、纜線自艏;拖拉艏背向自船、纜線自艉);standby/free 與自船同向。測試:`TugPresentationTests`。

## 5. 區域與船模資料

- 區域對照 `data/visual/scenario-areas.json`:`state.scenario.id` 的前綴(最長相符)→ `data/areas/<id>.json` 清單;`--area=` 可覆寫(逗號分隔 id 或路徑)。未列的情境只畫海面與船。
- **泊位後備**(審查發現 utc-mooring:E13/E14/E17 的對照原本對到不含泊位的區域檔,繫纜永遠畫不出來):`state.berth` 不帶泊位檔路徑,所以 `AreaCatalogue.AreasForScenario(scenarioId, berthId)` / `WithBerthArea` 在對照表(或 `--area`)的區域檔都不含 `state.berth.id` 時,掃描 `data/areas/*.json` 的 `berths[].id` 補上泊位所在的區域(`AreaIdsWithBerth`);泊位稍後才出現在狀態中或 id 改變時 `Main.EnsureHarbour` 重建一次港口幾何,仍找不到則 HUD/備註警告「泊位 … 找不到區域檔」。對照表仍應與情境 `berth.file` 一致:`ScenarioBerthAreaTests` 逐一載入 `data/scenarios` 中有 berth 的情境,斷言對照表區域含該泊位與所有 bollard id,並斷言空對照表時後備亦能補上。煙霧模式用合成泊位 `SMOKE-BERTH`,不走後備。
- `AreaLoader` 寬鬆讀取各區域檔變體:`land[].points`([lat, lon] 陣列;`height_m`)、`breakwaters[].polygon`(預設高 5 m;`head` → 堤頭紅燈)、`shoals[]`(水下面,鳥瞰可見)、`berths[]`(`quay` 線、`waterSide`、`fenders[].chainage_m`、`bollards[].chainage_m/setback_m` → 沿碼頭線定位;碼頭往陸側擠出 30 m、高 3 m)、`channel/fairway/holdingArea/berthSpeedZone/anchorages/speedLimitZones/lanes`(鳥瞰視角的示意線)、`leadingLights`(前/後導燈,白色發光球)、`pilotStations/harbourEntrance(s)/route`(標記)。單位全部轉成情境 ENU(m)。
- 船模:`HullParameters.FromParticulars(particulars, loading)` 取 LOA/Lpp/B/型深/吃水/球艏/方艉、`bridge.mastTopAboveBase_m`、`bridge.eyeHeightAboveBase_m`(**兩船目前皆為 null → 估計** 型深 + 3 層 × 2.8 m + 1.7 m;FSB1 ≈ 18.1 m、FSB2 ≈ 21.2 m,取得實測眼高後填入 particulars 即自動採用)。上層建築在艉段(前壁 −0.28 LOA、後壁 −0.47 LOA)、艏樓 0.12 LOA、駕駛台眼位 x = 前壁 − 1 m、翼橋 ±B/2。目標船以 `ForTarget(loa, beam, draft, shipType)` 近似(漁船/引水船駕駛台在前段)。`ProceduralHullBuilder`:16 個剖面(低面數 8)、U 形剖面、艏拋物線收尖、艉漸縮至方艉;輸出船殼/甲板/上層建築/艏樓/桅五個網格(FSB1 約 340 三角形)。**待替換為實船模型(glTF 放 `assets/` 或 `data/visual/`)**。

## 6. 品質分級規則(規劃書第 7.3 節)

| 等級 | 渲染器 | 解析度縮放 | 繪製距離 | 海面 | 陰影/後製 | 月光 / 月光陰影 | MSAA | 雨粒子 | 目標船 LOD / 細節距離 |
|---|---|---|---|---|---|---|---|---|---|
| 低 | Compatibility(`--rendering-method gl_compatibility`) | 0.75 | 3.5 nm | 64×64 平面 + 法線貼圖捲動,無泡沫 | 無 | 有 / 無 | 無 | 無 | 1.0 / 0.5 nm |
| 中 | Forward+ | 1.0 | 6 nm | 128×128 Gerstner(2 組波)+ 泡沫 | 有 | 有 / 有 | 2× | 有 | 2.0 / 1.0 nm |
| 高 | Forward+ | 1.0 | 10 nm | 256×256 Gerstner(3 組波)+ 泡沫 | 有 + 輝光 | 有 / 有 | 4× | 有 | 4.0 / 2.0 nm |

- **自動偵測**(`QualityDetector`,啟動時):GPU 名稱/類型 + 系統記憶體 → 獨顯高階(RTX 20 以上、RX 6600 以上、Arc A5/A7、Quadro)→ 高;其他獨顯 → 中;內顯(Iris Xe/UHD/Arc Graphics、Radeon Graphics/7xxM/8xxM、Apple M)且記憶體 ≥ 16 GB → 中,否則 → 低(記憶體未知時理由寫「未知」);未知/軟體渲染 → 低。啟動後前 3 秒實測幀率 → `QualityDetector.Refine`:**沿用啟動時的硬體資訊只補上實測幀率**,< 30 fps 降一級;不升級(渲染器無法執行中切換)。測試:`WarmupRefinementTests`(內顯基準機 60/120 fps 不得低於啟動分級)。
- **執行中自動降級**(`AdaptiveQualityController`):幀率 < 25 fps 連續累積 5 s → 降一級(低為下限);幀率 ≥ 27 fps 歸零(25–27 為遲滯帶,維持累積);降級後 30 s 冷卻;不自動升級(避免來回跳);每幀 dt > 0.5 s 視為卡頓忽略。每次改變寫 `build/visual/quality.log` 與 HUD;煙霧報告亦列出。
- **手動覆寫**:啟動參數 `--quality=` 或 HUD 的下拉(自動/低/中/高);覆寫期間停用自動降級;持久化到 `build/visual/quality.json`(`manualOverride`、`lastAutoTier` = 無覆寫時目前生效的自動等級(偵測 + 暖機 + 自動降級)、GPU、HUD 開關、FOV);寫入時機:啟動偵測後、每次等級改變、Esc、視窗關閉鈕;無頭/煙霧模式不寫(避免 CI 的 headless=low 影響實機)。下次啟動優先順序:啟動參數 > 持久化覆寫 > 自動偵測。
- 渲染器無法在執行中切換:由啟動腳本以 `--rendering-method gl_compatibility` 決定(`QualityPersistence.StartupRenderer` 規則,`tools/visual-run.ps1/.sh` 實作同一規則):`-Quality low` → Compatibility;`auto`(預設)讀 `quality.json`,`manualOverride` 為 low、或無覆寫且 `lastAutoTier` 為 low → Compatibility,否則 Forward+。第一次啟動沒有檔案 → Forward+,偵測/暖機/自動降級為 low 後立即寫入,**下次 auto 啟動自動改用 Compatibility**(不必手動加 `-Quality low`);本次仍以 Forward+ 執行時 HUD/主控台提示「重啟後生效」。測試:`StartupRendererTests`。

## 7. 啟動參數與多螢幕部署

`godot --path src/Visual.Godot [--rendering-method gl_compatibility] -- <使用者參數>`(`--` 之後為視景參數;`LaunchOptions.Parse`):

| 參數 | 值 | 預設 |
|---|---|---|
| `--view` | `bridge` / `portWing` / `stbdWing` / `aft` / `overview` | bridge |
| `--monitor` | 螢幕索引 0… | 0 |
| `--ws` | 狀態流位址(可省 `ws://`) | ws://127.0.0.1:8765 |
| `--quality` | `auto` / `low` / `mid` / `high` | auto |
| `--data-dir` | 專案根目錄(含 data/);預設自專案路徑往上找 `Directory.Build.props` 或 `SIMOSA_BRM_ROOT` | 自動 |
| `--area` | 區域 id 或檔案,逗號分隔 | 依 scenario-areas.json |
| `--ship` / `--loading` | FSB1 / FSB2;full / ballast | FSB1 / full |
| `--daynight` / `--start-utc` | `auto`(依 `state.utc` 與自船經緯度算太陽高度;狀態無 utc 時依 `--start-utc` + 模擬秒數)/ `day` / `dusk` / `night`;ISO 8601 | auto(皆無時 = 白天) |
| `--fullscreen`、`--no-hud` | 旗標 | 視窗、HUD 開 |
| `--smoke[=N]`、`--smoke-out` | 無頭煙霧模式(第 9 節) | — |

鍵盤:`1–5` 視角、滑鼠左鍵拖曳環視、滾輪視野角、`R` 重設環視、`←/→` 偏轉 10°、`H` HUD 開關、`F11` 全螢幕、`Esc` 離開(寫入 quality.json)。

多螢幕(每顯示器一個程序):
```
tools\visual-run.ps1 -View bridge   -Monitor 0 -Quality auto
tools\visual-run.ps1 -View portWing -Monitor 1 -Quality auto
tools\visual-run.ps1 -View stbdWing -Monitor 2 -Quality auto
```
三個程序各自訂閱 Host 8765(Host 每客戶端一條容量 1 的通道,慢客戶端只拿最新幀,不拖慢引擎),時間平滑各自獨立(同一狀態流,差異 < 1 幀)。教官筆電直連時 `-Ws ws://<船上筆電 IP>:8765`。狀態 `scenario.id` 改變時自動重建港口幾何。

## 8. Godot 使用者資料留在專案內

Godot 預設把編輯器設定、匯出範本、`user://`(紀錄、快取)寫到 `%APPDATA%\Godot`、`%LOCALAPPDATA%\Godot`(Windows)或 `~/.config|.local/share|.cache/godot`(Linux)。本專案的處理:

1. **啟動腳本導向**:`tools/visual-run.ps1`、`visual-headless-check.ps1` 在啟動 Godot 前把 `APPDATA`/`LOCALAPPDATA` 設為 `.cache\godot\appdata\{roaming,local}`(只影響該程序);Linux 腳本設 `XDG_DATA_HOME/XDG_CONFIG_HOME/XDG_CACHE_HOME` 為 `.cache/godot/xdg/*`。本容器實測:匯入與煙霧執行後,`.cache/godot/xdg/` 內有 `config/godot/editor_settings-4.5.tres`、`data/SimosaBRM-Visual/logs`、`data/godot/export_templates`,家目錄無新寫入。
2. **自含模式(建議的 Windows 安裝方式)**:把 `Godot_v4.5.1-stable_mono_win64.exe`(與 `GodotSharp\`)放到 `D:\Simosa BRM\.cache\godot\4.5.1\`,旁邊建立空檔 `_sc_`;Godot 即以該目錄下 `editor_data\` 存放全部編輯器與專案使用者資料(含從編輯器開啟專案時),不需環境變數。
3. `project.godot` 設 `use_custom_user_dir=true`、`custom_user_dir_name="SimosaBRM-Visual"`,使 `user://` 位置固定(在上述導向後即 `.cache/godot/.../SimosaBRM-Visual`)。視景自身的設定不用 `user://`,直接寫 `build/visual/`(quality.json、quality.log、smoke.json),路徑由 `--data-dir` 或自動找到的專案根目錄決定。
4. .NET:視景專案的 NuGet 依根目錄 `nuget.config` 用 `.cache/nuget`;`Godot.NET.Sdk` 的組件輸出在 `src/Visual.Godot/.godot/mono/temp/{bin,obj}`、MSBuild 中繼檔依根目錄 `Directory.Build.props` 在 `build/obj/Visual.Godot/`(皆在專案內;`.gitignore` 已排除 `.godot/` 與 `build/`);`DOTNET_CLI_HOME` 由 `tools/setup-windows.ps1` 指到 `.cache/dotnet-home`。

## 9. 建置、測試與無頭驗證

- **Visual.Shared**:在 `SimosaBRM.sln` 內,`dotnet build SimosaBRM.sln -c Release && dotnet test SimosaBRM.sln -c Release --no-build`(CI 的 .NET 工作自動涵蓋;`tests/Visual.Shared.Tests` 157 個測試:解析、插值/外推(含成批到達)、座標、拖船姿態、繫纜呈現、state.utc 日夜、月球位置/月相與夜間月光、品質偵測與遲滯、暖機再偵測、啟動渲染器、持久化、區域幾何與三角化、船模參數與網格繞向、視角、啟動參數、環境對應、太陽位置、煙霧報告、WebSocket 連線/重連(本機 HttpListener))。
- **Visual.Godot**:不在 sln(無 Godot 的 CI 不建置)。`dotnet build src/Visual.Godot/Visual.Godot.csproj`(Godot.NET.Sdk 4.5.1 自 nuget.org)→ `.godot/mono/temp/bin/Debug/SimosaBRM.Visual.Godot.dll`。Godot 從 `Debug` 載入,故 Godot 執行前用 Debug 建置(或在編輯器按 Build)。
- **無頭煙霧驗證**:`tools/visual-headless-check.sh --godot <Godot> [--frames 200] [--quality low]`(Windows `.ps1`):dotnet build → `--headless --import` → `--headless -- --smoke=N`:載入 E05 示意區域(`kaohsiung-approach`、`kaohsiung-south-approach`)與假狀態序列(`SmokeSequence`:自船 045/8 kn、4 艘目標、拖船、雨區關鍵幀每秒一次、每幀帶 `utc`;**t ≥ 2 s 起帶示意泊位 `SMOKE-BERTH` 與四條繫纜**(正常/黃/紅/斷纜各一;泊位幾何 `SmokeSequence.SmokeBerthArea()` 沿航跡左舷 22 m,Main 於煙霧模式併入港口幾何,船仍直線前進),每幀記錄處理時間、節點數、畫出的目標數、自船位置/SOG、受力與畫出的繫纜數(`mooringLinesInState/Drawn`)到 `build/visual/smoke.json`。煙霧時鐘固定每顯示幀 1/60 s(N 幀 = N/60 s 模擬時間,與主機幀距無關)。`ok`(與幀數無關;`--smoke=30` 亦可)= 有幀、每幀節點 > 0、港口網格 > 0、顯示時間單調不減且有前進、無 `ERROR` 開頭備註;模擬前進 ≥ 0.5 s 時再檢查自船位移 ≈ SOG × Δt(0.5–1.5 倍);最後一幀狀態有受力繫纜時必須有畫出的繫纜(本容器實測:最後幀受力 3 / 畫出 3,節點 119 → 120);失敗原因附在 `notes`(「ok 失敗:…」);結束碼 0/1。測試:`SmokeReportCriteriaTests`。
- **本容器實跑結果(2026-10-22,Godot 4.5.1 .NET headless,無 GPU)**:200 幀、111 個節點、2 個區域 2 個網格 44 三角形、自船 340 三角形、畫出目標 2/4(其餘超出低品質 3.5 nm 繪製距離)、平均 0.15 ms、最大 12.55 ms(第一幀建立幾何),`ok=true`,結束碼 0。headless 幀時間只證明功能與無例外,**不代表內顯機幀率**。
- **匯出**:`export_presets.cfg` 已有 Windows Desktop 與 Linux 預設(輸出到 `build/visual/export/`);本容器無匯出範本(`.cache/godot/xdg/data/godot/export_templates/4.5.1.stable.mono/` 缺 `linux_release.x86_64`),`--export-release` 失敗,**列為待辦**:取得 `Godot_v4.5.1-stable_mono_export_templates.tpz` 後放入該目錄(Windows:`.cache\godot\appdata\roaming\Godot\export_templates\4.5.1.stable.mono\`)再執行 `tools/visual-headless-check.ps1 -Export`。

## 10. 效能守則與內顯機實測步驟(規劃書第 14 章)

守則:
- 繪製距離依等級(3.5 / 6 / 10 nm)設相機 far;目標船超出距離整個隱藏,超過 LOD 距離換低面數(8 剖面、無艏樓/桅);航行燈為發光小球不投光;港口幾何建立一次不每幀改動。
- 無每幀配置:目標/拖船節點以 id 字典重用;插值器用環形緩衝與重用的清單;著色器參數名以 `StringName` 快取;HUD 文字每 0.25 s 以 `StringBuilder` 更新;拖船纜線與錨鏈用一個 `ImmediateMesh` 每幀重畫(頂點數極少)。
- 海面為單一網格跟隨相機吸附格距;法線貼圖由 `NoiseTexture2D` 程序產生(無貼圖資產、無匯入步驟)。
- 低品質以 Compatibility 渲染器 + 0.75 解析度縮放、無陰影/後製/粒子。

內顯基準機實測(第 14 章「視景於內顯基準機以自動選定的低/中品質 1080p ≥ 30 fps」):
1. 船上筆電規格(第 5.4 節)、Windows 11 LTSC、外接 1080p 螢幕;同時執行 SimCore.Host(E05 或 E20)、雷達服務、儀器頁、教官站與螢幕錄影。
2. `tools\visual-run.ps1 -View bridge -Monitor 1 -Quality auto -Fullscreen`;HUD 顯示偵測結果與等級(預期:Iris Xe/Radeon 780M + 16 GB → 中;8 GB → 低)。
3. 觀察 HUD fps 10 分鐘(含目標船最多、港內段):**期望 ≥ 30 fps,且 `build/visual/quality.log` 無自動降級**。若出現降級,改以 `-Quality low`(Compatibility 渲染器)重測,期望 ≥ 30 fps。
4. 強制驗證降級機制:以 `-Quality high` 啟動(高品質在內顯機預期 < 25 fps)→ 5 s 內 HUD 顯示 `mid`、quality.log 有一筆紀錄;再 30 s 冷卻後若仍 < 25 fps 降至 `low`。
5. 獨顯機(RTX 4060 等級):`-Quality auto` 應偵測為 `high`,1440p ≥ 60 fps。
6. 三螢幕:三個程序(bridge/portWing/stbdWing)同時執行,各自 ≥ 30 fps,且 Host 日誌的 50 Hz 無掉拍。
7. 結果記入驗證報告(第 14 章),附 `smoke.json`、`quality.log` 與 HUD 截圖。

## 11. 已知限制與待辦

1. 無實船 3D 模型:FSB1/FSB2 與所有目標船為程序化船體(第 5 節);眼高為估計值(particulars `bridge.eyeHeightAboveBase_m` 待填)。
2. 港口為示意:區域檔為手繪概略輪廓(誤差可達數百公尺),無建物/岸上細節、無燈浮標與航標(區域檔尚無航標資料);碼頭寬度固定 30 m;ENC 取得後需改以海岸線/碼頭線資料產生。
3. 海面為單層 Gerstner(2–3 組波)無次表面散射與船行波/舵螺槳水花;無潮汐水位變化。
4. 天候:雨為相機周圍粒子示意(中/高品質);霧為均勻指數霧;無雲層/閃電。日夜已改依 `state.utc`(第 4 節);月光已依 `MoonModel`(第 4 節),但月光/夜間環境光的能量係數與夜空底色為估計值,**需實機(內顯筆電、暗室)確認亮度**;霧/雨對月光的衰減為示意公式,非輻射傳輸;情境無 `startTimeUtc` 且主控程序未給 `EngineOptions.Clock` 時核心省略 `utc`,視景退回 `--start-utc`(**待辦**:`SimCore.Host/Program.cs` 建立引擎時傳 `new EngineOptions { Clock = TimeProvider.System }`,使無 `startTimeUtc` 的情境亦以實際時鐘代入 — 一行,與既有 NMEA 起始時刻一致)。
5. 錨鏈為直線(非懸鏈線);拖船纜線連到自船側面近似點(契約無連接點座標);繫纜已畫(第 4 節)但**導纜孔為估計**(契約 `berth.lines[]` 無導纜孔座標;若日後 state 帶 `fairlead {x, y}` 或視景讀情境檔即可改用真值),纜線為直線無懸垂、無絞機/纜樁細節。
6. 渲染器無法執行中切換;自動模式第一次啟動用 Forward+,偵測為低時寫入 `quality.json`,下次 `auto` 啟動由腳本自動改用 Compatibility(第 6 節);本次執行期間只套用縮放/距離等設定。
7. 匯出範本未取得,未產生可散布的 .exe(第 9 節);船上部署時由更新包(第 5.5 節)攜帶已匯出的視景程式。
8. 內顯機實測幀率尚未進行(第 10 節步驟);headless 數據只證明功能。
9. 多視窗同步:各程序獨立平滑,未做跨程序幀鎖;若日後改多投影(Class B)需另做同步。
10. Godot 編輯器自含模式需手動放 `_sc_`;`tools/setup-windows.ps1` 尚未自動化 Godot 下載(船上離線,不可連網)。
