// 下載工具:把文字內容以 Blob 交給瀏覽器儲存(BRM 標記、練習紀錄匯出;船上離線也可用)。

export function downloadText(filename: string, content: string, mime = 'application/json'): void {
  const blob = new Blob([content], { type: `${mime};charset=utf-8` });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  // 延後釋放,避免部分瀏覽器尚未開始下載就撤銷 URL
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/** 檔名用的時間戳 yyyyMMdd-HHmmss(本地時間)。 */
export function fileStamp(date = new Date()): string {
  const p = (n: number, w = 2) => n.toString().padStart(w, '0');
  return `${date.getFullYear()}${p(date.getMonth() + 1)}${p(date.getDate())}-${p(date.getHours())}${p(date.getMinutes())}${p(date.getSeconds())}`;
}
