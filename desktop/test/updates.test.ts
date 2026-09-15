import { describe, expect, it } from "vitest";
import { nextState, updateMode, type UpdateState } from "../src/updates";

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
