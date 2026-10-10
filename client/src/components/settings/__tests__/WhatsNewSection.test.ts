import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";

const { getMock, navigate } = vi.hoisted(() => ({ getMock: vi.fn(), navigate: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, POST: vi.fn().mockResolvedValue({}) } }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));

const { default: WhatsNewSection } = await import("@/components/settings/WhatsNewSection.vue");
const { useWhatsNew } = await import("@/composables/use-whats-new");
const { useSettingsNav } = await import("@/composables/use-settings-nav");

enableAutoUnmount(afterEach);

const body = (n: number, title: string) =>
  `## What's Changed\n* ${title} by @pgermishuys in https://github.com/pgermishuys/weave-fleet/pull/${n}\n`;

const releases = [
  { version: "0.47.0", publishedAt: "2026-10-08T08:00:00Z", body: body(395, "feat(settings): What's new in Fleet"), url: "https://x/v0.47.0" },
  { version: "0.46.0", publishedAt: "2026-10-07T08:00:00Z", body: body(391, "fix(sessions): dragging sessions works again"), url: "https://x/v0.46.0" },
  { version: "0.45.0", publishedAt: "2026-10-06T08:00:00Z", body: body(386, "Pin sessions above the projects"), url: "https://x/v0.45.0" },
];

function respond(status: { currentVersion: string; status: string; latestVersion: string | null }, error: string | null = null, list = releases) {
  getMock.mockImplementation((path: string) =>
    Promise.resolve({
      data: path === "/api/update/releases"
        ? { releases: list, fetchedAt: "2026-10-08T09:00:00Z", error }
        : { ...status, checkedAt: null, error: null, downloadBytesReceived: null, downloadBytesTotal: null },
    }),
  );
}

async function mountCard() {
  const wrapper = mount(WhatsNewSection, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

const versions = (wrapper: Awaited<ReturnType<typeof mountCard>>) =>
  wrapper.findAll("[data-testid='whats-new-version']").map((row) => ({
    version: row.attributes("data-version"),
    open: (row.element as HTMLDetailsElement).open,
    badge: row.find(".whats-new__badge").exists() ? row.find(".whats-new__badge").text() : null,
  }));

describe("What's new in Settings → System", () => {
  beforeEach(() => {
    getMock.mockReset();
    navigate.mockReset();
    Element.prototype.scrollIntoView = vi.fn();
  });

  it("opens the version waiting to install and the installed one, with their changes", async () => {
    respond({ currentVersion: "0.46.0", status: "staged", latestVersion: "0.47.0" });
    const wrapper = await mountCard();

    expect(versions(wrapper)).toEqual([
      { version: "0.47.0", open: true, badge: "Ready to install" },
      { version: "0.46.0", open: true, badge: "Installed" },
      { version: "0.45.0", open: false, badge: null },
    ]);
    const notes = wrapper.find("[data-version='0.47.0'] .whats-new__notes");
    expect(notes.find("h4").text()).toBe("New");
    expect(notes.find("li").text()).toBe("Settings What's new in Fleet #395");
    expect(wrapper.find("[data-version='0.46.0'] h4").text()).toBe("Fixed");
  });

  it("opens a pull request in the browser, not in Fleet's window", async () => {
    respond({ currentVersion: "0.46.0", status: "uptodate", latestVersion: "0.46.0" });
    const open = vi.spyOn(window, "open").mockReturnValue(null);
    const wrapper = await mountCard();

    await wrapper.find("[data-version='0.46.0'] li a").trigger("click");

    expect(open).toHaveBeenCalledWith("https://github.com/pgermishuys/weave-fleet/pull/391", "_blank", "noopener,noreferrer");
    open.mockRestore();
  });

  it("goes to Settings → System and scrolls to the version a What's new link named", async () => {
    respond({ currentVersion: "0.46.0", status: "uptodate", latestVersion: "0.46.0" });
    const Opener = defineComponent({
      setup() {
        return useWhatsNew();
      },
      render: () => null,
    });
    const opener = mount(Opener);
    const wrapper = await mountCard();

    (opener.vm as unknown as ReturnType<typeof useWhatsNew>).openWhatsNew("0.45.0");
    await flushPromises();

    expect(useSettingsNav().activeSection.value).toBe("system");
    expect(navigate).toHaveBeenCalledWith({ to: "/settings" });
    const row = wrapper.find("[data-version='0.45.0']");
    expect((row.element as HTMLDetailsElement).open).toBe(true);
    expect(row.classes()).toContain("whats-new__version--flash");
    expect(row.element.scrollIntoView).toHaveBeenCalled();
  });

  it("keeps showing the saved notes when GitHub can't be reached", async () => {
    respond({ currentVersion: "0.46.0", status: "uptodate", latestVersion: "0.46.0" }, "Couldn't reach GitHub");
    const wrapper = await mountCard();

    expect(wrapper.find("[data-testid='whats-new-stale']").text()).toContain("Couldn't reach GitHub");
    expect(versions(wrapper).length).toBe(3);
  });

  it("points to GitHub when Fleet has no notes at all", async () => {
    respond({ currentVersion: "0.46.0", status: "error", latestVersion: null }, "Couldn't reach GitHub", []);
    const wrapper = await mountCard();

    expect(wrapper.find("[data-testid='whats-new-empty']").text()).toContain("Fleet couldn't get the release notes (Couldn't reach GitHub).");
  });
});
