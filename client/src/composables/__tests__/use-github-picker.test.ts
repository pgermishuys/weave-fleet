import { flushPromises } from "@vue/test-utils";
import { effectScope, shallowRef } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));

import { useGitHubPicker } from "@/composables/use-github-picker";

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
const item = (number: number) => ({
  kind: "issue", owner: "acme", repo: "rocket", number, title: `Item ${number}`, url: "", state: "open", isDraft: false, author: null,
  authorAvatarUrl: null, createdAt: "", updatedAt: "", comments: 0, labels: [], headRef: null, baseRef: null, additions: null,
  deletions: null, checks: null, reviewDecision: null, mergeable: null, reviewers: [], assignees: [],
});
const page = (...numbers: number[]) => json({ items: numbers.map(item), totalCount: numbers.length, endCursor: null, hasNextPage: false });
const asked = () => apiFetchMock.mock.calls.map(([url]) => {
  const parsed = new URL(String(url), "http://x");
  return parsed.searchParams.get("q") ?? `${parsed.pathname}?${parsed.searchParams.toString()}`;
});

describe("useGitHubPicker", () => {
  let scope = effectScope();

  beforeEach(() => {
    scope = effectScope();
    vi.useFakeTimers();
    apiFetchMock.mockReset();
  });

  afterEach(() => {
    scope.stop();
    vi.useRealTimers();
  });

  it("offers items assigned to the user, then recent ones, when nothing is typed", async () => {
    apiFetchMock.mockImplementation(async (url: string) => (String(url).includes("assignee") ? page(5) : page(5, 6, 7)));
    const picker = scope.run(() => useGitHubPicker({ owner: "acme", repo: "rocket" }, ""))!;
    await flushPromises();

    expect(asked()).toEqual([
      "repo:acme/rocket is:open assignee:@me sort:updated-desc",
      "repo:acme/rocket is:open sort:updated-desc",
    ]);
    expect(picker.groups.value.map((group) => [group.label, group.items.map((i) => i.number)])).toEqual([
      ["Assigned to you", [5]],
      ["Recently updated", [6, 7]],
    ]);
  });

  it("puts the item with a typed number first, and waits for typing to pause", async () => {
    apiFetchMock.mockImplementation(async (url: string) => (String(url).includes("/items?") ? json([item(318)]) : page(318, 31)));
    const query = shallowRef<string | null>("3");
    const picker = scope.run(() => useGitHubPicker({ owner: "acme", repo: "rocket" }, query))!;
    await flushPromises();
    apiFetchMock.mockClear();

    query.value = "31";
    query.value = "318";
    await vi.advanceTimersByTimeAsync(250);
    await flushPromises();

    expect(asked()).toEqual(["/api/integrations/github/repos/acme/rocket/items?numbers=318", "repo:acme/rocket is:open 318 sort:updated-desc"]);
    expect(picker.groups.value[0].items.map((i) => i.number)).toEqual([318, 31]);
  });

  it("asks nothing without a repository", async () => {
    const picker = scope.run(() => useGitHubPicker(null, "flicker"))!;
    await flushPromises();

    expect(apiFetchMock).not.toHaveBeenCalled();
    expect(picker.groups.value).toEqual([]);
  });
});
