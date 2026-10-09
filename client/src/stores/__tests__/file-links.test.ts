import { flushPromises } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { liveTarget } from "@/lib/machine-target";
import { useFileLinksStore } from "@/stores/file-links";

const { resolveSessionFilesMock } = vi.hoisted(() => ({ resolveSessionFilesMock: vi.fn() }));

vi.mock("@/api/session-files", () => ({ resolveSessionFiles: resolveSessionFilesMock }));

const FILES = new Map([["src/billing/tax.ts", "src/billing/tax.ts"], ["./README.md", "README.md"]]);

describe("useFileLinksStore", () => {
  beforeEach(() => {
    resolveSessionFilesMock.mockReset();
    resolveSessionFilesMock.mockImplementation(async (_machine: unknown, _sessionId: string, paths: string[]) =>
      new Map(paths.filter((path) => FILES.has(path)).map((path) => [path, FILES.get(path)!])));
  });

  it("asks about everything named in one tick in one request, and knows the answers", async () => {
    const store = useFileLinksStore();

    store.request(liveTarget(), "s1", ["src/billing/tax.ts", "src/billing/gone.ts"]);
    store.request(liveTarget(), "s1", ["./README.md", "src/billing/tax.ts", "./README.md"]);
    expect(store.resolve("s1", "src/billing/tax.ts")).toBeUndefined();
    await flushPromises();

    expect(resolveSessionFilesMock).toHaveBeenCalledTimes(1);
    expect(resolveSessionFilesMock.mock.calls[0][2]).toEqual(["src/billing/tax.ts", "src/billing/gone.ts", "./README.md"]);
    expect(store.resolve("s1", "src/billing/tax.ts")).toBe("src/billing/tax.ts");
    expect(store.resolve("s1", "./README.md")).toBe("README.md");
    expect(store.resolve("s1", "src/billing/gone.ts")).toBeNull();
    expect(store.resolve("s2", "src/billing/tax.ts")).toBeUndefined();
  });

  it("doesn't ask again about what it knows", async () => {
    const store = useFileLinksStore();
    store.request(liveTarget(), "s1", ["src/billing/tax.ts", "src/billing/gone.ts"]);
    await flushPromises();

    store.request(liveTarget(), "s1", ["src/billing/tax.ts", "src/billing/gone.ts"]);
    await flushPromises();

    expect(resolveSessionFilesMock).toHaveBeenCalledTimes(1);
  });

  it("asks again about paths that weren't files once told to forget them", async () => {
    const store = useFileLinksStore();
    store.request(liveTarget(), "s1", ["src/billing/tax.ts", "src/billing/gone.ts"]);
    await flushPromises();

    store.forgetMissing("s1");
    expect(store.resolve("s1", "src/billing/tax.ts")).toBe("src/billing/tax.ts");
    expect(store.resolve("s1", "src/billing/gone.ts")).toBeUndefined();
    store.request(liveTarget(), "s1", ["src/billing/tax.ts", "src/billing/gone.ts"]);
    await flushPromises();

    expect(resolveSessionFilesMock).toHaveBeenCalledTimes(2);
    expect(resolveSessionFilesMock.mock.calls[1][2]).toEqual(["src/billing/gone.ts"]);
  });

  it("asks again after a failed request", async () => {
    const store = useFileLinksStore();
    resolveSessionFilesMock.mockRejectedValueOnce(new Error("offline"));
    store.request(liveTarget(), "s1", ["src/billing/tax.ts"]);
    await flushPromises();
    expect(store.resolve("s1", "src/billing/tax.ts")).toBeUndefined();

    store.request(liveTarget(), "s1", ["src/billing/tax.ts"]);
    await flushPromises();

    expect(store.resolve("s1", "src/billing/tax.ts")).toBe("src/billing/tax.ts");
  });
});
