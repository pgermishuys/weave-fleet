import { describe, expect, test } from "bun:test";
import { encodePairingPayload } from "@fleet/lib/pairing";
import { checkAddress, HTTPS_ONLY, targetFromScan, targetFromTyped } from "./pairing";

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

describe("targetFromTyped", () => {
  test("normalises the code", () => {
    expect(targetFromTyped("https://h.example", "k7qm 4t2x")).toEqual({ baseUrl: "https://h.example", manualCode: "K7QM-4T2X" });
  });
  test("rejects a short code", () => {
    expect(() => targetFromTyped("https://h.example", "K7Q")).toThrow("8 letters");
  });
});
