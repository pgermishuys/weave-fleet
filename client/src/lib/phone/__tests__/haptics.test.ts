import { describe, expect, it } from "vitest";
import { vibrationPattern } from "../haptics";

describe("haptics", () => {
  it("vibrates briefly for a tap, twice for a success", () => {
    expect(vibrationPattern("light")).toBe(9);
    expect(vibrationPattern("success")).toEqual([12, 60, 18]);
    expect(vibrationPattern("heavy")).toBe(22);
  });
});
