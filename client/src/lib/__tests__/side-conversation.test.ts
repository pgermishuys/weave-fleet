import { describe, expect, it } from "vitest";
import { messagesAfter, parseSideQuestion } from "@/lib/side-conversation";

describe("parseSideQuestion", () => {
  it("reads the question after /btw", () => {
    expect(parseSideQuestion("/btw what changed?")).toBe("what changed?");
    expect(parseSideQuestion("  /btw   spaced out  ")).toBe("spaced out");
    expect(parseSideQuestion("/btw first line\nsecond line")).toBe("first line\nsecond line");
  });

  it("gives an empty question for a bare /btw", () => {
    expect(parseSideQuestion("/btw")).toBe("");
    expect(parseSideQuestion("/btw   ")).toBe("");
  });

  it("isn't fooled by other commands or text", () => {
    expect(parseSideQuestion("/btwx question")).toBeNull();
    expect(parseSideQuestion("/compact")).toBeNull();
    expect(parseSideQuestion("by the way /btw")).toBeNull();
    expect(parseSideQuestion("")).toBeNull();
  });
});

describe("messagesAfter", () => {
  const messages = [{ messageId: "a" }, { messageId: "b" }, { messageId: "c" }];

  it("keeps what comes after the boundary", () => {
    expect(messagesAfter(messages, "a")).toEqual([{ messageId: "b" }, { messageId: "c" }]);
    expect(messagesAfter(messages, "c")).toEqual([]);
  });

  it("keeps everything without a boundary, or when it isn't loaded", () => {
    expect(messagesAfter(messages, null)).toBe(messages);
    expect(messagesAfter(messages, "older")).toBe(messages);
  });
});
