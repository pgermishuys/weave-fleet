import { describe, expect, it } from "vitest";
import { describeReach, parsePairingInput } from "../pairing-link";

describe("parsePairingInput", () => {
  it("reads a link into its address and code", () => {
    expect(parsePairingInput("http://100.64.90.72:2113/add-machine#K3MQ-9ZDT"))
      .toEqual({ kind: "link", baseUrl: "http://100.64.90.72:2113", code: "K3MQ-9ZDT" });
  });

  it("keeps the scheme, port and path of the address, and ignores spaces and a lower-case code", () => {
    expect(parsePairingInput("  https://falcon.tail9c2e.ts.net/fleet/add-machine#k3mq9zdt \n"))
      .toEqual({ kind: "link", baseUrl: "https://falcon.tail9c2e.ts.net/fleet", code: "K3MQ-9ZDT" });
  });

  it("takes a bare address and asks for the code next", () => {
    expect(parsePairingInput("100.64.90.72:2113")).toEqual({ kind: "address", baseUrl: "http://100.64.90.72:2113" });
    expect(parsePairingInput("https://falcon.example.test/")).toEqual({ kind: "address", baseUrl: "https://falcon.example.test" });
  });

  it("reads an address that still has /add-machine but no code", () => {
    expect(parsePairingInput("http://falcon:2113/add-machine")).toEqual({ kind: "address", baseUrl: "http://falcon:2113" });
  });

  it("refuses a link whose code is missing or wrong", () => {
    expect(parsePairingInput("http://falcon:2113/add-machine#")?.kind).toBe("invalid");
    expect(parsePairingInput("http://falcon:2113/add-machine#NOPE")?.kind).toBe("invalid");
  });

  it("refuses what isn't an address", () => {
    expect(parsePairingInput("http://")?.kind).toBe("invalid");
    expect(parsePairingInput("not a link at all")?.kind).toBe("invalid");
  });

  it("says nothing while the field is empty", () => {
    expect(parsePairingInput("")).toBeNull();
    expect(parsePairingInput("   ")).toBeNull();
  });
});

describe("describeReach", () => {
  it.each([
    ["http://100.64.90.72:2113", "over Tailscale at 100.64.90.72:2113"],
    ["https://falcon.tail9c2e.ts.net", "over Tailscale at falcon.tail9c2e.ts.net"],
    ["http://192.168.1.20:2113", "on your network at 192.168.1.20:2113"],
    ["http://falcon.local:2113", "on your network at falcon.local:2113"],
    ["http://localhost:2113", "on this machine at localhost:2113"],
    ["http://falcon.example.test:2113", "at falcon.example.test:2113"],
  ])("%s", (url, expected) => {
    expect(describeReach(url)).toBe(expected);
  });
});
