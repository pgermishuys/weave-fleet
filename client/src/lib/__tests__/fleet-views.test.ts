import { describe, expect, it } from "vitest";
import { fleetViewFromPath, fleetViewPath } from "@/lib/fleet-views";

describe("fleet views", () => {
  it("round-trips a view through its route", () => {
    expect(fleetViewPath({ kind: "needs-you" })).toBe("/fleet");
    expect(fleetViewPath({ kind: "machine", machineId: "a b" })).toBe("/fleet/machines/a%20b");
    expect(fleetViewFromPath("/fleet/machines/a%20b")).toEqual({ kind: "machine", machineId: "a b" });
  });

  it("shows Needs you for the bare route and for anything stale", () => {
    for (const path of ["/fleet", "/fleet/", "/fleet/machines", "/fleet/other/x", "/fleet/machines/%E0%A4%A"]) {
      expect(fleetViewFromPath(path)).toEqual({ kind: "needs-you" });
    }
  });
});
