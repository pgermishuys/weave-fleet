import { describe, expect, it } from "vitest";
import { canSendQueuedNow, composerStatus, harnessCapabilities, primaryAction, routeDraft, type HarnessCapabilities } from "../use-composer-actions";

const steers: HarnessCapabilities = { canSteer: true, steersByDefault: true, supportsShell: true, supportsSide: true };
const plain: HarnessCapabilities = { canSteer: false, steersByDefault: false, supportsShell: false, supportsSide: false };

describe("composerStatus", () => {
  it("reads busy, waiting and idle", () => {
    expect(composerStatus("busy")).toBe("busy");
    expect(composerStatus("delegating")).toBe("busy");
    expect(composerStatus("retry")).toBe("busy");
    expect(composerStatus("waiting_input")).toBe("waiting_input");
    expect(composerStatus("idle")).toBe("idle");
    expect(composerStatus(undefined, true)).toBe("busy");
  });
});

describe("harnessCapabilities", () => {
  const harnesses = [
    { type: "opencode2", capabilities: { supportsSteering: true, supportsShellCommands: true, supportsSideConversations: true } },
    { type: "opencode", capabilities: { supportsSteering: true, supportsShellCommands: true, supportsSideConversations: false } },
    { type: "claude-code", capabilities: {} },
  ];

  it("steers by default only on OpenCode 2", () => {
    expect(harnessCapabilities("opencode2", harnesses)).toEqual(steers);
    expect(harnessCapabilities("opencode", harnesses)).toMatchObject({ canSteer: true, steersByDefault: false, supportsSide: false });
    expect(harnessCapabilities("claude-code", harnesses)).toEqual(plain);
  });
});

describe("routeDraft", () => {
  const idle = { status: "idle" as const, steer: false, caps: steers, sideOpen: false };
  const busy = { ...idle, status: "busy" as const };

  it("sends when idle and queues while working", () => {
    expect(routeDraft("fix it", idle)).toEqual({ kind: "prompt", queue: false, steer: false });
    expect(routeDraft("fix it", busy)).toEqual({ kind: "prompt", queue: true, steer: false });
  });

  it("steers only when asked and the harness can", () => {
    expect(routeDraft("look at the logs first", { ...busy, steer: true })).toEqual({ kind: "prompt", queue: false, steer: true });
    expect(routeDraft("look at the logs first", { ...busy, steer: true, caps: plain })).toEqual({ kind: "prompt", queue: true, steer: false });
  });

  it("routes ! to the shell, queued while working, where the harness runs commands", () => {
    expect(routeDraft("!git status", idle)).toEqual({ kind: "shell", command: "git status", queue: false });
    expect(routeDraft("!git status", busy)).toEqual({ kind: "shell", command: "git status", queue: true });
    expect(routeDraft("!git status", { ...idle, caps: plain })).toMatchObject({ kind: "prompt" });
  });

  it("routes /btw and anything typed while the side is open to the side", () => {
    expect(routeDraft("/btw what's a flaky test?", busy)).toEqual({ kind: "side", question: "what's a flaky test?" });
    expect(routeDraft("and another", { ...idle, sideOpen: true })).toEqual({ kind: "side", question: "and another" });
  });

  it("routes slash commands as commands, queued while working", () => {
    expect(routeDraft("/review src", idle)).toEqual({ kind: "command", command: "review", args: "src", queue: false });
    expect(routeDraft("/review src", { ...busy, steer: true })).toMatchObject({ kind: "command", queue: true });
  });

  it("ignores an empty draft", () => {
    expect(routeDraft("   ", idle)).toEqual({ kind: "empty" });
  });
});

describe("the phone's buttons", () => {
  it("is Send when idle, Queue while working, Stop with nothing typed", () => {
    expect(primaryAction("idle", true)).toBe("send");
    expect(primaryAction("waiting_input", false)).toBe("send");
    expect(primaryAction("busy", true)).toBe("queue");
    expect(primaryAction("busy", false)).toBe("stop");
  });

  it("sends a queued message now only where it can go", () => {
    const prompt = { id: "1", text: "x", kind: "prompt" as const };
    const shell = { id: "2", text: "!ls", kind: "shell" as const };
    expect(canSendQueuedNow(shell, "idle", plain)).toBe(true);
    expect(canSendQueuedNow(prompt, "busy", steers)).toBe(true);
    expect(canSendQueuedNow(prompt, "busy", plain)).toBe(false);
    expect(canSendQueuedNow(shell, "busy", steers)).toBe(false);
  });
});
