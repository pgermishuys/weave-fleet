import { describe, expect, it } from "vitest";
import { isIos, pushState, type PushEnvironment } from "../capabilities";
import { groupsFor, kindsFor, urlBase64ToUint8Array } from "../subscribe";

const ready: PushEnvironment = {
  isSecureContext: true,
  hasServiceWorker: true,
  hasPushManager: true,
  hasNotification: true,
  permission: "default",
  isIos: false,
  isStandalone: false,
};

describe("pushState", () => {
  it("is ready on a secure Android or desktop browser", () => {
    expect(pushState(ready)).toBe("ready");
    expect(pushState({ ...ready, permission: "granted" })).toBe("ready");
  });

  it("explains plain http first", () => {
    expect(pushState({ ...ready, isSecureContext: false, hasPushManager: false })).toBe("insecure");
  });

  it("asks iPhones to add Fleet to the Home Screen first", () => {
    expect(pushState({ ...ready, isIos: true, hasPushManager: false })).toBe("ios-needs-install");
    expect(pushState({ ...ready, isIos: true, isStandalone: true })).toBe("ready");
  });

  it("says when the browser can't, or was told no", () => {
    expect(pushState({ ...ready, hasPushManager: false })).toBe("unsupported");
    expect(pushState({ ...ready, hasServiceWorker: false })).toBe("unsupported");
    expect(pushState({ ...ready, permission: "denied" })).toBe("denied");
  });

  it("recognises iPads that call themselves Macs", () => {
    expect(isIos("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)", 5)).toBe(true);
    expect(isIos("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", 5)).toBe(true);
    expect(isIos("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", 0)).toBe(false);
    expect(isIos("Mozilla/5.0 (Linux; Android 15; Pixel 9)", 5)).toBe(false);
  });
});

describe("choices", () => {
  it("maps switches to kinds and back", () => {
    expect(kindsFor(["needs-you", "failed"])).toEqual(["permission", "workflow", "failed"]);
    expect(groupsFor(["permission", "workflow", "question", "finished", "failed"])).toEqual(["needs-you", "questions", "finished", "failed"]);
    expect(groupsFor(["permission"])).toEqual([]);
  });

  it("decodes the VAPID key", () => {
    expect([...urlBase64ToUint8Array("AQID_w")]).toEqual([1, 2, 3, 255]);
  });
});
