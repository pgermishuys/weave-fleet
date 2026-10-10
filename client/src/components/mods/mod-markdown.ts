import type MarkdownIt from "markdown-it";
import { createMarkdownRenderer } from "@/lib/markdown-renderer";

/**
 * Markdown as a mod's text is drawn: the conversation's renderer (same look, raw HTML off) with the parts that reach out
 * taken away. Stage 1 mods must not reach the network, so an image is its alt text, never an `<img>`. A link is live only
 * when it is a web or mail address; anything else (a file path the conversation would open in a tab, `/api/…`, `#frag`)
 * is plain text. Live links open in a new tab without the opener.
 */
const LIVE_LINK = /^(?:https?:|mailto:)/i;

function modOnly(md: MarkdownIt): MarkdownIt {
  md.renderer.rules.image = (tokens, index) => md.utils.escapeHtml(tokens[index].content);

  md.core.ruler.push("mod-links", (state) => {
    for (const block of state.tokens) {
      let dead = false;
      for (const token of block.children ?? []) {
        if (token.type === "link_open") {
          dead = !LIVE_LINK.test((token.attrGet("href") ?? "").trim());
          if (dead) token.hidden = true;
          else {
            token.attrSet("target", "_blank");
            token.attrSet("rel", "noopener noreferrer");
          }
        } else if (token.type === "link_close" && dead) {
          token.hidden = true;
          dead = false;
        }
      }
    }
  });
  return md;
}

let renderer: MarkdownIt | null = null;

/** The one renderer every mod `Markdown` shares (built once: a renderer compiles its link patterns). */
export function modMarkdownRenderer(): MarkdownIt {
  renderer ??= modOnly(createMarkdownRenderer());
  return renderer;
}
