import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/edit-size/mod";
import { checked, pills, render } from "./render";

const unified = ["--- a/src/totals.ts", "+++ b/src/totals.ts", "@@ -1,3 +1,4 @@", " keep", "-old one", "-old two", "+new one", "+new two", "+new three"].join("\n");

describe("the edit-size sample", () => {
  test("passes the static check and hooks the write and patch tools", async () => {
    const report = await checked("edit-size");
    expect(report.hooks).toEqual([{ event: "ui.render", matcher: { component: "ToolUse", props: { tool: ["write", "patch"] } } }]);
  });

  test("a write is all additions (the input of a real OpenCode write call)", () => {
    const input = { filePath: "/work/cart-totals/src/index.ts", content: 'export * from "./totals";\nexport * from "./cart";\n' };
    expect(mod.sizeOf("write", input)).toEqual({ added: 2, removed: 0 });
    expect(mod.sizeOf("write", {})).toBeUndefined();
  });

  test("a patch counts its + and - lines, not the file headers", () => {
    expect(mod.sizeOf("patch", { patchText: unified })).toEqual({ added: 3, removed: 2 });
    expect(mod.sizeOf("patch", { patchText: "*** Begin Patch\n*** Update File: a.ts\n@@\n-old\n+new\n+newer\n*** End Patch" })).toEqual({ added: 2, removed: 1 });
    expect(mod.sizeOf("patch", {})).toBeUndefined();
  });

  test("the row line", async () => {
    const tree = (props: Record<string, unknown>) => render(mod, "ToolUse", props);
    expect(pills(await tree({ tool: "write", input: { content: "x\ny" } }))).toEqual(["+2"]);
    expect(pills(await tree({ tool: "patch", input: { patchText: unified } }))).toEqual(["+3", "−2"]);
    expect(pills(await tree({ tool: "patch", input: { patchText: " context only" } }))).toEqual(["no change"]);
    expect(await tree({ tool: "write", status: "error", input: { content: "x" } })).toBe("next");
    expect(await tree({ tool: "write", input: {} })).toBe("next");
  });
});
