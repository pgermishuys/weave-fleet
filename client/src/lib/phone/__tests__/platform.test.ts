import { describe, expect, it } from "vitest";
import { isIosWebKit } from "../platform";

const IPHONE = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1";
const PIXEL = "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Mobile Safari/537.36";

describe("phone platform", () => {
  it("knows an iPhone, and an iPad asking for desktop sites, is iOS WebKit", () => {
    expect(isIosWebKit({ userAgent: IPHONE })).toBe(true);
    expect(isIosWebKit({ userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", platform: "MacIntel", maxTouchPoints: 5 })).toBe(true);
    expect(isIosWebKit({ userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", platform: "MacIntel", maxTouchPoints: 0 })).toBe(false);
    expect(isIosWebKit({ userAgent: PIXEL })).toBe(false);
  });
});
