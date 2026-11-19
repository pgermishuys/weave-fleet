import { describe, expect, it } from "vitest";
import type { MachineAddress } from "@/lib/machine-routes";
import { pairWarning, phoneTailnet, reachNote, tailscaleLink, urlsOnlyOnTailscale, type ReachInput } from "@/lib/phone-reach";

const tail = (lastResult?: MachineAddress["lastResult"]): MachineAddress => ({ url: "http://atlas.tail4f2a.ts.net:6262", kind: "tailnet", learned: true, lastResult });
const tailIp: MachineAddress = { url: "http://100.64.0.7:6262", kind: "tailnet-ip", learned: true };
const lan = (lastResult?: MachineAddress["lastResult"]): MachineAddress => ({ url: "http://192.168.1.20:6262", kind: "lan", learned: true, lastResult });

const atlas = (state: ReachInput["state"], extra: Partial<ReachInput> = {}): ReachInput => ({ machineName: "atlas", state, addresses: [tail("no-answer"), tailIp], ...extra });

describe("reachNote", () => {
  it("says a Tailscale-only machine is out of reach when the phone isn't on the tailnet, with Get Tailscale", () => {
    const note = reachNote(atlas("offline"), "unknown", false);
    expect(note).toEqual({
      kind: "tailscale-only",
      headline: "Out of reach from this phone",
      detail: "atlas is only on Tailscale",
      action: { kind: "get", label: "Get Tailscale" },
    });
  });

  it("offers Turn on Tailscale once the phone has reached something over Tailscale before", () => {
    expect(reachNote(atlas("offline"), "off", true)?.action).toEqual({ kind: "turn-on", label: "Turn on Tailscale" });
  });

  it("keeps a machine that is off apart from one that is out of reach", () => {
    const at = new Date(2026, 10, 19, 8, 40).getTime();
    const note = reachNote({ machineName: "hangar", state: "offline", addresses: [lan("no-answer"), tail()], offlineSince: at }, "on", true);
    expect(note?.kind).toBe("offline");
    expect(note?.headline).toMatch(/^Off since .*8[:.]40/);
    expect(note?.action).toBeNull();
  });

  it("calls a Tailscale-only machine off, not out of reach, while the phone is on the tailnet", () => {
    expect(reachNote(atlas("offline"), "on", true)?.kind).toBe("offline");
  });

  it("says nothing while connected, connecting or signed out", () => {
    for (const state of ["connected", "connecting", "signed-out"] as const) expect(reachNote(atlas(state), "off", false)).toBeNull();
  });
});

describe("phoneTailnet", () => {
  it("is on when any machine answered over Tailscale", () => {
    expect(phoneTailnet([{ state: "connected", addresses: [tail("ok")] }])).toBe("on");
  });

  it("is off when another machine answers on the home network and Tailscale addresses don't answer", () => {
    expect(phoneTailnet([{ state: "connected", addresses: [lan("ok")] }, { state: "offline", addresses: [tail("no-answer")] }])).toBe("off");
  });

  it("is unknown when nothing answers at all", () => {
    expect(phoneTailnet([{ state: "offline", addresses: [tail("no-answer")] }])).toBe("unknown");
  });
});

describe("pairWarning", () => {
  it("warns before adding a Tailscale-only machine from a phone that isn't on Tailscale", () => {
    const warning = pairWarning("atlas", ["https://atlas.tail4f2a.ts.net", "http://100.64.0.7:6262"], "unknown", false);
    expect(warning?.title).toBe("atlas is only on Tailscale");
    expect(warning?.action.label).toBe("Get Tailscale");
  });

  it("doesn't warn when the machine has a home address too, or the phone is on the tailnet", () => {
    expect(pairWarning("studio", ["http://100.64.0.2:6262", "http://192.168.1.20:6262"], "off", false)).toBeNull();
    expect(pairWarning("atlas", ["http://100.64.0.7:6262"], "on", true)).toBeNull();
  });

  it("knows a code's addresses", () => {
    expect(urlsOnlyOnTailscale(["http://100.64.0.7:6262"])).toBe(true);
    expect(urlsOnlyOnTailscale([])).toBe(false);
  });
});

describe("tailscaleLink", () => {
  it("goes to the store for the platform", () => {
    expect(tailscaleLink("ios")).toContain("apps.apple.com");
    expect(tailscaleLink("android")).toContain("play.google.com");
    expect(tailscaleLink("web")).toBe("https://tailscale.com/download");
  });
});
