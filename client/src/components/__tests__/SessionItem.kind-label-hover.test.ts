// On hover the row's pin and archive buttons take the right edge. The kind label ("FORK", …) sits there too, so it
// steps aside like the time does, and stays muted (never lit, #573). jsdom can't apply :hover: read the stylesheet.
import { describe, expect, it } from "vitest";
import source from "../sessions/SessionItem.vue?raw";

function rules(css: string): { selector: string; body: string }[] {
  return [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => ({ selector: m[1].trim(), body: m[2] }));
}

describe("SessionItem kind label under the hover buttons", () => {
  const style = source.slice(source.indexOf("<style"));
  const hiding = rules(style).filter((r) => /visibility:\s*hidden/.test(r.body)).flatMap((r) => r.selector.split(",").map((s) => s.trim()));

  it("is hidden while the row is hovered", () => {
    expect(hiding).toContain(".session-item-shell:hover .session-item--has-action .session-kind");
  });

  it("is hidden while a row button has keyboard focus", () => {
    expect(hiding).toContain(".session-item-shell:has(.session-row-action:focus-visible) .session-kind");
  });
});
