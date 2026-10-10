import { describe, expect, it } from "vitest";
import { modPageUrl } from "@/lib/mods/page-url";

const apiUrl = (path: string) => `http://fleet.test:5000${path}`;
const url = (path: string, query?: Record<string, string>, over: Partial<Parameters<typeof modPageUrl>[0]> = {}) =>
  modPageUrl({ apiUrl, sessionId: "ses_1", mod: "demo-mod@v3", path, query, ...over });

describe("modPageUrl", () => {
  it("builds the address M6 serves, on the machine's API", () => {
    expect(url("ui/report.html")).toBe("http://fleet.test:5000/api/sessions/ses_1/mods/demo-mod%40v3/pages/ui/report.html");
    expect(url("a.html", { run: "1", who: "me" })).toBe(
      "http://fleet.test:5000/api/sessions/ses_1/mods/demo-mod%40v3/pages/a.html?run=1&who=me",
    );
  });

  it("has no query mark for an empty or missing query", () => {
    expect(url("a.html", {})).toBe("http://fleet.test:5000/api/sessions/ses_1/mods/demo-mod%40v3/pages/a.html");
    expect(url("a.html")).not.toContain("?");
  });

  it("encodes every segment", () => {
    const built = url("my ui/r&d é#1?.html")!;
    expect(built).toBe("http://fleet.test:5000/api/sessions/ses_1/mods/demo-mod%40v3/pages/my%20ui/r%26d%20%C3%A9%231%3F.html");
    const parsed = new URL(built);
    expect(parsed.search).toBe("");
    expect(parsed.hash).toBe("");
  });

  it("encodes the session id", () => {
    expect(url("a.html", undefined, { sessionId: "a/b?c" })).toContain("/api/sessions/a%2Fb%3Fc/mods/");
  });

  it("builds the query with URLSearchParams", () => {
    const built = url("a.html", { "a&b": "c=d", "q r": "é/#" })!;
    expect(built.endsWith("?a%26b=c%3Dd&q+r=%C3%A9%2F%23")).toBe(true);
    expect(new URL(built).hash).toBe("");
  });

  it.each([
    ["dot-dot", "../x.html"],
    ["dot-dot inside", "ui/../../api/x.html"],
    ["dot-dot last", "ui/.."],
    ["dot", "./x.html"],
    ["dot inside", "ui/./x.html"],
    ["backslash", "ui\\x.html"],
    ["leading slash", "/api/sessions/x.html"],
    ["double slash", "ui//x.html"],
    ["trailing slash", "ui/x.html/"],
    ["empty", ""],
    ["javascript", "javascript:alert(1).html"],
    ["javascript, upper", "JaVaScRiPt:alert(1)//.html"],
    ["data", "data:text/html,x.html"],
    ["scheme in a later segment", "ui/x:y.html"],
    ["absolute url", "https://evil.example/x.html"],
    ["protocol-relative", "//evil.example/x.html"],
    ["newline", "ui/x\n.html"],
    ["tab", "ui/\tx.html"],
    ["nul", "ui/x\u0000.html"],
    ["del", "ui/x\u007f.html"],
    ["not html", "ui/x.js"],
    ["no extension", "ui/x"],
    ["html in the middle", "x.html/y"],
    ["upper-case extension", "x.HTML"],
  ])("rejects %s", (_name, path) => {
    expect(url(path)).toBeNull();
  });

  it("rejects a bad mod id or an empty session id", () => {
    for (const mod of ["", "demo-mod", "A@v1", "1a@v1", "a_b@v1", "a/b@v1", "..", "a b@v1", `${"a".repeat(65)}@v1`, "x.html", "a@v0", "a@draft:", "a@v1/../b", "a@draft:x/y"]) {
      expect(url("a.html", undefined, { mod }), mod).toBeNull();
    }
    expect(url("a.html", undefined, { sessionId: "" })).toBeNull();
  });

  it("takes a mod id as one segment, and a draft's session in it", () => {
    expect(url("a.html", undefined, { mod: `a${"b".repeat(63)}@v12` })).not.toBeNull();
    const draft = url("a.html", undefined, { mod: "demo@draft:ses_0-1" })!;
    expect(draft).toBe("http://fleet.test:5000/api/sessions/ses_1/mods/demo%40draft%3Ases_0-1/pages/a.html");
    expect(new URL(draft).pathname.split("/")).toHaveLength(8);
  });

  it("accepts only an address on the machine's API origin", () => {
    // The base itself decides the origin: a path that stays inside it can't leave it.
    expect(url("a.html", undefined, { apiUrl: (p) => `https://other.example${p}` })).toBe(
      "https://other.example/api/sessions/ses_1/mods/demo-mod%40v3/pages/a.html",
    );
    // An apiUrl whose answer is on another origin than its own root is refused.
    let calls = 0;
    const drifting = (path: string) => (path === "/" && calls++ === 0 ? "http://fleet.test:5000/" : `http://evil.example${path}`);
    expect(url("a.html", undefined, { apiUrl: drifting })).toBeNull();
    // A relative apiUrl (same origin) works against the page's own origin.
    expect(url("a.html", undefined, { apiUrl: (p) => p })).toBe("/api/sessions/ses_1/mods/demo-mod%40v3/pages/a.html");
    // Something that is not a URL at all.
    expect(url("a.html", undefined, { apiUrl: () => "http://[bad" })).toBeNull();
  });

  it("ignores query values that are not strings", () => {
    expect(url("a.html", { a: "1", b: 2 as unknown as string })).toMatch(/\?a=1$/);
  });
});
