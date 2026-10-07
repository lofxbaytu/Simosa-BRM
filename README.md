# Simosa BRM — FS Bitumen No.1 / No.2 駕駛台資源管理操船模擬器

中塑海運兩艘瀝青船(FS Bitumen No.1 中塑油品壹號、FS Bitumen No.2 中塑油品貳號)專屬的 Windows 桌面版駕駛台操船模擬器,用於符合 STCW、IMO 示範課程 1.22/1.39、OCIMF TMSA 3 / SIRE 2.0 與航港局「領導統御與駕駛臺資源管理」課程要求的 BRM 訓練。

目前狀態:**規劃階段**(尚未進入實作)。

- 規劃書:[docs/Simosa-BRM-操船模擬器規劃書.md](docs/Simosa-BRM-操船模擬器規劃書.md)
- 預定開發資料夾:`D:\Simosa BRM`(結構見規劃書第 11 章)

規劃書摘要:
- 模擬核心(C#/.NET,MMG 標準船舶運動模型,以兩船試俥資料校正)與顯示端(3D 外景、OpenBridge 風格儀器、雷達/ARPA、ECDIS、教官站、講評站)分離,透過區域網路與 NMEA 0183 / IEC 61162-450 連接。
- 22 個對應法規條文的 BRM 練習情境、行為標記評估與自動量測指標、含語音的完整重播講評。
- 分階段認證策略:內部訓練工具 → DNV Class C/S 或 ClassNK(選擇)→ Class B 部分任務型駕駛台(選擇)。
- 2 至 3 名開發者約 12 至 18 個月;關鍵路徑為向船公司索取操縱手冊與試俥資料,以及台灣 ENC 授權。
