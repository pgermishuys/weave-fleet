import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

/**
 * Plain text as HTML with its web addresses made links. Only the text between and inside the links is escaped, so no
 * Markdown is read (`*` and `_` stay as typed) and no markup the text carries survives. Only `http(s)` addresses link.
 */
export function linkifyText(text: string): string {
  const matches = sharedMarkdownRenderer().linkify.match(text) ?? [];
  let html = "";
  let at = 0;
  for (const match of matches) {
    if (!/^https?:\/\//i.test(match.url)) continue;
    html += escapeHtml(text.slice(at, match.index));
    html += `<a href="${escapeHtml(match.url)}" target="_blank" rel="noopener noreferrer">${escapeHtml(match.text)}</a>`;
    at = match.lastIndex;
  }

  return html + escapeHtml(text.slice(at));
}
