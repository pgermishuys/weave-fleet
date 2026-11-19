import { describe, expect, test } from "bun:test";
import { encodePairingPayload } from "@fleet/lib/pairing";
import { checkAddress, HTTPS_ONLY, redeemPairing, resolvePairing, targetFromScan, targetFromTyped } from "./pairing";

const link = (url: string) =>
  `${url}/pair#p=${encodePairingPayload({ v: 1, machineId: "m1", machineName: "Hangar", url, secret: "s3cret" })}`;

describe("checkAddress", () => {
  test("accepts https, with or without a port or trailing slash", () => {
    expect(checkAddress(" https://hangar.tail9c2e.ts.net/ ")).toBe("https://hangar.tail9c2e.ts.net");
    expect(checkAddress("https://10.0.0.5:5431")).toBe("https://10.0.0.5:5431");
  });
  test("accepts plain http only to this computer", () => {
    expect(checkAddress("http://127.0.0.1:5431")).toBe("http://127.0.0.1:5431");
    expect(checkAddress("http://localhost:5431")).toBe("http://localhost:5431");
  });
  test("says what is needed otherwise", () => {
    for (const bad of ["http://hangar.local", "hangar.local", "ftp://x", "", "https://"]) {
      expect(() => checkAddress(bad)).toThrow(HTTPS_ONLY);
    }
  });
});

describe("targetFromScan", () => {
  test("reads the pairing link", () => {
    expect(targetFromScan(link("https://hangar.example"))).toEqual({ baseUrl: "https://hangar.example", secret: "s3cret" });
  });
  test("ignores other codes", () => {
    expect(targetFromScan("https://example.com/menu")).toBeNull();
    expect(targetFromScan("https://example.com/pair#p=garbage")).toBeNull();
  });
  test("refuses a link over plain http", () => {
    expect(() => targetFromScan(link("http://hangar.example"))).toThrow(HTTPS_ONLY);
  });
});

const many = (urls: string[]) =>
  `${urls[0]}/pair#p=${encodePairingPayload({ v: 1, machineId: "m1", machineName: "Hangar", url: urls[0], secret: "s3cret", urls })}`;

describe("a code with every address", () => {
  const tailnet = "http://hangar.tail4f2a.ts.net:6262";
  const lan = "http://192.168.1.31:6262";

  test("keeps each address the phone may use, in order, with the machine's id to check", () => {
    expect(targetFromScan(many([tailnet, lan]))).toEqual({ baseUrl: tailnet, secret: "s3cret", machineId: "m1", addresses: [tailnet, lan] });
  });
  test("leaves out an address the phone wouldn't use over plain http, and keeps the rest", () => {
    expect(targetFromScan(many(["https://hangar.example", "http://hangar.example", lan]))?.addresses).toEqual(["https://hangar.example", lan]);
  });
  test("refuses a code none of whose addresses it may use", () => {
    expect(() => targetFromScan(many(["http://hangar.example", "http://203.0.113.9:6262"]))).toThrow(HTTPS_ONLY);
  });
  test("goes to the first address where the machine answers with its id", async () => {
    const target = targetFromScan(many([tailnet, lan]))!;
    const asked: string[] = [];
    const resolved = await resolvePairing(target, async (url) => {
      asked.push(url);
      return url === lan ? "ok" : "no-answer";
    });
    expect(resolved.baseUrl).toBe(lan);
    expect(asked).toContain(tailnet);
  });
  test("sends nothing to an address where another machine answers", async () => {
    const target = targetFromScan(many([tailnet, lan]))!;
    await expect(resolvePairing(target, async () => "other-machine")).rejects.toThrow("Couldn't reach Fleet");
  });
  test("uses a code with one address as it is, with no check", async () => {
    const target = targetFromScan(link("https://hangar.example"))!;
    expect(await resolvePairing(target, async () => "no-answer")).toBe(target);
  });
  test("the machine keeps every address from the code, the one that answered first", async () => {
    const original = globalThis.fetch;
    globalThis.fetch = (async () => new Response(JSON.stringify({ deviceId: "d1", token: "t", machine: { id: "m1", name: "Hangar" } }), { status: 200 })) as unknown as typeof fetch;
    try {
      const target = { ...targetFromScan(many([tailnet, lan]))!, baseUrl: lan };
      const machine = await redeemPairing(target, "iPhone", "ios");
      expect(machine.baseUrl).toBe(lan);
      expect(machine.addresses?.map((a) => a.url)).toEqual([lan, tailnet]);
      expect(machine.addresses?.map((a) => a.learned)).toEqual([false, true]);
    } finally {
      globalThis.fetch = original;
    }
  });
});

describe("targetFromTyped", () => {
  test("normalises the code", () => {
    expect(targetFromTyped("https://h.example", "k7qm 4t2x")).toEqual({ baseUrl: "https://h.example", manualCode: "K7QM-4T2X" });
  });
  test("rejects a short code", () => {
    expect(() => targetFromTyped("https://h.example", "K7Q")).toThrow("8 letters");
  });
});
