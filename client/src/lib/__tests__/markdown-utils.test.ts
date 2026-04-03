import { extractLanguage, extractText } from "@/lib/markdown-utils";

// ─── extractLanguage ──────────────────────────────────────────────────────────

describe("extractLanguage", () => {
  it('extracts language from "language-typescript"', () => {
    expect(extractLanguage("language-typescript")).toBe("typescript");
  });

  it('extracts language from "hljs language-python"', () => {
    expect(extractLanguage("hljs language-python")).toBe("python");
  });

  it('extracts language from "language-javascript"', () => {
    expect(extractLanguage("language-javascript")).toBe("javascript");
  });

  it('extracts language from "language-bash"', () => {
    expect(extractLanguage("language-bash")).toBe("bash");
  });

  it('returns "" for empty string', () => {
    expect(extractLanguage("")).toBe("");
  });

  it('returns "" for undefined', () => {
    expect(extractLanguage(undefined)).toBe("");
  });

  it('returns "" for a className with no language- prefix', () => {
    expect(extractLanguage("hljs")).toBe("");
  });

  it('returns "" for a className with only "hljs"', () => {
    expect(extractLanguage("foo bar baz")).toBe("");
  });

  it("handles compound language identifiers like language-c++", () => {
    expect(extractLanguage("language-c++")).toBe("c++");
  });
});

// ─── extractText ──────────────────────────────────────────────────────────────

describe("extractText", () => {
  it("returns string values as-is", () => {
    expect(extractText("hello world")).toBe("hello world");
  });

  it("converts numbers to string", () => {
    expect(extractText(42)).toBe("42");
  });

  it("returns empty string for null", () => {
    expect(extractText(null)).toBe("");
  });

  it("returns empty string for undefined", () => {
    expect(extractText(undefined)).toBe("");
  });

  it("returns empty string for false", () => {
    expect(extractText(false)).toBe("");
  });

  it("joins array elements recursively", () => {
    expect(extractText(["hello", " ", "world"])).toBe("hello world");
  });

  it("joins nested arrays", () => {
    expect(extractText(["a", ["b", "c"]])).toBe("abc");
  });

  it("extracts text from a React-like element with string children", () => {
    const element = { props: { children: "code text" } };
    expect(extractText(element)).toBe("code text");
  });

  it("extracts text from deeply nested React-like elements", () => {
    const element = { props: { children: { props: { children: "deep text" } } } };
    expect(extractText(element)).toBe("deep text");
  });

  it("extracts text from React-like element with array children", () => {
    const element = { props: { children: ["line 1", "\n", "line 2"] } };
    expect(extractText(element)).toBe("line 1\nline 2");
  });

  it("returns empty string for an empty array", () => {
    expect(extractText([])).toBe("");
  });

  it("returns empty string for an object without props", () => {
    expect(extractText({ type: "span" })).toBe("");
  });
});
