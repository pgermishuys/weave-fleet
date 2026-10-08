import { describe, expect, it } from "vitest";
import { Cable, Hexagon, TerminalSquare } from "lucide-vue-next";
import type { HarnessPresentation } from "@/api/client";
import { harnessDisplay, harnessShortName, splitCode } from "@/lib/harness-display";

const presentation = (extra: Partial<HarnessPresentation> = {}): HarnessPresentation => ({
  order: 0,
  eyebrow: "CLI harness",
  description: "An invented harness for tests.",
  icon: "terminal",
  ...extra,
});

describe("harnessDisplay", () => {
  it("shows the harness as it describes itself", () => {
    const display = harnessDisplay({ presentation: presentation({ icon: "hexagon", pitch: "Fast and small." }) });

    expect(display).toEqual({
      eyebrow: "CLI harness",
      description: "An invented harness for tests.",
      icon: Hexagon,
      pitch: "Fast and small.",
    });
  });

  it("draws a plug for an icon it doesn't know", () => {
    expect(harnessDisplay({ presentation: presentation({ icon: "rocket" }) }).icon).toBe(Cable);
    expect(harnessDisplay({ presentation: presentation() }).icon).toBe(TerminalSquare);
  });

  it("stands in for a Fleet that sends no presentation", () => {
    expect(harnessDisplay({ presentation: undefined })).toEqual({
      eyebrow: "Harness",
      description: "Harness runtime registered by the backend.",
      icon: Cable,
      pitch: undefined,
    });
  });
});

describe("harnessShortName", () => {
  it("uses the short name the harness sends, else its display name", () => {
    expect(harnessShortName({ displayName: "Acme Code", presentation: presentation({ shortName: "Acme" }) })).toBe("Acme");
    expect(harnessShortName({ displayName: "Acme Code", presentation: presentation({ shortName: null }) })).toBe("Acme Code");
    expect(harnessShortName({ displayName: "Acme Code" })).toBe("Acme Code");
  });
});

describe("splitCode", () => {
  it("marks the pieces between backticks as code", () => {
    expect(splitCode("Hands it over as `ACME_CONFIG`, then `{env:NAME}`.")).toEqual([
      { text: "Hands it over as ", code: false },
      { text: "ACME_CONFIG", code: true },
      { text: ", then ", code: false },
      { text: "{env:NAME}", code: true },
      { text: ".", code: false },
    ]);
  });
});
