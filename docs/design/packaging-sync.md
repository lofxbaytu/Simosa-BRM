# 簽章更新包、紀錄包與兩筆電直連同步 — 設計文件

> 對應規劃書第 5.5 節(船上部署、更新與資料回傳)、第 9.1 節(程序參數管理)、第 14 章「船上部署」「SMS 更新流程」驗收列;需求 R13、R14;決策 D6、D19、D20。
> 程式:`src/Tools.Packaging/`(類別庫)、`src/Tools.Packaging.Cli/`(`simosa-pkg`)、`tests/Tools.Packaging.Tests/`;Windows 包裝腳本 `tools/simosa-pkg.ps1`。

## 1. 範圍與角色

| 角色 | 機器 | 做什麼 |
|---|---|---|
| 訓練負責人(訪船船長) | 教官筆電 | 編修參數集/情境 → 影響分析 → 重新驗證並記錄 → 以**主金鑰**(硬體金鑰)簽章產生更新包 → 訪船直連同步 → 取回紀錄包 → 岸上備份 → 下次訪船送「已備份回報」 |
| DPA/海務主管 | — | 保管**備援金鑰**;主金鑰遺失或交接時簽章「信任更新包」更換船上信任公鑰 |
| 船上筆電 | 船上 | `simosa-pkg serve` 等待直連;驗證並套用更新包;打包紀錄並以**裝置金鑰**簽章;收到回報才刪除副本 |

不設岸上伺服器(D6);兩機以一條網路線直連,固定私有 IP 寫在 `config/packaging.json`;專用隨身碟為備援。

## 2. 金鑰與信任

- 演算法固定 **ECDSA P-256 + SHA-256(ES256)**,簽章為 IEEE P1363(r‖s,64 位元組)。.NET 內建,智慧卡/PIV 安全金鑰普遍支援。
- **金鑰 ID** = SubjectPublicKeyInfo(DER)的 SHA-256(小寫 hex 64 字元;畫面顯示前 16 字元)。
- `IKeyProvider`:
  - `CertificateStoreKeyProvider`(僅 Windows):以憑證指紋自 `CurrentUser\My`(或 `--cert-store machine`)取得私鑰;智慧卡/PIV 經 Windows 迷你驅動程式登錄後即在此存放區,**PIN 由作業系統提示,私鑰不離開硬體**。CLI:`--cert <指紋>`。
  - `PemKeyProvider`:開發/測試用 PEM 檔(可 PBES2/AES-256 加密,`--key-password` 或 `SIMOSA_KEY_PASSWORD`)。CLI 每次使用都顯示警告。
  - `EcdsaKeyProvider`:裝置金鑰(DPAPI 解開後)。
- **PEM 中繼資料**:`-----BEGIN` 之前的 `# 名稱: 值` 註解行,例如:
  ```
  # Simosa BRM 信任公鑰
  # role: backup
  # name: DPA 備援金鑰
  # holder: DPA 林○○
  # keyId: 3f1c…(64 hex)
  -----BEGIN PUBLIC KEY-----
  …
  ```
  `role` 為 `primary`(主金鑰)、`backup`(備援金鑰)或 `device`(裝置金鑰,只用於教官端紀錄庫)。`keyId` 若標示則必須與公鑰相符。
- **船上信任清單**:`<root>/config/trusted-keys/*.pem`(主金鑰 + 備援金鑰,寫入系統映像、備用 SSD 與備援筆電)。任一信任金鑰簽章的更新包都可套用,套用前顯示簽章者(信任清單中的名稱、角色、保管人,而非簽章檔自稱的名稱)。
- **金鑰輪替**:`kind = trust` 的更新包**只接受備援金鑰簽章**;內容為新增的公鑰檔(`config/trusted-keys/*.pem`)與要移除的金鑰 ID;套用後信任清單必須仍有備援金鑰,否則拒絕。套用記入稽核 `trust.rotated`。其他種類的更新包不得觸及信任清單(`files` 與 `remove` 都以正規化後的路徑、不分大小寫比對,目錄本身也算)。訪船船長以管理者帳號手動更換用 `simosa-pkg trust add|remove --admin <名>`(稽核 `trust.manualChange`)。
  - 「仍有備援金鑰」依**實際套用後的檔案集合**判定:現有檔案 − `trust.remove` 金鑰的檔案 + `trust.add`。`trust.add` 的路徑與現有金鑰檔同名、而該金鑰未列在 `trust.remove` 時拒絕(覆寫等於未宣告的移除);同名且已列入 `trust.remove` 時視為原地更換(例如沿用 `backup.pem` 換備援金鑰),新公鑰不會被當成移除項刪掉。trust 包不可使用 `manifest.remove`(只能以 `trust.remove` 的金鑰 ID 移除)。組好新信任清單目錄後再重新載入核對一次(有備援金鑰、新增的金鑰都在、移除的都不在),不符即不替換。
  - trust 包**不可 rollback**(否則被撤銷的金鑰會重新受信任);撤回信任變更須由備援金鑰簽章新的 trust 包。
- **裝置金鑰**:安裝時 `simosa-pkg device init --ship FSB1` 產生。Windows:私鑰以 **DPAPI(LocalMachine 範圍 + 應用程式熵)** 加密存 `config/device/device-key.dpapi`;其他平台(Linux 測試)存 PEM(權限 600)並警告。公鑰 `device-key.pub.pem` 於安裝時以 `store register-device` 登錄到教官筆電;直連或匯入時遇到未登錄的裝置預設拒絕,`--enroll-device` 可首次登錄(TOFU,記入稽核,須與船上 `device show` 的金鑰 ID 核對)。

## 3. 檔案格式

### 3.1 更新包 `.sbupd`(zip)

```
manifest.json      簽章對象(原始位元組)
manifest.sig       SignatureEnvelope
payload/<安裝路徑>  例如 payload/data/parameters/company-procedures.yaml
```
安裝路徑只允許 `app/`、`data/`、`config/` 三個頂層元件,不得為絕對路徑或含 `..`。zip 內容必須與 `files` 一一對應(不得多、不得少),每檔核對 SHA-256 與大小;zip 內任何項目名稱重複(不分大小寫,包括第二個 `manifest.json`/`manifest.sig`)即拒絕。
- `packageId`:英數字開頭,只含英數字與 `. _ -`,最多 128 字元(用於 `state/staging/<packageId>/` 目錄名;`..` 之類一律拒絕,`pack --package-id` 同規則)。
- `remove`:套用時刪除的安裝路徑。必須已正規化(與原字串相同),並受與 `files` 相同的種類前綴限制(例如 software 只能移除 `app/` 下);`config/device/`(裝置金鑰)與 `config/packaging.json` 任何更新包都不得寫入或移除;trust 包不可使用 `remove`。

`manifest.json` 範例(參數集更新):
```json
{
  "format": "simosa-update/1",
  "packageId": "upd-20261017T030000-1a2b3c4d",
  "version": "2026.10.2",
  "createdUtc": "2026-10-17T03:00:00Z",
  "kind": "parameters",
  "summary": "DWO-007 第 4 版:沿岸 CPA 0.5 → 0.6 nm",
  "changes": ["navigation.cpa.coastal_nm 0.5 → 0.6"],
  "smsDocuments": [{ "documentId": "DWO-007", "revision": "4", "approvedBy": "DPA 林○○", "approvedOn": "2026-10-15" }],
  "ships": [],
  "files": [{ "path": "data/parameters/company-procedures.yaml", "sha256": "…", "size": 6123 }],
  "remove": [],
  "parameterSet": { "id": "simosa-company-procedures", "version": "2026.10.2", "sha256": "…", "path": "data/parameters/company-procedures.yaml",
                    "sourceRevisions": { "DWO-007": "4", "SMS-NAV-012": "2" } },
  "affectedScenarios": [{
    "scenarioId": "E01_baseline", "path": "data/scenarios/E01_baseline.yaml", "sha256": "…",
    "reasons": ["引用變更的參數"], "changedKeys": ["navigation.cpa.coastal_nm"],
    "assessmentRules": ["assessment.minCpa_nm", "assessment.bands[0](minCpa)"], "errors": 0, "warnings": 0,
    "verification": { "scenarioId": "E01_baseline", "scenarioSha256": "…", "parameterSetVersion": "2026.10.2", "parameterSetSha256": "…",
                      "method": "run", "recordRef": "build/records/20261016-…-E01_baseline.jsonl", "recordSha256": "…",
                      "result": "pass", "verifiedBy": "訪船船長 王○○", "verifiedUtc": "2026-10-16T08:00:00Z" } }],
  "validation": { "status": "verified", "checkedUtc": "2026-10-17T03:00:00Z", "baselineParameterSetVersion": "2026.10.1",
                  "changedParameterKeys": ["navigation.cpa.coastal_nm"], "verificationRecord": "verification.json", "verificationRecordSha256": "…", "problems": [] },
  "signer": { "keyId": "…", "name": "訪船船長 王○○" },
  "createdBy": "INSTR-LT simosa-pkg 1.0.0"
}
```
- `kind`:`software`(app/)、`scenarios`(data/scenarios/)、`parameters`(data/parameters/)、`charts`(data/charts/)、`trust`(config/trusted-keys/;附 `trust: { add: [{keyId,name,role,holder,path}], remove: [keyId], reason }`)、`mixed`(app/ 與 data/,不含信任清單)。
- `version`:1–4 段數字,逐段比較;船上拒絕「不大於曾套用最高版本」者(防降版與重播;rollback 後最高版本不下降)。
- `validation.status`:`verified`(受影響情境全部無錯誤且有相符驗證紀錄)、`notRequired`(未含參數集或情境)、`forced`(以 `--force` 產生,附 `forcedReason`、`problems`;**船上預設拒絕**,僅船上管理者本機 `apply --allow-unverified` 可套用並記入稽核;直連同步一律拒絕)。
- 參數、情境、信任與混合更新包必須標示 `smsDocuments`(編號、版次、核准人)。

### 3.2 簽章 `manifest.sig`(SignatureEnvelope)

```json
{ "format": "simosa-sig/1", "alg": "ES256", "keyId": "…64 hex…", "signer": "訪船船長 王○○",
  "signedUtc": "2026-10-17T03:00:00Z", "publicKey": "-----BEGIN PUBLIC KEY-----…", "signature": "base64(r‖s)" }
```
驗證只以 `keyId` 在信任清單中找公鑰;`signer`、`publicKey` 僅供顯示與裝置首次登錄。

### 3.3 紀錄包 `.sbrec`(zip,裝置金鑰簽章)

```
manifest.json  manifest.sig  records/<相對 build/records 的路徑>
```
收集船上 `build/records/` 第一層 `*.jsonl`(練習紀錄)與 `build/records/reports/**`(報告);未寫 footer 且 2 分鐘內仍在修改的紀錄視為練習進行中,不打包。已打包(相同路徑 + 相同雜湊)者不重複打包。

```json
{
  "format": "simosa-records/1", "packageId": "rec-FSB1-20261017T040000-9f8e7d6c", "shipId": "FSB1", "deviceId": "FSB1-LT01",
  "createdUtc": "2026-10-17T04:00:00Z", "visitSeq": 3,
  "files": [{ "path": "records/20261012-013000-E01_baseline.jsonl", "sha256": "…", "size": 1234567, "kind": "record" },
            { "path": "records/reports/E01-debrief.html", "sha256": "…", "size": 2345, "kind": "report" }],
  "exercises": [{ "file": "records/20261012-013000-E01_baseline.jsonl", "scenarioId": "E01_baseline", "scenarioName": "…",
                  "shipId": "FSB1", "loading": "ballast", "startUtc": "2026-10-12T01:30:00Z", "endUtc": "2026-10-12T02:30:00Z",
                  "durationS": 3600, "simDurationS": 3600, "endEstimated": true,
                  "participants": [{ "name": "王大副", "role": "OOW" }], "integrityOk": true, "hasSummary": true,
                  "parameterSetVersion": "2026.10.1" }],
  "trainingHours": { "exerciseCount": 1, "totalS": 3600, "totalHours": 1.0, "simTotalS": 3600, "estimated": true,
                     "participants": [{ "name": "王大副", "role": "OOW", "exercises": 1, "totalS": 3600, "totalHours": 1.0,
                                        "sessions": [{ "scenarioId": "E01_baseline", "startUtc": "…", "endUtc": "…", "durationS": 3600,
                                                       "simDurationS": 3600, "endEstimated": true }] }] },
  "installedVersion": "2026.10.2", "parameterSetVersion": "2026.10.2",
  "device": { "keyId": "…", "publicKey": "-----BEGIN PUBLIC KEY-----…" }
}
```
練習起訖與時長一律用**牆鐘**(供轉錄至船上工時/休息時間紀錄;SimCore 可倍速 0.1–10 或凍結,模擬時間與實際經過時間不同):開始 = `header.createdUtc`;結束 = `footer.closedUtc`(SimCore 寫入時;**目前尚未寫入,建議 SimCore 後續在 footer 加上**)→ 否則紀錄檔最後修改時間(SimCore 寫完 footer 即關檔)→ 否則開始 + 模擬時長;後兩者標 `endEstimated: true`。`durationS` = 結束 − 開始;模擬時長(`footer.finalTick × header.dt`,無 footer 時取最後一行 state 的 t)另列為 `simDurationS`。參與者:紀錄標頭 `participants`(SimCore 一律寫 `{name, role, rank?, certificateNo?}` 物件陣列,`RecordReader` 只接受此格式;字串陣列只供舊/外部紀錄由 `RecordInspector` 讀取),有 `restHoursConfirmation` 時原樣帶出;紀錄開啟後才送達的 `sessionInfo` 寫成最後一行 `session`(見下文)。完整性同 `RecordReader`(footer.sha256 對 footer 前所有行)。

### 3.4 已備份回報 `.sbbak`(SignedDocument,教官金鑰簽章)

```json
{ "format": "simosa-signed/1",
  "payload": "{\"format\":\"simosa-backup-report/1\",\"reportId\":\"bak-FSB1-…\",\"shipId\":\"FSB1\",\"issuedUtc\":\"…\",\"packages\":[{\"packageId\":\"rec-FSB1-…\",\"sha256\":\"…(.sbrec 檔雜湊)\",\"backedUpUtc\":\"…\",\"backupTarget\":\"加密外接碟 #1\"}]}",
  "signature": { "format": "simosa-sig/1", "alg": "ES256", "keyId": "…", "signature": "…" } }
```
`payload` 為被簽 JSON 原文字串。船上以信任清單(主/備援)驗證、核對 `shipId`,只刪除帳冊中雜湊相符的紀錄包與內容未再變更的原始紀錄檔。刪除逐檔進行:先刪原始紀錄檔,全部成功才刪 `state/outbox` 的紀錄包副本並在帳冊標記已刪除;有檔案刪不掉(Windows 上被講評站或防毒開著)時記入 `cleanupResult.failed` 與警告,該紀錄包保留(仍列為待清理),下次回報時重試;帳冊一定寫回,session 不中斷。

### 3.4.1 清理通知 `.sbcln`(SignedDocument,船上裝置金鑰簽章;隨身碟途徑)

```json
{ "format": "simosa-cleanup/1", "shipId": "FSB1", "deviceId": "FSB1-LT01", "reportId": "bak-FSB1-…", "deleted": ["rec-FSB1-…"], "createdUtc": "…" }
```
船上處理隨身碟上的已備份回報後寫到 `from-ship/<ship>/<reportId>.sbcln`;教官端 `import --role instructor` 以紀錄庫登錄的裝置公鑰驗證後標記「船上已刪除」(等同直連的 `cleanupResult`),驗證通過即自隨身碟刪除;未登錄裝置或簽章不符者拒絕並保留檔案。

### 3.5 船上狀態與帳冊

| 檔案 | 內容 |
|---|---|
| `state/installed.json` | 現行版本、`highestVersion`、各元件版次 `components{software,scenarios,parameters,charts,trust}`、`parameterSet`、`smsDocuments`、`history`(最近 50 筆,含 `rolledBack`) |
| `state/previous/` | 上一版元件目錄 + `previous.json`(`replacedByPackageId/Version/Kind`、元件)+ 舊 `installed.json`(rollback 用,只保留一層) |
| `state/previous.stale-*/` | 提交時移開、但刪不掉的更早一版(檔案鎖定;下次套用時再清理) |
| `state/previous.unsaved-*/` | 提交時無法放入 `state/previous` 的上一版元件(不自動刪除,供手動回復) |
| `state/records-ledger.json` | `visits[]`(訪船序號、首次/最後連線時間、連線數、tcp/usb)、`packages[]`(打包/取回/備份/刪除時間) |
| `state/outbox/*.sbrec` | 待回傳紀錄包 |
| `state/audit.jsonl` | 稽核紀錄(第 3.7 節) |

### 3.6 教官筆電紀錄庫(`<root>/store/`,可設 `--store`)

```
records/<ship>/<yyyy>/<packageId>.sbrec   原始簽章紀錄包(證據保存)
records/<ship>/<yyyy>/<packageId>/…       解開的紀錄(manifest.json、manifest.sig、*.jsonl、reports/)
devices/<ship>/<deviceId>.pem             已登錄的裝置公鑰
store-ledger.json                         匯入/備份/船上已刪除
fleet-versions.json                       兩船版次清單(每次同步/隨身碟匯入更新)
audit.jsonl
```

### 3.7 稽核紀錄(JSONL)

每行 `{"ts":"…Z","event":"…","host":"…","details":{…}}`。事件:`update.applied`(含簽章者 keyId/name/role、版本、種類、SMS 文件、參數集、受影響情境、經由、操作者)、`update.rejected`、`update.failed`、`update.rolledBack`、`trust.rotated`、`trust.manualChange`、`device.initialized`、`records.packed|pulled|deleted`、`backupReport.accepted|rejected`、`sync.session.start|end`、`sync.auth.failed`(reason `untrusted-key`/`bad-signature`/`protocol`;對方提供的 `keyId` 只在為 64 位 hex 時原樣記錄,否則記 `(invalid)`,`claimedSigner` 截到 128 字元)、`sync.auth.throttled`(未驗證失敗超過速率上限:`phase` start/end,end 含略過次數)、`sync.session.error`;教官端 `store.imported|importRejected|deviceEnrolled|backedUp|reportIssued`、`package.built|forced`。

## 4. 直連同步協定(TCP,預設埠 7300)

**框架**:4 位元組大端序長度 + UTF-8 JSON(上限 1 MiB;**驗證前的 hello/auth 上限 4 KiB**),每則含 `type`。檔案:先送檔頭訊息 `{type, size, sha256, …}`,緊接著恰好 `size` 位元組原始資料(不加框)。單則訊息閒置逾時 2 分鐘(握手亦同,教官端硬體金鑰可能要求輸入 PIN);檔案上限 8 GiB。船端一次只服務一個連線。

**驗證前的防護**:任何能連到 7300 埠的人都不需要金鑰就能握手,因此驗證前寫入稽核的對方欄位一律截斷或檢查格式;未驗證的失敗(未信任金鑰、簽章錯誤、訊息過大或格式錯誤)在 1 分鐘內逐筆記錄最多 10 次,之後只計數(`sync.auth.throttled`)並在每次失敗後延遲 2 秒才斷線,稽核紀錄與磁碟不會被灌爆。

**握手(雙向驗證)**:
```
教官 → 船  {"type":"hello","protocol":1,"role":"instructor","clientNonce":"<base64 32B>","client":"INSTR-LT"}
船 → 教官  {"type":"challenge","protocol":1,"shipId":"FSB1","deviceId":"FSB1-LT01","nonce":"<base64 32B>",
            "deviceKeyId":"…","devicePublicKey":"-----BEGIN PUBLIC KEY-----…","deviceSignature":"<base64>"}
教官 → 船  {"type":"auth","keyId":"…","signer":"訪船船長 王○○","signature":"<base64>"}
船 → 教官  {"type":"welcome","shipId":"FSB1","deviceId":"FSB1-LT01","signer":"訪船船長 王○○(主金鑰,金鑰 3f1c…,保管人 …)","signerRole":"primary","visitSeq":4}
```
- 裝置簽章內容:`"simosa-sync/1\ndevice\n{shipId}\n{deviceId}\n{clientNonce}\n{nonce}"`(UTF-8),教官端以紀錄庫登錄的裝置公鑰驗證(證明是登錄的船上筆電)。
- 教官簽章內容:`"simosa-sync/1\ninstructor\n{shipId}\n{deviceId}\n{nonce}\n{clientNonce}"`,船端以信任清單(主/備援)驗證(證明是訓練負責人)。錯誤 nonce 或未信任金鑰 → `{"type":"error","code":"auth-failed"|"untrusted-key","message":"…"}` 並斷線、記入稽核。
- nonce 以作業系統 CSPRNG 產生;此工具不屬模擬核心,不受「亂數只用情境種子」規則約束,也不影響狀態雜湊。

**指令(驗證後)**:

| 請求 | 回應 |
|---|---|
| `{"type":"status"}` | `statusResult`(第 7 節 ShipStatus) |
| `{"type":"pushUpdate","name":"x.sbupd","packageId":"…","size":N,"sha256":"…"}` + N 位元組 | `{"type":"updateResult","result":{UpdateApplyResult}}` |
| `{"type":"listRecords"}`(船端先打包新紀錄) | `{"type":"recordList","packages":[{packageId,sha256,size,createdUtc,visitSeq,files,exercises,trainingS,pulledUtc}],"warnings":[打包問題],"retentionWarnings":[保留上限警告]}` |
| `{"type":"getRecord","packageId":"…"}` | `{"type":"file","name":"….sbrec","packageId":"…","size":N,"sha256":"…"}` + N 位元組,或 `error`(`not-found`) |
| `{"type":"backupReport","name":"….sbbak","size":N,"sha256":"…"}` + N 位元組 | `{"type":"cleanupResult","result":{CleanupResult}}` |
| `{"type":"bye"}` | `{"type":"bye"}` 後關閉 |

錯誤代碼:`protocol`、`auth-failed`、`untrusted-key`、`not-found`、`transfer`、`unknown-type`、`internal`。

**教官端流程**(`simosa-pkg sync`):握手 → 依版本由小到大推送更新包(每包回報結果,失敗不中斷)→ `listRecords` → 拉取紀錄庫沒有的紀錄包並驗證匯入 → 若有「已備份且船上仍保留」者,送已備份回報 → 船端清理 → `status` 更新兩船版次清單 → `bye`。保留上限警告:有送回報且船端處理成功時用 `cleanupResult.warnings`(清理後的狀態),否則用 `recordList.retentionWarnings`。

**訪船與連線**:每個通過驗證的連線(與隨身碟匯入)都會登記到帳冊,但距上一個連線不到 `visitMergeHours`(`packaging.json`,預設 12 小時)者視為**同一次訪船**(重連、補推更新包、`--no-pull` 只推送),不另計訪船間隔;保留上限以訪船次數計算。

## 5. 套用與回復(船上)

1. **verify**:簽章(信任清單)→ manifest 格式 → 檔案清單 ↔ zip → 每檔雜湊與大小 → 版本 > 曾套用最高版本、未套用過 → 船舶限定 → 驗證狀態 → trust 類須備援金鑰。簽章不符即停止,不解讀 manifest。
2. **解壓到暫存** `state/staging/<packageId>/` 並重算雜湊(以解出的檔案為準)。
3. **套用前檢查**:參數集可載入(`ParameterSetLoader`)、情境以新(或現行)參數集可載入且 `ScenarioValidator` 無錯誤;trust 類計算變更後信任清單。
4. **原子替換**:對受影響的每個頂層元件,先建 `state/next/<元件>`(複製現行 → **先刪除** `remove` 與 trust 類被移除金鑰的檔案 → **再寫入**新檔,新檔與被移除的檔案同名時不會被刪掉;trust 類再以組好的信任清單目錄重新載入核對),再以目錄改名把現行移到 `state/previous.pending/`、新目錄移到 `<root>/<元件>`;寫 `installed.json`。任何一步失敗(含寫狀態檔)即逐一移回,**完整回復原狀**,稽核 `update.failed`。
5. **提交**:`installed.json` 寫入後新版即已上線,此後的失敗**不得回報「未變更」**。舊的 `state/previous` 先改名移開(`previous.stale-*`),`previous.pending` 改名為 `state/previous`,再盡力刪除舊的;刪不掉(Windows 檔案鎖定)只記警告,下次套用時再清理。若連 `state/previous` 都無法放入,回報成功加警告,上一版元件另存 `state/previous.unsaved-*`(不刪除),殘留的舊 `state/previous` 失效(不能 rollback 到錯誤版本)。稽核 `update.applied`(+ `trust.rotated`);稽核寫入失敗也只記警告。
6. **rollback**(`simosa-pkg rollback`):把 `state/previous` 的元件移回、恢復舊 `installed.json`(`highestVersion` 不下降、該包歷史標 `rolledBack`)。`state/previous` 必須正是現行更新包取代掉的上一版(`previous.json.replacedByPackageId` = 現行 `packageId`),且上一個套用的不是 trust 包,否則拒絕;`status` 的 `rollbackAvailable` 依同樣條件。

同一時間只允許一個套用/打包/清理程序(`state/.lock`)。軟體元件(app/)替換時,執行中的程式不可位於 app/ 內:正式安裝時 `simosa-pkg` 與 serve 服務放在 app/ 之外(安裝程式另置 `tools/updater/`),模擬器在套用期間須關閉。

## 6. 操作步驟

結束碼:0 成功、1 失敗或拒絕(含參數集/情境 YAML 語法或型別錯誤)、2 用法錯誤。布林旗標(`--allow-unverified`、`--enroll-device`、`--json`、`--force`、`--overwrite`、`--all`、`--pem`、`--no-pull`、`--no-report`)不取下一個參數為值,可寫在位置參數之前;要關閉時寫 `--flag=false`。

### 6.1 安裝時(系統映像)
```
simosa-pkg keygen …          ← 僅開發/測試;正式主/備援金鑰在硬體金鑰內產生,只匯出公鑰
simosa-pkg trust add --root "D:\Simosa BRM" --file primary.pub.pem --admin 安裝人員
simosa-pkg trust add --root "D:\Simosa BRM" --file backup.pub.pem  --admin 安裝人員
simosa-pkg device init --ship FSB1 --device-id FSB1-LT01
simosa-pkg device show        → 把公鑰帶回教官筆電:
simosa-pkg store register-device --ship FSB1 --device FSB1-LT01 --public FSB1-LT01.pub.pem
```
`config/packaging.json`:船上 `{"role":"ship","shipId":"FSB1","deviceId":"FSB1-LT01","listen":"192.168.77.11","port":7300,"maxVisitIntervals":2,"visitMergeHours":12}`;教官 `{"role":"instructor","ships":{"FSB1":"192.168.77.11","FSB2":"192.168.77.12"},"store":"store"}`。

### 6.2 SMS 程序修訂 → 更新包(教官筆電,第 9.1 節)
```
simosa-pkg impact --parameters new.yaml --baseline deployed-2026.10.1.yaml          # 列出受影響情境與評估規則、自動檢查
(以新參數集執行受影響情境,或以既有紀錄重播評分)
simosa-pkg verification add --file verification.json --scenario data/scenarios/E01_baseline.yaml \
    --parameters new.yaml --method run --record build/records/…-E01_baseline.jsonl --by "訪船船長 王○○"
simosa-pkg pack --kind parameters --version 2026.10.2 --summary "DWO-007 第 4 版" \
    --sms DWO-007:4:"DPA 林○○":2026-10-15 --file data/parameters/company-procedures.yaml=new.yaml \
    --baseline deployed-2026.10.1.yaml --verification verification.json --cert <硬體金鑰憑證指紋> --out build/packages/params-2026.10.2.sbupd
```
未驗證或有錯誤時 `pack` 拒絕並列出問題;`--force --force-reason "…"` 可強制產生(manifest 標示 forced、記入稽核),船上預設拒絕套用。

### 6.3 訪船
船上:`simosa-pkg serve`(或開機服務)。教官:`simosa-pkg sync --ship FSB1 --cert <指紋> --push build/packages/*.sbupd`。
回岸後:`simosa-pkg store backup --to E:\`(加密外接碟,複製後逐檔核對雜湊並標記已備份)或 `store mark-backed-up --all --target "公司檔案共享"`;下次訪船同步時自動送出已備份回報,船上才刪除對應副本。

### 6.4 隨身碟備援
教官:`simosa-pkg export --to F:\ --role instructor --push x.sbupd --cert <指紋>`(更新包 + 各船已備份回報)。
船上:`simosa-pkg import --from F:\ --role ship`(依版本套用、處理回報並寫回清理通知 `.sbcln`,並把待回傳紀錄包與 status.json 寫回隨身碟)。
教官:`simosa-pkg import --from F:\ --role instructor`(匯入紀錄包、處理清理通知以標記船上已刪除)。隨身碟上只有簽章檔(status.json 除外,僅供顯示),兩端都完整驗證。

### 6.5 金鑰遺失/交接
DPA 以備援金鑰:`simosa-pkg pack --kind trust --version <新版> --summary "更換主金鑰" --reason "主金鑰遺失" --sms SMS-CYB-001:2:DPA --file config/trusted-keys/primary-2027.pem=new-primary.pub.pem --remove-key <舊主金鑰ID> --cert <備援金鑰指紋> --out trust.sbupd`,下次訪船派送。

## 7. 給網頁端(教官站、船上版次顯示)的契約

網頁端不直接處理簽章;以 `simosa-pkg status --json`(或直連的 `statusResult`)與教官端 `fleet-versions.json`、`sync` 結果呈現。

**ShipStatus**(`status --json`、`statusResult`):
```json
{
  "type": "statusResult", "shipId": "FSB1", "deviceId": "FSB1-LT01",
  "version": "2026.10.2", "packageId": "upd-…", "appliedUtc": "2026-10-17T05:00:00Z",
  "signer": { "keyId": "…", "name": "訪船船長 王○○", "role": "primary" },
  "highestVersion": "2026.10.2",
  "components": { "parameters": { "version": "2026.10.2", "packageId": "upd-…", "appliedUtc": "…", "summary": "DWO-007 第 4 版" },
                  "software": { "version": "2026.10.1", "packageId": "upd-…", "appliedUtc": "…", "summary": "…" } },
  "parameterSet": { "id": "simosa-company-procedures", "version": "2026.10.2", "sha256": "…", "path": "data/parameters/company-procedures.yaml",
                    "sourceRevisions": { "DWO-007": "4" } },
  "smsDocuments": [{ "documentId": "DWO-007", "revision": "4", "approvedBy": "DPA 林○○", "approvedOn": "2026-10-15" }],
  "trustedKeys": [{ "keyId": "…", "name": "訪船船長 王○○", "role": "primary", "holder": "訓練負責人" },
                  { "keyId": "…", "name": "DPA 備援金鑰", "role": "backup", "holder": "DPA" }],
  "outstandingRecordPackages": 1, "visitSeq": 4, "rollbackAvailable": true,
  "warnings": ["1 個紀錄包已累積超過 2 個訪船間隔仍未取得教官筆電的已備份回報…"]
}
```
- 練習啟動畫面與講評報告顯示 `parameterSet.version` 與 `smsDocuments`(第 9.1 節「船上顯示現行參數集版次」);參數集版次與紀錄標頭 `header.parameterSet.version` 不同時標示「參數集版次已變更」。
- **UpdateApplyResult**(`updateResult.result`):`{ ok, packageId, version, kind, summary, signer, signerRole, components[], errors[], warnings[], rolledBack, message }`。
- **CleanupResult**(`cleanupResult.result`):`{ ok, reportId, signer, deleted[], deletedFiles[], retained[], mismatched[], unknown[], failed[], warnings[], errors[] }`(`failed`:無法刪除的檔案「紀錄包 路徑:原因」,該紀錄包保留待下次重試)。
- **fleet-versions.json**(教官端兩船版次清單):`{ "ships": { "FSB1": { shipId, deviceId, version, packageId, components, parameterSet, updatedUtc, via } } }`。
- 練習紀錄標頭(SimCore 已實作;教官站 `sessionInfo` 指令):`participants: [{ "name": "王大副", "role": "oow", "rank"?, "certificateNo"? }]`、`restHoursConfirmation: { "confirmedBy": "船長", "confirmedAtUtc": "…", "compliant": true, "note"? }`、`course?`;紀錄已開啟後才送達者寫成最後一行 `{"kind":"session", …}`,`RecordInspector` header 有就用 header、否則取最後一行 session(`sessionInfoSource`),紀錄包據此彙總訓練時數(`participants[].rank/certificateNo` 一併帶出)。footer 已含 `closedUtc`(關檔時的牆鐘 UTC),紀錄包以它為練習結束時間(舊紀錄無此欄時仍以檔案最後修改時間估計)。

## 8. 金鑰保管程序要點(納入公司網路風險管理程序,TMSA 第 13 要素)

1. 主金鑰由訓練負責人保管於 PIN 保護的硬體金鑰(智慧卡/PIV),私鑰於硬體內產生且不可匯出;**不得以檔案存於教官筆電**。PEM 檔金鑰只用於開發與測試,不得放入儲存庫或系統映像。
2. 備援金鑰由 DPA 或海務主管保管(另一把硬體金鑰,與主金鑰分處保管);僅用於簽章信任更新包或主金鑰無法使用時的緊急派送。
3. 兩把公鑰預載於船上筆電系統映像、備用 SSD 與備援筆電;公鑰指紋(金鑰 ID 前 16 字元)列入資產清單,船上套用前畫面顯示簽章者供核對。
4. PIN 連續錯誤鎖定依硬體預設;PIN 不得記錄於筆電或隨行文件。
5. 遺失、失竊、交接:立即通報 DPA → 以備援金鑰簽章信任更新包移除舊公鑰、加入新公鑰 → 下次訪船(或隨身碟途徑)派送 → 核對兩船 `status` 的信任金鑰清單 → 記入稽核與程序紀錄。無法派送時由訪船船長以管理者帳號 `trust add/remove` 手動更換。
6. 裝置金鑰由 DPAPI 綁定該台船上筆電;更換筆電或重灌時重新 `device init` 並重新登錄到教官筆電(舊裝置公鑰自 `devices/` 移除)。
7. 教官筆電紀錄庫為主本:每次訪船後 `store backup` 至加密外接碟(留存公司)或公司檔案共享;備份完成才發出已備份回報。

## 9. 測試(`tests/Tools.Packaging.Tests`,Linux 以 PEM 金鑰執行)

簽章/驗證(竄改檔案、多餘檔案、竄改 manifest、重複 manifest 項目、未信任金鑰、降版與重播、船舶限定、路徑穿越、`remove` 越權(信任清單、裝置金鑰、他種類路徑,含反斜線/重複斜線/大小寫變形)、`packageId` 白名單)、金鑰輪替(備援金鑰更換主金鑰、主金鑰簽章的信任包被拒、移除最後一把備援被拒、覆寫現有金鑰檔被拒、沿用同名檔更換金鑰、trust 包不可 rollback)、影響分析(列出引用變更鍵的情境與評估規則、共用鍵影響多情境、被移除鍵、情境錯誤)、驗證把關(未驗證拒絕、舊參數集的驗證不算數、--force 記錄且船上拒絕)、套用與 rollback、中途失敗完整回復、提交步驟失敗(回報已套用加警告、rollback 不回到錯誤版本)、稽核內容、紀錄包往返與訓練時數(牆鐘:倍速、凍結)、竄改/他台裝置簽章/重複 manifest 被拒、練習進行中不打包、已備份回報後清理(只刪確認者;未信任/竄改回報被拒;單檔刪除失敗保留重試)、保留上限警告(同一次訪船重連不另計)、TCP 本機迴路端對端(推送、拉取、回報清理、錯誤 nonce、未信任教官金鑰、未登錄裝置、被拒的更新包不中斷 session、未驗證連線灌稽核)、隨身碟往返(含清理通知回寫、偽造通知被拒)、CLI(冒煙、YAML 錯誤回 1、布林旗標在位置參數前)。測試暫存目錄在 `build/test-tmp/packaging/`,結束即刪除。

## 10. 尚未涵蓋

- 傳輸加密:直連為實體隔離的點對點網路線,內容均已簽章;紀錄包內容(個資)若需傳輸加密,可於下一版加上以船上裝置公鑰/教官公鑰的 ECDH + AES-GCM 包裝。
- 教官站「直連模式」的 Wi-Fi/藍牙停用檢查(第 14 章「教官筆電 Wi-Fi 開啟時直連模式拒絕同步」)由教官站在呼叫 `sync` 前執行。
- 錄影檔(數 GB)目前與紀錄包分開;日後以相同 manifest 格式另包並以串流傳輸。
- 選配的船舶電子郵件途徑沿用隨身碟格式(單一簽章檔),未另行實作。
- 永久撤銷清單:目前以「trust 包不可 rollback」防止被撤銷的金鑰回到信任清單;管理者手動 `trust add` 仍可加回(屬刻意操作,記入稽核)。
- 練習結束的牆鐘時間待 SimCore 在 footer 寫入 `closedUtc`;在此之前以紀錄檔最後修改時間估計(`endEstimated`)。
- 直連為單一連線服務:未驗證者持續佔用連線仍可阻擋同步(不影響稽核與磁碟);實體上以網路線直連、`listen` 綁定直連介面降低暴露。
