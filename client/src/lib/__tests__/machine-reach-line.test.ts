import { describe, expect, it } from "vitest";
import { reachLine } from "@/lib/machine-reach-line";

const tailnet = { kind: "tailnet" as const };
const lan = { kind: "lan" as const };

describe("reachLine", () => {
  it("says a machine on Tailscale and the home network reaches your phone both ways", () => {
    expect(reachLine({ name: "atlas", answering: true, addresses: [tailnet, lan] }))
      .toEqual({ text: "Reaches your phone over Tailscale and the home network", tone: "ok" });
  });

  it("says a machine with only Tailscale addresses is only on Tailscale", () => {
    expect(reachLine({ name: "atlas", answering: true, addresses: [tailnet, { kind: "tailnet-ip" }] }))
      .toEqual({ text: "Only on Tailscale", tone: "limited" });
  });

  it("says the home network alone, and the person's own address", () => {
    expect(reachLine({ name: "atlas", answering: true, addresses: [lan] }).text).toBe("Reaches your phone on the home network");
    expect(reachLine({ name: "studio", answering: true, addresses: [], reachMode: "own", ownAddress: "https://fleet.example.com" }).text)
      .toBe("Reaches your phone at https://fleet.example.com");
  });

  it("says the desktop app is only this machine until it lets others reach it", () => {
    expect(reachLine({ name: "studio", answering: true, addresses: [], reachMode: "off" }))
      .toEqual({ text: "Only this machine", tone: "limited" });
  });

  it("says when a machine stopped answering, apart from the Tailscale case", () => {
    const at = new Date(2026, 10, 19, 8, 40).getTime();
    expect(reachLine({ name: "atlas", answering: false, offlineSince: at, addresses: [tailnet] }))
      .toEqual({ text: "Off since 08:40", tone: "out" });
    expect(reachLine({ name: "atlas", answering: false, addresses: [lan] }).text).toBe("Not reachable");
  });
});
