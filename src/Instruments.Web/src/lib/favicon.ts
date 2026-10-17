// 頁面沒有 favicon 時 Chromium 會自動抓 /favicon.ico 而在 console 記 404;離線船上筆電也不該多發請求,這裡以內嵌 SVG 補上。
// 儀器頁與教官站共用;頁面已有 <link rel="icon"> 時不動。
export function ensureFavicon(doc: Document = document): void {
  if (doc.querySelector('link[rel~="icon"]')) return;
  const link = doc.createElement('link');
  link.rel = 'icon';
  link.href =
    'data:image/svg+xml,' +
    encodeURIComponent(
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><circle cx="8" cy="8" r="7" fill="#1f5f8b"/><path d="M8 2v12M2 8h12" stroke="#fff" stroke-width="1.5"/></svg>',
    );
  doc.head.appendChild(link);
}
