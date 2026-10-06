import { describe, expect, it } from "vitest";
import { grownHeight } from "../keyboard";

describe("growing text boxes", () => {
  it("grows with the text up to five lines, then scrolls", () => {
    expect(grownHeight(40)).toBe(40);
    expect(grownHeight(110)).toBe(110);
    expect(grownHeight(400)).toBe(5 * 24 + 14);
    expect(grownHeight(400, 3, 20, 10)).toBe(70);
  });
});
