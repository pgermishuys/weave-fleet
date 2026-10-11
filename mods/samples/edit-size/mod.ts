import type { Register } from "fleet-mods";

// Draws a file write as the lines it wrote ("+42") and a patch as the lines it added and removed ("+12 −3").
// Fleet already shows +/- on edit rows, so this covers the two tools that have no count of their own.
export type Size = { added: number; removed: number };

const lines = (text: unknown): string[] => (typeof text === "string" && text !== "" ? text.replace(/\n$/, "").split("\n") : []);

/** The size of one call's input, or undefined when it has none of the fields below. */
export function sizeOf(tool: string, input: Record<string, unknown>): Size | undefined {
  if (tool === "write") return typeof input.content === "string" ? { added: lines(input.content).length, removed: 0 } : undefined;
  const patch = lines(input.patchText ?? input.patch ?? input.input);
  if (patch.length === 0) return undefined;
  // "*** Begin Patch" or a unified diff: "+" and "-" lines, not the "+++ b/file" and "--- a/file" headers.
  const body = patch.filter((l) => !/^(\+\+\+|---) /.test(l));
  return { added: body.filter((l) => l.startsWith("+")).length, removed: body.filter((l) => l.startsWith("-")).length };
}

export const register: Register = (on) => {
  on("ui.render", { component: "ToolUse", props: { tool: ["write", "patch"] } }, async ($, e, next) => {
    if (e.component !== "ToolUse" || e.props.status !== "completed") return next(e); // not while it runs, not when it failed
    const size = sizeOf(e.props.tool, e.props.input);
    if (!size) return next(e);

    const { Box, Pill } = $.ui.resolve(e);
    return Box({
      flexDirection: "row",
      gap: 1,
      children: [
        size.added > 0 && Pill({ tone: "good", label: `+${size.added}` }),
        size.removed > 0 && Pill({ tone: "bad", label: `−${size.removed}` }),
        size.added + size.removed === 0 && Pill({ tone: "neutral", label: "no change" }),
      ],
    });
  });
};
