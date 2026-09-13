import { describe, expect, it } from "vitest";
import { formatTerminalContext, terminalLineRange, type TerminalContextDraft } from "@/lib/format-terminal-context";

function context(overrides: Partial<TerminalContextDraft> = {}): TerminalContextDraft {
  return { id: "c1", terminalId: "t1", label: "zsh", from: 9, to: 11, text: "one\ntwo", ...overrides };
}

describe("terminalLineRange", () => {
  it("names one line or a range", () => {
    expect(terminalLineRange(3, 3)).toBe("line 3");
    expect(terminalLineRange(9, 11)).toBe("lines 9–11");
  });
});

describe("formatTerminalContext", () => {
  it("leaves a message with nothing attached alone", () => {
    expect(formatTerminalContext([], "hello")).toBe("hello");
  });

  it("puts each attachment in a labelled block before the message", () => {
    const text = formatTerminalContext(
      [context(), context({ id: "c2", label: "zsh 2", from: 1, to: 1, text: "ready" })],
      "  Why?  ",
    );

    expect(text).toBe(
      "Terminal zsh, lines 9–11:\n```text\none\ntwo\n```\n\n"
      + "Terminal zsh 2, line 1:\n```text\nready\n```\n\n"
      + "Why?",
    );
  });

  it("sends just the blocks when nothing was typed", () => {
    expect(formatTerminalContext([context()], "   ")).toBe("Terminal zsh, lines 9–11:\n```text\none\ntwo\n```");
  });

  it("uses a longer fence when the output has one of its own", () => {
    const text = formatTerminalContext([context({ text: "```js\nx\n```" })], "");

    expect(text).toBe("Terminal zsh, lines 9–11:\n````text\n```js\nx\n```\n````");
  });
});
