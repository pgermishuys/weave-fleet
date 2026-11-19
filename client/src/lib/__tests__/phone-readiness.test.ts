import { describe, expect, it } from "vitest";
import { reachLines } from "@/lib/phone-readiness";

describe("reachLines", () => {
  it("names the home network and Tailscale, each with its address", () => {
    expect(reachLines(["http://studio.tail4f2a.ts.net:6262", "http://192.168.1.20:6262"])).toEqual([
      { id: "home", title: "At home, on your Wi-Fi", hosts: ["192.168.1.20"] },
      { id: "tailscale", title: "Away from home, with Tailscale on the phone", hosts: ["studio.tail4f2a.ts.net"] },
    ]);
  });

  it("shows the Tailscale address only when there is no name", () => {
    expect(reachLines(["http://100.64.90.72:6262"]).map((line) => line.hosts)).toEqual([["100.64.90.72"]]);
    expect(reachLines(["http://100.64.90.72:6262", "http://studio.tail4f2a.ts.net:6262"]).map((line) => line.hosts)).toEqual([["studio.tail4f2a.ts.net"]]);
  });

  it("falls back to the host name for the home network when there is no private address", () => {
    expect(reachLines(["http://studio:6262"])).toEqual([{ id: "home", title: "At home, on your Wi-Fi", hosts: ["studio"] }]);
  });

  it("lists an address of the person's own, and never a loopback one", () => {
    expect(reachLines(["https://fleet.example.com", "http://127.0.0.1:6262", "http://localhost:6262"])).toEqual([
      { id: "own", title: "From anywhere, at your own address", hosts: ["fleet.example.com"] },
    ]);
  });

  it("has nothing to say about a machine that listens only on itself", () => {
    expect(reachLines(["http://127.0.0.1:6262"])).toEqual([]);
    expect(reachLines([])).toEqual([]);
  });
});
