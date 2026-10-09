import { describe, expect, it } from "vitest";
import { parseFileReference, type FileReferenceEnv } from "@/lib/file-references";
import { createMarkdownRenderer } from "@/lib/markdown-renderer";

describe("parseFileReference", () => {
  it.each([
    ["src/billing/tax.ts", { path: "src/billing/tax.ts" }],
    ["src/billing/tax.ts:13", { path: "src/billing/tax.ts", line: 13 }],
    ["src/billing/tax.ts:13:7", { path: "src/billing/tax.ts", line: 13 }],
    ["src/billing/tax.ts:13-20", { path: "src/billing/tax.ts", line: 13 }],
    ["src/billing/tax.ts#L18", { path: "src/billing/tax.ts", line: 18 }],
    ["src/billing/tax.ts#L18-L24", { path: "src/billing/tax.ts", line: 18 }],
    ["test/billing/invoice-service.test.ts", { path: "test/billing/invoice-service.test.ts" }],
    ["README.md", { path: "README.md" }],
    [".gitignore", { path: ".gitignore" }],
    ["./src/main.rs", { path: "./src/main.rs" }],
    ["docker/Dockerfile", { path: "docker/Dockerfile" }],
    ["src\\Billing\\Tax.cs:28", { path: "src\\Billing\\Tax.cs", line: 28 }],
    ["C:\\dev\\harbor-api\\src\\billing\\tax.ts", { path: "C:\\dev\\harbor-api\\src\\billing\\tax.ts" }],
    ["/home/sam/dev/harbor-api/src/billing/tax.ts:17", { path: "/home/sam/dev/harbor-api/src/billing/tax.ts", line: 17 }],
  ])("reads %s as a file", (text, expected) => {
    expect(parseFileReference(text)).toEqual(expected);
  });

  it.each([
    "lineTotal",
    "this.lineTotal",
    "HttpContext.Items",
    "1.2.3",
    "roundCents(net)",
    "a + b",
    "https://example.com/a.ts",
    "--force",
    "Dockerfile",
    "..",
    "x = 1",
  ])("leaves %s alone", (text) => {
    expect(parseFileReference(text)).toBeNull();
  });
});

describe("file references in rendered Markdown", () => {
  const md = createMarkdownRenderer();
  const files: Record<string, string> = {
    "src/billing/tax.ts": "src/billing/tax.ts",
    "/home/sam/dev/harbor-api/src/billing/invoice-service.ts": "src/billing/invoice-service.ts",
  };

  function render(markdown: string) {
    const env: FileReferenceEnv = { fileRefs: [], resolveFileRef: (path) => files[path] ?? null };
    return { html: md.render(markdown, env), refs: env.fileRefs };
  }

  it("marks inline code that is a file in the folder, with its path from the folder and its line", () => {
    const { html } = render("See `/home/sam/dev/harbor-api/src/billing/invoice-service.ts:17`.");

    expect(html).toContain('<code class="file-ref" data-file-path="src/billing/invoice-service.ts" data-file-line="17"');
    expect(html).toContain('role="link" tabindex="0"');
    expect(html).toContain("/home/sam/dev/harbor-api/src/billing/invoice-service.ts:17</code>");
  });

  it("marks a Markdown link to a file", () => {
    const { html } = render("The rule is in [tax.ts](src/billing/tax.ts#L18).");

    expect(html).toContain('<a href="src/billing/tax.ts#L18" class="file-ref" data-file-path="src/billing/tax.ts" data-file-line="18"');
  });

  it("collects every path named, and leaves those that aren't files as they were", () => {
    const { html, refs } = render("`src/billing/tax.ts:13` and `src/billing/legacy-rates.ts`, not `lineTotal`.");

    expect(refs).toEqual(["src/billing/tax.ts", "src/billing/legacy-rates.ts"]);
    expect(html).toContain("<code>src/billing/legacy-rates.ts</code>");
    expect(html).toContain("<code>lineTotal</code>");
  });

  it("leaves paths in code blocks and web links alone", () => {
    const { html, refs } = render("```\nsrc/billing/tax.ts\n```\n\n[docs](https://example.com/tax.ts)");

    expect(refs).toEqual([]);
    expect(html).not.toContain("file-ref");
  });

  it("marks nothing without a session to resolve against", () => {
    expect(md.render("`src/billing/tax.ts`")).toBe("<p><code>src/billing/tax.ts</code></p>\n");
  });
});

describe("file names in prose", () => {
  const md = createMarkdownRenderer();

  it("aren't web addresses", () => {
    const html = md.render("Update README.md, main.rs and setup.py.");

    expect(html).not.toContain("<a");
    expect(html).toContain("Update README.md, main.rs and setup.py.");
  });

  it("leave real web addresses linked", () => {
    const html = md.render("See www.example.de, example.com, github.com/harbor/api and https://example.md/x.");

    expect(html).toContain('<a href="http://www.example.de">www.example.de</a>');
    expect(html).toContain('<a href="http://example.com">example.com</a>');
    expect(html).toContain('<a href="http://github.com/harbor/api">github.com/harbor/api</a>');
    expect(html).toContain('<a href="https://example.md/x">https://example.md/x</a>');
  });
});
