import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import FilesChanged from "@/components/session/FilesChanged.vue";

describe("FilesChanged", () => {
  it("renders_file_count_and_total_line_counts", () => {
    const wrapper = mount(FilesChanged, {
      props: {
        files: [
          { path: "src/App.vue", additions: 12, deletions: 3 },
          { path: "src/main.ts", additions: 5, deletions: 1 },
        ],
      },
    });

    const badge = wrapper.get("button");

    expect(badge.text()).toContain("2 files changed");
    expect(badge.text()).toContain("+17");
    expect(badge.text()).toContain("-4");
    expect(badge.attributes("aria-label")).toBe("2 files changed, 17 additions, 4 deletions");
    expect(wrapper.text()).not.toContain("src/App.vue");
  });

  it("emits_click_and_open_payload_when_badge_is_clicked", async () => {
    const wrapper = mount(FilesChanged, {
      props: {
        expanded: false,
        files: [
          { path: "src/App.vue", additions: 2, deletions: 1 },
        ],
      },
    });

    await wrapper.get("button").trigger("click");

    const expectedPayload = {
      open: true,
      fileCount: 1,
      additions: 2,
      deletions: 1,
    };
    expect(wrapper.emitted("click")).toEqual([[expectedPayload]]);
    expect(wrapper.emitted("open")).toEqual([[expectedPayload]]);
  });

  it("emits_close_payload_when_expanded_badge_is_clicked", async () => {
    const wrapper = mount(FilesChanged, {
      props: {
        expanded: true,
        files: [
          { path: "src/App.vue", additions: 2, deletions: 1 },
        ],
      },
    });

    await wrapper.get("button").trigger("click");

    expect(wrapper.emitted("click")?.[0]?.[0]).toEqual({
      open: false,
      fileCount: 1,
      additions: 2,
      deletions: 1,
    });
  });

  it("does_not_emit_while_loading", async () => {
    const wrapper = mount(FilesChanged, {
      props: {
        isLoading: true,
        files: [
          { path: "src/App.vue", additions: 2, deletions: 1 },
        ],
      },
    });

    const badge = wrapper.get("button");
    await badge.trigger("click");

    expect(badge.attributes("disabled")).toBeDefined();
    expect(badge.attributes("aria-busy")).toBe("true");
    expect(badge.text()).toContain("Loading changes");
    expect(wrapper.emitted("click")).toBeUndefined();
    expect(wrapper.emitted("open")).toBeUndefined();
  });

  it("does_not_emit_when_disabled", async () => {
    const wrapper = mount(FilesChanged, {
      props: {
        disabled: true,
        files: [
          { path: "src/App.vue", additions: 2, deletions: 1 },
        ],
      },
    });

    const badge = wrapper.get("button");
    await badge.trigger("click");

    expect(badge.attributes("disabled")).toBeDefined();
    expect(wrapper.emitted("click")).toBeUndefined();
    expect(wrapper.emitted("open")).toBeUndefined();
  });

  it("renders_empty_and_error_edge_states", () => {
    const wrapper = mount(FilesChanged, {
      props: {
        error: "HTTP 500",
        files: [
          { path: "src/App.vue", additions: Number.NaN, deletions: null },
        ],
      },
    });

    const badge = wrapper.get("button");

    expect(badge.text()).toContain("1 file changed");
    expect(badge.text()).toContain("+0");
    expect(badge.text()).toContain("-0");
    expect(badge.attributes("title")).toBe("HTTP 500");
    expect(badge.attributes("aria-label")).toBe("1 file changed, 0 additions, 0 deletions. HTTP 500");
    expect(wrapper.find(".files-changed__error-indicator").exists()).toBe(true);
  });

  it("renders_no_badge_when_unavailable", () => {
    const wrapper = mount(FilesChanged, {
      props: {
        unavailable: true,
        files: [
          { path: "src/App.vue", additions: 2, deletions: 1 },
        ],
      },
    });

    expect(wrapper.find("button").exists()).toBe(false);
    expect(wrapper.html()).toBe("<!--v-if-->");
  });
});
