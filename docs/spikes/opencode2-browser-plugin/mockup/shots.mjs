import { chromium } from "/home/pgermishuys/camofox-browser/node_modules/playwright-core/index.mjs";
const file = "file://" + new URL("./agent-browser.html", import.meta.url).pathname;
const out = process.env.OUT;
const b = await chromium.launch({ executablePath: process.env.CHROME, args: ["--no-sandbox"] });
const errors = [];
for (const [w, h, tag] of [[1440, 1000, "desk"], [390, 844, "phone"]]) {
  for (const theme of ["light", "dark"]) {
    const p = await b.newPage({ viewport: { width: w, height: h } });
    p.on("pageerror", (e) => errors.push(String(e)));
    for (const [view, q] of [["overview", ""], ["drive", "&step=4&view=agent"], ["drive", "&step=4&view=you"], ["settings", ""], ["skills", ""], ["how", ""], ["evidence", ""], ["plan", ""]]) {
      await p.goto(`${file}?theme=${theme}${q}&n=${Math.random()}#${view}`, { waitUntil: "load" });
      await p.waitForTimeout(250);
      const name = `${out}/${tag}-${theme}-${view}${q.includes("view=you") ? "-you" : ""}.png`;
      await p.screenshot({ path: name, fullPage: true });
      const sw = await p.evaluate(() => document.documentElement.scrollWidth);
      if (sw > w) errors.push(`${name}: page scrolls sideways (${sw} > ${w})`);
    }
    await p.close();
  }
}
console.log(errors.length ? errors.join("\n") : "no page errors, no sideways scroll");
await b.close();
