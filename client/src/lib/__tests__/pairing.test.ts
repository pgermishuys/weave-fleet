import { describe, expect, it } from "vitest";
import {
  choosePhoneBaseUrl,
  decodePairingFragment,
  encodePairingPayload,
  guessDeviceName,
  guessPlatform,
  isLoopbackHost,
  normalizeManualCode,
  pairingUrl,
  supportsInstall,
  type PairingPayloadV1,
} from "../pairing";

const payload: PairingPayloadV1 = {
  v: 1,
  machineId: "4f1c",
  machineName: "hangar ✈",
  url: "https://hangar.tail9c2e.ts.net",
  secret: "abc-_123",
};

describe("pairing payload", () => {
  it("round-trips through the fragment", () => {
    const url = pairingUrl(payload);
    expect(url.startsWith("https://hangar.tail9c2e.ts.net/pair#p=")).toBe(true);
    expect(decodePairingFragment(new URL(url).hash)).toEqual(payload);
    expect(decodePairingFragment(`p=${encodePairingPayload(payload)}`)).toEqual(payload);
  });

  it("reads what the server encodes", () => {
    // Fleet's own encoding: base64url of the camelCase JSON, no padding.
    const json = JSON.stringify({ v: 1, machineId: "m", machineName: "n", url: "http://x:2113", secret: "s" });
    const encoded = Buffer.from(json).toString("base64url");
    expect(decodePairingFragment(`#p=${encoded}`)?.url).toBe("http://x:2113");
  });

  it("refuses another version", () => {
    const v2 = Buffer.from(JSON.stringify({ ...payload, v: 2 })).toString("base64url");
    expect(decodePairingFragment(`#p=${v2}`)).toBeNull();
  });

  it("refuses missing fields, junk and non-http urls", () => {
    const missing = Buffer.from(JSON.stringify({ v: 1, machineId: "m" })).toString("base64url");
    const js = Buffer.from(JSON.stringify({ ...payload, url: "javascript:alert(1)" })).toString("base64url");
    expect(decodePairingFragment(`#p=${missing}`)).toBeNull();
    expect(decodePairingFragment(`#p=${js}`)).toBeNull();
    expect(decodePairingFragment("#p=%%%")).toBeNull();
    expect(decodePairingFragment("")).toBeNull();
    expect(decodePairingFragment("#q=1")).toBeNull();
  });
});

describe("choosePhoneBaseUrl", () => {
  const addresses = [{ url: "http://100.64.90.72:2113" }, { url: "https://hangar.tail9c2e.ts.net" }];

  it("prefers the saved phone address", () => {
    expect(choosePhoneBaseUrl("https://saved.example/", { origin: "https://page.example", hostname: "page.example" }, addresses))
      .toBe("https://saved.example");
  });

  it("uses the page's origin when a phone could reach it", () => {
    expect(choosePhoneBaseUrl(null, { origin: "https://hangar.tail9c2e.ts.net", hostname: "hangar.tail9c2e.ts.net" }, []))
      .toBe("https://hangar.tail9c2e.ts.net");
  });

  it("skips loopback for the first https address", () => {
    expect(choosePhoneBaseUrl(null, { origin: "http://localhost:2113", hostname: "localhost" }, addresses))
      .toBe("https://hangar.tail9c2e.ts.net");
    expect(choosePhoneBaseUrl(null, { origin: "http://127.0.0.1:2113", hostname: "127.0.0.1" }, [{ url: "http://192.168.1.4:2113" }]))
      .toBe("http://192.168.1.4:2113");
    expect(choosePhoneBaseUrl(null, { origin: "http://localhost:2113", hostname: "localhost" }, [])).toBeNull();
  });
});

describe("helpers", () => {
  it("knows loopback", () => {
    expect(isLoopbackHost("localhost")).toBe(true);
    expect(isLoopbackHost("127.0.0.1")).toBe(true);
    expect(isLoopbackHost("[::1]")).toBe(true);
    expect(isLoopbackHost("fleet.localhost")).toBe(true);
    expect(isLoopbackHost("hangar.tail9c2e.ts.net")).toBe(false);
  });

  it("only offers install over https", () => {
    expect(supportsInstall("https://hangar.ts.net")).toBe(true);
    expect(supportsInstall("http://100.64.90.72:2113")).toBe(false);
  });

  it("normalizes typed codes", () => {
    expect(normalizeManualCode("abcd efgh")).toBe("ABCD-EFGH");
    expect(normalizeManualCode("oil0-1234")).toBe("0110-1234");
    expect(normalizeManualCode("ABCD-EFG")).toBeNull();
    expect(normalizeManualCode("ABCD-EFGU")).toBeNull();
  });
});

describe("guessing the device", () => {
  const iphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
  const pixel = "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36";
  const reduced = "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Mobile Safari/537.36";
  const ipad = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";

  it("names the phone", () => {
    expect(guessDeviceName(iphone)).toBe("iPhone");
    expect(guessDeviceName(pixel)).toBe("Pixel 9");
    expect(guessDeviceName(reduced)).toBe("Android phone");
    expect(guessDeviceName(ipad, 5)).toBe("iPad");
    expect(guessDeviceName(ipad, 0)).toBe("Phone");
  });

  it("knows the platform", () => {
    expect(guessPlatform(iphone)).toBe("ios");
    expect(guessPlatform(ipad, 5)).toBe("ios");
    expect(guessPlatform(pixel)).toBe("android");
    expect(guessPlatform(ipad)).toBe("other");
  });
});
