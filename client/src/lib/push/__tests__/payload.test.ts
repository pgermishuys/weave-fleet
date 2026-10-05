import { describe, expect, it } from "vitest";
import { MAX_BODY_LENGTH, MAX_TITLE_LENGTH, parsePushPayload } from "../payload";
import { notificationFor } from "../notification-options";

const valid = {
  v: 1,
  machineId: "hangar-id",
  machineName: "hangar",
  sessionId: "s1",
  kind: "permission",
  reason: "needs_you",
  title: "Fix flaky SignalR reconnect test",
  body: "Wants to run dotnet test",
  url: "/phone/s/hangar-id/s1?ask=perm-1",
  tag: "hangar-id:s1",
  requestId: "perm-1",
};

describe("parsePushPayload", () => {
  it("reads a version 1 push", () => {
    expect(parsePushPayload(valid)).toEqual(valid);
  });

  it("refuses another version or a missing field", () => {
    expect(parsePushPayload({ ...valid, v: 2 })).toBeNull();
    expect(parsePushPayload({ ...valid, sessionId: undefined })).toBeNull();
    expect(parsePushPayload({ ...valid, kind: "spam" })).toBeNull();
    expect(parsePushPayload(null)).toBeNull();
    expect(parsePushPayload("text")).toBeNull();
  });

  it("clips long titles and bodies", () => {
    const parsed = parsePushPayload({ ...valid, title: "t".repeat(500), body: "b".repeat(500) });
    expect(parsed?.title.length).toBe(MAX_TITLE_LENGTH);
    expect(parsed?.title.endsWith("…")).toBe(true);
    expect(parsed?.body.length).toBe(MAX_BODY_LENGTH);
  });

  it("keeps the tap on this origin", () => {
    expect(parsePushPayload({ ...valid, url: "https://evil.example/x" })?.url).toBe("/phone");
    expect(parsePushPayload({ ...valid, url: "//evil.example/x" })?.url).toBe("/phone");
    expect(parsePushPayload({ ...valid, url: undefined })?.url).toBe("/phone");
  });

  it("fills a missing tag and leaves out a missing request", () => {
    const parsed = parsePushPayload({ ...valid, tag: undefined, requestId: undefined, kind: "finished" });
    expect(parsed?.tag).toBe("hangar-id:s1");
    expect(parsed && "requestId" in parsed).toBe(false);
  });
});

describe("notificationFor", () => {
  it("shows the machine and keeps what a tap needs, without secrets", () => {
    const { title, options } = notificationFor(parsePushPayload(valid)!);
    expect(title).toBe(valid.title);
    expect(options.body).toBe("hangar · Wants to run dotnet test");
    expect(options.tag).toBe("hangar-id:s1");
    expect(options.data).toEqual({ url: valid.url, machineId: "hangar-id", sessionId: "s1", kind: "permission", requestId: "perm-1" });
    expect(options.requireInteraction).toBe(true);
    expect(JSON.stringify(options)).not.toContain("fdt_");
  });

  it("doesn't hold finished notifications on screen", () => {
    const { options } = notificationFor(parsePushPayload({ ...valid, kind: "finished", requestId: undefined })!);
    expect(options.requireInteraction).toBe(false);
  });
});
