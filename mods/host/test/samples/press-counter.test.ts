import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/press-counter/mod";
import { checked } from "./render";

/** Runs the mod's hook the way the host would, over a $.state kept in `state`, and returns the tree and the button's callback. */
async function draw(state: Map<string, number>) {
  let hook: any;
  mod.register(((_event: string, _matcher: unknown, fn: unknown) => (hook = fn)) as never, {});
  const element = (type: string) => (p: Record<string, any>) => ({ type, ...p });
  const $ = {
    ui: { resolve: () => ({ Box: element("Box"), Text: element("Text"), Button: element("Button") }) },
    state: { get: (key: string) => state.get(key), set: (key: string, value: number) => void state.set(key, value) },
  };
  const tree = await hook($, { component: "ToolResult", sessionId: "ses_1", requestId: "call_1", props: { tool: "bash" } }, async () => "fleet body");
  const row = tree.children[0];
  return { tree, button: row.children[0], line: row.children[1].children[0] as string };
}

describe("the press-counter sample", () => {
  test("passes the static check and hooks the opened shell row", async () => {
    const report = await checked("press-counter");
    expect(report.hooks).toEqual([{ event: "ui.render", matcher: { component: "ToolResult", props: { tool: ["bash", "shell"] } } }]);
    expect(report.state).toEqual(["presses"]);
  });

  test("draws the count and Fleet's own body, and a press raises the count", async () => {
    const state = new Map<string, number>();
    const first = await draw(state);
    expect(first.line).toBe("Pressed 0 times");
    expect(first.button).toMatchObject({ type: "Button", key: "count", label: "Count a press" });
    expect(first.tree.children[1]).toBe("fleet body");

    first.button.onPress();
    first.button.onPress();
    expect((await draw(state)).line).toBe("Pressed 2 times");
  });
});
