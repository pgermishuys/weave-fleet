// The session list's lineage (lines, kind labels and the caret) is always muted, never lit: not for the open
// session's family and not on hover (decided 17 October 2026, #573). jsdom can't apply :hover, so this reads the
// component's own stylesheet: no rule that targets the caret on hover or focus may change its colour.
import { describe, expect, it } from "vitest";
import source from "../sessions/SessionItem.vue?raw";

function rules(css: string): { selector: string; body: string }[] {
  return [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => ({ selector: m[1].trim(), body: m[2] }));
}

describe("SessionItem lineage caret", () => {
  const style = source.slice(source.indexOf("<style"));

  it("is muted", () => {
    const caret = rules(style).find((r) => r.selector === ".session-caret");
    expect(caret?.body).toMatch(/color:\s*var\(--muted\)/);
  });

  it("never lights on hover or focus", () => {
    const lit = rules(style).filter(
      (r) => r.selector.split(",").some((s) => /\.session-caret\b[^,]*:(hover|focus)/.test(s) || /:(hover|focus)[^,]*\.session-caret\b/.test(s))
        && /(^|;|\s)color\s*:/.test(r.body),
    );
    expect(lit.map((r) => r.selector)).toEqual([]);
  });
});
