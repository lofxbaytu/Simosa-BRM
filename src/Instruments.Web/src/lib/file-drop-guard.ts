// 檔案拖到頁面上「不接受檔案的地方」時,不讓瀏覽器把整個頁面換成那個檔案(第三十一輪;各頁的進入點都裝)。
//
// 瀏覽器的預設行為:頁面沒有在 dragover 取消預設動作的地方,把檔案放下去 = 在這個分頁開啟那個檔案 —— 練習進行中,
// 教官站或駕駛台的畫面會整個被一份 .jsonl 的文字取代(要按上一頁或重新整理才回得來)。重播檢視與講評報告頁現在接受把紀錄檔拖進來
// (lib/record-source.ts),教官拖錯地方(頁首、「即時」分頁、另一個視窗的駕駛台)的機會比以前多,所以整個頁面都擋:
//  - dragover:有檔案、而且沒有人接受(defaultPrevented 為 false)→ 取消預設動作並把 dropEffect 設成 none(游標顯示「不能放」,放下去沒有反應);
//  - drop:同樣的條件 → 取消預設動作(保險;dropEffect 是 none 時瀏覽器本來就不會送 drop)。
// 接受檔案的元件(brm-replay-view、brm-report-app)在自己的處理函式裡先取消了預設動作,事件冒泡到 window 時這裡看得到,不動它。
// 不是檔案的拖曳(選取的文字、時間軸滑桿)一律不攔。
//
// 開發環境的限制:自動化(CDP 的 Input.dispatchDragEvent)送的拖放事件不會觸發「在分頁開啟檔案」那個預設行為(審查時試了 5 處都沒有發生),
// 所以這裡驗的是事件的處理(tests/file-drop-guard.test.ts 與端對端 02-instructor),真實的滑鼠拖放要在實機確認。

/** 拖放的內容有沒有檔案(dragover 時決定要不要接受)。 */
export function dataTransferHasFiles(dt: DataTransfer | null | undefined): boolean {
  if (!dt) return false;
  return Array.from(dt.types ?? []).includes('Files');
}

type DragLike = Event & { dataTransfer?: DataTransfer | null };

function blockUnclaimedFileDrag(ev: Event): void {
  const dt = (ev as DragLike).dataTransfer;
  if (ev.defaultPrevented || !dataTransferHasFiles(dt)) return;
  ev.preventDefault();
  if (dt && ev.type === 'dragover') dt.dropEffect = 'none';
}

/**
 * 在 target(預設目前的 window)裝上防護;回傳拆掉它的函式。重複呼叫不會重複處理(第二次起事件已經是 defaultPrevented)。
 * 沒有 window 的環境(單元測試的 node)什麼都不做。
 */
export function installFileDropGuard(target: Pick<EventTarget, 'addEventListener' | 'removeEventListener'> | undefined = typeof window === 'undefined' ? undefined : window): () => void {
  if (!target) return () => undefined;
  target.addEventListener('dragover', blockUnclaimedFileDrag);
  target.addEventListener('drop', blockUnclaimedFileDrag);
  return () => {
    target.removeEventListener('dragover', blockUnclaimedFileDrag);
    target.removeEventListener('drop', blockUnclaimedFileDrag);
  };
}
