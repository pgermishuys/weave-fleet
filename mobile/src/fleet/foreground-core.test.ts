import { describe, expect, test } from "bun:test";
import { foregroundChange } from "./foreground-core";

/** Plays an AppState sequence the way hooks.ts does and returns what the manager was told. */
function play(states: string[]) {
  let foreground = true;
  const told: boolean[] = [];
  for (const state of states) {
    const change = foregroundChange(foreground, state);
    if (change === null) continue;
    foreground = change;
    told.push(change);
  }
  return told;
}

describe("foreground changes", () => {
  test("Control Center (inactive, then active) tells the manager nothing", () => {
    expect(play(["inactive", "active"])).toEqual([]);
  });

  test("the app switcher and back tells it nothing; a real background trip tells it twice", () => {
    expect(play(["inactive", "active", "inactive", "active"])).toEqual([]);
    expect(play(["inactive", "background", "inactive", "active"])).toEqual([false, true]);
    expect(play(["background", "active"])).toEqual([false, true]);
  });
});
