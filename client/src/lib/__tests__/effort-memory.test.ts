import { describe, expect, it } from "vitest";
import { effortLabel, effortPreferenceKey, rememberedEffort } from "@/lib/effort-memory";

describe("effort memory", () => {
  it("keys one level per harness and model, in one URL segment", () => {
    expect(effortPreferenceKey("opencode2", "anthropic/opus-5.5")).toBe("effort.opencode2.anthropic:opus-5.5");
    expect(effortPreferenceKey("claude-code", JSON.stringify(["anthropic", "claude-opus-5-5"]))).toBe("effort.claude-code.anthropic:claude-opus-5-5");
  });

  it("returns the remembered level only while the model still offers it", () => {
    expect(rememberedEffort("high", ["low", "medium", "high"])).toBe("high");
    expect(rememberedEffort("xhigh", ["low", "medium", "high"])).toBe("");
    expect(rememberedEffort("", ["low"])).toBe("");
    expect(rememberedEffort(undefined, ["low"])).toBe("");
  });

  it("names an unset level Default", () => {
    expect(effortLabel("")).toBe("Default");
    expect(effortLabel("xhigh")).toBe("Xhigh");
  });
});
