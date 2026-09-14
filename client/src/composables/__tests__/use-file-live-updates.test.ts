import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FILE_EVENT_DEBOUNCE_MS, isSameFile, useFileLiveUpdates } from "@/composables/use-file-live-updates";
import type { DomainEvent } from "@/lib/domain-events";
import { openBuffer } from "@/lib/code-editor/buffers";
import { fileCanvasId, useCanvasesStore } from "@/stores/canvases";
import { useFileBuffersStore } from "@/stores/file-buffers";

const { readSessionFileMock, handlers } = vi.hoisted(() => ({
  readSessionFileMock: vi.fn(),
  handlers: [] as ((event: DomainEvent) => void)[],
}));

vi.mock("@/api/session-files", () => ({
  readSessionFile: readSessionFileMock,
  writeSessionFile: vi.fn(),
}));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({
    subscribeV2: (_topic: string, _onSnapshot: unknown, onEvent: (event: DomainEvent) => void) => {
      handlers.push(onEvent);
      return () => handlers.splice(handlers.indexOf(onEvent), 1);
    },
  }),
}));

function disk(content: string, hash: string) {
  return { path: "src/app.ts", content, hash, isBinary: false, isTruncated: false };
}

function emit(event: unknown) {
  for (const handler of [...handlers]) handler(event as DomainEvent);
}

function filesChanged(...paths: string[]) {
  return { type: "files.changed", payload: { sessionId: "s1", files: paths.map((path) => ({ path, changeType: "change" })) } };
}

async function settle() {
  vi.advanceTimersByTime(FILE_EVENT_DEBOUNCE_MS);
  await flushPromises();
  await flushPromises();
}

async function setup() {
  const wrapper = mount(defineComponent({ setup: () => (useFileLiveUpdates(ref("s1")), () => h("div")) }));
  readSessionFileMock.mockResolvedValueOnce(disk("one\ntwo\n", "h1"));
  const record = await openBuffer("s1", "src/app.ts");
  readSessionFileMock.mockClear();
  return { wrapper, record };
}

describe("useFileLiveUpdates", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    handlers.length = 0;
    readSessionFileMock.mockReset();
  });

  afterEach(() => vi.useRealTimers());

  it("matches the agent's absolute paths and a save's relative ones", () => {
    expect(isSameFile("/home/me/repo/src/app.ts", "src/app.ts")).toBe(true);
    expect(isSameFile("src/app.ts", "src/app.ts")).toBe(true);
    expect(isSameFile("C:\\repo\\src\\app.ts", "src/app.ts")).toBe(true);
    expect(isSameFile("/repo/src/app.tsx", "src/app.ts")).toBe(false);
    expect(isSameFile("/repo/xsrc/app.ts", "src/app.ts")).toBe(false);
  });

  it("a clean buffer takes the agent's change in place, and the tab pulses", async () => {
    const { wrapper, record } = await setup();
    readSessionFileMock.mockResolvedValue(disk("one\nTWO\n", "h2"));

    emit(filesChanged("/repo/src/app.ts"));
    await settle();

    expect(readSessionFileMock).toHaveBeenCalledWith("s1", "src/app.ts");
    expect(record.state?.doc.toString()).toBe("one\nTWO\n");
    expect(useCanvasesStore().updatedAt[fileCanvasId("src/app.ts")]).toBeDefined();
    wrapper.unmount();
  });

  it("a dirty buffer isn't touched: it gets the conflict bar", async () => {
    const { wrapper, record } = await setup();
    record.state = record.state!.update({ changes: { from: 0, insert: "mine " } }).state;
    useFileBuffersStore().patch("s1", "src/app.ts", { dirty: true });
    readSessionFileMock.mockResolvedValue(disk("agent's\n", "h2"));

    emit(filesChanged("/repo/src/app.ts"));
    await settle();

    expect(record.state?.doc.toString()).toBe("mine one\ntwo\n");
    expect(useFileBuffersStore().info("s1", "src/app.ts")?.conflict).toEqual({ diskText: "agent's\n", hash: "h2" });
    wrapper.unmount();
  });

  it("changes nothing when the file on disk still has the same hash", async () => {
    const { wrapper, record } = await setup();
    const before = record.state;
    readSessionFileMock.mockResolvedValue(disk("one\ntwo\n", "h1"));

    emit(filesChanged("src/app.ts"));
    await settle();

    expect(record.state).toBe(before);
    expect(useCanvasesStore().updatedAt[fileCanvasId("src/app.ts")]).toBeUndefined();
    wrapper.unmount();
  });

  it("doesn't read files that aren't open, and reads a burst of events once", async () => {
    const { wrapper } = await setup();
    readSessionFileMock.mockResolvedValue(disk("one\ntwo\n", "h1"));

    emit(filesChanged("/repo/src/other.ts"));
    await settle();
    expect(readSessionFileMock).not.toHaveBeenCalled();

    emit(filesChanged("/repo/src/app.ts"));
    emit(filesChanged("/repo/src/app.ts"));
    emit(filesChanged("/repo/src/app.ts"));
    await settle();
    expect(readSessionFileMock).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });

  it("re-reads open files at the end of a turn, which catches shell edits", async () => {
    const { wrapper, record } = await setup();
    readSessionFileMock.mockResolvedValue(disk("sed did this\n", "h3"));

    emit({ type: "turn.ended", payload: { sessionID: "s1", messageID: "m1", index: 0, reason: null, cost: 0 } });
    await settle();

    expect(record.state?.doc.toString()).toBe("sed did this\n");
    wrapper.unmount();
  });

  it("re-reads when the window gets focus back, without waiting", async () => {
    const { wrapper } = await setup();
    readSessionFileMock.mockResolvedValue(disk("one\ntwo\n", "h1"));

    window.dispatchEvent(new Event("focus"));
    vi.advanceTimersByTime(0);
    await flushPromises();

    expect(readSessionFileMock).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });

  it("ignores another session's events", async () => {
    const { wrapper } = await setup();
    emit({ type: "files.changed", payload: { sessionId: "s2", files: [{ path: "src/app.ts", changeType: "change" }] } });
    await settle();
    expect(readSessionFileMock).not.toHaveBeenCalled();
    wrapper.unmount();
  });
});
