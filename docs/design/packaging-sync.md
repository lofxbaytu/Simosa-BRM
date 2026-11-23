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
收集船上 `build/records/` 第一層 `*.jsonl`(練習紀錄)、`build/records/reports/**`(報告)與 `build/records/media/`(教官站畫面錄影 `*.webm` 與旁檔 `*.json`,kind `media` / `media-meta`;`docs/design/screen-recording.md` 第 4 節);未寫 footer 且 2 分鐘內仍在修改的紀錄視為練習進行中,不打包;`.part` 與沒有旁檔的 `.webm` 為錄影進行中,不打包。已打包(相同路徑 + 相同雜湊)者不重複打包。單段錄影超過 `packaging.json` `mediaMaxMb`(預設 2048;`records pack --media-max-mb` 覆寫)時只放旁檔並在 `media[]` 標 `omitted: true` 與原因,錄影留在船上(`records list` 警告;旁檔在備份回報時保留,待錄影檔補打包後一併刪除)。

```json
{
  "format": "simosa-records/1", "packageId": "rec-FSB1-20261017T040000-9f8e7d6c", "shipId": "FSB1", "deviceId": "FSB1-LT01",
  "createdUtc": "2026-10-17T04:00:00Z", "visitSeq": 3,
  "files": [{ "path": "records/20261012-013000-E01_baseline.jsonl", "sha256": "…", "size": 1234567, "kind": "record" },
            { "path": "records/reports/E01-debrief.html", "sha256": "…", "size": 2345, "kind": "report" },
            { "path": "records/media/20261012-013005-instructor-E01-9f8e7d.webm", "sha256": "…", "size": 734003200, "kind": "media" },
            { "path": "records/media/20261012-013005-instructor-E01-9f8e7d.json", "sha256": "…", "size": 812, "kind": "media-meta" }],
  "exercises": [{ "file": "records/20261012-013000-E01_baseline.jsonl", "scenarioId": "E01_baseline", "scenarioName": "…",
                  "shipId": "FSB1", "loading": "ballast", "startUtc": "2026-10-12T01:30:00Z", "endUtc": "2026-10-12T02:30:00Z",
                  "durationS": 3600, "simDurationS": 3600, "endEstimated": true,
                  "participants": [{ "name": "王大副", "role": "OOW" }], "integrityOk": true, "hasSummary": true,
                  "parameterSetVersion": "2026.10.1",
                  "media": [{ "mediaId": "20261012-013005-instructor-E01-9f8e7d", "file": "records/media/20261012-013005-instructor-E01-9f8e7d.webm",
                              "sidecar": "records/media/20261012-013005-instructor-E01-9f8e7d.json", "station": "instructor", "mime": "video/webm;codecs=vp9,opus",
                              "startUtc": "2026-10-12T01:30:05Z", "endUtc": "2026-10-12T02:30:07Z", "durationS": 3601.2, "durationEstimated": false,
                              "sizeBytes": 734003200, "sha256": "…", "truncated": false, "omitted": false,
                              "record": "20261012-013000-E01_baseline.jsonl", "recordCreatedUtc": "2026-10-12T01:30:00Z", "scenarioId": "E01_baseline" }] }],
  "media": [ "…(本包全部錄影,含未對應到任何練習者;欄位同上)" ],
  "trainingHours": { "exerciseCount": 1, "totalS": 3600, "totalHours": 1.0, "simTotalS": 3600, "estimated": true,
                     "participants": [{ "name": "王大副", "role": "OOW", "exercises": 1, "totalS": 3600, "totalHours": 1.0,
                                        "sessions": [{ "scenarioId": "E01_baseline", "startUtc": "…", "endUtc": "…", "durationS": 3600,
                                                       "simDurationS": 3600, "endEstimated": true }] }] },
  "installedVersion": "2026.10.2", "parameterSetVersion": "2026.10.2",
  "device": { "keyId": "…", "publicKey": "-----BEGIN PUBLIC KEY-----…" }
}
```
練習起訖與時長一律用**牆鐘**(供轉錄至船上工時/休息時間紀錄;SimCore 可倍速 0.1–10 或凍結,模擬時間與實際經過時間不同):開始 = `header.createdUtc`;結束 = `footer.closedUtc`(SimCore 寫入時;**目前尚未寫入,建議 SimCore 後續在 footer 加上**)→ 否則紀錄檔最後修改時間(SimCore 寫完 footer 即關檔)→ 否則開始 + 模擬時長;後兩者標 `endEstimated: true`。`durationS` = 結束 − 開始;模擬時長(`footer.finalTick × header.dt`,無 footer 時取最後一行 state 的 t)另列為 `simDurationS`。參與者:紀錄標頭 `participants`(SimCore 一律寫 `{name, role, rank?, certificateNo?}` 物件陣列;舊/外部紀錄的字串陣列 `RecordInspector` 與 `RecordReader`/`Replayer` 都可讀,字串元素讀成 `{name, role: null}`,`RecordParticipant` 的 JsonConverter,2026-10-29),有 `restHoursConfirmation` 時原樣帶出;紀錄開啟後才送達的 `sessionInfo` 寫成最後一行 `session`(見下文)。完整性同 `RecordReader`(footer.sha256 對 footer 前所有行)。

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
| `state/installed-files.json` | 更新包覆蓋清單(`simosa-installed-files/1`):更新包寫入的每個路徑的 SHA-256、大小、來源更新包,與被更新包刪除的路徑;所依據的 `manifest.json` 身分;降版的證據(`downgrade`);安裝模式核對用(第 5.1 節);與 `installed.json` 同時寫入、rollback 一起還原 |
| `state/previous/` | 上一版元件目錄 + `previous.json`(`replacedByPackageId/Version/Kind`、元件、套用當時的 `manifest.json` 身分 `release`/`releaseRecorded`/`releaseUnreadable`)+ 舊 `installed.json`、舊 `installed-files.json`(套用前沒有則沒有;rollback 用,只保留一層) |
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
4. **原子替換**:對受影響的每個頂層元件,先建 `state/next/<元件>`(複製現行 → **先刪除** `remove` 與 trust 類被移除金鑰的檔案 → **再寫入**新檔,新檔與被移除的檔案同名時不會被刪掉;trust 類再以組好的信任清單目錄重新載入核對),再以目錄改名把現行移到 `state/previous.pending/`、新目錄移到 `<root>/<元件>`;先寫覆蓋清單 `installed-files.json`(第 5.1 節;套用前的清單先存進 `previous.pending/`),再寫 `installed.json`。任何一步失敗(含寫這兩個狀態檔)即逐一移回、覆蓋清單還原成套用前,**完整回復原狀**,稽核 `update.failed`。
5. **提交**:`installed.json` 寫入後新版即已上線,此後的失敗**不得回報「未變更」**。舊的 `state/previous` 先改名移開(`previous.stale-*`),`previous.pending` 改名為 `state/previous`,再盡力刪除舊的;刪不掉(Windows 檔案鎖定)只記警告,下次套用時再清理。若連 `state/previous` 都無法放入,回報成功加警告,上一版元件另存 `state/previous.unsaved-*`(不刪除),殘留的舊 `state/previous` 失效(不能 rollback 到錯誤版本)。稽核 `update.applied`(+ `trust.rotated`);稽核寫入失敗也只記警告。
6. **rollback**(`simosa-pkg rollback`):把 `state/previous` 的元件移回、恢復舊 `installed.json`(`highestVersion` 不下降、該包歷史標 `rolledBack`)與舊覆蓋清單(套用前沒有清單則刪除;這一步失敗只記警告,核對時列為無法核對)。`state/previous` 必須正是現行更新包取代掉的上一版(`previous.json.replacedByPackageId` = 現行 `packageId`)、上一個套用的不是 trust 包,而且套用之後**沒有重新安裝過**發行版(第 5.1 節「重新安裝」;重新安裝之後 `state/previous` 是重新安裝之前的元件,回復會留下兩個版本混雜的 `app/`),否則拒絕。`previous.json` 自本版起另記套用當時 `manifest.json` 的身分(`release`:版本、檔案 SHA-256、建置時間、內容身分 `contentSha256`;`releaseRecorded: true`,`release` 省略即當時沒有可讀的發行清單 — 再以 `releaseUnreadable: true` 標出「檔案在、但讀不到」,省略即「檔案不在」),舊版的 `previous.json` 沒有這些欄位照樣讀。套用時 `manifest.json` 在、卻讀不到(損毀或 IO 錯誤)**照常套用、不拒絕**,但結果加一則警告(發行清單讀不到、請先 `verify-install` 或重新安裝,並寫明之後 rollback 的判斷依據)。這時覆蓋清單若仍保留更早一次讀得到的套用記下的 `baseline`(最後已知的發行版;讀不到的這次套用沒有身分可比,不會改動它),就沿用它,之後 rollback 與核對都依它判斷是否重新安裝過(警告寫「依覆蓋清單記錄的最後已知發行版 X 判斷」);覆蓋清單也沒有身分時才記 `baselineUnreadable: true`,之後改依建置時間判斷(警告寫「改依發行版的建置時間判斷」)。rollback 本身、`status` 的 `rollbackAvailable` 與安裝模式核對的「請不要執行 rollback」提醒都以同一個判斷 `UpdateApplier.CheckRollback` 為準。

同一時間只允許一個套用/打包/清理程序(`state/.lock`)。軟體元件(app/)替換時,執行中的程式不可位於 app/ 內:正式安裝時 `simosa-pkg` 與 serve 服務放在 app/ 之外(安裝程式另置 `tools/updater/`),模擬器在套用期間須關閉。

### 5.1 安裝模式核對與更新包覆蓋清單(`release verify --installed`)

`simosa-pkg release verify --dir <目錄>`(`ReleaseManifestBuilder.Verify`)要求目錄內容與 `manifest.json` 完全相同(缺少、不符、**多出**都是錯誤),只適用剛複製好的發行版。使用中的安裝根目錄有執行期資料、本機修改的設定與更新包換過的檔案,要用**安裝模式**:

```
simosa-pkg release verify --dir <安裝根目錄> --installed [--json] [--max-items <N>]
simosa-pkg verify-install [--root <安裝根目錄>] [--json] [--max-items <N>]        ← 同一個實作(InstalledVerifier);預設根目錄同其他指令
```

船上筆電離線、程式可能放在隨身碟,這是「程式與資料檔有沒有損毀或不完整」的核對,不是簽章驗證(`manifest.json` 不簽章;更新包的簽章在套用時驗證)。一般模式的行為不變;對看起來是使用中的安裝根目錄(有 `state/`、`build/records`、`build/logs` 或 `config/packaging.json`)未加 `--installed` 時多印一行提示,不改結束碼。`simosa-pkg status` 不做雜湊,文字輸出中有一行指向本指令(在「環境:」一行之前)。

**發行清單只列安裝程式會安裝的檔案**:安裝程式 `[Files]` 的 `Excludes`(`*.pdb`、`Thumbs.db`)不安裝的檔名若列進 `manifest.json`,以安裝程式安裝的筆電上就沒有這些檔案,安裝模式會報「檔案缺少」(審查第二十九輪:Godot .NET 匯出把 `.pdb` 放進 `app/visual/data_*/`)。排除規則只有一份 — `ReleaseManifestBuilder.InstallerExcludedPatterns`(`src/Tools.Packaging/Release/ReleaseManifest.cs`):`release manifest` 遇到目錄內有符合的檔案(`Thumbs.db` 依檔名略過者除外,即目前的 `*.pdb`)就**失敗並列出檔案**,不產生清單;`tools/installer/check-iss.py` 讀這個常數與 `.iss` 的 `Excludes` 對照(`Tools.Packaging.Tests` 再對照一次),**兩邊要一起改**。`tools/build-release.*` 因此在 Godot 視景匯出(步驟 5)之後、`pack`(步驟 6)與 `release manifest`(步驟 7)之前再刪一次 `app/`、`tools/` 之下的 `*.pdb`(軟體更新包也不含偵錯符號)。舊版工具產生、列有這類檔案的清單:一般模式 `release verify` 加一則警告;安裝模式把缺少的這類檔案註明「安裝程式不安裝這類檔案,不是檔案損毀」,只有這種錯誤時「下一步」是回報開發者而不是重新安裝。

**預期內容** = `manifest.json` 所列檔案 − 覆蓋清單 `removed` 的路徑(檔案或整個目錄)+ 覆蓋清單 `files`(取代或新增)。

**分級**(結束碼:有錯誤 1,否則 0;`--max-items` 等參數寫錯是用法錯誤 2,在核對開始之前就回報):

| 級別 | `category` |
|---|---|
| 錯誤 | `missing` 預期的程式/資料檔不在;`mismatch` 大小或 SHA-256 不符;`unreadable` 讀取失敗(磁碟錯誤、權限);`extra` 程式目錄 `app/`、`tools/updater/` 內多出預期以外的檔案(舊版殘留的組件、被放進去的檔案會被載入或被網頁伺服送出),或程式目錄內(含其上層,例如 `tools/` 本身)有**目錄連結**(其下內容無法核對;`path` 以 `/` 結尾);`manifest` 發行清單讀不到、格式錯(含 `files` 為 null)、路徑不合法或重複 |
| 提醒 | `configModified`、`configMissing`:預期內容中 `config/` 之下的檔案與預期不同或不在;`unlisted`:程式目錄與執行期資料位置以外的未列檔案,或該處的目錄連結(`path` 以 `/` 結尾);`unverifiable`:無法核對的前綴(見下);`state`:狀態檔讀不到、發行版在更新包之後重新安裝過、**降版**(以較舊的安裝程式重新安裝過;見下「重新安裝」) |
| 略過 | 執行期資料位置之下**預期內容沒有列**的檔案:不進入這些目錄、不逐一列出(只列出實際存在的位置)。預期內容有列的檔案即使位於這些位置仍照常核對 |

- **設定檔不在是提醒、不是錯誤**:`config/` 是使用者的檔案(安裝程式對既有設定不覆寫、解除安裝不刪);船上筆電用不到 `instructor-laptop.json`、教官筆電用不到 `ship-laptop.json`,核對工具無從判斷哪一個必要;缺了必要設定時啟動腳本會明確報錯,補救也不是重新安裝程式檔。trust 更新包寫入的信任公鑰同屬 `config/`:與覆蓋清單不同或不在只列提醒(管理者可用 `trust add/remove` 手動變更;現況以 `status` / `trust list` 為準)。
- **執行期資料位置**集中定義在 `InstalledPaths.RuntimeData`(`src/Tools.Packaging/Common/InstalledPaths.cs`):`build/`、`.cache/`、`state/`、`store/`、`config/device/`、`config/trusted-keys/`、`config/packaging.json`、`data/records/`,以及岸上放入離線執行階段安裝檔的 `tools/webview2/`、`tools/dotnet/`;測試以 `InstallLayout` 的各路徑與安裝程式 `tools/installer/SimosaBRM.iss` 的 `[Dirs]`、`[UninstallDelete]` 對照(`docs/ops/本機資料位置.md` 第 2 節為人讀的對照表)。`packaging.json` 自訂的 `recordsDir` / `store` 位於安裝根目錄內時也算,但不得涵蓋程式目錄。另依檔名略過 `Thumbs.db`、`.DS_Store`、解除安裝檔 `unins000.*` 與 `desktop.ini`。
- Windows 上路徑比對不分大小寫(與套用時的檔名比較一致)。
- **目錄連結(junction/symlink)不跟進,但一定列出**(避免走出安裝根目錄或形成迴圈;連結之下多出的檔案查不到):位於程式目錄內 → `extra` 錯誤(「目錄連結,其下內容無法核對」— 連結指到的內容會被載入或被網頁伺服送出;處理方式是移除連結本身);其他位置 → `unlisted` 提醒;執行期資料位置(例如把 `build\records` 連到另一顆磁碟)不進入、不列。清單所列的檔案仍依路徑直接核對(會跟著連結走)。
- **文字輸出**:標頭(發行清單版本、現行更新包、覆蓋清單筆數、已核對檔案數、未列入的位置)→ 提醒 → 錯誤(stderr)→「核對通過/核對失敗」→ 有錯誤時的「下一步」。每一類預設列前 10 筆再給總數(`--max-items <N>`,0 = 全部)。
- **下一步**(`nextSteps`)依狀況:
  - 程式或資料檔缺少、不符、讀不出來,且沒套用過更新包 → 以原安裝程式(或專用隨身碟上的發行版)重新安裝到同一位置。
  - 同上,套用過更新包 → 取得安裝程式重新安裝,或取得更新包套用。**安裝程式的版本基準 = 發行清單版本與現行更新包版本的較大者**(「版本不低於 X 的安裝程式」):套用更新包之後又以更新的安裝程式重新安裝過時,現行更新包版本比發行版舊,只看它會把整個發行版降版。例外:目前的發行版之上還有套用中的更新包、而發行版版本不低於現行更新包時,以同一個發行版重新安裝只會把更新包換過的檔案還原(核對把這種檔案標成「內容與發行清單相同、更新包的變更被還原」的 `mismatch`),所以寫「版本**高於** <發行版> 的安裝程式」。**更新包**那一句寫明三件事:版本須高於上述基準與曾套用過的最高版本(`highestVersion`,rollback 後不下降)兩者的較大者;必須**包含受損的檔案**(`app\` 的程式檔要完整的軟體更新包,只含情境或參數的更新包修不好;`data\` 的檔案要含該檔的更新包);不在更新包可寫入位置(`app\`、`data\` 以外,例如 `tools\`、`docs\`)的檔案只能以安裝程式修復。已套用過的更新包不能重複套用。也寫明不要用**較舊的**安裝程式:那會把整個發行版降版,核對只以「降版」的 `state` 提醒指出,不列為錯誤(提醒保留到套用新的軟體更新包或以夠新的安裝程式重新安裝為止;見下「重新安裝」)。
  - **更新包新增、發行版沒有、又不在程式目錄的檔案**(例如圖資更新包放進 `data\charts\` 的新檔;`errors[].repairableBy: package`)缺少、不符或讀不出來 → 另寫一項:安裝程式沒有這些檔案、也不清除 `data\`(覆蓋清單在重新安裝後仍保留它們的紀錄),重新安裝之後仍會列出 — 照「重新安裝」做只會讓要求的安裝程式版本一直往上推,還會被導向換磁碟 — 所以下一步是取得「包含這些檔案、版本高於曾套用過的最高版本(`highestVersion`)的更新包」套用;「重新安裝後仍有錯誤 → 換磁碟」不套用在這類檔案(與其他錯誤並列時,該句註明「更新包新增的檔案除外」)。
  - **順序**:重新安裝那一項(或只有「安裝程式不安裝的檔案」時的說明,兩者擇一)→ 緊接著更新包新增檔案那一項 → 多出的檔案 → 最後才是「重新安裝(或套用更新包)後仍有錯誤 → 磁碟可能故障」(`InstalledVerifier.Finish`;測試 `NextSteps_Order_InstallerExcludedFilesOnly_ThenPackageAddedFiles_MatchesTheDocs` 與 `FileAddedByUpdateOutsideProgramDirs_Damaged_NextStepIsAPackage_NotReinstall` 對照)。互相參照一律寫明編號「見下一步(N)」(文字輸出有兩項以上時編號為「下一步(1)」…;`--json` 的 `nextSteps[N-1]`),不用「下一項」這類相對說法。
  - 程式目錄內多出的檔案或目錄連結 → 確認後依清單刪除(連結只移除連結本身,不要刪它指向的內容);以**安裝程式**重新安裝也會清除多出的檔案(安裝前先清空 `app\` 與 `tools\updater\`,見下「重新安裝」;有目錄連結時先手動移除連結再安裝),以壓縮檔或手動複製覆蓋則不會。
- **`--json`**(`format: simosa-install-check/1`;完整清單不截斷;null 的欄位整個省略):頂層 `format`、`mode`(`installed`)、`ok`、`root`、`release`{`version`、`gitRevision`?、`runtime`?、`builtUtc`、`files`}、`installed`{`version`?、`packageId`?、`kind`?}(`installed.json` 讀不到時沒有;沒套用過更新包時是空物件)、`installedFiles`{`present`、`consistent`、`packageId`?、`version`?、`files`、`removed`、`reinstalled`、`interruptedPackageId`?、`interruptedVersion`?}、`downgrade`?{`releaseVersion`、`basisVersion`、`previousReleaseVersion`?、`softwareVersion`?、`highestVersion`?(被重新安裝覆蓋的軟體更新包 — software 或含 `app/` 的 mixed 包、未 rollback — 的最高版本;圖資、情境、參數更新包不計)、`softwareStale`、`message`}(降版時才有,見下「重新安裝」;與 `status --json` 的 `releaseDowngrade` 同一個物件)、`checked`、`skippedLocations[]`、`errors[]`、`notices[]`、`counts`{分類 → 筆數}、`nextSteps[]`(發行清單讀不到時 `release`、`installed`、`installedFiles` 三個物件都沒有)。`errors[]`/`notices[]` 每筆:`category` 與 `path` 必有;`detail` 選填(說明文字);`source`(`release` 或 `package:<版本>`,預期內容的來源)只出現在 `missing`、`mismatch`、`unreadable` 三類錯誤(`unreadable` 的「無法讀取目錄」沒有);`count`(其下未核對的檔案數)只出現在 `unverifiable`;`repairableBy`(`package`:只能以含該檔的更新包修復)只出現在更新包新增、發行清單沒有、不在程式目錄的檔案的 `missing`、`mismatch`、`unreadable`。一般模式的 `release verify --json` 是另一個格式 `simosa-release-verify/1`:`format`、`mode`(`release`)、`ok`、`dir`、`version`?、`gitRevision`?、`runtime`?、`files`?、`checked`、`errors[]`(字串)、`warnings[]`(字串)、`hint`?。

**覆蓋清單 `state/installed-files.json`**(`InstalledFiles`):

```json
{ "format": "simosa-installed-files/1",
  "packageId": "upd-20261120T030000-1a2b3c4d", "version": "2026.11.5", "updatedUtc": "2026-11-22T01:00:00Z",
  "baseline": { "version": "2026.11.1", "sha256": "<安裝根目錄 manifest.json 的 SHA-256>", "builtUtc": "2026-11-01T03:00:00Z",
                "contentSha256": "<解析後內容的 SHA-256>" },
  "packages":  [ { "packageId": "upd-…", "version": "2026.11.5", "kind": "software", "appliedUtc": "…", "prefixes": ["app/"] } ],
  "untracked": [ { "packageId": "upd-…", "version": "2026.10.2", "kind": "software", "prefixes": ["app/"], "reason": "notRecorded" } ],
  "files":   [ { "path": "app/simcore/SimosaBRM.SimCore.Host.dll", "sha256": "…", "size": 123456, "packageId": "upd-…", "version": "2026.11.5" } ],
  "removed": [ { "path": "app/web/assets/index-abc.js", "packageId": "upd-…", "version": "2026.11.5" } ],
  "downgrade": { "releaseVersion": "2026.11.1", "previousReleaseVersion": "2026.12.1", "softwarePackageId": "upd-…", "softwareVersion": "2026.12.2",
                 "highestAppliedVersion": "2026.12.2", "recordedUtc": "…" } }
```

`downgrade`(降版的證據,見下「重新安裝」的**降版**)與 `baselineUnreadable`(套用時 `manifest.json` 在、但讀不到,而且清單也沒有先前記下的 `baseline` 可沿用;`true` 時才寫)只在需要時出現;舊版 simosa-pkg 寫的清單沒有這兩個欄位照樣讀。

- **逐檔記錄,沒有「被整個取代的前綴」**:套用時每個頂層元件是「複製現行 → 刪除 → 寫入新檔」再整個目錄改名(第 5 節第 4 步),更新包沒有列的現行檔案會保留,所以 software 包也不是把 `app/` 整個換掉。`files` 是更新包寫入的每個路徑(後套用者取代先套用者的紀錄),`removed` 是套用時實際刪除的路徑(`manifest.remove`,加上 trust 包由 `trust.remove` 推得的金鑰檔;可以是整個目錄);記錄順序與套用相同(先刪後寫),之後又被寫入的檔案以 `files` 為準。路徑一律是 `PackagePaths` 正規化的相對路徑。`packages` 為已納入的更新包(新到舊,最多 50 筆);mixed 包的 `prefixes` 依實際觸及的路徑。
- **與 `installed.json` 同在提交點之前寫入**(先覆蓋清單、後 `installed.json`;任何一個寫不進去都整體回復,更新包視同未套用、可原包重試)。不採「提交後只記警告」:同一版本不能重複套用,提交後才發現清單寫不進去就再也補不回來,該更新包的前綴會長期停在「無法核對」。套用前的清單存入 `state/previous/`,rollback 時還原(套用前沒有清單則刪除)。
- **一致性**:`packageId`/`version` 必須等於 `installed.json` 的現行更新包。對不上(覆蓋清單已寫而 `installed.json` 未寫就中斷、rollback 時清單還原失敗、檔案被舊備份蓋回)時,把兩邊對不上的更新包所屬種類的前綴(`UpdateKinds.AllowedPrefixes`)列為 `unverifiable`(`reason: diverged`),其下的紀錄一律不採信 — 不比對、不報錯,但一定有提醒,不會顯示成「沒有提醒的通過」。`installed.json` 讀不到時,更新包能寫入的全部前綴(`app/`、`data/`、`config/trusted-keys/`)都列為無法核對。下一次套用時這些更新包記入 `untracked`,清單恢復一致;之後核對時,這些已寫進清單的 `diverged` 紀錄以過去式說明(「先前…曾與 installed.json 不一致…現在兩者已一致」),不再指示以 `status` 確認(`--json` 的 `installedFiles.consistent` 此時是 true)。
- **套用中斷後以原包重試**:清單在全部元件換完之後、`installed.json` 之前寫入;若正好在兩者之間中斷(斷電),清單的現行更新包在 `installed.json` 完全沒有紀錄(連 `rolledBack` 都沒有)。核對時仍列為不一致,但註明「該更新包的套用在最後一步中斷,以同一個更新包檔重新套用即可恢復一致」(`installedFiles.interruptedPackageId/Version`;它沒有被記為已套用,版本檢查會通過)。重新套用**同一個**更新包時(`UpdateApplier` 把正在套用的 `packageId` 傳給 `InstalledFiles.Reconcile`)清單原樣沿用 — 它是在元件換完之後、依同一份 `installed.json` 對齊過的,可信 — 不列入 `untracked`、不丟棄紀錄,套用後即一致、沒有提醒。rollback 時清單還原失敗的情況不同(`installed.json` 有該包標為 `rolledBack` 的紀錄),仍照不一致處理;中斷之後改套用別的更新包也照不一致處理。
- **沒有覆蓋清單、`installed.json` 卻有套用中的更新包**(被 rollback 的不算套用中)分兩種:
  - **實際的舊安裝**:`simosa-pkg` 放在 `tools/updater/`,不會被軟體更新包替換;船上要有會寫覆蓋清單的版本,唯一途徑是重新安裝發行版。因此舊版套用過的更新包,套用時間都早於目前發行版的建置時間(`manifest.json` 的 `builtUtc`)— 發行版一定是在它們之後才安裝的,安裝已覆寫清單所列的每個檔案。這些更新包**不列為無法核對**,清單所列檔案以發行清單照常核對,並以 `state` 提醒說明(`status` 顯示的版次或參數集可能與實際檔案不符)。舊更新包新增而新發行清單沒有的檔案若留在 `app/`,是「程式目錄內多出的檔案」。
  - **清單遺失或損毀、之後又重新安裝過**:`state/previous/previous.json`(屬於現行更新包者;rollback 判斷用的同一份)記錄的套用當時 `manifest.json` 身分與目前不同(`PreviousMeta.ReleaseChangedSince`)→ 套用中的更新包都在那次重新安裝之前,全部算「已被安裝覆蓋」(與上一種相同的處理與 `state` 提醒,文字改為「套用之後重新安裝過發行版(previous.json 記錄套用時的發行版 X,目前 Y)」),不依時間規則;降版的判斷也以此為依據(見下)。
  - **清單遺失或損毀**(套用時間不早於發行版建置時間,也沒有上一項的依據):這些更新包所屬種類的前綴列為 `unverifiable`(`reason: notRecorded`),其他路徑照常核對。**不能靠重新套用同一個更新包建立清單**(版本須遞增,同版本會被拒絕),也不需要:下一次套用任何更新包時,現有的套用歷史記入 `untracked`,該包寫入的檔案開始有紀錄並納入核對;無法核對的前綴之下只核對有紀錄的檔案,其餘只計數。當前綴之下已沒有未核對的檔案(例如套用了涵蓋 `app/` 全部檔案的完整軟體更新包)且清單一致時,提醒自動消失。
  - 以時間判斷的前提是船上筆電的時鐘大致正確(套用時間 `appliedUtc` 取自船上筆電、建置時間 `builtUtc` 取自岸上建置機)。船上時鐘**落後**時,在發行版之後套用的更新包可能被當成「早於發行版」:清單遺失時會把該列為無法核對的檔案當成錯誤(偏向多報,不會漏報),rollback 被拒絕(誤擋,較安全)。船上時鐘**超前**時則相反:在發行版之前套用的更新包被當成「之後套用」,列為無法核對(不誤報),rollback 不會因此被擋(漏擋)。同樣的時間規則也用在「不一致」的更新包,以及沒有覆蓋清單時 rollback 的「重新安裝過」判斷(見下)。
- **重新安裝**:`baseline` 記錄清單所依據的 `manifest.json`(版本、檔案 SHA-256、建置時間、內容身分 `contentSha256`)。**是否為同一個發行版**一律以 `InstalledBaseline.SameRelease` 判斷(`Reconcile`、核對、rollback 共用):檔案的 SHA-256 相同 → 是;不同時比**內容身分** — `version`、`builtUtc`、`gitRevision` 與依路徑排序的「`path`\t`sha256`\t`size`」各行(UTF-8)的 SHA-256 — 所以只改編碼(加 BOM、LF→CRLF,例如以編輯器開過存檔)仍是同一個發行版,下一次套用時清單改記目前的身分;舊版 simosa-pkg 寫的紀錄沒有 `contentSha256`,以 `version` + `builtUtc` 比較。覆蓋清單的 `baseline` 為 null(套用時 `manifest.json` 不在)而現在讀得到,也算重新安裝過(發行清單是之後由安裝程式或手動複製放進來的)。套用時 `manifest.json` 在、只是讀不到時,清單若仍有更早一次記下的 `baseline` 就沿用(最後已知的發行版,照上述身分比較;rollback 也依它判斷,見下);沒有可沿用的 `baseline`(`baselineUnreadable: true`)則沒有身分可比,不能直接當成重新安裝:目前的發行版建置於清單最後一次寫入(`updatedUtc`)之後才算重新安裝過;否則視為同一個發行版,改記目前的身分(核對另列一則 `state` 提醒說明),更新包寫入的檔案照覆蓋清單核對 — 若其間其實以較舊的安裝程式重新安裝過,會報 `mismatch`(偏向多報)。覆蓋清單最後一次寫入之後又重新安裝了發行版時,依序(先做這一步,再比對與 `installed.json` 的一致性):
  - **清單所列路徑改以新的發行清單為準**(安裝會覆寫清單所列的每個檔案):這些路徑的 `files` 紀錄、涵蓋它們的 `removed` 紀錄作廢。
  - **程式目錄(`app/`、`tools/updater/`)之下、新清單沒有列的紀錄一律作廢**:安裝程式的 `[InstallDelete]` 在安裝前清空這兩個資料夾(`tools/installer/SimosaBRM.iss`;`check-iss.py` 以白名單限定只能列這兩項,`Tools.Packaging.Tests` 與 `InstalledPaths.ProgramPrefixes` 對照),重新安裝後程式目錄與發行版完全相同 — 先前更新包在 `app/` 新增的檔案已不在,若仍留在預期內容會誤報「檔案缺少」。以**壓縮檔或手動複製**覆蓋發行版時沒有這一步清空:舊版留下而新清單沒有的檔案(每次建置檔名雜湊都不同的網頁資產、先前更新包新增的檔案)列為 `extra`「程式目錄內多出的檔案」,依清單刪除(不再以覆蓋清單放行,否則舊組件會一直留在 `app/` 被載入)。
  - **其他位置(`data/`、`config/trusted-keys/`;安裝程式不清空)**:更新包新增而發行清單沒有的檔案仍以覆蓋清單核對。
  - 既有的 `untracked` 作廢(那些前綴已被安裝覆寫)。

  核對時以 `state` 提醒說明(更新包對 N 個檔案的變更已被安裝覆蓋、在程式目錄新增的 M 個檔案不再列入預期;`status` 顯示的版次或參數集可能與實際檔案不符;`simosa-pkg rollback` 因此會拒絕時另註明「請不要執行 rollback」— 這一句由 `UpdateApplier.CheckRollback` 的實際結果產生,沒有覆蓋清單的舊安裝的「之前套用」提醒也一樣),下一次套用時把調整寫回清單。**rollback 的「重新安裝過」判斷**(`UpdateApplier.ReinstalledSinceLastApply`;`state/previous` 是重新安裝之前的元件,回復會留下兩個版本混雜的安裝):目前沒有可讀的 `manifest.json` → 無從判斷,不擋;`previous.json` 記錄了套用當時的身分(本版起)→ 以它為準(與目前不同、或當時沒有而現在有 → 拒絕;當時讀不到(`releaseUnreadable`)→ 先看覆蓋清單(屬於現行更新包,或寫入時間不早於 `previous.json`)仍保留的 `baseline`(更早一次套用記下的最後已知發行版;核對的 `Rebase` 也依它判斷,兩邊同一個依據 — 審查第二十九輪 P5):與目前不同 → 拒絕,相同 → 放行並附一則警告說明依據;覆蓋清單也沒有身分(`baseline` 為 null、`baselineUnreadable`,或沒有覆蓋清單)時才依建置時間 — 目前的發行版建置於套用之後才拒絕,否則放行,rollback 結果附一則警告說明依據較弱,請回復後再 `verify-install`);舊版 `previous.json` 而有覆蓋清單 → 以覆蓋清單的 `baseline` 判斷(含 `baseline` 為 null 而現在讀得到 → 拒絕;`baselineUnreadable` 同上依建置時間);也沒有覆蓋清單 → 與核對相同的 `Reconcile` 結果(有更新包的套用時間早於目前發行版的建置時間,即 `SupersededPackages` 非空 → 拒絕;時鐘偏差的方向見上)。
  **降版**:**保存證據,不是每次推斷**(審查第二十九輪 P4)。`Reconcile` 看得到重新安裝時(上述 `Rebase`,或沒有覆蓋清單時 `SupersededPackages` 非空 — 含以 `previous.json` 判斷者),以重新安裝之前的狀態 — 先前安裝的發行版(`baseline.version` 或 `previous.json` 的 `release.version`)、被這次重新安裝覆蓋的 `software` 元件(`installed.json` 的套件 ID 與版次)、被這次重新安裝覆蓋的**軟體**更新包最高版本 — 與目前的發行版比較,任一項比較新就把這些記進覆蓋清單的 `downgrade`(已有紀錄時,先前的發行版與最高版本取較大者),套用時寫回。**「被這次重新安裝覆蓋」只算真的被蓋掉的更新包**(審查第二十九輪 P5;`ReconcileInfo.OverwrittenPackages`):看得到 `Rebase` 時,是作廢的 `files` / `removed` 紀錄所屬的更新包(清單所列路徑、程式目錄之下),加上套用中的軟體更新包與含 `app/` 的 mixed 包(安裝程式清空 `app/` 重裝,只刪檔的軟體包也算)與 `software` 元件;沒有覆蓋清單時(時間規則或 `previous.json`),是 software / 含 `app/` 的 mixed 包,與前綴和發行清單所列路徑相交的種類。其中**只有軟體更新包計入比較基準**:圖資包放進 `data\charts\`、發行清單沒有的檔案不會被安裝蓋掉,本來就不算;參數、情境包換過的發行清單所列檔案雖然被蓋回,由「被安裝覆蓋」的 `state` 提醒說明即可 — 把它們的版本算進來,會把正常升級(2026.11.1 + 圖資 2026.12.4 → 以 2026.12.1 升級)或回到原本的發行版誤報為降版,還要求可能根本不存在的「版本不低於某個參數包」的安裝程式。已 rollback 的更新包不算(**不用** `highestVersion`:rollback 不會降低它,升級 → 套用 → rollback 會誤報;也不再以它補位)。是否已 rollback 以 `installed.json` 為準:rollback 時「還原覆蓋清單」失敗、覆蓋清單仍記著已撤回的包時,`InstalledFiles.CollectOverwritten` 也只計 `installed.json` 仍在套用中的包(第二十九輪第五次審查)。只有非軟體更新包被覆蓋、先前的發行版也不比目前新時不寫 `downgrade`。合併時不會納入降版之後才套用的更新包:之後套用的軟體更新包會清除紀錄,已 rollback 的不算,非軟體更新包本來就不計 — 所以 2026.12.1 + 圖資 → 以 2026.11.1 重新安裝(降版,比較基準是先前的發行版 2026.12.1)→ 參數包 → 以 2026.12.1 重新安裝,結果與中間沒有套用參數包時相同:不報降版。之後套用圖資、情境、參數、信任清單更新包時原樣保留(這時覆蓋清單已對齊到目前的發行版,**這一次**看不到重新安裝 — 若仍只依這一次推斷,提醒會消失,status 卻還顯示較新的 software 版次);**清除**的條件只有:套用新的軟體更新包(或含 `app/` 的 mixed 包;`InstalledFiles.Record`),或以版本不低於比較基準的發行版重新安裝(`Reconcile` 重新評估時各項都不比目前的發行版新)。rollback 那個軟體更新包時,套用前的覆蓋清單連同證據一起還原,提醒又出現。判斷一律是 `DowngradeRecord.Evaluate`(核對與 status 共用;`software` 那一項只在 `installed.json` 的 `software` 元件仍是記錄的那個更新包時才算):有比目前的發行版新的項目 → 另列一筆 `state` 提醒(`path: manifest.json`,開頭「降版:」),寫出兩邊的版本;`software` 那一項成立時註明「`simosa-pkg status` 顯示的軟體版次不是實際執行的程式」,並寫明提醒會保留到何時。**不列為錯誤**:以較舊版本的安裝程式回到舊版是 rollback 拒絕訊息建議的正規作法;若不是刻意回到舊版,提醒寫明以版本不低於比較基準(比目前的發行版新的各項中的最大者)的安裝程式重新安裝(或請岸上派送版本更高的軟體更新包)。沒有重新安裝的跡象時不算降版(發行版 2026.11.1 上套用 2026.11.2 的軟體更新包是常態;只刪除檔案的軟體更新包也一樣)。`simosa-pkg status` 以同一個判斷(`InstalledVerifier.DetectDowngrade`,讀 `manifest.json`、狀態檔與 `previous.json`,不做逐檔雜湊)在文字輸出加一行「警告:降版:…」(stderr),`--json` 新增 `releaseVersion` 與 `releaseDowngrade`(既有欄位不變)。證據保存不到的情況:降版到本功能之前的發行版(它附的舊版 simosa-pkg 不寫也不保留 `downgrade`);降版之後又套用過更新包、覆蓋清單**之後**才遺失或損毀(`previous.json` 已是降版之後那次套用的,身分與目前相同)。因此「以安裝程式重新安裝 / 升級」之後核對即通過,不需要手動清理。以**同一個**發行版(`manifest.json` 相同,偵測不到重新安裝)覆蓋已套用更新包的安裝,更新包寫入的檔案被還原成舊內容、新增的檔案被清掉,仍是 `mismatch`/`missing` 錯誤(`installed.json` 記的版次與實際檔案不符,不能當成通過);「下一步」因此要求版本高於該發行版的安裝程式(見上)。以**較舊的**發行版重新安裝則是整個降版:上面的「降版」提醒,不是錯誤。
- 核對只讀不寫,也不取 `state/.lock`(唯讀媒體上也能執行);套用更新包進行中同時核對,可能看到暫時的不一致。

**教官站頁首的發行版本與降版提醒**(第三十輪;使用者的選擇:降版提醒顯示在教官站畫面上,不必下指令才看得到)

`simosa-pkg status` 的 `releaseVersion` 與 `releaseDowngrade`(第 7 節)經下列路徑送到教官站(完整與簡化模式;駕駛台頁不顯示):

```
啟動腳本(start-ship-laptop / start-instructor-laptop,.ps1 與 .sh;launch-common 的 Write-LaunchReleaseStatus / write_release_status)
  └─ 啟動網頁伺服之前執行一次  simosa-pkg status --root <安裝根目錄> --json  →  <安裝根目錄>/build/run/release-status.json(status 的完整輸出,留在本機)
simosa-web --status-file <該檔>(tools/launch/StaticWeb/Program.cs 的 ReleaseStatusFile)
  └─ GET /_status/release.json  →  { "format": "simosa-release-status/1", "generatedUtc": "<狀態檔的修改時間>" | null,
                                     "releaseVersion": "2026.11.1" | null,
                                     "softwareVersion": "2026.12.2" | null,        ← status 的 components.software.version
                                     "releaseDowngrade": null | { releaseVersion, basisVersion, previousReleaseVersion, softwareVersion, highestVersion, softwareStale, message } }
                                   狀態檔不存在或不合格時:同一格式、各欄 null(「沒有發行版狀態」;仍是 200)
教官站 <brm-release-status>(src/Instruments.Web:lib/release-status.ts、components/instructor/release-status.ts;instructor-main.ts 啟用)
  └─ 載入時取同源的 /_status/release.json(本機 = 提供這個頁面的電腦):頁首小字「發行版 <版本>」;套用過比發行版新的軟體更新包(沒有降版)時
     改寫「軟體更新 <版次>」;有 releaseDowngrade 時改為警示色的徽章「發行版 <版本> · 降版提醒」,按一下在頁首下方展開說明(目前執行的發行版、
     比它新的各項、status 的軟體版次不是實際執行的程式、建議的處理、status 的完整訊息),可收合(「收合」、再按一次徽章、Esc、點說明以外的地方)
  └─ 頁面與核心不在同一台電腦(教官筆電以 start-instructor-laptop 開的教官站;第三十一輪):連上核心後另外**跨來源**取船上筆電的
     http://<船上 IP>:<船上網頁埠,預設 8080>/_status/release.json(船上的 simosa-web 只對本機迴路位址的頁面回 CORS 標頭)→
     頁首改寫「船上 <版本>」;船上降版 / 本機降版 / 兩台實際執行的版本不同時是警示徽章「船上 <版本> · <標記>」,展開依序寫船上、本機、版本不同
     (各台 status 的完整訊息預設收合;完整教官站的說明不蓋住執行控制)
```

- **頁首寫的是實際執行的那一版**(第三十輪審查 BC-rBC-4):`releaseVersion` 是安裝根目錄 `manifest.json` 的版本,也就是**安裝程式的版本**,套用軟體更新包不會改它。
  船上平常靠更新包升級,只寫發行版的話頁首會一直停在安裝程式的版本,教官容易以為更新沒有套用。所以沒有降版、而且 `softwareVersion` 比 `releaseVersion` 新時
  (例:發行版 2026.12.1 套用軟體更新包 2026.12.2;實際執行的是更新包的程式),頁首改寫「**軟體更新 2026.12.2**」,title 寫明「套用在發行版 2026.12.1 之上」。
  `softwareVersion` 不比發行版新(之後以更新的安裝程式重新安裝過,那個更新包已被覆蓋)時照寫「發行版 <版本>」;有降版時由降版提醒說明,不寫「軟體更新」。
  兩個版本不並排:「發行版 X · 軟體更新 Y」比「發行版 X」寬約 125 px,簡化模式簡報頁 1366×768 的頁首會多折一行(36 → 65.6 px),同儕評估提示的最後一條被擠出簡報框
  22 px(端對端實測);「軟體更新 <版次>」只比「發行版 <版本>」寬一個字(120 與 108 px),E13 × 6 種視窗 × 完整 / 簡化選情境頁的頁首高度與版面判定都與「發行版 <版本>」相同。

- **只在安裝版取**:根目錄有 `manifest.json` 才執行(`tools\updater\simosa-pkg.dll`;沒有時用已建置的開發輸出,**不為此建置**),與是否啟動同步服務無關(`-NoSync` 也取)。開發模式(儲存庫)沒有狀態檔,`/_status/release.json` 回 200 與各欄 `null`(見下一點),教官站不顯示任何東西,瀏覽器主控台也沒有錯誤。
- **不擋住啟動**:逾時(20 秒)、結束碼非 0、輸出不是 JSON 物件都只警告,並在 `build\logs\launch.log` 記一行(`release-status …`;stderr 在 `release-status.err.log`)。每次啟動先刪掉上一次的狀態檔,所以這次沒有取到時教官站不顯示,不會顯示過期的版本。Windows 版以 `Start-Process` 把輸出導到檔案(Windows PowerShell 5.1 擷取原生程式的輸出時以主控台字碼頁解碼,中文會變亂碼)。
- **只送網頁需要的欄位**:狀態檔是 status 的完整輸出(裝置 ID、信任金鑰名稱與保管人、環境版次、警告;與在本機下指令看到的相同),`simosa-web` 只輸出上列白名單欄位 — 版本欄位只接受 1 至 4 段數字(其他內容輸出 `null`)、`message` 最多 4000 字 — 其餘一律不送。網址是固定的:請求的內容不影響讀哪個檔;`/_status/` 之下其他路徑(不分大小寫)一律 404、不落到網頁目錄;狀態檔不存在、超過 256 KiB、不是 JSON(含字串裡有不合法的 UTF-8)、`type` 不是 `statusResult` 時回 **200 與各欄 `null` 的同一格式**(「沒有發行版狀態」;log 記原因,不記內容)— 第三十輪審查 BC-rBC-2:教官站每次載入都取這個網址,回 404 的話瀏覽器每次都記一筆 console error(`Failed to load resource`),網頁端消不掉,端對端的 `PageErrors` 與任何檢查主控台的量測腳本都會把它算成錯誤。沒有 `--status-file` 時才是 404(這個伺服器不提供發行版狀態;量測腳本自己起 `simosa-web` 時請帶 `--status-file` 指到一個不存在的 `.json` 路徑);`--status-file` 必須是 `.json` 檔的路徑,而且不可放在網頁目錄內(那樣會被當成一般的靜態檔整份送出;`simosa-web` 啟動時以結束碼 2 拒絕)。回應 `Cache-Control: no-store`,只接受 GET / HEAD。
- **顯示的是哪一台**:同源取到的是提供該頁面的電腦(網址列的主機;「本機」)。船上筆電自己開的簡化教官站(`start-ship-laptop` 開啟的 `instructor.html?mode=simple`,或教官筆電的瀏覽器直接開 `http://<船上 IP>:8080/instructor.html`)顯示**船上筆電**的發行版,只有這一台。教官筆電以 `start-instructor-laptop` 開的教官站,頁面由教官筆電自己的 `simosa-web` 提供(`http://127.0.0.1:<埠>`;只有核心的 WebSocket 指向船上,`config\instructor-laptop.json` 的 `web`),同源取到的是**教官筆電**的 — 第三十一輪起另外向船上筆電取,頁首的主要資訊改成**船上筆電**實際執行的版本(下一點)。`simosa-pkg sync` 收到的 `statusResult` 雖然帶 `releaseVersion` / `releaseDowngrade`,仍只印現行更新包版本、參數集與環境,`fleet-versions.json` 也沒有這兩欄(沒有改;sync 要硬體金鑰,而且只有同步當下的值)。
- **教官筆電顯示船上筆電的版本**(第三十一輪;使用者 2026-11-23 的選擇):
  - **資料路徑**:教官站跨來源向船上筆電的 `simosa-web` 取同一個網址 `http://<船上 IP>:<埠>/_status/release.json`(即時;不必同步、不需要金鑰)。**什麼時候取**:教官站的核心連線變成「已連線」時(每次重新連上都重取 — 船上可能套用更新後重新啟動過);取不到時重試 5 次:連上後約 4、19、49、109、169 秒(`SHIP_RETRY_DELAYS_MS` = 4、15、30、60、60 秒;連同第一次最多 6 次),仍取不到就當成沒有,等下一次重新連上或重新整理。為什麼要到這麼久:`start-ship-laptop` 在核心就緒之後依序啟動操控台服務(1 秒)、等雷達(最多 15 秒)、取發行版狀態(`simosa-pkg status`,最多 20 秒),然後才啟動網頁伺服 — 最慢約 36 秒再加各程序的啟動時間;教官站已經開著時,只試到 19 秒的話慢的筆電上就一直看不到船上的版本(審查實測:網頁伺服比核心晚 25 秒就緒時,之後 60 秒都沒有再取)。不無限重試:船上是舊版時每次失敗瀏覽器都在主控台記錯誤(見下)。船上**連得到、但沒有發行版狀態**(回應是同一格式而各欄 `null`:船上從儲存庫啟動的開發模式,或船上啟動時 `simosa-pkg status` 失敗 / 逾時)是另一種結果(`SHIP_NO_STATUS`):不重試(狀態檔在船上下次啟動前不會出現;重新連上核心時會再取)。斷線期間保留上一次取到的內容;重新連上後取不到或沒有發行版狀態就清掉(不顯示可能已經過期的版本);較早的請求晚回來(其間斷線又連上、換了一台船)一律丟棄。
  - **船上網頁位址**(`lib/release-status.ts` 的 `shipWebBaseUrl`):教官站網址的 `?shipWeb=`(`http://<主機>[:埠]`,可以只寫 `主機[:埠]`;`0` / `off` = 不取)優先;沒有時由 `?ws=` 的主機推得 `http://<ws 主機>:8080` — 既有安裝的 `config\instructor-laptop.json`(`openPages` 只有 `ws` 參數)不必改。`?shipWeb=` **只寫埠號**(`8081` 或 `:8081`;設定檔的 `ports.shipWeb` 就是一個埠號,手動在網址補參數時容易照著寫)= `?ws=` 的主機配這個埠;埠號不在 1–65535、或主機只有數字的其他寫法(`8081/`、`http://8081`:URL 會把數字解析成 IP 位址 `0.0.31.145`)當成沒給。船上的網頁埠不是 8080 時,在教官筆電的 `config\instructor-laptop.json` 設 `ports.shipWeb`(預設 `null`),啟動腳本(`launch-common` 的 `Get-LaunchShipWebPort` / `Get-LaunchShipWebUrl` / `Add-LaunchShipWebParam`、`ship_web_port_cfg` / `ship_web_url` / `ship_web_page`)只在直連模式、只對 `instructor.html` 的頁面加上 `shipWeb=http://<船上 IP>:<埠>`(頁面帶 `#` 片段時加在片段之前)。`ports.shipWeb` 只接受整數(`8081` 或 `"8081"`,1–65535);`8081.0`、陣列、物件、`true`、文字都不是埠 — 兩套啟動腳本同樣警告「設定檔的 ports.shipWeb(…)不是 1–65535 的埠,略過」並改用預設的 8080。
  - **不發跨來源請求的情形**:`ws` 的主機是本機迴路位址(教官筆電的 `-Standalone`、開發模式、船上筆電自己開的教官站以 `localhost` 連核心)— 核心就在這台電腦;頁面不是由本機迴路位址提供(船上筆電的 `http://<船上 IP>:8080/…`:同源取到的已經是船上的;船上的 `simosa-web` 也不會給這種頁面 CORS 標頭);頁面不是 `http:`;重播檢視;駕駛台頁。
  - **CORS 只開給本機迴路位址的頁面**(`simosa-web` 的 `ReleaseStatusFile.IsLoopbackPageOrigin`):`/_status/release.json` 的 200 回應,在請求的 `Origin` **完全等於** `http://127.0.0.1[:埠]` 或 `http://localhost[:埠]`(小寫、埠 1–65535、沒有結尾斜線)時加 `Access-Control-Allow-Origin: <該 Origin>` 與 `Vary: Origin`;其他來源(別的主機、`https`、`null`、大小寫不同、`127.0.0.1.evil.example` / `localhost.evil.example` 這類主機名、重複的 `Origin` 標頭)與沒有 `Origin` 的請求(同源、curl)沒有任何 CORS 標頭。**不接受 `[::1]`**:`simosa-web` 只聽 IPv4、啟動腳本開的是 `http://127.0.0.1:<埠>`,沒有頁面會是這個來源,允許清單不放用不到的項目。只有這個網址有 CORS 標頭:靜態檔、`/_status/` 之下其他路徑(404)、405、沒有 `--status-file` 的 404 都沒有;不帶認證(沒有 `Access-Control-Allow-Credentials`)。意義:教官筆電上只有它自己的 `simosa-web` 提供的頁面讀得到船上的發行版狀態,其他網站的頁面(教官筆電在岸上會連網)讀不到;送出的內容本來就只有白名單欄位(版本與降版說明)。伺服器 log 記允許的 Origin(形式固定),不允許的只記「不是本機迴路位址的頁面」、不記內容。
  - **沒有 preflight、沒有 Private Network Access 的問題**(Chromium 153 實測;`build/tmp/r31/a/probes/pna-probe.mjs`):教官站的請求是 `fetch(url, { cache: 'no-store', credentials: 'omit' })`,沒有自訂標頭 — 簡單請求,瀏覽器不送 `OPTIONS`(船上的伺服器 log 只有一行 `GET`;`simosa-web` 對 `OPTIONS` 照舊回 405)。Chromium 的 Private / Local Network Access 管的是「較公開的位址空間 → 較私有的」(public → local / loopback、local → loopback);本機迴路位址的頁面是最私有的一級,向私有位址(192.168.77.x)或其他位址請求都不受限:迴路頁面 → `localhost`、→ 非迴路位址、→ 以 `--ip-address-space-overrides` 歸類成 `local`(私有位址)的位址,三種都直接成功、沒有 preflight、主控台沒有訊息(反方向 — 非迴路位址的頁面 → `localhost` — 會被擋:「the resource is in more-private address space」,與本功能無關)。**尚未在 Windows 的 Edge 與真實的兩台筆電(192.168.77.x)上確認**(Edge 與 Chromium 同一套規則;企業原則若另外限制 `http://127.0.0.1` 的頁面連私有位址,核心的 WebSocket 也會連不上)。
  - **畫面**(版面量測見 `docs/design/web-layout.md` 第 2.2 節):
    - 兩台實際執行的版本相同(各取「軟體更新包版次,沒有或有降版時取發行版」比較;缺的段當 0)、都沒有降版 → 小字「**船上 <版本>**」(不是按鈕)。只寫版本,不寫「發行版 / 軟體更新」(在 title:船上實際執行的是什麼、本機相同、兩邊的來源與取得時間):「船上 2026.11.31」(約 96 px)比只有本機時的「軟體更新 2026.11.31」(約 120 px)窄,頁首不會因此多折一行。
    - 有要提醒的事 → 警示色的徽章鈕「**船上 <版本> · <標記>**」(與既有的降版徽章同樣的樣式、略窄),標記取最前面成立的一項:`兩台降版` > `船上降版` > `本機降版` > `版本不同`。展開的說明(浮貼、不佔版面高度)依序:**船上筆電**(有降版:「船上筆電 · 降版提醒」、重點、處理,以及**預設收合**的「完整訊息(船上筆電的 simosa-pkg status)」— 點標題列才展開;「status 顯示的軟體版次不是實際執行的程式」那一句在完整訊息裡。沒有降版:實際執行的版本)→ **這台電腦(本機)** 同樣的寫法(沒有發行版狀態時寫明)→ **版本不同**(哪一台較舊;處理:更新較舊的那一台 — 以較新的安裝程式重新安裝,或套用軟體更新包,同步步驟見第 6 節;這是提醒,不擋操作。**較舊的那一台同時有降版提醒時**,處理改成「較舊的是有降版提醒的那一台,依上面降版提醒的處理;刻意回到舊版時兩台版本不同是預期的,需要兩台同一版就把另一台也換成那一版」— 否則上一段說「刻意回到舊版時不必處理」、這一段又叫人更新它)→ 兩邊的來源與取得時間、「收合」(這一列貼在說明的底邊,內容要捲動時也看得到)。
    - **說明不蓋住執行控制**:完整教官站把執行控制第一個鈕(「凍結」)的上緣交給元件(`detailLimit`),說明的最大高度縮到底邊在它之上 4 px(不小於 120 px、不大於 320 px 與視窗高度的 60%),放不下的內容在說明裡捲動 — 展開時「凍結 / 恢復 / 重設 / 結束練習」一定點得到(1093×614 的頁首下方到這一列只有 264 px;兩台的完整訊息都攤開時說明原本高 320 px,蓋住這四個鈕)。簡化模式沒有執行控制面板,不限制(說明蓋住 BRM 標記鈕時點旁邊就收合,與第三十輪相同)。各狀態 × 視窗的高度見 `docs/design/web-layout.md` 第 2.2 節。
    - 本機沒有發行版狀態(教官筆電是開發模式、或啟動時沒有取到)→ 只寫船上的(「船上 <版本>」或「船上 <版本> · 船上降版」),無從比較版本。
    - 取不到船上的狀態 → 畫面與只有本機時完全相同,不顯示錯誤;title 多一句「船上筆電的版本:未取得(連上船上的核心後向 <位址> 取;船上是較舊的版本或網頁伺服沒有啟動時取不到)」,展開的說明(本機有降版時)只加短句「船上筆電的版本:未取得」(長句在 1093×614 讓說明多折兩行、蓋住航跡圖的鈕)。
    - 船上連得到、但沒有發行版狀態 → 畫面同上;title 寫「船上筆電沒有發行版狀態(已向 <位址> 取到回應:船上是開發模式,或船上啟動時沒有取到 simosa-pkg status)」,展開的說明加「船上筆電沒有發行版狀態」— 不寫「未取得 / 船上是較舊的版本」(那時船上是同一版、網頁伺服也在執行)。主控台沒有錯誤。
  - **船上是舊版時的瀏覽器主控台**:船上的 `simosa-web` 是第三十輪(有 `/_status/`、沒有 CORS 標頭)或更早(沒有這個網址,404)時,跨來源請求被瀏覽器擋下,每次失敗在主控台留下兩筆(`Access to fetch at … has been blocked by CORS policy: No 'Access-Control-Allow-Origin' header…` 與 `Failed to load resource: net::ERR_FAILED`;連不上時是一筆 `net::ERR_CONNECTION_REFUSED`),含重試最多六次(連上核心後約 0、4、19、49、109、169 秒;每次重新連上核心重新算)。這是瀏覽器記的,網頁端消不掉;畫面不受影響(端對端有這三種情形)。**正常支援的組態**(開發模式、`-Standalone`、船上與教官筆電都是這一版)載入教官站時主控台沒有錯誤。把船上筆電更新到這一版(安裝程式,或含 `app\web-server\` 的軟體更新包)之後就沒有。
- **什麼時候更新**:只在啟動時取一次。套用更新包或重新安裝之後要重新啟動,頁首才會反映(套用軟體更新包本來就要先 `stop-all -KeepSync` 再重新啟動):套用軟體更新包後「發行版 2026.12.1」變成「軟體更新 2026.12.2」;以新的安裝程式重新安裝後回到「發行版 <新版本>」;降版提醒在套用新的軟體更新包或以夠新的安裝程式重新安裝後消失。套用圖資、情境、參數更新包不改頁首的版本。
- **版本落差**:`tools\updater\` 不被更新包替換。較舊的 `simosa-pkg`(沒有 `releaseVersion` 欄位)配上軟體更新包帶來的新啟動腳本與網頁時,狀態檔沒有發行版本,`simosa-web` 回 `releaseVersion: null`,教官站不顯示;反過來(舊的啟動腳本沒有 `--status-file`)是 404,也不顯示(主控台有一筆 404)。兩台之間:教官筆電是這一版、船上筆電是第三十輪或更早 → 教官筆電取不到船上的(見上一點「船上是舊版時的瀏覽器主控台」);教官筆電是舊版、船上是這一版 → 教官站沒有這個功能,照舊只顯示教官筆電的。
- 狀態檔在執行期資料位置 `build/`(`InstalledPaths.RuntimeData`),安裝模式核對不把它列為多出的檔案;解除安裝不刪(只刪 `build\logs`、`build\tmp`),下一次啟動會覆寫或刪除。
- 測試:`tools/launch/tests/launch-tests.sh` 的 `test_web_status_file`(選項、白名單、路徑、各種不合格的狀態檔 → 各欄 `null`、HEAD 以原始 TCP 確認沒有本體、字首不分大小寫)、`test_release_status_write`(開發模式、成功、失敗、逾時、不是 JSON)、`test_release_status_launch`(以真實的 `simosa-pkg` 在「安裝版」資料夾套用軟體更新包 → `softwareVersion`;再做出降版 → 啟動腳本 → `simosa-web` → 降版資訊;`verify-install` 通過);`launch-common.tests.ps1` 的「發行版狀態」(pwsh);網頁 `tests/release-status.test.ts`;端對端 `02-instructor.spec.ts` 的「發行版本與降版提醒…」(端對端的 `simosa-web` 也帶 `--status-file`,狀態檔是「發行版 + 較新的軟體更新包」:全部版面案例都在頁首有「軟體更新 <版次>」的狀態下量測;該案例另把狀態檔暫時移走,確認沒有狀態檔時不顯示、沒有 console error);`tools/ci/windows-e2e.ps1` 的 launch / instructor 階段(Windows PowerShell 5.1 實跑:狀態檔的版本等於安裝的版本、`/_status/release.json` 的欄位)。
  第三十一輪(船上筆電的版本):`launch-tests.sh` 的 `test_web_status_cors`(真實的 `simosa-web`:6 種允許的 Origin、37 種不允許的、沒有 Origin、標頭名稱大小寫與前後空白、重複 / 空的 Origin、HEAD 與 GET 相同、靜態檔 / 其他路徑 / POST / OPTIONS / 沒有 `--status-file` 都沒有 CORS 標頭、log 不記不允許的 Origin、狀態檔不存在時照樣帶 CORS 標頭)與 `test_instructor_ship_web`(`ship_web_url` / `ship_web_page` 的規則、啟動腳本的網址與提示、`ports.shipWeb` 不是埠時警告);`launch-common.tests.ps1` 的「船上筆電的網頁位址…」;網頁 `tests/release-status.test.ts`(位址推導、同一台不發請求、版本比較、各種徽章與展開的說明、取不到、連上才取與重試、教官站把位址交給元件);端對端 `02-instructor.spec.ts` 的「教官筆電的教官站顯示船上筆電的版本…」(`global-setup` 多啟動一個 `simosa-web` 當作「船上」;頁面在 `127.0.0.1`、船上位址用 `localhost` — 兩者在瀏覽器是不同來源,所以是真的跨來源請求與真的 CORS 標頭:版本相同、版本不同、船上降版、連不上 / 舊版沒有 CORS 標頭 / 404 三種取不到、同一台不發請求、伺服器 log 沒有 `OPTIONS`、版面判定);`windows-e2e.ps1` 的 launch 階段以 `Invoke-WebRequest -Headers @{ Origin = … }` 確認 CORS 標頭、instructor 階段確認 `-Standalone` 不帶 `shipWeb`。突變檢查 17 項(單元 / 啟動腳本測試層 15 項:Origin 允許清單 5 項、同一台不發請求、版本比較 2 項、船上降版的顯示 2 項、取不到時不顯示錯誤 2 項、連上才取、啟動腳本 2 項;端對端層 2 項:同一台不發請求、取不到時不顯示錯誤)都有測試失敗。
  審查後修正(第三十一輪):`test_web_status_cors` 補「名稱以 Origin 開頭的其他標頭(`Origin-Agent-Cluster`)不算 Origin」與「空行之後的本體裡的 `Origin:` 不算」;`test_instructor_ship_web` 補 `--standalone` 與 `standalone.enabled` 的**實際啟動**(不加參數、不提示)、`--help` 只印檔頭註解、頁面帶 `#` 片段、`ports.shipWeb` 是 `8081.0` / 陣列 / 物件 / `true`;`launch-common.tests.ps1` 補 `Get-LaunchShipWebPort`、片段、提示在 `if (-not $standalone)` 區塊內,以及(Linux 的 pwsh)實際啟動 `start-instructor-laptop.ps1` 的直連模式與 `-Standalone`;網頁單元測試補只寫埠號、沒有發行版狀態(不重試、說明不同)、重試時刻、較早的請求晚回來要丟棄、換船時上一台的內容不留、較舊的那一台有降版時的處理、完整訊息預設收合、`detailLimit`;端對端補船上沒有發行版狀態、ship-web.log 改以 `expect.poll` 讀(伺服器先送回應、後寫 log)、新案例「既有安裝的教官站網址(沒有 shipWeb 參數)…」(另開帶 `--host-resolver-rules` 的 Chromium:`ws://192.168.77.11:<核心埠>` 與 `http://192.168.77.11:8080` 對到本機;1093×614 展開船上降版的說明時執行控制不被蓋住、真實點擊「凍結」有作用、「收合」不必捲動)。審查後修正的突變檢查 43 項(就地改檔 → 跑對應的測試 → 還原;`build/tmp/r31/fix/mut/`):啟動腳本 11 項(10 項有測試失敗;1 項是等價突變 — `.ps1` 在 `-Standalone` 時算出船上網頁位址但不使用)、`simosa-web` 的 Origin 解析 2 項(審查時存活的那兩項)、網頁單元測試層 22 項(世代檢查、換船、重試時刻、沒有發行版狀態、只寫埠號、數字主機、處理的文字、完整訊息預設收合、`detailLimit` 等)、端對端層 8 項(教官站沒有把 `detailLimit` 交給元件、完整訊息預設展開、沒有發行版狀態也重試、最後一列不貼底、推導的埠、沒有發行版狀態當成取不到、處理的文字、ship-web.log 一次性讀取 + 以 strace 把 log 的寫入延後 300 ms)都有測試失敗。
- 尚未確認 / 沒有做:Windows 實機上的降版畫面(CI 沒有較舊的安裝程式可以做出降版,只驗到「沒有降版」);**教官筆電顯示船上版本的整條路徑沒有在兩台真實的筆電上跑過**(192.168.77.x 直連、Windows 的 Edge、船上的防火牆 8080:容器裡的「船上」是同一台機器上的第二個 `simosa-web`,私有位址的部分以 Chromium 的 `--ip-address-space-overrides` 模擬);Windows CI 只驗到 CORS 標頭與 `-Standalone` 的網址,沒有跑直連模式(執行器只有一台機器),`windows-e2e.ps1` 新增的檢查也還沒有在 Windows 執行器跑過;`simosa-pkg sync` 不印出、`fleet-versions.json` 不存船上的 `releaseVersion` / `releaseDowngrade`(沒有做;要不要補由使用者決定)。

## 6. 操作步驟

結束碼:0 成功、1 失敗或拒絕(含參數集/情境 YAML 語法或型別錯誤)、2 用法錯誤。布林旗標(`--allow-unverified`、`--enroll-device`、`--json`、`--installed`、`--force`、`--overwrite`、`--all`、`--pem`、`--no-pull`、`--no-report`)不取下一個參數為值,可寫在位置參數之前;要關閉時寫 `--flag=false`。

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

### 6.6 程式檔完整性核對(船上與教官筆電)
安裝前(剛複製好的發行版):`simosa-pkg release verify --dir <發行目錄>`。安裝後、使用中、套用或 rollback 更新包之後、懷疑隨身碟上的檔案損毀時:`simosa-pkg verify-install`(= `release verify --dir <安裝根目錄> --installed`;第 5.1 節)。結束碼 0 = 沒有錯誤(可能有提醒),1 = 有錯誤並列出下一步。

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
  "environment": { "os": "Microsoft Windows 10.0.26100", "dotnet": ".NET 8.0.11", "webview2": "130.0.2849.56", "collectedUtc": "2026-10-17T05:30:00Z" },
  "warnings": ["1 個紀錄包已累積超過 2 個訪船間隔仍未取得教官筆電的已備份回報…"],
  "releaseVersion": "2026.10.0"
}
```
- `releaseVersion`:安裝根目錄 `manifest.json` 的發行版本(沒有或讀不到時省略)。`releaseDowngrade`:降版時才有(第 5.1 節「重新安裝」的降版判斷,與 `verify-install --json` 的 `downgrade` 同一個物件),例如 `{ "releaseVersion": "2026.11.1", "basisVersion": "2026.12.2", "previousReleaseVersion": "2026.12.1", "softwareVersion": "2026.12.2", "highestVersion": "2026.12.2", "softwareStale": true, "message": "降版:目前的發行版 2026.11.1 比…舊 — …" }`(剛重新安裝時 `rollbackAvailable` 通常是 false);`softwareStale: true` 表示 `components.software.version` 不是實際執行的程式,教官站與船上版次顯示應以 `releaseVersion` 為準並標示(教官站頁首已顯示發行版本(或實際執行的軟體更新包版次)與降版提醒:啟動腳本存下 `status --json`,`simosa-web` 以 `/_status/release.json` 只提供這兩項與 `components.software.version`,見第 5.1 節末「教官站頁首的發行版本與降版提醒」)。這個物件依覆蓋清單保存的降版證據產生:之後套用非軟體更新包也還在,直到套用新的軟體更新包或以夠新的安裝程式重新安裝(第 5.1 節「重新安裝」的**降版**)。兩個欄位都是本版新增,舊版船端沒有時讀成 `null`。
- `environment`(船上筆電環境版次;規劃書第 5.5 節兩船版次清單,`Common/EnvironmentInfo.cs`):`os` = `RuntimeInformation.OSDescription`、`dotnet` = `RuntimeInformation.FrameworkDescription`、`webview2` = Evergreen WebView2 Runtime 版本(Windows 登錄檔 `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` 的 `pv`,依序後援同路徑不含 `WOW6432Node` 與 `HKCU`;非 Windows、未安裝、`pv` 為 0.0.0.0 或只有固定版本散布時為 `null`,JSON 略過該鍵)、`collectedUtc` = 船端收集時刻。純資訊欄位不簽章;舊版船端沒有此欄位時教官端讀成 `null`。
- 練習啟動畫面與講評報告顯示 `parameterSet.version` 與 `smsDocuments`(第 9.1 節「船上顯示現行參數集版次」);參數集版次與紀錄標頭 `header.parameterSet.version` 不同時標示「參數集版次已變更」。
- **UpdateApplyResult**(`updateResult.result`):`{ ok, packageId, version, kind, summary, signer, signerRole, components[], errors[], warnings[], rolledBack, message }`。
- **CleanupResult**(`cleanupResult.result`):`{ ok, reportId, signer, deleted[], deletedFiles[], retained[], mismatched[], unknown[], failed[], warnings[], errors[] }`(`failed`:無法刪除的檔案「紀錄包 路徑:原因」,該紀錄包保留待下次重試)。
- **fleet-versions.json**(教官端兩船版次清單):`{ "ships": { "FSB1": { shipId, deviceId, version, packageId, components, parameterSet, environment, updatedUtc, via } } }`;`environment` 即上面 ShipStatus 的同名物件,直連 `status` 與隨身碟 `status.json` 兩條途徑都寫入,每次同步整筆取代該船項目(另一船的項目不受影響);唯船端未回報 `environment`(舊版船端、舊格式的 `status.json`)時沿用上次已知值,陳舊與否看 `collectedUtc`。`simosa-pkg store list` 的「版次」行同列環境摘要。訪船時核對 Windows/WebView2/.NET 版次不必再手動登錄;Windows 累積更新(KB)清單與 Windows 11 Pro 功能版本到期日仍為手動登錄(安裝文件第 2.1 節)。
- 練習紀錄標頭(SimCore 已實作;教官站 `sessionInfo` 指令):`participants: [{ "name": "王大副", "role": "oow", "rank"?, "certificateNo"? }]`、`restHoursConfirmation: { "confirmedBy": "船長", "confirmedAtUtc": "…", "compliant": true, "note"? }`、`course?`;紀錄已開啟後才送達者寫成最後一行 `{"kind":"session", …}`,`RecordInspector` header 有就用 header、否則取最後一行 session(`sessionInfoSource`),紀錄包據此彙總訓練時數(`participants[].rank/certificateNo` 一併帶出);舊/外部紀錄的字串陣列 participants 兩邊(`RecordInspector`、核心 `RecordReader`)皆讀成 `{name, role: null}`。footer 已含 `closedUtc`(關檔時的牆鐘 UTC),紀錄包以它為練習結束時間(舊紀錄無此欄時仍以檔案最後修改時間估計)。

## 8. 金鑰保管程序要點(納入公司網路風險管理程序,TMSA 第 13 要素)

1. 主金鑰由訓練負責人保管於 PIN 保護的硬體金鑰(智慧卡/PIV),私鑰於硬體內產生且不可匯出;**不得以檔案存於教官筆電**。PEM 檔金鑰只用於開發與測試,不得放入儲存庫或系統映像。
2. 備援金鑰由 DPA 或海務主管保管(另一把硬體金鑰,與主金鑰分處保管);僅用於簽章信任更新包或主金鑰無法使用時的緊急派送。
3. 兩把公鑰預載於船上筆電系統映像、備用 SSD 與備援筆電;公鑰指紋(金鑰 ID 前 16 字元)列入資產清單,船上套用前畫面顯示簽章者供核對。
4. PIN 連續錯誤鎖定依硬體預設;PIN 不得記錄於筆電或隨行文件。
5. 遺失、失竊、交接:立即通報 DPA → 以備援金鑰簽章信任更新包移除舊公鑰、加入新公鑰 → 下次訪船(或隨身碟途徑)派送 → 核對兩船 `status` 的信任金鑰清單 → 記入稽核與程序紀錄。無法派送時由訪船船長以管理者帳號 `trust add/remove` 手動更換。
6. 裝置金鑰由 DPAPI 綁定該台船上筆電;更換筆電或重灌時重新 `device init` 並重新登錄到教官筆電(舊裝置公鑰自 `devices/` 移除)。
7. 教官筆電紀錄庫為主本:每次訪船後 `store backup` 至加密外接碟(留存公司)或公司檔案共享;備份完成才發出已備份回報。

## 9. 測試(`tests/Tools.Packaging.Tests`,Linux 以 PEM 金鑰執行)

簽章/驗證(竄改檔案、多餘檔案、竄改 manifest、重複 manifest 項目、未信任金鑰、降版與重播、船舶限定、路徑穿越、`remove` 越權(信任清單、裝置金鑰、他種類路徑,含反斜線/重複斜線/大小寫變形)、`packageId` 白名單)、金鑰輪替(備援金鑰更換主金鑰、主金鑰簽章的信任包被拒、移除最後一把備援被拒、覆寫現有金鑰檔被拒、沿用同名檔更換金鑰、trust 包不可 rollback)、影響分析(列出引用變更鍵的情境與評估規則、共用鍵影響多情境、被移除鍵、情境錯誤)、驗證把關(未驗證拒絕、舊參數集的驗證不算數、--force 記錄且船上拒絕)、套用與 rollback、中途失敗完整回復、提交步驟失敗(回報已套用加警告、rollback 不回到錯誤版本)、稽核內容、紀錄包往返與訓練時數(牆鐘:倍速、凍結)、竄改/他台裝置簽章/重複 manifest 被拒、練習進行中不打包、已備份回報後清理(只刪確認者;未信任/竄改回報被拒;單檔刪除失敗保留重試)、保留上限警告(同一次訪船重連不另計)、TCP 本機迴路端對端(推送、拉取、回報清理、錯誤 nonce、未信任教官金鑰、未登錄裝置、被拒的更新包不中斷 session、未驗證連線灌稽核)、隨身碟往返(含清理通知回寫、偽造通知被拒)、CLI(冒煙、YAML 錯誤回 1、布林旗標在位置參數前)、安裝模式核對(`InstalledVerifyTests`:執行期資料不算多出、設定檔修改/不在為提醒、程式或資料檔差一個位元組/缺少與 `app/` 多出檔案為錯誤、software/scenarios/parameters/trust/mixed 更新包套用後以覆蓋清單核對、刪除後又寫入的順序、rollback 與狀態檔寫入失敗後覆蓋清單還原、清單遺失與不一致列為無法核對、舊版套用後才安裝新發行版者以發行清單核對、清單內容損毀不使套用或核對中止、以安裝程式重新安裝後(程式目錄清空重裝)不需手動清理即通過、手動覆蓋殘留的舊程式檔列為多出、`data/` 之下更新包新增的檔案在重新安裝後仍以覆蓋清單核對、以同一個發行版重新安裝仍報「被還原」、程式目錄內的目錄連結為錯誤而其他位置為提醒、套用中斷後原包重試不留下無法核對、「下一步」的版本基準與更新包說明、清單列有安裝程式不安裝的檔案時的說明、路徑分類與 `InstallLayout`/安裝程式 `[Dirs]`、`[InstallDelete]` 對照、Windows CI 實際的安裝根目錄內容不產生錯誤或提醒、CLI 結束碼/`--json`/每類筆數上限/`--max-items` 先解析、一般模式行為不變加提示、降版提醒在之後套用圖資與參數更新包後仍在而套用新的軟體更新包才消失(rollback 該包又出現)、以不低於比較基準的發行版重新安裝才清除、覆蓋清單刪除或損毀時以 `previous.json` 判斷仍提醒、只刪除檔案的軟體更新包與升級 → 套用 → rollback 不報降版、套用時 `manifest.json` 讀不到的警告與「讀不到 / 不在」分開記錄、混合錯誤時「見下一步(N)」指到的項目;降版的比較基準只計被安裝覆蓋的軟體更新包 — 2026.12.1 + 圖資 → 2026.11.1 → 參數 → 2026.12.1 不報降版、2026.11.1 + 較高版本的圖資 → 2026.12.1 升級不報降版、被覆蓋的參數包不提高真降版的比較基準;套用時讀不到發行清單而覆蓋清單仍有最後已知身分時,之後以較舊的安裝程式重新安裝 rollback 會拒絕、只修好清單則放行;只有「安裝程式不安裝的檔案」與更新包新增的檔案時「下一步」的順序);發行清單(`ReleaseTests`:目錄含 `*.pdb` 時 `release manifest` 失敗、排除樣式與 `.iss` 的 `Excludes` 相同、`build-release.*` 在 Godot 匯出之後與 pack 之前刪除 `*.pdb`、清單的 `files` 為 null 或含 null 項目時一般模式回報核對失敗而不是未處理例外)。測試暫存目錄在 `build/test-tmp/packaging/`,結束即刪除。

## 10. 尚未涵蓋

- 傳輸加密:直連為實體隔離的點對點網路線,內容均已簽章;紀錄包內容(個資)若需傳輸加密,可於下一版加上以船上裝置公鑰/教官公鑰的 ECDH + AES-GCM 包裝。
- 教官站「直連模式」的 Wi-Fi/藍牙停用檢查(第 14 章「教官筆電 Wi-Fi 開啟時直連模式拒絕同步」)由教官站在呼叫 `sync` 前執行。
- 錄影檔已隨紀錄包回傳(2026-11-01;`screen-recording.md`),單段上限 `mediaMaxMb`;超限者留在船上以外接碟人工搬移,尚無串流/分包傳輸。
- 選配的船舶電子郵件途徑沿用隨身碟格式(單一簽章檔),未另行實作。
- 永久撤銷清單:目前以「trust 包不可 rollback」防止被撤銷的金鑰回到信任清單;管理者手動 `trust add` 仍可加回(屬刻意操作,記入稽核)。
- 練習結束的牆鐘時間待 SimCore 在 footer 寫入 `closedUtc`;在此之前以紀錄檔最後修改時間估計(`endEstimated`)。
- 直連為單一連線服務:未驗證者持續佔用連線仍可阻擋同步(不影響稽核與磁碟);實體上以網路線直連、`listen` 綁定直連介面降低暴露。
- 安裝模式核對(第 5.1 節)是完整性檢查,不防蓄意竄改:`manifest.json` 與 `state/installed-files.json` 都不簽章,能改程式檔的人也能改清單。以安裝程式重新安裝 / 升級時,安裝程式先清空 `app\` 與 `tools\updater\`(`[InstallDelete]`),舊版殘留的程式檔不會留下,核對即通過;以壓縮檔或手動複製覆蓋發行版時舊檔不會清除,會列為「程式目錄內多出的檔案」錯誤,依清單刪除。以**同一個**發行版重新安裝已套用過更新包的筆電偵測不到(`manifest.json` 相同),會報「更新包的變更被還原」,要用更新的安裝程式或更新包才收斂。重新安裝之後 `state/previous`(上一版元件)仍是重新安裝之前的內容,`rollback` 因此拒絕執行(以 `previous.json` 或覆蓋清單記錄的 `manifest.json` 身分判斷,只改編碼不算;下一次套用更新包後 `state/previous` 才又是可回復的上一版)。沒有覆蓋清單、`previous.json` 也是舊格式的舊安裝(由舊版 simosa-pkg 套用)改以與核對相同的時間規則判斷(更新包的套用時間早於目前發行版的建置時間 → 拒絕):船上時鐘超前、或重新安裝的發行版在套用之前就已建置(例如以較舊的安裝程式重新安裝)時看不出來,仍會放行;完全不依賴時鐘的作法是讓安裝程式的 `[InstallDelete]` 一併刪除 `state\previous\previous.json`(`check-iss.py` 的白名單與測試要跟著改),尚未採用。以較舊的安裝程式重新安裝(降版)只列「降版」提醒、不算錯誤(以舊版安裝程式回到舊版是允許的作法),`status` 同時警告其 `software` 版次不是實際執行的程式;提醒依覆蓋清單的 `downgrade` 證據保留到套用新的軟體更新包或以夠新的安裝程式重新安裝為止,但降版到本功能之前的發行版(舊版 simosa-pkg 不保存證據)、或降版之後又套用過更新包而覆蓋清單之後才遺失時保存不到;教官站與船上版次顯示尚未讀取新增的 `releaseVersion` / `releaseDowngrade`。套用時 `manifest.json` 讀不到、之後又以**建置於那次套用之前**的較舊安裝程式重新安裝時,覆蓋清單仍有更早一次記下的發行版身分(最後已知身分)就照常看得出來(rollback 拒絕、核對以新的發行清單為準);覆蓋清單也沒有身分(從第一次套用起就讀不到、之後也沒有讀得到的套用,或覆蓋清單之後遺失;先有一次讀得到的套用時,之後連續讀不到也依最後已知身分判斷)時只能依建置時間,看不出重新安裝(核對偏向報 `mismatch`,rollback 會放行並附警告)。最後已知身分本身也可能過時(在它之後、讀不到的那次套用之前已重新安裝過):之後只修好清單時 rollback 會誤擋(較安全),核對把那次更新包換過的程式檔當成被安裝覆蓋而報 `mismatch`;又裝回最後已知的那個發行版時 rollback 會放行(附警告)。降版的比較基準不計圖資、情境、參數更新包:這些更新包換過的發行清單檔案被較舊的安裝程式蓋回時,只有「被安裝覆蓋」的 `state` 提醒(下一次套用更新包之後就不再出現),`status` 的該元件版次仍是那個更新包。覆蓋清單遺失後的「無法核對」提醒要等之後的更新包涵蓋該前綴才會消失。Windows 實機上的行為(安裝程式安裝後、啟動過、套用與 rollback 後皆通過;執行時 `app\` 內不會自行產生檔案;`[InstallDelete]` 在重新安裝時確實清空兩個程式目錄、執行中的程式被鎖住時的表現;含 Godot 視景匯出的發行版安裝後通過)尚待 CI 與實機確認。
