import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import StorageSection from "@/components/settings/StorageSection.vue";

const { GET, POST } = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn() }));
vi.mock("@/lib/machine-target", () => ({ liveTarget: () => ({ api: { GET, POST } }) }));

const checkout = (id: string, repository: string, title: string, verdict: string, reason: string) => ({
  workspaceId: id, sessionId: `s-${id}`, title, repository, directory: `/w/${id}`, branch: `fleet/${id}`, verdict, reason, unpushedCommits: null, archivedAt: null,
});

const list = [
  checkout("w1", "harbor-api", "Export to CSV", "remove", "Archived 9 days ago."),
  checkout("w2", "pocket-notes", "Fix note sync", "remove", "Archived 12 days ago."),
  checkout("w3", "lumen-site", "Checkout flow", "keep", "Has an ignored file you may want: .env.local"),
  checkout("w4", "lumen-site", "Hero refresh", "recent", "Archived yesterday."),
  checkout("w5", "harbor-api", "Add webhooks", "in-use", "The session isn't archived."),
];
const GB = 1024 ** 3;

beforeEach(() => {
  GET.mockReset();
  POST.mockReset();
  GET.mockImplementation(async (path: string, options?: { params?: { path?: { workspaceId: string } } }) => {
    if (path === "/api/storage/worktrees") return { data: { checkouts: list, archivedDays: 7 } };
    return { data: { bytes: options!.params!.path!.workspaceId === "w2" ? GB : 2 * GB, complete: true } };
  });
});

describe("StorageSection", () => {
  it("shows the counts, fills sizes in, and previews what would go", async () => {
    const wrapper = mount(StorageSection);
    await flushPromises();

    expect(wrapper.get("[data-testid='storage-total']").text()).toBe("9.0 GB in 5 checkouts");
    expect(wrapper.get("[data-testid='storage-split-remove']").text()).toContain("3.0 GB");
    expect(wrapper.get("[data-testid='storage-split-remove']").text()).toContain("2 could be freed");
    expect(wrapper.get("[data-testid='storage-split-keep']").text()).toContain("1 need a look");

    await wrapper.get("[data-testid='storage-preview']").trigger("click");
    const panel = wrapper.get("[data-testid='storage-preview-panel']").text();
    expect(panel).toContain("Would remove 2 checkouts and free 3.0 GB");
    expect(panel).toContain("1 would be kept, and 1 more is too recent");
    expect(panel).toContain("harbor-api · Export to CSV");
    expect(panel).toContain("Has an ignored file you may want: .env.local");
  });

  it("removes the would-remove checkouts and reports what went and what stayed", async () => {
    POST.mockResolvedValue({
      data: { removed: [list[0]], kept: [{ ...list[1], verdict: "keep", reason: "A terminal is open in it." }] },
    });
    const wrapper = mount(StorageSection);
    await flushPromises();
    await wrapper.get("[data-testid='storage-preview']").trigger("click");
    await wrapper.get("[data-testid='storage-confirm']").trigger("click");
    await flushPromises();

    expect(POST).toHaveBeenCalledWith("/api/storage/worktrees/clean", { body: { workspaceIds: ["w1", "w2"] } });
    const report = wrapper.get("[data-testid='storage-report']").text();
    expect(report).toContain("Freed 2.0 GB");
    expect(report).toContain("Removed 1 checkout. Kept 2, and 1 is too recent.");
    // What the preview already kept is in the report too, after what the run kept.
    expect(report).toContain("Has an ignored file you may want: .env.local");
    expect(report).toContain("A terminal is open in it.");
  });

  it("disables Preview when nothing could be freed", async () => {
    GET.mockImplementation(async (path: string) => path === "/api/storage/worktrees"
      ? { data: { checkouts: [list[4]], archivedDays: 7 } }
      : { data: { bytes: GB, complete: false } });
    const wrapper = mount(StorageSection);
    await flushPromises();

    expect(wrapper.get("[data-testid='storage-preview']").attributes("disabled")).toBeDefined();
    expect(wrapper.text()).toContain("no session has been archived for 7 days");
    expect(wrapper.get("[data-testid='storage-total']").text()).toContain("≥ 1.0 GB");
  });
});
