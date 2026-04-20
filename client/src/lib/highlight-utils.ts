import type { VNode } from "vue";
import { h } from "vue";

/**
 * Splits text around case-insensitive query matches and wraps each match in a
 * `<mark>` element. Returns an array containing plain strings and VNodes.
 *
 * - If `query` or `text` is empty, returns `[text]`.
 * - Regex-special characters in `query` are escaped before matching.
 */
export function highlightText(text: string, query: string): (string | VNode)[] {
  if (!query || !text) return [text];

  // Escape regex special characters so literal dots, parens, etc. are matched
  const escaped = query.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const matchRe = new RegExp(escaped, "i");

  // Split with a capturing group so matched tokens are included in the array
  const parts = text.split(new RegExp(`(${escaped})`, "gi"));

  return parts
    .filter((p) => p !== "")
    .map((part, i) =>
      matchRe.test(part)
        ? h(
            "mark",
            {
              key: i,
              class: "bg-yellow-500/30 text-foreground rounded-sm px-0.5",
            },
            part
          )
        : part
    );
}
