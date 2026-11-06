import { describe, expect, test } from "bun:test";
import {
  adoptOrder, betterAddress, canUse, isBest, learnAddresses, orderForHome, pickAddress, recordOutcomes, reorderAddresses, routeLine,
  seedAddresses, type MachineAddress, type Probe, type ProbeOutcome,
} from "./routes-core";

const TS_NAME = "http://studio-desktop.tail4f2a.ts.net:2113";
const TS_HTTPS = "https://studio-desktop.tail4f2a.ts.net";
const TS_IP = "http://100.88.14.3:2113";
const LAN = "http://192.168.1.42:2113";
const HOST = "http://studio-desktop:2113";

const learned = (url: string, kind: MachineAddress["kind"]): MachineAddress => ({ url, kind, learned: true });
const urls = (list: MachineAddress[]) => list.map((a) => a.url);

/** A probe that answers from a table after a delay, and records what it was asked. */
function fakeProbe(answers: Record<string, ProbeOutcome | [ProbeOutcome, number]>) {
  const asked: string[] = [];
  const probe: Probe = (url, signal) => {
    asked.push(url);
    const entry = answers[url] ?? "no-answer";
    const [outcome, delay] = Array.isArray(entry) ? entry : [entry, 0];
    return new Promise((resolve) => {
      const timer = setTimeout(() => resolve(outcome), delay);
      signal.addEventListener("abort", () => {
        clearTimeout(timer);
        resolve("no-answer");
      });
    });
  };
  return { probe, asked };
}

describe("which addresses a phone uses", () => {
  test("https anywhere, plain http only on Tailscale, the local network or this device", () => {
    expect(canUse(TS_HTTPS)).toBe(true);
    expect(canUse("https://fleet.example.com")).toBe(true);
    expect(canUse(TS_NAME)).toBe(true);
    expect(canUse(TS_IP)).toBe(true);
    expect(canUse(LAN)).toBe(true);
    expect(canUse("http://10.0.2.2:5431")).toBe(true);
    expect(canUse("http://127.0.0.1:5431")).toBe(true);
    // A host name could resolve to anything; a public address over http would carry the key in the clear.
    expect(canUse(HOST)).toBe(false);
    expect(canUse("http://203.0.113.9:2113")).toBe(false);
    expect(canUse("http://fleet.example.com")).toBe(false);
    expect(canUse("not a url")).toBe(false);
  });
});

describe("learning a machine's addresses", () => {
  test("the paired address stays first and what the machine reports goes in at its kind's place", () => {
    const list = learnAddresses(seedAddresses(TS_HTTPS + "/"), [HOST, LAN, TS_IP, TS_NAME], TS_HTTPS);
    expect(urls(list)).toEqual([TS_HTTPS, TS_NAME, TS_IP, LAN, HOST]);
    expect(list[0]).toEqual({ url: TS_HTTPS, kind: "tailnet", learned: false });
    expect(list.slice(1).every((a) => a.learned)).toBe(true);
  });

  test("a new DHCP lease takes the old address's place, so the owner's order holds", () => {
    const current = [learned(LAN, "lan"), ...seedAddresses(TS_HTTPS), learned(TS_IP, "tailnet-ip")];
    const list = learnAddresses(current, ["http://192.168.1.77:2113", TS_IP], TS_HTTPS);
    expect(urls(list)).toEqual(["http://192.168.1.77:2113", TS_HTTPS, TS_IP]);
  });

  test("a learned address no longer reported drops away, unless it's the one in use", () => {
    const current = [...seedAddresses(TS_HTTPS), learned(TS_IP, "tailnet-ip"), learned(LAN, "lan")];
    expect(urls(learnAddresses(current, [TS_IP], TS_HTTPS))).toEqual([TS_HTTPS, TS_IP]);
    expect(urls(learnAddresses(current, [TS_IP], LAN))).toEqual([TS_HTTPS, TS_IP, LAN]);
  });

  test("a new address never jumps above one the owner moved up", () => {
    const current = [learned(LAN, "lan"), ...seedAddresses(TS_HTTPS)];
    expect(urls(learnAddresses(current, [LAN, TS_IP], TS_HTTPS))).toEqual([LAN, TS_HTTPS, TS_IP]);
  });
});

describe("the owner's order", () => {
  const list = [...seedAddresses(TS_HTTPS), learned(TS_IP, "tailnet-ip"), learned(LAN, "lan")];

  test("reordering takes exactly the machine's addresses", () => {
    expect(urls(reorderAddresses(list, [LAN, TS_HTTPS, TS_IP])!)).toEqual([LAN, TS_HTTPS, TS_IP]);
    expect(reorderAddresses(list, [LAN, TS_HTTPS])).toBeNull();
    expect(reorderAddresses(list, [LAN, LAN, TS_IP])).toBeNull();
    expect(reorderAddresses(list, [LAN, TS_HTTPS, HOST])).toBeNull();
  });

  test("home's order applies to the addresses home lists; the phone's own https one keeps its place", () => {
    expect(urls(adoptOrder(list, [LAN, TS_NAME, TS_IP]))).toEqual([TS_HTTPS, LAN, TS_IP]);
  });

  test("the order saved on home is home's list as the phone orders it", () => {
    const phone = reorderAddresses(list, [LAN, TS_HTTPS, TS_IP])!;
    expect(orderForHome(phone, [TS_NAME, TS_IP, LAN])).toEqual([LAN, TS_IP, TS_NAME]);
  });
});

describe("picking an address", () => {
  const list = [...seedAddresses(TS_HTTPS), learned(TS_IP, "tailnet-ip"), learned(LAN, "lan"), learned(HOST, "hostname")];

  test("stays on the address in use when it answers as this machine", async () => {
    const { probe, asked } = fakeProbe({ [TS_HTTPS]: "ok" });
    expect((await pickAddress(list, TS_HTTPS, probe)).url).toBe(TS_HTTPS);
    expect(asked).toEqual([TS_HTTPS]);
  });

  test("falls back to the first in the order that answers, never trying a host name over http", async () => {
    const { probe, asked } = fakeProbe({ [TS_IP]: ["no-answer", 30], [LAN]: "ok" });
    const picked = await pickAddress(list, TS_HTTPS, probe);
    expect(picked.url).toBe(LAN);
    expect(picked.outcomes).toEqual({ [TS_HTTPS]: "no-answer", [TS_IP]: "no-answer", [LAN]: "ok" });
    expect(asked).not.toContain(HOST);
  });

  test("a device that took over an old address is skipped", async () => {
    const { probe } = fakeProbe({ [TS_IP]: "other-machine", [LAN]: "ok" });
    const picked = await pickAddress(list, TS_HTTPS, probe);
    expect(picked.url).toBe(LAN);
    expect(picked.outcomes[TS_IP]).toBe("other-machine");
  });

  test("an earlier address that answers late still wins over a later one that answers first", async () => {
    // The local network starts 750 ms in and answers at once; Tailscale's IP answers at 900 ms and still wins.
    const { probe, asked } = fakeProbe({ [TS_IP]: ["ok", 900], [LAN]: "ok" });
    expect((await pickAddress(list, TS_HTTPS, probe)).url).toBe(TS_IP);
    expect(asked).toContain(LAN);
  });

  test("the paired address is trusted when the Fleet there is too old to say who it is; a learned one isn't", async () => {
    expect((await pickAddress(list, TS_HTTPS, fakeProbe({ [TS_HTTPS]: "no-probe", [LAN]: "ok" }).probe)).url).toBe(TS_HTTPS);
    expect((await pickAddress(list, TS_IP, fakeProbe({ [TS_IP]: "no-probe", [LAN]: "ok" }).probe)).url).toBe(LAN);
  });

  test("nothing answering keeps the address in use", async () => {
    expect((await pickAddress(list, LAN, fakeProbe({}).probe)).url).toBe(LAN);
  });
});

describe("moving back", () => {
  const list = [...seedAddresses(TS_HTTPS), learned(TS_IP, "tailnet-ip"), learned(LAN, "lan")];

  test("finds the first address above the one in use that answers again", async () => {
    const { probe, asked } = fakeProbe({ [TS_IP]: "ok" });
    expect((await betterAddress(list, LAN, probe)).winner).toBe(TS_IP);
    expect(asked).not.toContain(LAN);
  });

  test("finds nothing while Tailscale is still down", async () => {
    const better = await betterAddress(list, LAN, fakeProbe({}).probe);
    expect(better.winner).toBeNull();
    expect(better.outcomes).toEqual({ [TS_HTTPS]: "no-answer", [TS_IP]: "no-answer" });
  });

  test("nothing to look for on the first address", async () => {
    const { probe, asked } = fakeProbe({});
    expect(isBest(list, TS_HTTPS)).toBe(true);
    expect(isBest(list, LAN)).toBe(false);
    expect((await betterAddress(list, TS_HTTPS, probe)).winner).toBeNull();
    expect(asked).toEqual([]);
  });

  test("an address the phone can't use doesn't count as better", () => {
    expect(isBest([learned(HOST, "hostname"), learned(LAN, "lan")], LAN)).toBe(true);
  });
});

describe("the machine screen", () => {
  const list = [...seedAddresses(TS_HTTPS), learned(TS_IP, "tailnet-ip"), learned(LAN, "lan")];

  test("says how the phone is connected, and why when it fell back", () => {
    expect(routeLine(list, TS_HTTPS)).toEqual({ text: "Connected over Tailscale", note: null });
    expect(routeLine(list, TS_IP)).toEqual({ text: "Connected over Tailscale", note: null });
    expect(routeLine(list, LAN)).toEqual({ text: "Connected on your network", note: "Tailscale isn't answering" });
    expect(routeLine([learned(LAN, "lan")], LAN)).toEqual({ text: "Connected on your network", note: null });
  });

  test("records what each probe found", () => {
    const at = new Date("2026-11-06T12:00:00Z");
    const recorded = recordOutcomes(list, { [TS_HTTPS]: "no-answer", [TS_IP]: "other-machine", [LAN]: "ok" }, at);
    expect(recorded.map((a) => a.lastResult)).toEqual(["no-answer", "other-machine", "ok"]);
    expect(recorded[0].lastTriedAt).toBe(at.toISOString());
  });
});
