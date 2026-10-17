// 內嵌 favicon(happy-dom):儀器頁與教官站共用。
// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest';
import { ensureFavicon } from '../src/lib/favicon.js';

describe('ensureFavicon', () => {
  it('沒有 icon 時補上內嵌 SVG,已有時不重複', () => {
    document.head.querySelectorAll('link[rel~="icon"]').forEach((l) => l.remove());
    ensureFavicon();
    const links = document.head.querySelectorAll('link[rel~="icon"]');
    expect(links.length).toBe(1);
    expect((links[0] as HTMLLinkElement).href.startsWith('data:image/svg+xml,')).toBe(true);
    ensureFavicon();
    expect(document.head.querySelectorAll('link[rel~="icon"]').length).toBe(1);
  });
});
