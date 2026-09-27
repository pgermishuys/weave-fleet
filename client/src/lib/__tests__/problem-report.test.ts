import { afterEach, describe, expect, it } from "vitest";
import {
  clientPrivateValues,
  countLabels,
  describeApp,
  describeScreen,
  splitLabels,
  summarizeReplacements,
} from "@/lib/problem-report";

describe("problem report helpers", () => {
  afterEach(() => {
    localStorage.clear();
    delete (window as { fleetDesktop?: unknown }).fleetDesktop;
  });

  it("splits text into plain runs and labels", () => {
    expect(splitLabels("in ‹folder-1›/x by ‹user›.")).toEqual([
      { text: "in ", label: false },
      { text: "‹folder-1›", label: true },
      { text: "/x by ", label: false },
      { text: "‹user›", label: true },
      { text: ".", label: false },
    ]);
    expect(countLabels("‹secret-1› and ‹secret-2›")).toBe(2);
    expect(countLabels(null)).toBe(0);
  });

  it("sums up what was replaced in words", () => {
    expect(summarizeReplacements([
      { label: "‹folder-1›", kind: "folder", shown: "/a" },
      { label: "‹folder-2›", kind: "folder", shown: "/b" },
      { label: "‹secret-1›", kind: "secret", shown: "ghp_…" },
    ])).toBe("2 folders, 1 token");
  });

  it("names the browser, or the desktop app when it's there", () => {
    expect(describeApp("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36")).toBe("Chrome 140");
    expect(describeApp("Mozilla/5.0 Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0")).toBe("Edge 140");
    expect(describeApp("Mozilla/5.0 (Macintosh) AppleWebKit/605.1.15 Version/19.1 Safari/605.1.15")).toBe("Safari 19");

    (window as { fleetDesktop?: unknown }).fleetDesktop = { version: "0.39.0", platform: "linux", getUpdateState: () => undefined };
    expect(describeApp("anything")).toBe("Desktop app 0.39.0 · linux");
  });

  it("names the screen from the address", () => {
    expect(describeScreen("/")).toBe("Sessions");
    expect(describeScreen("/sessions/abc")).toBe("Sessions");
    expect(describeScreen("/settings")).toBe("Settings");
    expect(describeScreen("/automations/a1")).toBe("Automations");
  });

  it("keeps other machines' names and addresses, and Fleet's own address, out of the report", () => {
    localStorage.setItem("weave:machines", JSON.stringify([
      { id: "m1", name: "studio-mac", baseUrl: "http://100.64.90.72:2113", token: "t", addedAt: "" },
    ]));

    expect(clientPrivateValues("https://fleet.example.net")).toEqual([
      { kind: "machine", value: "studio-mac" },
      { kind: "url", value: "http://100.64.90.72:2113" },
      { kind: "url", value: "https://fleet.example.net" },
    ]);
  });
});
