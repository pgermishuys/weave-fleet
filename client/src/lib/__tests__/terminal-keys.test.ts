import { describe, expect, it } from "vitest";
import type { GlobalShortcut } from "@/lib/command-registry";
import { terminalKeyOwner } from "@/lib/terminal-keys";

const toggleTerminal: GlobalShortcut = { key: "j", platformModifier: true };
const toggleRightPanel: GlobalShortcut = { key: "b", platformModifier: true, shiftKey: true };
const passThrough = [toggleTerminal, toggleRightPanel];

function key(init: KeyboardEventInit): KeyboardEvent {
  return new KeyboardEvent("keydown", init);
}

// jsdom's navigator isn't a Mac, so platformModifier means Ctrl here.
describe("terminalKeyOwner (Linux and Windows)", () => {
  it.each([
    ["Esc", { key: "Escape" }],
    ["Ctrl K", { key: "k", ctrlKey: true }],
    ["Ctrl B", { key: "b", ctrlKey: true }],
    ["Ctrl [", { key: "[", ctrlKey: true }],
    ["Ctrl C", { key: "c", ctrlKey: true }],
    ["Ctrl V", { key: "v", ctrlKey: true }],
    ["a letter", { key: "a" }],
  ])("gives %s to the shell", (_name, init) => {
    expect(terminalKeyOwner(key(init), false, passThrough)).toBe("shell");
  });

  it("leaves Ctrl J and Ctrl Shift B to Fleet", () => {
    expect(terminalKeyOwner(key({ key: "j", ctrlKey: true }), false, passThrough)).toBe("fleet");
    expect(terminalKeyOwner(key({ key: "B", ctrlKey: true, shiftKey: true }), false, passThrough)).toBe("fleet");
  });

  it("copies with Ctrl Shift C and pastes with Ctrl Shift V", () => {
    expect(terminalKeyOwner(key({ key: "C", ctrlKey: true, shiftKey: true }), false, passThrough)).toBe("copy");
    expect(terminalKeyOwner(key({ key: "V", ctrlKey: true, shiftKey: true }), false, passThrough)).toBe("paste");
  });

  it("follows a rebound terminal shortcut", () => {
    const rebound: GlobalShortcut = { key: "`", ctrlKey: true };

    expect(terminalKeyOwner(key({ key: "`", ctrlKey: true }), false, [rebound])).toBe("fleet");
    expect(terminalKeyOwner(key({ key: "j", ctrlKey: true }), false, [rebound])).toBe("shell");
  });
});

describe("terminalKeyOwner (Mac)", () => {
  it("leaves Ctrl Shift C to the shell, since ⌘ C copies", () => {
    expect(terminalKeyOwner(key({ key: "C", ctrlKey: true, shiftKey: true }), true, passThrough)).toBe("shell");
  });
});
