# Captainskill Cat（船長平板 App）方案評估

> 日期:2026-11-06。依據:Google Drive 上 Maritime Handy Tool Seagull(以下簡稱 MHT)的 `CURRENT_STATUS.md`(v128,2026-11-01)、`實機測試清單.md`(51 項)、`擴充性檢查_2026-10-22.md`,以及本儲存庫(Simosa-BRM)的現況。
> 本文只談架構與範圍,不含任何金鑰、專案 ID、帳號等識別資訊;可自行搬到 MHT 的 docs 資料夾。

## 0. 結論(2026-11-06 評審後修正版)

> 本節先寫第二輪評審工作流(三位辯護、三位質疑、一位綜合,全部讀過 MHT 狀態快照)的修正結論;原第一版結論保留在 0.2 節供對照。

### 0.1 修正後的建議:直接在 MHT 內擴充,不另做 App,也先不出第二支 APK

**回答「直接在 MHT 內擴充,還是做新 App 再串接 MHT?」:直接在 MHT 內擴充。** 做法是加第三種導覽版面 `NavProfile.captain` 與船長儀表板,船長模組各自一個 feature 資料夾登記進 `kAppModules`;平板寬版元件放共用層。約 1 天就能在業主的平板上看到「船長模式」。

評審找出的關鍵事實(都出自 MHT 狀態快照,第一版評估沒看到或看錯):

1. **MHT 在 2026-06 已經做過「兩 flavor」然後作廢**,最終定案「單一 App、執行期判定身分」。第一版建議的方案 B(product flavor)等於重走一次被放棄的路;另外 gradle flavor 與 `--dart-define` 是兩個真相來源,漏打其中一個就得到「Cat 圖示、Seagull 邏輯」的 APK。
2. **同 schema 不等於兩台裝置之間有資料通路。** `kSyncedTables` 白名單至今只有 `crew_members`、`work_rest_days` 兩張;備份還原是「整庫覆蓋」不是合併;自 v49 起 Drive 上的備份是 manifest + blob 的增量內部格式,不是一個 `.mhtbackup`;而且 `drive.file` 範圍只看得到同一個 OAuth client 建的檔,不同 applicationId 的 App 看不到 MHT 的備份。所以不論 B 或 Y,「手機 MHT 與平板即時同步」都不是自動附送的。
3. **船長的簽核權限在現行角色模型裡不存在。** `GroupRole{companyAdmin, officer, crew, none}`,船上的人只能是 crew,`canApproveReports` 是 officer/companyAdmin 的權限,`firestore.rules` 已部署「approved/locked 後船員不能改」。要做「簽核」就得在 `GroupRole` 加船長角色並更新 `firestore.rules`,這在任何方案都省不掉。
4. **內購綁 applicationId。** `mht_full_unlock` 是一次性商品、本機信任、靠 `restorePurchases` 回填;第二支 APK 的買家在 Cat 裡查無購買紀錄,要嘛重複收費,要嘛在共用的授權邏輯裡加跨產品判斷。這是第二支 APK 最髒的地方。
5. **「新 App 再串接」的致命傷:MHT 的語意不在資料表裡,在純 Dart 規則檔裡。** 船帳結存由 `ship_fund_books.dart` 從第 1 期滾算(v122 之後舊表留著但不再顯示,直接讀表會讀到死資料)、到期總覽在 `due_items.dart`、定期工作在 `recurring_rules.dart`、工休違規在 `work_rest_rules.dart`、油耗稽核在 `noon_fuel_audit.dart`。新 App 讀到原始列算不出船長要看的數字,要嘛重寫一份規則(回到被否決的複製方案),要嘛每次 MHT 出版就對一次欄位;而 10/15 到 10/22 一週內 schema 從 31 跳到 36,還有 `drawing_settings`、`extraJson`、`guides/`、`form_templates/` 這類不動 `user_version` 的改動完全抓不到。MHT 對外只接 `ACTION_SEND application/pdf`,匯入中心不是 intent 接收端,回寫狀態一樣得改 MHT 主幹,「不動 MHT」不成立。成本對等後是 14 到 22 天對 11 到 14 天,外加每次 schema 升級的經常稅。
6. **一人開發的 QA 頻寬是硬上限。** 實機清單 27 到 51 共 25 項業主還沒測,平板項目「不知道怎麼測」;第二支 APK 代表每版雙 APK 驗證,結果只會是 Cat 不測就出或 MHT 節奏被拖慢。

決策規則:

- 只是「船長在平板上用」,買家是同一家公司或同一位船長:**直接在 MHT 內擴充**。
- 確定需要獨立名稱、Play 上架、不同定價:先在 MHT 內做完船長模式,再升級成 product flavor(第 3 節);程式碼全部留用,但要先解決上面第 1、4 點。
- Cat 只做與 MHT 資料無關的駕駛台計算(引航卡讀 Simosa-BRM 的 particulars、UKC、NMEA、夜令):才考慮獨立 App,但那是放棄 MHT 資料,不是「串接」。

不論哪條路,有兩筆工程省不掉:(a) 把船長會改的表(申請單狀態、缺失、交接、夜令)加進 `kSyncedTables`,並在兩台裝置實機驗證同步(v93 的逐列同步從沒實機驗證過);(b) `GroupRole` 加船長角色並更新 `firestore.rules`。

### 0.2 第一版結論(2026-11-06 上午,評審前;保留供對照)

**不要用「複製程式碼到新 repo」的方式做。** Captainskill Cat 與 MHT 的重疊估計在七成以上,而且重疊的正是 MHT 最重的部分(Drift 資料庫 36 版 migration、同步觸發器、模組註冊表、850 項測試、Google 登入與備份、AI 辨識)。複製一份之後,每修一個 bug 都要修兩次,兩邊的資料庫 migration 會立刻分叉,半年內一定有一邊被放棄。

建議的做法是 **同一套程式碼出兩個產品**:在 MHT 現有的「模組註冊表 + 導覽版面(v128 的 NavProfile)」上加一個 **產品維度**,用 Android product flavor 出另一個 applicationId、名稱、圖示;Cat 只載入它要的模組,並補上一組 **平板專用的寬版版面** 與 **船長專屬模組**。MHT 的架構已經替這條路鋪好九成(見第 2 節),剩下的是把幾個「寫死」的地方變成可設定。

成本對比(以過去 MHT 的開發節奏估):

| 路線 | 第一個可用版本 | 長期維護 | 建議 |
|---|---|---|---|
| A. 複製到新 repo | 快(1 到 2 天就能跑) | **最差**:兩份 schema、兩份測試、兩份修 bug | 不建議 |
| B. 同一程式碼、第二個產品(flavor + 註冊表子集) | 3 到 5 個工作天到第一版 | 好:共用全部核心,Cat 專屬的東西只在 Cat 出現 | **建議** |
| C. 不另立 App,在 MHT 加第三種導覽「船長」 | 半天到一天 | 最好,但沒有獨立的名稱、上架、定價 | 若「獨立產品」不是硬需求,先做這個驗證需求 |

B 和 C 不互斥:**先做 C 驗證船長到底要什麼**(一天內就能在業主的平板上跑),確定後再把它升級成 B(獨立 App)。C 的所有程式碼在 B 裡都會留用。

## 1. 兩個產品的事實

### 1.1 MHT Seagull 現況(v128)

- 技術:Flutter/Dart、Riverpod、go_router、Drift(SQLite,schema 36)、Firebase(登入、群組、錯誤回報)、Google Drive 備份、Play 內購、自備金鑰的 AI 影像辨識(Claude/Gemini/ChatGPT)。測試 850 項全過。APK 約 62 MB。
- 架構:`kAppModules` 模組註冊表(一筆 = 路由 + 分頁 + 授權身分 + 擁有的資料表),22 個 feature 資料夾;`FeatureGate`/`AppFeature` 一條授權線(免費/角色/付費);資料庫層同步觸發器;軟刪除;出廠乾淨與 APK 乾淨清單自動查核。
- 版面:`core/design_system/responsive.dart` 已有 mobile/tablet/desktop 三段(600/1200 dp),≥600 dp 用左側 NavigationRail,內容寬度上限 900 dp。v128 加了 **兩套導覽版面**(船上/公司)與「我的船」首頁儀表板。
- 已知技術債(擴充性檢查 2026-10-22):加一個模組要動 9 處(4 處可自動化);22 個 feature 只有 1 個用 arb 多語;船帳表號、題庫、航路網寫死在程式或 assets;**工具箱 `tools_screen.dart` 2,637 行、15 個工具寫死**;公司品牌用 SharedPreferences 覆寫;同步白名單只有工休 2 張表。

### 1.2 Captainskill Cat 的定位(依你的描述推定)

- Android 平板、橫向為主、離線優先(船上沒網路)。
- 使用者是船長,重點是「決策與簽核」而不是「填表」:到期總覽、稽查準備、航次規劃、靠港前準備、交接、簽章、船隊/公司訊息。
- 內容大量與 MHT 重疊,但 **呈現方式** 不同:平板要的是「一眼看全」(左清單右明細、多欄儀表板),不是手機的單欄捲動。

## 2. 功能對照:MHT 已有的能不能直接用

分成四類。「直接共用」= 同一模組登記到 Cat 即可;「寬版改版」= 邏輯共用、要補平板版面;「不要」= Cat 不載入;「Cat 新增」見第 4 節。

| MHT 模組(feature 資料夾) | Cat 怎麼用 | 備註 |
|---|---|---|
| vessels:船舶資料、證書(含 AI 讀日期、檢驗報告)、到期總覽、年度簽證窗口 | **寬版改版** | 船長最常看;平板要左證書清單右原件預覽 |
| vessels:船圖(自動分類、OCR、圖內搜尋) | 直接共用 | 平板看圖本來就比手機好;閱讀器要支援雙頁/旋轉 |
| drills 操練提醒 | 直接共用 | — |
| vessels:應變部署表(muster) | 直接共用 | — |
| vessels:午報(匯入、趨勢、油耗稽核) | **寬版改版** | 趨勢圖與稽核表在平板可以並排 |
| shipfund 船帳三本帳 | 直接共用(船長要核月結) | 「檢查月結」分頁是船長的視角,其他分頁可降為次要 |
| parts 物料申請 | 直接共用(船長核准) | 申請單狀態流程 submitted→approved 正好是船長的動作 |
| workrest 工休表、撥鐘、crew list OCR | 直接共用 | 船長看合規摘要即可;編輯交給大副 |
| sire:SIRE 2.0 稽查、抵港前 PSC 自查 | **寬版改版** | 船長準備稽查的主場;平板適合「題目 + 證據照片 + 指南」三欄 |
| dmp 缺失/虛驚/事故(FM01、AI 分析) | 直接共用 | 船長簽核 |
| ism 文件庫 | 直接共用 | — |
| reports 月報、recurring 定期工作與自動報表 | 直接共用 | — |
| bulletin 布告欄、admin 公司後台 | 布告欄共用;**後台不要** | 船長不是公司管理者;後台留在 MHT 公司模式 |
| pdfsign PDF 簽章(自簽憑證、蓋章) | 直接共用 | 船長簽章的需求最大;平板手寫簽名體驗更好 |
| searegs「我在哪」(船位 × 法規、海圖) | 直接共用 | 見第 5.4 節:平板常無 GNSS,要能吃船上 NMEA |
| voyage「我要去哪」(航路、海域、各國到港要求、書刊) | **寬版改版** | 平板可以地圖 + 清單並排;這是船長功能 |
| logguide 日誌速查 | 直接共用 | — |
| tools 工具箱:羅經盤、風流、天文定位、目視定位、船舶常數/靜水力 | **先拆再共用** | 2,637 行單檔,要先改成工具註冊表才能「只拿部分」(第 6 節) |
| importcenter 匯入中心、settings、公司表單範本 | 直接共用 | — |
| myship「我的船」首頁 | **改成船長儀表板** | 卡片分組換成船長視角(第 4.1 節) |
| cloud 備份/同步、groups 群組、licensing 付費 | 直接共用 | 但 applicationId 不同帶來的連帶事項見第 5.3 節 |

結論:22 個 feature 裡,**只有 admin 不要**;約 6 個要做平板寬版;其餘直接登記就能用。這就是不該複製的理由:要「複製」的其實是整個 App。

## 3. 建議方案 B 的具體做法

目標:**一個 repo、一個 Drift 資料庫、一份測試;兩個 APK**。

### 3.1 產品維度(半天)

1. 新檔 `lib/core/product/product.dart`:`enum ProductId { seagull, captainCat }`,由 `--dart-define=MHT_PRODUCT=captainCat` 決定(預設 seagull,舊的建置指令完全不變)。
2. `AppModule` 加欄位 `products: Set<ProductId>`(預設兩個都有);`kAppModules` 在 router/shell/註冊表測試裡一律經過 `.forProduct(current)` 過濾。admin 模組標只給 seagull。
3. `module_registry_test` 的絆線改成「每個產品各驗一次」:表數相同(資料庫不分產品),分頁數 ≤5,路由不重複。
4. `NavProfile` 加 `captain`,`kNavLayouts[captain]`:例如 **駕駛台|稽查|航次|簽核|更多**(名稱待定);`resolveNavProfile` 在 Cat 產品下固定回 captain(不看公司角色)。
5. 品牌:`CompanyBranding` 之外加 `ProductBranding`(App 名、圖示、主題預設、備份資料夾名、Play 商品 ID 前綴),全部由 flavor 的 `BuildConfig`/dart-define 帶入,**不寫死在 Dart**。

### 3.2 Android product flavor(半天)

- `android/app/build.gradle` 加 `productFlavors { seagull {...}; captainCat { applicationId "…captainskill_cat"; resValue app_name; } }`,各自的 `ic_launcher`。
- `flutter build apk --flavor captainCat --dart-define=MHT_PRODUCT=captainCat`。
- MHT 現有的 `tools/verify_apk.py` 乾淨清單照跑,兩個 APK 都查。
- 平板:`AndroidManifest` 不限制方向,但 Cat 預設 `landscape` 優先(設定可改);`<supports-screens>` 標 large/xlarge;Play 上架要平板截圖與「平板版面」審查(2024 起 Google 對平板品質有要求)。

### 3.3 平板寬版版面(2 到 3 天,是 Cat 真正的工)

- `responsive.dart` 已有 `AppSize` 與 `ResponsiveCenter(900 dp)`。Cat 要的不是「置中留白」而是「用滿」:新增 `MasterDetail` 與 `TwoPane` 兩個共用元件(左 320 到 400 dp 清單、右明細;< 900 dp 退回單欄 push),給證書、SIRE/PSC、缺失、午報、航次規劃用。
- 這些元件 **放在 MHT 共用層**,不是 Cat 專屬。Fold 展開與 Windows 版也受益,MHT 的「第 11 項寬螢幕」問題一起解。
- 觸控目標:平板上保留 48 dp,但表格列高可用 40 dp(工休表月表就是案例)。
- 船長儀表板(`myship` 的 Cat 版):到期(證書/操練/定期工作/缺失逾期)、下一港(ETA、到港要求、書刊、代理信件待辦)、待簽核(申請單、FM01、月結、工休違規)、公司訊息(布告欄)。全部是 MHT 既有資料的 **彙總視圖**,沒有新資料表。

### 3.4 Play、Firebase、備份的連帶事項(半天,大多是操作不是程式)

- 新 applicationId = Firebase 要再登記一個 Android app(同一個 Firebase 專案,`google-services.json` 支援多個 app)、補 SHA-1、OAuth client;Play Console 另一個上架與內購商品 ID。這些是業主操作,照 MHT 當年的上架教學再走一次。
- **備份資料夾一定要分開**(`kBackupParentFolderName` 依產品),否則兩個 App 互相覆蓋備份。但 schema 相同,所以「MHT 手機備份 → 還原到 Cat 平板」是可行的,還原時要檢查備份來源產品並提示。
- 群組/授權:同一個 Firestore 公司與會員資料可共用;Cat 的付費線可以是「公司訂閱已解鎖就全開」,個人買斷另設商品 ID。

## 4. Cat 專屬新模組(MHT 沒有、船長才要的)

以下依你 Drive 裡的實務文件(船長交接清單、代理信件九類、Master standing order、引航安全主機配置、BMP5/ReCAAP)推定,**請勾選**:

### 4.1 第一批(與既有資料最接近,各半天到一天)

1. **靠港前準備(Pre-arrival)**:把「我要去哪」的目的港到港要求 + 代理信件分類(PSC、檢疫、海關、VTS、繫泊圖、卸貨問卷、泊位排程)做成一張可勾的清單,附件掛到船舶文件;完成狀態進到期總覽。
2. **船長交接(Handover)**:交接清單(保管物品、證書、圖紙、帳目、未結缺失、進行中的申請單)由各模組自動帶出,產生交接報告 PDF 並雙方簽章(用 pdfsign)。內容是公司表單,走「公司表單範本」機制,不內建表號。
3. **常規命令與夜令簿(Standing / Night orders)**:船長寫、當值駕駛簽閱,離線;範本匯入(同日誌速查的匯入原則)。
4. **引航卡與操縱特性(Pilot card / Wheelhouse poster)**:從船舶資料產生 IMO A.601 格式的引航卡 PDF。**這裡可直接用 Simosa-BRM 的 `data/ships/*/particulars.json` 與 `trial_targets.json`**(迴旋、停船、側推資料),格式已有 JSON Schema(`src/Contracts/`),Dart 端寫一個讀取器即可。

### 4.2 第二批(計算類,可從 Simosa-BRM 移植公式)

5. **UKC / Squat 計算**:Simosa-BRM 的 Python 參考實作已有 ICORELS squat 與有效水深/潮汐,公式移植到 Dart 並拿 Python 黃金夾具當測試基準。
6. **錨泊計算**:鏈長倍數、迴旋圈、走錨判斷(Simosa-BRM `physics-tugs-anchors-mooring.md` 有模型與係數)。
7. **惡劣天氣/颱風決策輔助**:離港決策清單(對應 E17 情境)、風壓與拖船可用性估算。
8. **引航安全主機配置(Safe engine configuration for pilotage)**:依 MSQ 文件做成檢核表。

### 4.3 與 Simosa-BRM 的另一個接點:用模擬器測 Cat

MHT 的「我在哪」目前靠平板 GPS;船上平板常常只有 Wi-Fi、沒有 GNSS。建議 Cat 加 **NMEA 0183 接收(UDP/TCP,GGA/RMC/HDT/VTG/DPT)**,從駕駛台網路取船位、航向、水深。Simosa-BRM 的 `Gateway.Nmea` 正好輸出同一組句子到 UDP 10110,所以 **不用上船就能在辦公室用模擬器跑完整個「我在哪 / UKC / 引航卡」流程**,也能當 Cat 的端對端測試夾具。

## 5. 做 B 之前必須先還的 MHT 技術債

這些在擴充性檢查裡是 P2/P3,對 Cat 變成 P1,因為「只拿一部分」正是它們擋住的事:

| 項目 | 原優先 | 為什麼 Cat 需要 | 估時 |
|---|---|---|---|
| 工具箱拆成 `kTools` 註冊表、每個工具一個檔 | P3 | Cat 只要船長工具(風流、天文、目視定位、常數、簽章),不要全部 15 個 | 1 天 |
| 模組 4 個機械式觸點自動化(`_syncScopeExpr`、`wipeAllData`、`getDashboardCounts`、`featureLabel`) | P2 | Cat 會新增 4 到 8 個模組,每個省 4 處 | 半天 |
| 新畫面一律 arb 多語 | P1 | Cat 的新畫面從第一天就走 arb,不再欠債;舊畫面不動 | 隨做 |
| 知識包雲端更新(到港要求、題庫、航路網) | P1 | 船長功能最依賴法規資料的新鮮度 | 1 天 |
| 備份資料夾改記 id 並依產品命名 | P2 | 第 3.4 節 | 半小時 |

## 6. 風險與要你拍板的事

1. **Cat 是獨立產品還是 MHT 的一種模式?** 決定 B 或 C。判準:要不要獨立名稱/上架/定價、買家是不是同一批人。若只是「船長在平板上用」,C 就夠。
2. **平板規格**:船上要配什麼平板?有無 GNSS、有無 Google Play 服務(文件掃描器與 ML Kit 依賴它,有退路但體驗差)、記憶體(船圖 OCR 與 7 GB 船圖)。
3. **離線範圍**:Cat 哪些功能允許連線(AI 辨識、知識包更新、群組同步)?建議與 MHT 一致:核心全離線,連線只做加值。
4. **資料歸屬**:同一位船長用 MHT 手機與 Cat 平板時,資料以誰為準?若要雙向,MHT 的同步白名單(目前只有工休 2 張表)要擴到船長會改的表(申請單狀態、缺失、交接、夜令)。這是 MHT 的 P3 項目,會被 Cat 提前。
5. **公司專屬內容**:交接表、常規命令、代理信件格式都是公司文件,一律走「公司資料包/表單範本匯入」,APK 不內建(維持 MHT 出廠乾淨原則)。
6. **名稱**:「Captainskill Cat」若要上架,先查 Play 與商標是否有衝突;套件名一旦發布不能改。

## 7. 建議的工作分期

| 期 | 內容 | 估時 | 產出 |
|---|---|---|---|
| 0 | 拍板第 6 節六件事;確認第 2 節對照表與第 4 節勾選 | 業主半天 | 範圍定案 |
| 1 | 路線 C:`NavProfile.captain` + 船長儀表板(彙總視圖)+ 在業主的平板實測 | 1 天 | MHT 一個版本,平板上能看到「船長模式」 |
| 2 | 技術債:工具註冊表、4 個觸點自動化、備份資料夾 | 1.5 天 | MHT 一個版本,測試數不減 |
| 3 | 產品維度 + flavor + 品牌 + Play/Firebase 登記 | 1 天(含業主操作) | 第一個 Cat APK(內容 = 船長模式) |
| 4 | 平板寬版:MasterDetail/TwoPane,套到證書、SIRE/PSC、缺失、午報、航次 | 2 到 3 天 | Cat 第二版 |
| 5 | 船長新模組第一批(靠港前、交接、夜令、引航卡) | 3 到 4 天 | Cat 第三版 |
| 6 | NMEA 接收 + 用 Simosa-BRM 模擬器做端對端;計算類第二批 | 2 到 3 天 | Cat 第四版 |

總計約 11 到 14 個工作天到「功能完整的第一個正式版」;其中第 1 期結束就能讓船長在平板上用,第 3 期結束就有獨立 App。

## 8. 如果最後仍決定要「複製」

請用 **git subtree 或 melos monorepo 的 package**,不要用檔案複製:

- 可拆成共用 package 的(純 Dart、無 Flutter UI 或 UI 很薄):`core/db`(Drift 表與 DAO)、`core/modules`、`core/forms`(XlsxTemplate、FormKit PDF)、`core/licensing`、`core/cloud`、`features/searegs/data`、`features/voyage/data`、`features/tools/data`(WMM、岸標、靜水力)、`features/logguide/data`、`features/importcenter/data`、全部 `assets/data/*.json` 與 `tool/*.py` 資料產生腳本。
- 不能乾淨拆的:Drift 資料庫本體(一個 `@DriftDatabase` 必須字面列出全部表)、同步觸發器、`tools_screen.dart`(拆前)。這些一拆就是兩份 migration 線,這正是不建議複製的核心原因。

---
*本文由 Claude 依 2026-11-06 可讀到的文件整理;功能清單與行數引用自 MHT 的狀態快照與擴充性檢查,未實際開啟 MHT 原始碼(git bundle 在 Drive 上,未在本工作階段取出)。*
