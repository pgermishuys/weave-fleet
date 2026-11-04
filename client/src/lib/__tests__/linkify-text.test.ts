import { describe, expect, it } from "vitest";
import { linkifyText } from "@/lib/linkify-text";

describe("linkifyText", () => {
  it("links a bare web address and keeps the text around it", () => {
    expect(linkifyText("Open https://example.com/a?b=1&c=2 now")).toBe(
      'Open <a href="https://example.com/a?b=1&amp;c=2" target="_blank" rel="noopener noreferrer">https://example.com/a?b=1&amp;c=2</a> now',
    );
  });

  it("escapes markup in the text and in the address", () => {
    const html = linkifyText('<img src=x onerror=alert(1)> https://example.com/"><script>');

    expect(html).not.toContain("<img");
    expect(html).not.toContain("<script");
    expect(html).toContain("&lt;img");
  });

  it("reads no Markdown and links no scheme but http(s)", () => {
    expect(linkifyText("a *b* _c_ javascript:alert(1) mailto:x@example.com")).toBe("a *b* _c_ javascript:alert(1) mailto:x@example.com");
  });

  it("leaves text without an address as escaped text", () => {
    expect(linkifyText("1 < 2 & 3")).toBe("1 &lt; 2 &amp; 3");
  });
});
