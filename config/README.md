# config/ — 安裝設定範例(不含任何金鑰)

| 檔案 | 用途 | 讀取者 |
|---|---|---|
| `ship-laptop.json` | 船上筆電:船舶/裝置 ID、直連固定 IP、各服務埠、預設練習、雷達、網頁、視景、同步、記錄目錄 | `tools/launch/start-ship-laptop.ps1`(`.sh`) |
| `instructor-laptop.json` | 教官筆電:直連 IP、兩船 IP、教官站/雷達鏡像頁、離船單機模式、同步說明 | `tools/launch/start-instructor-laptop.ps1`(`.sh`) |
| `packaging.ship.example.json` | 船上 `config/packaging.json`(simosa-pkg serve/apply/records) | `simosa-pkg` |
| `packaging.instructor.example.json` | 教官 `config/packaging.json`(simosa-pkg sync/store/export/import) | `simosa-pkg` |

發行版(`tools/build-release.*`)把本資料夾複製到 `<發行目錄>/config/`;安裝步驟見 `docs/ops/安裝與啟動.md`。
金鑰相關檔案(`config/trusted-keys/*.pem`、`config/device/`)只在安裝時於該機產生或放入,**不得放進儲存庫或發行版**;開發用 PEM 金鑰由 `build-release` 放在 `build/release/dev-keys/`(僅供測試)。
路徑一律相對安裝根目錄(`D:\Simosa BRM`;規劃書第 11.2 節),所有輸出留在根目錄內(`build/logs`、`build/records`、`build/visual`)。
