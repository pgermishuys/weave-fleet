import { describe, expect, it } from "vitest";
import { reachChoices } from "../reach-choices";

const found = [
  { url: "http://studio.tail4f2a.ts.net:6262", kind: "tailnet" as const },
  { url: "http://100.64.90.72:6262", kind: "tailnet" as const },
  { url: "http://192.168.1.20:6262", kind: "lan" as const },
];

describe("the choices in Let others reach studio", () => {
  it("offers Tailscale and home network first, recommended, with the name Tailscale gave and the home address", () => {
    const [both, home, own] = reachChoices(found);

    expect(both?.mode).toBe("tailscale-and-home");
    expect(both?.recommended).toBe(true);
    expect(both?.addresses.map((address) => address.label)).toEqual(["studio.tail4f2a.ts.net", "192.168.1.20"]);
    expect(home?.addresses.map((address) => address.label)).toEqual(["192.168.1.20"]);
    expect(own?.mode).toBe("own");
    expect(own?.disabled).toBe(false);
  });

  it("shows the Tailscale IP when Tailscale gave no name", () => {
    const [both] = reachChoices([{ url: "http://100.64.90.72:6262", kind: "tailnet" }]);

    expect(both?.addresses.map((address) => address.label)).toEqual(["100.64.90.72"]);
  });

  it("switches off the choices that have nothing to listen on, and says why", () => {
    const [both, home, own] = reachChoices([]);

    expect(both?.disabled).toBe(true);
    expect(home?.disabled).toBe(true);
    expect(home?.hint("studio")).toContain("didn't find a home network address");
    expect(own?.disabled).toBe(false);
  });

  it("only recommends the first choice when Tailscale is there, and says what to expect without it", () => {
    const [both] = reachChoices([{ url: "http://192.168.1.20:6262", kind: "lan" }]);

    expect(both?.recommended).toBe(false);
    expect(both?.hint("studio")).toContain("at home only");
  });
});
