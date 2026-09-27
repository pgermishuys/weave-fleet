import { describe, expect, it } from "vitest";
import { nextState, updateCheckMessage, updateMode, type UpdateState } from "../src/updates";

const now = new Date("2026-09-15T08:00:00Z");
const idle = (mode: UpdateState["mode"] = "install"): UpdateState => ({ status: "idle", mode, currentVersion: "0.24.0" });

describe("update mode", () => {
  it("installs on Windows and from an AppImage", () => {
    expect(updateMode({ packaged: true, platform: "win32", env: {} })).toBe("install");
    expect(updateMode({ packaged: true, platform: "linux", env: { APPIMAGE: "/home/me/Fleet.AppImage" } })).toBe("install");
  });

  it("only tells you about a new version on macOS (unsigned) and from the .deb", () => {
    expect(updateMode({ packaged: true, platform: "darwin", env: {} })).toBe("notify");
    expect(updateMode({ packaged: true, platform: "linux", env: {} })).toBe("notify");
  });

  it("is off in development and when turned off", () => {
    expect(updateMode({ packaged: false, platform: "win32", env: {} })).toBe("off");
    expect(updateMode({ packaged: true, platform: "win32", env: { FLEET_DESKTOP_UPDATES: "off" } })).toBe("off");
  });
});

describe("update state", () => {
  it("downloads a new version where it can install it", () => {
    const downloading = nextState(idle(), { type: "available", version: "0.25.0" }, now);
    expect(downloading).toMatchObject({ status: "downloading", version: "0.25.0", percent: 0 });
    const progressed = nextState(downloading, { type: "progress", percent: 41.6 }, now);
    expect(progressed.percent).toBe(42);
    expect(nextState(progressed, { type: "downloaded", version: "0.25.0" }, now)).toMatchObject({ status: "ready", version: "0.25.0" });
  });

  it("links to the release where it can't install", () => {
    expect(nextState(idle("notify"), { type: "available", version: "0.25.0" }, now)).toMatchObject({
      status: "available",
      version: "0.25.0",
      releaseUrl: "https://github.com/pgermishuys/fleet-releases/releases/tag/v0.25.0",
    });
  });

  it("keeps a downloaded update through later checks and errors", () => {
    const ready: UpdateState = { ...idle(), status: "ready", version: "0.25.0" };
    expect(nextState(ready, { type: "checking" }, now)).toBe(ready);
    expect(nextState(ready, { type: "not-available" }, now)).toBe(ready);
    expect(nextState(ready, { type: "error", message: "offline" }, now)).toBe(ready);
  });

  it("stays ready when a check finds the version it already downloaded", () => {
    const ready = nextState(nextState(idle(), { type: "available", version: "0.25.0" }, now), { type: "downloaded", version: "0.25.0" }, now);
    expect(nextState(ready, { type: "available", version: "0.25.0" }, now)).toBe(ready);
    expect(nextState(ready, { type: "available", version: "0.26.0" }, now)).toMatchObject({ status: "downloading", version: "0.26.0" });
  });

  it("reports a failed check", () => {
    expect(nextState(idle(), { type: "error", message: "offline" }, now)).toMatchObject({ status: "error", error: "offline" });
  });

  it("goes back to idle when there's nothing new", () => {
    const checking = nextState(idle(), { type: "checking" }, now);
    expect(checking.status).toBe("checking");
    expect(nextState(checking, { type: "not-available" }, now)).toMatchObject({ status: "idle", checkedAt: now.toISOString() });
  });

  it("ignores progress when nothing is downloading", () => {
    const state = idle();
    expect(nextState(state, { type: "progress", percent: 50 }, now)).toBe(state);
  });
});

describe("Check for Updates… from the menu", () => {
  it("leaves a found update to the UI's card", () => {
    expect(updateCheckMessage({ ...idle(), status: "ready", version: "0.25.0" })).toBeNull();
    expect(updateCheckMessage({ ...idle("notify"), status: "available", version: "0.25.0" })).toBeNull();
  });

  it("says a found update is still downloading instead of opening a card with nothing to install", () => {
    expect(updateCheckMessage({ ...idle(), status: "downloading", version: "0.25.0", percent: 10 })).toBe(
      "Fleet 0.25.0 is downloading. Fleet lets you know when it's ready to install.",
    );
  });

  it("says when there's nothing new, or the check failed", () => {
    expect(updateCheckMessage(idle())).toBe("You're on the latest version (0.24.0).");
    expect(updateCheckMessage({ ...idle(), status: "error", error: "offline" })).toBe("Couldn't check for updates: offline");
    expect(updateCheckMessage({ ...idle(), status: "off", mode: "off" })).toBe("This build of Fleet doesn't update itself.");
  });
});
