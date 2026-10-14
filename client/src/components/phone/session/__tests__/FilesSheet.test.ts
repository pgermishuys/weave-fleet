import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";

const { browse, read } = vi.hoisted(() => ({ browse: vi.fn(), read: vi.fn() }));
vi.mock("@/api/session-files", () => ({ browseSessionDirectory: browse, readSessionFile: read }));

import FilesSheet from "../FilesSheet.vue";

const BottomSheet = defineComponent({ setup: (_, { slots }) => () => h("div", [slots.head?.(), slots.default?.()]) });

describe("Files sheet", () => {
  it("opens the file a chip names, with its folder behind it", async () => {
    browse.mockResolvedValue({ entries: [] });
    read.mockResolvedValue({ content: "line one\nline two\n", isBinary: false, isTruncated: false });
    const sheet = mount(FilesSheet, { props: { open: false, sessionId: "s1", openPath: ".weave/attachments/3f9a2c1d/build-output.log" }, global: { stubs: { BottomSheet } } });

    await sheet.setProps({ open: true });
    await flushPromises();

    expect(browse).toHaveBeenCalledWith(expect.anything(), "s1", ".weave/attachments/3f9a2c1d");
    expect(read).toHaveBeenCalledWith(expect.anything(), "s1", ".weave/attachments/3f9a2c1d/build-output.log");
    expect(sheet.text()).toContain("line one");
  });

  it("starts at the session's folder without a path", async () => {
    browse.mockClear();
    const sheet = mount(FilesSheet, { props: { open: false, sessionId: "s1" }, global: { stubs: { BottomSheet } } });

    await sheet.setProps({ open: true });

    expect(browse).toHaveBeenCalledWith(expect.anything(), "s1", undefined);
  });
});
