import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed, ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ChangesCanvas from "@/components/canvas/ChangesCanvas.vue";
import type { FileDiffItem, SessionDiffBase } from "@/api/client";

const { readSessionFileMock } = vi.hoisted(() => ({
  readSessionFileMock: vi.fn(),
}));

vi.mock("@/api/session-files", () => ({
  browseSessionDirectory: vi.fn(),
  readSessionFile: readSessionFileMock,
  writeSessionFile: vi.fn(),
}));

const sharedDiffs = {
  hasLoaded: ref(true),
  diffs: ref<FileDiffItem[]>([]),
  base: ref<SessionDiffBase | null>(null),
  refusal: ref<{ message: string; fixCommand: string } | null>(null),
  error: ref<string | undefined>(undefined),
  fetchDiffs: vi.fn(async () => {}),
  byFile: computed((): ReadonlyMap<string, FileDiffItem> => new Map(sharedDiffs.diffs.value.map((diff) => [diff.file, diff]))),
};

function mountCanvas() {
  return mount(ChangesCanvas, {
    props: { sessionId: "s1" },
    global: {
      provide: { sharedDiffs },
    },
  });
}

describe("ChangesCanvas", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    readSessionFileMock.mockReset();
    sharedDiffs.diffs.value = [];
    sharedDiffs.base.value = null;
    sharedDiffs.hasLoaded.value = true;
    sharedDiffs.error.value = undefined;
    sharedDiffs.fetchDiffs.mockClear();
  });

  it("says the changes could not be loaded, with Try again, instead of No changes when /diffs fails", async () => {
    sharedDiffs.error.value = "HTTP 500";
    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.text()).not.toContain("No changes");
    expect(wrapper.get('[data-testid="changes-load-failed"]').text()).toContain("Couldn't load the changes");
    await wrapper.get('[data-testid="changes-retry"]').trigger("click");
    expect(sharedDiffs.fetchDiffs).toHaveBeenCalledTimes(1);
  });

  it("says_why_when_git_refuses_the_folder_and_shows_the_fix_command", () => {
    sharedDiffs.refusal.value = {
      message: "Git won't read this folder because another user owns it.",
      fixCommand: "git config --global --add safe.directory /srv/app",
    };
    const wrapper = mountCanvas();
    sharedDiffs.refusal.value = null;

    expect(wrapper.get('[data-testid="changes-git-refused"]').text()).toContain("another user owns it");
    expect(wrapper.get('[data-testid="git-fix-command"]').text()).toBe("git config --global --add safe.directory /srv/app");
    expect(wrapper.text()).not.toContain("No changes");
  });

  it("says so when the session has no changes", async () => {
    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.text()).toContain("No changes in this session yet.");
  });

  it("says it is loading, not empty, until the first answer arrives", async () => {
    sharedDiffs.hasLoaded.value = false;

    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.text()).toContain("Loading changes…");
    expect(wrapper.text()).not.toContain("No changes");

    sharedDiffs.hasLoaded.value = true;
    await flushPromises();

    expect(wrapper.text()).toContain("No changes in this session yet.");
    expect(wrapper.text()).not.toContain("Loading changes");
  });

  it("says a branch is compared with where it left main", async () => {
    sharedDiffs.base.value = { kind: "branch", branch: "main", commit: "8a61fdd7c0ffee" };

    const wrapper = mountCanvas();
    await flushPromises();

    const base = wrapper.get("[data-testid='changes-base']");
    expect(base.text()).toBe("Compared with main8a61fdd");
    expect(base.attributes("title")).toContain("since it left main at 8a61fdd");
    expect(wrapper.text()).toContain("No changes on this branch yet.");
  });

  it("says a folder off any branch is compared with the session start", async () => {
    sharedDiffs.base.value = { kind: "session" };

    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.get("[data-testid='changes-base']").text()).toBe("Since this session started");
    expect(wrapper.text()).toContain("No changes in this session yet.");
  });

  it("leaves the base line out when changes show: the scope select carries it", async () => {
    sharedDiffs.base.value = { kind: "session" };
    sharedDiffs.diffs.value = [{ file: "a.ts", status: "modified", additions: 1, deletions: 0 }] as FileDiffItem[];

    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.find("[data-testid='changes-base']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='review-scope']").attributes("title")).toBe("Since this session started");
  });
})
