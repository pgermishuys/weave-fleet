import { beforeEach, describe, expect, it, vi } from "vitest";
import type { FileDiffItem } from "@/api/client";
import type { MachineTarget } from "@/lib/machine-target";
import { useFileDiffContents } from "../use-file-diff-contents";

const { apiFetchOnMock } = vi.hoisted(() => ({ apiFetchOnMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: apiFetchOnMock }));

const machine = { key: "home", connection: null, isLive: true, api: {} } as unknown as MachineTarget;
const item = { file: "src/a.ts", status: "modified", additions: 2, deletions: 1, before: "x", after: "y" } as unknown as FileDiffItem;

function answer(body: FileDiffItem): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

describe("useFileDiffContents", () => {
  beforeEach(() => { apiFetchOnMock.mockReset(); });

  it("draws a file it already fetched at once when the panel is reopened", async () => {
    apiFetchOnMock.mockImplementation(async () => answer(item));
    const first = useFileDiffContents(machine, () => "s-reopen");
    first.load("src/a.ts", "modified:2:1");
    await settle();
    expect(first.entries.value["src/a.ts"]?.status).toBe("ready");

    // The panel closes and opens again: a new instance for the same session.
    const second = useFileDiffContents(machine, () => "s-reopen");
    second.load("src/a.ts", "modified:2:1");
    expect(second.entries.value["src/a.ts"]?.status).toBe("ready");
    await settle();
    expect(second.entries.value["src/a.ts"]?.status).toBe("ready");
  });

  it("asks for at most four files at once, so a long list (find loads every file) can't hold up the app's other requests", async () => {
    // A browser opens six connections to a server: hundreds of file diffs fired together queued everything else
    // (switching session, sending a message) behind them for over a minute.
    const other = { ...machine, key: "limit-test" } as MachineTarget;
    const waiting: Array<() => void> = [];
    apiFetchOnMock.mockImplementation(() => new Promise<Response>((resolve) => waiting.push(() => resolve(answer(item)))));
    const contents = useFileDiffContents(other, () => "s-many");
    for (let i = 0; i < 10; i++) contents.load(`src/f${i}.ts`, "modified:2:1");
    await settle();
    expect(apiFetchOnMock).toHaveBeenCalledTimes(4);
    expect(contents.entries.value["src/f9.ts"]?.status).toBe("loading");

    waiting.shift()!();
    await settle();
    await settle();
    expect(apiFetchOnMock).toHaveBeenCalledTimes(5);

    while (waiting.length) {
      waiting.shift()!();
      await settle();
      await settle();
    }
    expect(apiFetchOnMock).toHaveBeenCalledTimes(10);
    expect(Object.values(contents.entries.value).every((entry) => entry.status === "ready")).toBe(true);
  });

  it("doesn't show one session's file in another", () => {
    apiFetchOnMock.mockReturnValue(new Promise(() => {}));
    const other = useFileDiffContents(machine, () => "s-other");
    other.load("src/a.ts", "modified:2:1");
    expect(other.entries.value["src/a.ts"]?.status).toBe("loading");
  });
});
