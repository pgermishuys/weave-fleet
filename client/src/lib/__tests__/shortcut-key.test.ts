import { describe, expect, it } from "vitest";
import { shortcutKey } from "../shortcut-key";
import { matchesKeyboardShortcut } from "@/composables/use-keyboard-shortcut";
import { isFindShortcut } from "../conversation-find";
import { stepShortcut } from "../changes-find";

const press = (init: KeyboardEventInit) => new KeyboardEvent("keydown", init);

describe("shortcutKey", () => {
  it("falls back to the physical key for a non-Latin layout while Ctrl is held", () => {
    expect(shortcutKey(press({ key: "л", code: "KeyK", ctrlKey: true }))).toBe("k");
    expect(shortcutKey(press({ key: "κ", code: "KeyK", metaKey: true }))).toBe("k");
    expect(shortcutKey(press({ key: "ك", code: "KeyF", ctrlKey: true }))).toBe("f");
  });

  it("never remaps plain typing", () => {
    expect(shortcutKey(press({ key: "л", code: "KeyK" }))).toBe("л");
  });

  it("keeps matching a Latin layout by key, not by position", () => {
    expect(shortcutKey(press({ key: "a", code: "KeyQ", ctrlKey: true }))).toBe("a");
    expect(shortcutKey(press({ key: "K", code: "KeyK", ctrlKey: true }))).toBe("k");
  });
});

describe("shortcuts on a Cyrillic layout", () => {
  it("matches a Ctrl shortcut", () => {
    const event = press({ key: "л", code: "KeyK", ctrlKey: true });
    expect(matchesKeyboardShortcut(event, { key: "k", ctrlKey: true })).toBe(true);
  });
  it("doesn't match a plain letter shortcut for the wrong key", () => {
    expect(matchesKeyboardShortcut(press({ key: "л", code: "KeyK" }), { key: "k" })).toBe(false);
  });
  it("finds with Ctrl+F", () => {
    expect(isFindShortcut(press({ key: "а", code: "KeyF", ctrlKey: true }), false)).toBe(true);
  });
  it("steps with Ctrl+G", () => {
    expect(stepShortcut(press({ key: "п", code: "KeyG", ctrlKey: true }), false)).toBe(1);
  });
});
