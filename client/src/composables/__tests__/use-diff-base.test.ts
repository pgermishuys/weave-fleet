import { beforeEach, describe, expect, it, vi } from "vitest";
import { nextTick, shallowRef } from "vue";
import { flushAll, mountComposable } from "./test-utils";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));

vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

import { clearDiffBaseCache, fetchDiffBase, useDiffBase } from "@/composables/use-diff-base";

function diffResponse(before: string): Response {
  return new Response(JSON.stringify({ file: "src/a.ts", status: "modified", additions: 1, deletions: 1, before, after: "x" }), { status: 200 });
}

describe("useDiffBase", () => {
  beforeEach(() => {
    clearDiffBaseCache();
    apiFetchMock.mockReset();
  });

  it("fetches a changed file's base once and reuses it", async () => {
    apiFetchMock.mockImplementation(async () => diffResponse("old\n"));

    expect(await fetchDiffBase("s1", "src/a.ts")).toBe("old\n");
    expect(await fetchDiffBase("s1", "src/a.ts")).toBe("old\n");

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/diffs/file?path=src%2Fa.ts");
  });

  it("doesn't cache a failed read", async () => {
    apiFetchMock.mockResolvedValueOnce(new Response(null, { status: 404 }));
    apiFetchMock.mockResolvedValueOnce(diffResponse("old\n"));

    expect(await fetchDiffBase("s1", "src/a.ts")).toBeNull();
    await flushAll();
    expect(await fetchDiffBase("s1", "src/a.ts")).toBe("old\n");
  });

  it("only loads while the file is among the changes", async () => {
    apiFetchMock.mockImplementation(async () => diffResponse("old\n"));
    const changed = shallowRef(false);

    const { result, wrapper } = await mountComposable(() => useDiffBase("s1", "src/a.ts", changed));
    await flushAll();
    expect(result.value).toBeNull();
    expect(apiFetchMock).not.toHaveBeenCalled();

    changed.value = true;
    await nextTick();
    await flushAll();
    expect(result.value).toBe("old\n");

    changed.value = false;
    await nextTick();
    expect(result.value).toBeNull();

    wrapper.unmount();
  });
});
