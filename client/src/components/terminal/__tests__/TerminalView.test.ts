import { flushPromises, mount } from "@vue/test-utils";
import { createPinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { resize, textarea, term } = vi.hoisted(() => {
  const textarea = document.createElement("textarea");
  const term = {
    cols: 80,
    rows: 24,
    textarea,
    loadAddon: vi.fn(),
    open: vi.fn(),
    focus: vi.fn(),
    dispose: vi.fn(),
    attachCustomKeyEventHandler: vi.fn(),
    onData: vi.fn(),
    onBinary: vi.fn(),
    onSelectionChange: vi.fn(),
    onScroll: vi.fn(),
    onResize: vi.fn(),
  };
  return { resize: vi.fn(), textarea, term };
});

// xterm needs a real layout engine; what matters here is what the view tells the shell about its size.
vi.mock("@xterm/xterm", () => ({ Terminal: function () { return term; } }));
vi.mock("@xterm/addon-fit", () => ({ FitAddon: function () { return { fit: vi.fn() }; } }));
vi.mock("@xterm/addon-web-links", () => ({ WebLinksAddon: vi.fn() }));
vi.mock("@xterm/xterm/css/xterm.css", () => ({}));
vi.mock("@/lib/terminal-connection", () => ({
  openTerminalConnection: vi.fn(() => ({ resize, write: vi.fn(), clear: vi.fn(), dispose: vi.fn() })),
}));

import TerminalView from "@/components/terminal/TerminalView.vue";

describe("TerminalView", () => {
  beforeEach(() => resize.mockClear());

  it("tells the shell its own size when it gets the keyboard, so another client that attached since doesn't leave it wrapping at their width", async () => {
    mount(TerminalView, { props: { sessionId: "s1", terminalId: "t1", shown: true }, global: { plugins: [createPinia()] }, attachTo: document.body });
    await flushPromises();
    await new Promise((r) => setTimeout(r, 0));

    term.cols = 200;
    term.rows = 50;
    textarea.dispatchEvent(new Event("focus"));

    expect(resize).toHaveBeenCalledWith(200, 50);
  });
});
