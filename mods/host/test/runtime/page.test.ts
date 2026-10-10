import { describe, expect, test } from "bun:test";
import { mkdtempSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { renderE, setup, writeMod } from "../helpers/harness";

const pane = renderE("Pane", "notes", { title: "Notes" });
const drawPage = (path: string) => `on("ui.render", { component: "Pane" }, ($, e) => $.ui.resolve(e).Page({ key: "p", path: ${JSON.stringify(path)}, title: "Notes" }));`;

async function loadWith(s: ReturnType<typeof setup>, name: string, body: string, extra: Record<string, string> = {}, link?: [string, string]) {
  const root = writeMod(name, body, extra);
  if (link) symlinkSync(link[1], join(root, link[0]));
  return s.peer.call("load", { id: `${name}@v1`, name, version: 1, root });
}

describe("Page paths at render time", () => {
  test("a Page naming an .html file in the mod's folder draws", async () => {
    const s = setup();
    await loadWith(s, "page-ok", drawPage("notes.html"), { "notes.html": "<p>notes</p>" });
    const r = await s.render(["page-ok@v1"], pane);
    expect(r.failures).toEqual([]);
    expect(r.result).toEqual({ type: "Page", props: { key: "p", path: "notes.html", title: "Notes" } });
  });

  test("a Page naming a file that isn't there is a throw failure", async () => {
    const s = setup();
    await loadWith(s, "page-missing", drawPage("gone.html"));
    const r = await s.render(["page-missing@v1"], pane);
    expect(r.failures[0].kind).toBe("throw");
    expect(r.failures[0].message).toContain("gone.html");
  });

  test("a Page reaching outside the folder through ../ or a link is a throw failure", async () => {
    const outside = mkdtempSync(join(tmpdir(), "fleet-page-outside-"));
    writeFileSync(join(outside, "secret.html"), "<p>outside</p>");
    const s = setup();
    await loadWith(s, "page-escape", drawPage("../escape.html"), {});
    expect((await s.render(["page-escape@v1"], pane)).failures).toHaveLength(1);
    const t = setup();
    await loadWith(t, "page-link", drawPage("linked.html"), {}, ["linked.html", join(outside, "secret.html")]);
    const r = await t.render(["page-link@v1"], pane);
    expect(r.failures).toHaveLength(1);
    expect(r.result).toEqual({ type: "Fleet" });
  });
});
