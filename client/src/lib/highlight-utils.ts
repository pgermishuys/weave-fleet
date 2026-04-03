import type { ReactNode } from "react";
import { createElement } from "react";

/**
 * Splits text around case-insensitive query matches and wraps each match in a
 * `<mark>` element. Returns an array containing plain strings and ReactNode
 * elements (the mark elements).
 *
 * - If `query` or `text` is empty, returns `[text]`.
 * - Regex-special characters in `query` are escaped before matching.
 */
export function highlightText(text: string, query: string): (string | ReactNode)[] {
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
        ? createElement(
            "mark",
            {
              key: i,
              className: "bg-yellow-500/30 text-foreground rounded-sm px-0.5",
            },
            part
          )
        : part
    );
}
