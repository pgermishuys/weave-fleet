import { describe, expect, it } from "vitest";
import { draftAfterRefusal } from "../refused-draft";

describe("draftAfterRefusal", () => {
  it("brings the refused text back into an empty box", () => {
    expect(draftAfterRefusal("fix the login test", "")).toBe("fix the login test");
    expect(draftAfterRefusal("fix the login test", "  \n")).toBe("fix the login test");
  });
  it("puts the refused text in front of what was typed since, with a blank line between", () => {
    expect(draftAfterRefusal("fix the login test\n", "and the logout one")).toBe("fix the login test\n\nand the logout one");
  });
  it("doesn't double text that is already back", () => {
    expect(draftAfterRefusal("same", "same")).toBe("same");
  });
});
