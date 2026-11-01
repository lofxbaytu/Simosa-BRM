# 內建 VHF 角色扮演頻道 — 設計與限制

> 規劃書依據:第 7.2 節儀器表列「VHF/DSC」、第 9.1 節教官站「角色扮演(VTS/引水人/拖船/他船)」、第 8.2 節行為標記「通訊」、第 14 章教官列;
> 需求追溯表 R1「VHF/DSC」、R6「角色扮演 VHF」;STCW-DNV 對照「VHF/DSC、內線、PA」「與學員通訊」。
> 取代原規劃的 Mumble(規劃書第 5.2 節選配;船上完全離線、不另裝語音伺服器)。
> 程式:`src/SimCore.Host/VhfRelayServer.cs`、`src/SimCore.Host/Program.cs`(接線)、`src/SimCore/Recording/RecordLines.cs`(`RecordVhf`)、
> `src/Instruments.Web/src/lib/vhf.ts`(協定與狀態機)、`lib/vhf-client.ts`(WebSocket/MediaRecorder/播放)、`components/vhf-panel.ts`(`<brm-vhf>`)、
> `lib/record-replay.ts`、`report/debrief-report.ts`、`report/training-record.ts`(講評);協定 `src/Contracts/vhf-protocol.md`;
> 測試 `tests/SimCore.Tests/VhfRelayTests.cs`、`src/Instruments.Web/tests/vhf.test.ts`、`vhf-client.test.ts`、`vhf-panel.test.ts`、`vhf-record.test.ts`。

## 1. 目標與範圍

學員站(操船台)與教官站之間以本機網路(船上筆電 ↔ 教官筆電直連,或同一台筆電)模擬 VHF 通話,不需網際網路與額外伺服器:

| 功能 | 做法 |
|---|---|
| 頻道選擇 | 旋鈕 16、12、13、14、06、08、10、67、68、69、71、72、73、77(規劃書第 7.2 節);同頻道才聽得到 |
| PTT 按住說話 | 滑鼠/觸控按住、或空白鍵(焦點不在輸入框時);瀏覽器 `MediaRecorder` 以 `audio/webm;codecs=opus` 每 100 ms 一片送出 |
| 接收端自動播放 | `MediaSource` 逐片段 `appendBuffer`(Chromium/WebView2);不支援時整段通話結束後以 `Audio` 播 Blob;都不行則只收文字 |
| 同時發話互相壓制 | 伺服器每頻道一個佔用者:後發者收到 `busy`「頻道佔用」,其音訊不轉送、不記錄(真實 VHF 的互相干擾簡化為「先到先得」) |
| 文字訊息 | 無麥克風時的備援;教官扮演 VTS/引水人/他船打字即可;同頻道轉送 |
| DSC 簡化 | DISTRESS / URGENCY 兩鍵(面板二次確認),送所有站的文字警示(標示頻道 70);不做真實 DSC 編碼、無 MMSI/位置格式 |
| 雙守聽 | 教官站可同時守聽 16 + 工作頻道(`watch`);學員站單頻道 |
| 紀錄 | 每次通話結束(站、頻道、秒數)、文字、DSC 寫核心紀錄 `vhf` 行(帶外、不進雜湊)並以事件框送教官站事件流;講評報告與 training record 統計次數與秒數 |

不在範圍:真實 VHF 傳播(距離、天線高度、衰減、雜訊)、DSC 編碼/MMSI、通話錄音、多艘自船間的 VHF(R5 未實作)、對講/PA 的獨立介面(以頻道模擬;見第 6 節)。

## 2. 架構

```
操船台 index.html?station=bridge ──┐                                  ┌── 教官站 instructor.html(dualWatch 16 + 工作頻道)
   <brm-vhf>(VhfClient)           │  ws://<核心主機>:8767/            │     <brm-vhf dualWatch>
      ├ 文字框 {type:"vhf",kind:…} ├──────► SimCore.Host VhfRelayServer ◄──┤
      └ 二進位框 [id][webm/opus]   │          (獨立 HttpListener)        │
                                   │                 │ Logged            │
                                   │                 ▼                   │
                                   │   HostCommandRouter.Recorder.WriteVhf(ev)  →  紀錄 vhf 行
                                   │   WebSocketStateServer.PublishEvent({type:"event",event})  →  教官站事件流(source "vhf")
```

- `VhfRelayServer`:與狀態 WebSocket 相同的 `HttpListener` 寫法(Windows 無 URL ACL 時退回 localhost 並提示 `netsh http add urlacl`),每站一條容量 256、丟最舊的出站通道(慢速客戶端不拖慢其他站)。只做轉送與佔用判定,不解碼音訊。
- `Program.cs` 只加少量行:建立伺服器、`Logged` → 紀錄行 + 事件框、結束時 Dispose;`--vhf-port`、`--no-vhf`。埠被占用時只提示、核心照常執行(VHF 為選配)。
- 客戶端網址由狀態 WebSocket 網址推得(同主機、埠 8767);`?vhf=ws://…` 覆寫、`?vhf=0` 不顯示;示範模式(無核心)與重播檢視不顯示。站名取 `?station=`(操船台 `bridge|portWing|stbdWing|ecr`,與警報面板共用;教官站預設 `instructor`)。

## 3. 客戶端狀態機(`lib/vhf.ts`)

```
idle ──pttDown(已 join)──► transmitting ──pttUp──► idle(記錄本站通話)
  │                            │ busy(伺服器)──► busy ──1.5 s──► idle
  └─pttStart(他站、守聽頻道)─► receiving ──pttEnd(同 id)──► idle(記錄他站通話)
```

- 半雙工:發話中不接收;接收中/佔用中按 PTT 無效。
- 轉頻道:發話中先結束通話;轉離正在接收的頻道(且不在守聽內)停止播放。
- 狀態機只回傳效果(送訊息、開始/停止擷取與播放、記錄),由 `VhfClient` 接上 WebSocket、`getUserMedia`/`MediaRecorder` 與播放器;瀏覽器 API 全部可注入,vitest 以假物件驗證。
- 放開 PTT 時先停錄音、等最後一片送出,再送 `pttEnd`(尾音不遺失;伺服器在 `pttEnd` 前仍轉送)。
- 斷線:回 idle、停止擷取/播放、指數退避 1 → 5 s 重連並重新 `join`(頻道/守聽沿用)。

## 4. 紀錄與講評

- 紀錄行 `{"kind":"vhf","tick","t","event":{kind: vhfCall|vhfText|vhfDsc, source:"vhf", itemId: 頻道, data:{station, channel, duration_s?, text?, category?, endReason?, utc}}}`(`RecordVhf`)。
  **選擇帶外行而非 `event` 行**:VHF 不是引擎輸入、重播無法重現,若寫成 `event` 行會使重播的事件序列比對失敗;比照 `session` 行:不進狀態雜湊鏈、不在事件序列內、只計入 footer 的檔案 SHA-256(`VhfRelayTests.VhfLines_WrittenToRecord_ReplayHashAndEventSequenceUnaffected`)。
  紀錄未開啟或已關閉(情境切換中)時該筆不寫(`WriteVhf` 回 false),教官站事件流仍收到事件框。
- 教官站:`SimEventSource` 新增 `vhf`,事件流有「VHF」篩選鈕;`eventLabel` 顯示「VHF CH16 bridge 通話 3.2 s」「VHF CH16 vts 文字:…」「DSC DISTRESS 遇險 由 …」,`vhfDsc` 的嚴重度 distress 紅、urgency 黃。
- 講評站(`record-replay.ts`):`vhf` 行的 `event` 併入事件清單(倒帶時與其他事件一樣丟棄舊分支),講評報告事件時間軸列出,「二、自動量測與評分」另有「通訊(VHF 角色扮演)」小節:通話次數/總秒數、文字/DSC 筆數、各站分列(`vhfSummary`)。教官站自己的 JSON 紀錄因事件框也含這些事件,同樣可統計。
- training record:`SessionExercise.vhf {calls, duration_s, texts, dsc}` 由講評報告帶入,個人訓練紀錄 `results[].vhf`,班次 CSV 新增 `vhfCalls / vhfDuration_s / vhfTexts / vhfDsc` 四欄(團隊層級,作為「通訊」行為標記的佐證,不是個人評分)。

## 5. 瀏覽器麥克風權限(安裝文件第 2.7 節)

- `getUserMedia` 只在安全來源可用:`https://` 或 `http://localhost` / `http://127.0.0.1`。船上筆電的操船台以 `http://127.0.0.1:8080`(WebView2 外殼預設 `--base`)開啟即為安全來源;教官筆電以 `http://127.0.0.1:8080/instructor.html?ws=ws://192.168.77.11:8765` 開本機網頁伺服也是(VHF 網址自動推為 `ws://192.168.77.11:8767/`)。
- 若必須以船上 IP 開頁(`http://192.168.77.11:8080`),Chromium/Edge 需加旗標 `--unsafely-treat-insecure-origin-as-secure=http://192.168.77.11:8080`(或 `edge://flags/#unsafely-treat-insecure-origin-as-secure`);WebView2 外殼可以 `--additional-browser-arguments` 等效設定(待外殼實作該參數,目前建議用 127.0.0.1)。
- Windows:設定 → 隱私權與安全性 → 麥克風:「麥克風存取」與「允許應用程式存取麥克風」開啟,桌面應用程式(瀏覽器/外殼)允許;USB 耳麥在聲音設定設為預設輸入裝置。
- 第一次按 PTT 時瀏覽器詢問授權;拒絕或無裝置時面板顯示提示,PTT 停用,文字與 DSC 仍可用。自動播放:接收端第一次需有使用者互動(點頁面任一處)才有聲音。

## 6. 限制與已知事項

| 項目 | 說明 |
|---|---|
| 非真實 VHF | 無距離/天線/雜訊模型;所有站只要同頻道就聽得到;「互相壓制」簡化為先到先得(後發者收到佔用,不是兩邊都雜音) |
| DSC | 只有 DISTRESS/URGENCY(面板)與 safety/routine(協定)文字警示;無 DSC 編碼、MMSI、位置、ACK 流程 |
| 內線/PA | 無獨立對講或廣播介面;以約定的工作頻道(例如 67/68)模擬機控室內線,PA 用文字訊息;STCW-DNV 對照「VHF/DSC、內線、PA」列狀態為「已實作(軟體,內建)」並註明此簡化 |
| 錄音 | 通話內容不錄,只記次數/頻道/秒數(錄音屬 R6 聲音/影像擷取,另案) |
| 延遲 | 100 ms 片段 + 直連網路,端到端約 200–400 ms;MSE 不可用時退回整段播放(延遲 = 通話長度) |
| 單人同台 | 同一台筆電同時開操船台與教官站測試時,麥克風會回授到喇叭;實際配置兩台筆電各接耳麥 |
| 需實機 | 兩船筆電直連的麥克風授權、WebView2 外殼內 `getUserMedia`、音量/耳麥配置(第 14 章船上部署列「VHF」仍需實機) |
| 重播 | 重播檢視只列事件與統計,不重放語音;Host `--replay` 不比對 vhf 行 |
