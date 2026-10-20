import { describe, expect, it } from "vitest";
import { createMarkdownRenderer } from "@/lib/markdown-renderer";

describe("createMarkdownRenderer", () => {
  it("draws GitHub task lists as checklist items", () => {
    const html = createMarkdownRenderer().render("## Test plan\n\n- [x] unit tests\n- [ ] live on a scratch Fleet\n- plain item\n");

    expect(html).toContain('<li class="task-list-item task-list-item--done">unit tests</li>');
    expect(html).toContain('<li class="task-list-item">live on a scratch Fleet</li>');
    expect(html).toContain("<li>plain item</li>");
    expect(html).not.toContain("[x]");
  });

  it("leaves brackets that aren't task markers alone", () => {
    const html = createMarkdownRenderer().render("- [link](https://example.com) here\n- [x]no space\n\n[x] outside a list\n");

    expect(html).not.toContain("task-list-item");
    expect(html).toContain("[x]no space");
    expect(html).toContain("[x] outside a list");
  });
});

describe("folds", () => {
  const render = (src: string, env: object = {}) => createMarkdownRenderer().render(src, env);

  it("draws <details> with a summary as a closed fold holding Markdown", () => {
    const html = render("<details>\n<summary>Why a **map**</summary>\n\n- one\n- two\n\n</details>\n");

    expect(html).toContain('<details class="md-details" data-fold="0">');
    expect(html).toContain("<summary>Why a <strong>map</strong></summary>");
    expect(html).toContain('<div class="md-details__body">');
    expect(html).toContain("<li>one</li>");
    expect(html).not.toContain("&lt;details");
  });

  it("opens a fold the author wrote open, and names one with no summary Details", () => {
    const html = render("<details open>\nbody\n</details>");

    expect(html).toContain('<details class="md-details" data-fold="0" open="">');
    expect(html).toContain("<summary>Details</summary>");
  });

  it("nests folds and numbers them by position", () => {
    const html = render("<details>\n<summary>outer</summary>\n<details>\n<summary>inner</summary>\ntext\n</details>\nafter\n</details>\n");

    expect(html).toContain('data-fold="0"');
    expect(html).toContain('data-fold="1"');
    expect(html.match(/<\/details>/g)).toHaveLength(2);
    expect(html.indexOf("inner")).toBeLessThan(html.indexOf("after"));
  });

  it("does not end a fold at a </details> inside a code fence", () => {
    const html = render("<details>\n<summary>code</summary>\n\n```html\n</details>\n```\n\nstill inside\n</details>\n");

    expect(html.match(/<\/details>/g)).toHaveLength(1);
    expect(html).toContain("&lt;/");
    expect(html.indexOf("still inside")).toBeLessThan(html.lastIndexOf("</details>"));
  });

  it("leaves a </details> in a code span as text", () => {
    const html = render("<details>\n<summary>s</summary>\nuse `</details>` to close\n</details>\n");

    expect(html).toContain("<code>&lt;/details&gt;</code>");
    expect(html.match(/<\/details>/g)).toHaveLength(1);
  });

  it("keeps other attributes and tags as escaped text", () => {
    const attr = render('<details onclick="alert(1)">\n<summary>x</summary>\nbody\n</details>');
    const tag = render("<details>\n<summary class=\"x\">x</summary>\nbody\n</details>\n<script>alert(1)</script>");

    expect(attr).not.toContain("<details");
    expect(attr).toContain("&lt;details onclick=");
    expect(tag).toContain("&lt;script&gt;");
    expect(tag).not.toContain("<script");
    expect(tag).not.toContain('class="x"');
  });

  it("leaves an unclosed fold as text once the message has settled", () => {
    const html = render("<details>\n<summary>x</summary>\nbody");

    expect(html).not.toContain("<details");
    expect(html).toContain("&lt;details&gt;");
  });

  it("draws an unclosed fold open, with its text so far, while streaming", () => {
    const html = render("<details>\n<summary>Log</summary>\nline one\nline t", { streaming: true });

    expect(html).toContain('data-fold="0" open=""');
    expect(html).toContain("line t");
  });

  it("keeps a closed fold open until the message settles", () => {
    const src = "<details>\n<summary>Log</summary>\nbody\n</details>";

    expect(render(src, { streaming: true })).toContain('open=""');
    expect(render(src, { streaming: false })).not.toContain('open=""');
  });

  it("waits to draw a fold until its summary line is complete, and hides a half-written tag", () => {
    expect(render("intro\n\n<details>", { streaming: true })).not.toContain("details");
    expect(render("<details>\n<summary>Wh", { streaming: true })).toBe("");
    expect(render("<details>\n<summary>Why</summ", { streaming: true })).toBe("");
    expect(render("text\n\n<deta", { streaming: true })).toBe("<p>text</p>\n");
    expect(render("text\n\n</detai", { streaming: true })).toBe("<p>text</p>\n");
    expect(render("<details>\n<summary>Why</summary>", { streaming: true })).toContain("<summary>Why</summary>");
  });

  it("keeps what the reader chose across redraws", () => {
    const folds = new Map([["0", true], ["1", false]]);
    const html = render("<details open>\n<summary>a</summary>\nx\n</details>\n\n<details>\n<summary>b</summary>\ny\n</details>", { folds, streaming: true });

    expect(html.match(/open=""/g)).toHaveLength(1);
    expect(html).toContain('data-fold="0" open=""');
    expect(html).toContain('data-fold="1">');
  });
});

describe("languages", () => {
  const render = (src: string) => createMarkdownRenderer().render(src);

  it.each([
    ["java", "public class A { int x = 1; }"],
    ["c", "int main(void) { return 0; }"],
    ["cpp", "#include <vector>\nstd::vector<int> v;"],
    ["ruby", "def hi\n  puts 'x'\nend"],
    ["php", "<?php echo 'x'; ?>"],
    ["sql", "SELECT id FROM users WHERE id = 1;"],
    ["kotlin", "fun main() { val x = 1 }"],
    ["swift", "let x: Int = 1\nfunc f() {}"],
    ["dockerfile", "FROM node:22\nRUN npm ci"],
    ["powershell", "Get-ChildItem -Path . | Where-Object { $_.Length -gt 1 }"],
    ["ini", "[server]\nport = 8080"],
    ["toml", "[server]\nport = 8080"],
    ["c++", "int main() { return 0; }"],
    ["kt", "fun main() { val x = 1 }"],
    ["rb", "def hi\nend"],
    ["ps1", "Get-Item ."],
    ["docker", "FROM node:22"],
    ["h", "int f(void);"],
    ["hpp", "class A {};"],
    ["kts", "val x = 1"],
  ])("colours a %s fence", (language, code) => {
    expect(render(`\`\`\`${language}\n${code}\n\`\`\``)).toContain('<span class="hljs-');
  });

  it("leaves an unknown language, and a fence with none, plain", () => {
    const unknown = render("```brainfuck\n+++ SELECT 1\n```");
    const none = render("```\nSELECT 1 FROM t\n```");

    expect(unknown).not.toContain("hljs-");
    expect(none).not.toContain("hljs-");
  });
});
