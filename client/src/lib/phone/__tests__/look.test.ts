import { describe, expect, it } from "vitest";
import { detectLook, isIosWebKit, resolveLook } from "../look";

const IPHONE = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1";
const PIXEL = "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Mobile Safari/537.36";
const DESKTOP = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";

describe("phone look", () => {
  it("dresses as Android on Android and as iOS everywhere else", () => {
    expect(detectLook({ userAgent: PIXEL })).toBe("android");
    expect(detectLook({ userAgent: IPHONE })).toBe("ios");
    expect(detectLook({ userAgent: DESKTOP })).toBe("ios");
  });

  it("believes userAgentData's platform over the user agent string", () => {
    expect(detectLook({ userAgent: DESKTOP, uaDataPlatform: "Android" })).toBe("android");
    expect(detectLook({ userAgent: PIXEL, uaDataPlatform: "iOS" })).toBe("ios");
  });

  it("knows an iPad asking for desktop sites is still iOS WebKit", () => {
    expect(isIosWebKit({ userAgent: IPHONE })).toBe(true);
    expect(isIosWebKit({ userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", platform: "MacIntel", maxTouchPoints: 5 })).toBe(true);
    expect(isIosWebKit({ userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", platform: "MacIntel", maxTouchPoints: 0 })).toBe(false);
    expect(isIosWebKit({ userAgent: PIXEL })).toBe(false);
  });

  it("lets ?look= override it, remembers that, and forgets it on ?look=auto", () => {
    expect(resolveLook({ query: "android", stored: null, signals: { userAgent: IPHONE } })).toEqual({ look: "android", store: "android" });
    expect(resolveLook({ query: null, stored: "android", signals: { userAgent: IPHONE } })).toEqual({ look: "android", store: undefined });
    expect(resolveLook({ query: "auto", stored: "android", signals: { userAgent: IPHONE } })).toEqual({ look: "ios", store: null });
    expect(resolveLook({ query: "windows", stored: "nonsense", signals: { userAgent: PIXEL } })).toEqual({ look: "android", store: undefined });
  });
});
