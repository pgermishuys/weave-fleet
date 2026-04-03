import { describe, it, expect } from "vitest";
import { highlightText } from "@/lib/highlight-utils";

// Helper to check if an element is a React mark element
// createElement returns { type, key, props, ... } — we read these fields directly.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function isMarkElement(value: unknown): value is { type: string; props: Record<string, any> } {
  return (
    typeof value === "object" &&
    value !== null &&
    "type" in value &&
    (value as Record<string, unknown>).type === "mark" &&
    "props" in value
  );
}

// ─── highlightText ────────────────────────────────────────────────────────────

describe("highlightText", () => {
  it("returns array containing original text when query is empty string", () => {
    const result = highlightText("hello world", "");
    expect(result).toEqual(["hello world"]);
  });

  it("returns array containing original text when query is empty (type check)", () => {
    const result = highlightText("foo bar", "");
    expect(result).toHaveLength(1);
    expect(result[0]).toBe("foo bar");
  });

  it("returns array with original text when text is empty string", () => {
    const result = highlightText("", "foo");
    expect(result).toEqual([""]);
  });

  it("returns original text in array when query is not found", () => {
    const result = highlightText("hello world", "xyz");
    expect(result).toHaveLength(1);
    expect(result[0]).toBe("hello world");
  });

  it("highlights a single match — returns [prefix, mark, suffix]", () => {
    const result = highlightText("say hello there", "hello");
    expect(result).toHaveLength(3);
    expect(result[0]).toBe("say ");
    // Middle element should be a React mark element
    expect(isMarkElement(result[1])).toBe(true);
    if (isMarkElement(result[1])) {
      expect(result[1].props.children).toBe("hello");
    }
    expect(result[2]).toBe(" there");
  });

  it("highlights match at start of text — returns [mark, suffix]", () => {
    const result = highlightText("hello world", "hello");
    expect(result).toHaveLength(2);
    expect(isMarkElement(result[0])).toBe(true);
    if (isMarkElement(result[0])) {
      expect(result[0].props.children).toBe("hello");
    }
    expect(result[1]).toBe(" world");
  });

  it("highlights match at end of text — returns [prefix, mark]", () => {
    const result = highlightText("say hello", "hello");
    expect(result).toHaveLength(2);
    expect(result[0]).toBe("say ");
    expect(isMarkElement(result[1])).toBe(true);
    if (isMarkElement(result[1])) {
      expect(result[1].props.children).toBe("hello");
    }
  });

  it("highlights multiple non-overlapping matches", () => {
    const result = highlightText("foo and foo", "foo");
    // mark "foo", " and ", mark "foo"
    expect(result).toHaveLength(3);
    expect(isMarkElement(result[0])).toBe(true);
    if (isMarkElement(result[0])) {
      expect(result[0].props.children).toBe("foo");
    }
    expect(result[1]).toBe(" and ");
    expect(isMarkElement(result[2])).toBe(true);
    if (isMarkElement(result[2])) {
      expect(result[2].props.children).toBe("foo");
    }
  });

  it("is case-insensitive — matches lowercase query against mixed-case text", () => {
    const result = highlightText("Hello World", "hello");
    expect(result.length).toBeGreaterThanOrEqual(2);
    const mark = result.find(isMarkElement);
    expect(mark).toBeDefined();
    if (mark && isMarkElement(mark)) {
      expect((mark.props.children as string).toLowerCase()).toBe("hello");
    }
  });

  it("is case-insensitive — matches uppercase query against lowercase text", () => {
    const result = highlightText("hello world", "HELLO");
    const mark = result.find(isMarkElement);
    expect(mark).toBeDefined();
    expect(isMarkElement(mark)).toBe(true);
  });

  it("escapes regex-special chars — query 'foo.bar' matches literal 'foo.bar' not 'fooXbar'", () => {
    const result = highlightText("fooXbar and foo.bar", "foo.bar");
    // Only the literal "foo.bar" should be highlighted
    const marks = result.filter(isMarkElement) as Array<{ type: string; props: Record<string, unknown> }>;
    expect(marks).toHaveLength(1);
    expect(marks[0].props.children).toBe("foo.bar");
  });

  it("escapes regex-special chars — query '(hello)' matches literal parentheses", () => {
    const result = highlightText("say (hello) there", "(hello)");
    const marks = result.filter(isMarkElement) as Array<{ type: string; props: Record<string, unknown> }>;
    expect(marks).toHaveLength(1);
    expect(marks[0].props.children).toBe("(hello)");
  });

  it("escapes regex-special chars — query with * does not throw and matches literal", () => {
    const result = highlightText("foo*bar baz", "foo*bar");
    const marks = result.filter(isMarkElement) as Array<{ type: string; props: Record<string, unknown> }>;
    expect(marks).toHaveLength(1);
    expect(marks[0].props.children).toBe("foo*bar");
  });

  it("mark element has correct className for styling", () => {
    const result = highlightText("test match here", "match");
    const mark = result.find(isMarkElement);
    expect(mark).toBeDefined();
    if (mark && isMarkElement(mark)) {
      expect(mark.props.className).toContain("bg-yellow-500/30");
    }
  });

  it("exact text match returns a single mark element (no surrounding strings)", () => {
    const result = highlightText("hello", "hello");
    expect(result).toHaveLength(1);
    expect(isMarkElement(result[0])).toBe(true);
    if (isMarkElement(result[0])) {
      expect(result[0].props.children).toBe("hello");
    }
  });
});
