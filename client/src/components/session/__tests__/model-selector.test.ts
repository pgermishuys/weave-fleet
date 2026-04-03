import { describe, it, expect } from "vitest";
import { composeSearchValue, parseModelValue } from "../model-selector";

describe("composeSearchValue", () => {
  it("includes provider name, model name, and model ID", () => {
    const result = composeSearchValue(
      "OpenRouter",
      "Claude 3.5 Sonnet",
      "anthropic/claude-3.5-sonnet"
    );
    expect(result).toContain("OpenRouter");
    expect(result).toContain("Claude 3.5 Sonnet");
    expect(result).toContain("anthropic/claude-3.5-sonnet");
  });

  it("handles empty strings without crashing", () => {
    const result = composeSearchValue("", "", "");
    expect(typeof result).toBe("string");
  });

  it("separates fields with spaces for multi-field matching", () => {
    const result = composeSearchValue("Anthropic", "Haiku", "claude-3-haiku");
    expect(result).toBe("Anthropic Haiku claude-3-haiku");
  });
});

describe("parseModelValue", () => {
  it("parses providerID::modelID format", () => {
    expect(parseModelValue("openai::gpt-4")).toEqual({
      providerID: "openai",
      modelID: "gpt-4",
    });
  });

  it("returns null for __default__", () => {
    expect(parseModelValue("__default__")).toBeNull();
  });

  it("returns null for value without separator", () => {
    expect(parseModelValue("no-separator")).toBeNull();
  });

  it("handles model IDs containing colons", () => {
    expect(parseModelValue("aws::us.anthropic.claude-3:5")).toEqual({
      providerID: "aws",
      modelID: "us.anthropic.claude-3:5",
    });
  });
});
