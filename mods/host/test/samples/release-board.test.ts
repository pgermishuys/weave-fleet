import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/release-board/mod";
import { checked } from "./render";

/** Runs the mod's hooks the way the host would, recording what `$.ui.open` and `$.ui.close` are asked. */
async function hooks() {
  const found: any[] = [];
  mod.register(((event: string, matcher: unknown, fn: unknown) => found.push({ event, matcher, fn })) as never, {});
  const calls: string[] = [];
  const element = (type: string) => (p: Record<string, any>) => ({ type, ...p });
  const $ = {
    ui: {
      resolve: () => ({ Box: element("Box"), Text: element("Text"), Pill: element("Pill"), Button: element("Button") }),
      open: async (pane: { id: string; title?: string }) => void calls.push(`open ${pane.id} ${pane.title}`),
      close: async (pane: { id: string }) => void calls.push(`close ${pane.id}`),
    },
  };
  return { found, calls, $ };
}

describe("the release-board sample", () => {
  test("passes the static check and hooks a shell row's body and a pane", async () => {
    const report = await checked("release-board");
    expect(report.hooks).toEqual([
      { event: "ui.render", matcher: { component: "ToolResult", props: { tool: ["bash", "shell"] } } },
      { event: "ui.render", matcher: { component: "Pane" } },
    ]);
  });

  test("the row's button opens the board, and the board's button closes it", async () => {
    const { found, calls, $ } = await hooks();
    const row = await found[0].fn($, { component: "ToolResult", sessionId: "s", requestId: "c", props: {} }, async () => "fleet body");
    expect(row.children[0]).toMatchObject({ type: "Button", key: "open", label: "Open board" });
    expect(row.children[1]).toBe("fleet body");
    await row.children[0].onPress();

    const board = await found[1].fn($, { component: "Pane", sessionId: "s", requestId: "board", props: { title: "Release board" } }, async () => null);
    const close = board.children.at(-1).children[0];
    expect(close).toMatchObject({ type: "Button", key: "close", label: "Close" });
    await close.onPress();
    expect(calls).toEqual(["open board Release board", "close board"]);
  });

  test("draws nothing for another pane id", async () => {
    const { found, $ } = await hooks();
    const other = await found[1].fn($, { component: "Pane", sessionId: "s", requestId: "other", props: { title: "x" } }, async () => "next");
    expect(other).toBe("next");
  });
});
