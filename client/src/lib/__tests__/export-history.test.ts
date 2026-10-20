import { afterEach, describe, expect, it, vi } from "vitest";

const apiFetch = vi.fn();
vi.mock("@/lib/api-client", () => ({ apiFetch: (...args: unknown[]) => apiFetch(...args) }));

import { ExportHistoryError, loadWholeHistory } from "@/lib/export-history";

const message = (id: string) => ({ id, role: "user", parts: [{ type: "text", kind: 0, text: id }], timestamp: "2026-10-20T10:00:00Z", textContent: id });
const page = (ids: string[], hasMore: boolean) => ({
  ok: true,
  status: 200,
  json: async () => ({ messages: ids.map(message), pagination: { hasMore, oldestMessageId: ids[0] ?? null } }),
});

afterEach(() => apiFetch.mockReset());

describe("loadWholeHistory", () => {
  it("pages back with the oldest id until nothing is left, oldest message first", async () => {
    apiFetch.mockResolvedValueOnce(page(["m3", "m4"], true)).mockResolvedValueOnce(page(["m1", "m2"], false));
    const progress: number[] = [];

    const all = await loadWholeHistory("s 1", (count) => progress.push(count));

    expect(all.map((entry) => entry.messageId)).toEqual(["m1", "m2", "m3", "m4"]);
    expect(all.every((entry) => entry.sessionId === "s 1")).toBe(true);
    expect(progress).toEqual([2, 4]);
    expect(apiFetch.mock.calls[0][0]).toBe("/api/sessions/s%201/messages?limit=200");
    expect(apiFetch.mock.calls[1][0]).toBe("/api/sessions/s%201/messages?limit=200&before=m3");
  });

  it("keeps what a tool call returned, which OpenCode 2 and Claude Code put on the part", async () => {
    const withTool = { ...message("m1"), parts: [{ type: "tool", kind: 1, toolCallId: "c1", toolName: "shell", arguments: { command: "ls" }, state: 2, output: "a.ts" }] };
    apiFetch.mockResolvedValueOnce({ ok: true, status: 200, json: async () => ({ messages: [withTool], pagination: { hasMore: false, oldestMessageId: "m1" } }) });

    const [loaded] = await loadWholeHistory("s");

    expect(loaded.parts[0]).toMatchObject({ type: "tool", state: { status: "completed", output: "a.ts" } });
  });

  it("fails with a message naming the page when one fails, instead of returning half", async () => {
    apiFetch.mockResolvedValueOnce(page(["m3"], true)).mockResolvedValueOnce({ ok: false, status: 502 });

    await expect(loadWholeHistory("s")).rejects.toThrow(/error 502 after 1 messages/);
  });

  it("fails when Fleet can't be reached", async () => {
    apiFetch.mockRejectedValueOnce(new TypeError("network"));

    await expect(loadWholeHistory("s")).rejects.toBeInstanceOf(ExportHistoryError);
  });

  it("stops when the cursor doesn't move", async () => {
    apiFetch.mockResolvedValue(page(["m3"], true));

    await expect(loadWholeHistory("s")).rejects.toThrow(/did not say where/);
  });
});
