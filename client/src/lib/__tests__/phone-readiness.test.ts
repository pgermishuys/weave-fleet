import { describe, expect, it } from "vitest";
import { phoneReadiness } from "../phone-readiness";

describe("what a computer needs before a phone can use Fleet", () => {
  it("is all there behind tailscale serve with a key", () => {
    const items = phoneReadiness({
      machineName: "hangar",
      phoneUrl: "https://hangar.tail9c2e.ts.net",
      addresses: [],
      requiresToken: true,
      port: 2113,
    });

    expect(items.map((item) => [item.id, item.ok])).toEqual([["tailscale", true], ["https", true], ["key", true]]);
    expect(items.every((item) => !item.command)).toBe(true);
    expect(items[1]?.detail).toBe("Your phone opens https://hangar.tail9c2e.ts.net.");
  });

  it("says what to run for each thing that's missing", () => {
    const items = phoneReadiness({
      machineName: "hangar",
      phoneUrl: "http://192.168.1.13:2113",
      addresses: [{ url: "http://192.168.1.13:2113", kind: "lan" }],
      requiresToken: false,
      port: 2113,
    });

    expect(items.map((item) => item.ok)).toEqual([false, false, false]);
    expect(items[0]?.link?.href).toBe("https://tailscale.com/download");
    expect(items[1]?.command).toBe("tailscale serve --bg --https=443 http://127.0.0.1:2113");
    expect(items[2]?.command).toBe("fleet --port 2113 --require-token");
  });

  it("counts a tailnet address even before https is set up", () => {
    const items = phoneReadiness({
      machineName: "hangar",
      phoneUrl: "http://100.64.90.72:2113",
      addresses: [{ url: "http://100.64.90.72:2113", kind: "tailnet" }],
      requiresToken: true,
      port: 2113,
    });

    expect(items.map((item) => item.ok)).toEqual([true, false, true]);
  });
});
