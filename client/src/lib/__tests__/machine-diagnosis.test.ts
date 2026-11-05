import { describe, expect, it } from "vitest";
import { diagnosisText, mixedContentDiagnosis, tunnelDownDiagnosis } from "@/lib/machine-diagnosis";

describe("machine diagnosis", () => {
  it("says an https page can't call an http address, and nothing otherwise", () => {
    const diagnosis = mixedContentDiagnosis("https:", "http://100.64.90.72:2113", "falcon")!;

    expect(diagnosis.code).toBe("https-page");
    expect(diagnosis.try).toBe("This page is https, so the browser won't call an http address. Use the machine's tailscale serve address.");
    expect(diagnosis.steps.map((step) => step.state)).toEqual(["ok", "failed", "notChecked", "notChecked", "notChecked", "notChecked"]);
    expect(mixedContentDiagnosis("https:", "https://falcon.tail9c2e.ts.net", "falcon")).toBeNull();
    expect(mixedContentDiagnosis("http:", "http://100.64.90.72:2113", "falcon")).toBeNull();
  });

  it("gives a down tunnel its own headline", () => {
    expect(tunnelDownDiagnosis("osprey", "alice@osprey").headline).toBe("The SSH tunnel to osprey is down");
    expect(tunnelDownDiagnosis("osprey", "alice@osprey").try).toContain("ssh alice@osprey");
  });

  it("writes the details as text to paste", () => {
    const text = diagnosisText(tunnelDownDiagnosis("osprey", null));

    expect(text.split("\n")[0]).toBe("The SSH tunnel to osprey is down");
    expect(text).toContain("- Machine answers: failed (The tunnel Fleet keeps open isn't up.)");
    expect(text).toContain("- Key accepted: not checked");
  });
});
