import { describe, expect, it } from "vitest";
import { splitTurnErrorMessage } from "@/lib/turn-error";

describe("splitTurnErrorMessage", () => {
  it("keeps the sentence and folds the stack trace OpenCode appends", () => {
    const message = "ProviderModelNotFoundError: Model not found: github-copilot/claude-sonnet-4.5. Did you mean: claude-sonnet-5? "
      + "at <anonymous> (/$bunfs/root/chunk-y568bp57.js:439:94278) at SessionPrompt.getModel (/$bunfs/root/chunk-9mk16qmt.js:1142:11505)";

    const { summary, details } = splitTurnErrorMessage(message);

    expect(summary).toBe("Model not found: github-copilot/claude-sonnet-4.5. Did you mean: claude-sonnet-5?");
    expect(details).toBe("at <anonymous> (/$bunfs/root/chunk-y568bp57.js:439:94278) at SessionPrompt.getModel (/$bunfs/root/chunk-9mk16qmt.js:1142:11505)");
  });

  it("leaves a message without a stack trace as it is", () => {
    expect(splitTurnErrorMessage("Rate limit reached, try again in 20 seconds")).toEqual({
      summary: "Rate limit reached, try again in 20 seconds",
      details: null,
    });
  });

  it("keeps an error name that is the whole message", () => {
    expect(splitTurnErrorMessage("AbortError: ")).toEqual({ summary: "AbortError:", details: null });
  });

  it("finds multi-line stack traces", () => {
    const { summary, details } = splitTurnErrorMessage("TypeError: x is undefined\n    at run (file:///a.js:1:2)\n    at main (file:///a.js:3:4)");
    expect(summary).toBe("x is undefined");
    expect(details).toBe("at run (file:///a.js:1:2)\n    at main (file:///a.js:3:4)");
  });
});
