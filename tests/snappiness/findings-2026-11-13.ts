/**
 * The 2026-11-13 report's words: what was measured, what it showed, causes with file:line. Numbers come from
 * compare.json; this file holds the reading of them. Writes findings.json for report.ts.
 *   bun findings-2026-11-13.ts <out.json> <report assets dir>
 */
import { writeFileSync } from "node:fs";
import { join } from "node:path";

const [out, assets] = process.argv.slice(2) as [string, string];

const findings = {
  title: "Fleet UI snappiness: v0.60.0 vs main",
  subtitle: "Measured 13 Nov 2026 on taron (Windows, 16 GB) against scratch Fleets seeded with invented data. v0.60.0 (21 Oct) is the last release before the layout, machines, folded-turns and Changes-review wave; main is 1e5a98f6.",
  summary: `
<p><b>At normal CPU speed most of the UI got faster, not slower:</b> cold start, opening and switching sessions, rail switches and sending are 11 to 92% quicker on main.</p>
<p><b>One regression explains the "slower" feel, and it's big:</b> with a diff open in the Changes panel, every repaint got expensive. On a slower CPU (4× throttle) a keystroke takes <b>161 ms</b> to show instead of <b>38 ms</b> (p95 445 vs 59 ms), and four streaming sessions cost 1.1 s of blocked main thread every 6 s instead of 0.06 s. Cause: the new Changes review panel gives every diff line two <code>position: sticky</code> number cells (1,569 for 11 files), and each repaint re-layerizes and re-hit-tests all of them. <b>Fixed</b> on branch <code>perf/changes-sticky-gutters</code>: typing back to 50 ms median, streaming long tasks −85%.</p>
<p><b>The rest of the slowdown is the same panel mounting:</b> on a slower CPU, opening a session whose folder has changes costs 32% more (1.4 → 1.9 s at 4×), because the full review renders with it; opening the Changes tab is 7× slower (21 → 142 ms at 1×). Those belong to the Changes session's investigation, with the numbers below.</p>`,
  tiles: [
    { n: "161 → 50 ms", k: "keystroke → paint, Changes open, 4× CPU (v0.60.0: 38 ms)" },
    { n: "−38%", k: "cold start to session list (1×)" },
    { n: "−31%", k: "switching two long sessions (1×)" },
    { n: "no leak", k: "heap, DOM and listeners flat over 120 session switches" },
  ],
  causes: [
    {
      title: "1 · Sticky line numbers in the Changes review panel slow every repaint (regression, fixed)",
      body: `
<p><b>Symptom.</b> Typing, sending and streaming are slower only in sessions whose folder has changes. At 4× CPU, keystroke → paint median 38 → 161 ms, p95 59 → 445 ms; long tasks while four sessions stream 57 → 1,097 ms per 6 s; a sent prompt settles in 2.1 s instead of 0.6 s. With no changes in the folder, main types as fast as v0.60.0 (median 40 vs 39 ms), so the composer itself isn't the cause.</p>
<p><b>Evidence.</b> A Chrome trace of typing 28 characters (same session, same edits, 4×):</p>
<table><thead><tr><th></th><th>v0.60.0</th><th>main</th></tr></thead><tbody>
<tr><td>Layerize</td><td>162 ms</td><td>767 ms</td></tr><tr><td>HitTest</td><td>8 ms</td><td>248 ms</td></tr><tr><td>Commit</td><td>23 ms</td><td>201 ms</td></tr><tr><td>Layout</td><td>~8 ms/key</td><td>~12 ms/key</td></tr><tr><td>Long tasks</td><td>0</td><td>18</td></tr></tbody></table>
<p>Script and layout barely moved; compositing did. The Changes panel on main renders every diff line inline: 4,920 DOM nodes (v0.60.0's Changes list: 138), with <b>1,569 <code>position: sticky</code></b> elements, the line-number cells <code>.rline__no</code> (<code>client/src/components/canvas/ChangesReview.vue:910–911, 934, 946, 995–996, 1027, 1046</code>; sticky rule at <code>:1512</code>), inside 20 horizontal scrollers <code>.rscroll</code> (<code>:1479</code>). Introduced by #862 (9 Nov, "review panel"). What-if runs that only injected CSS confirmed it: <code>.rline__no{position:static}</code> took typing from 135/422 to 49/90 ms (median/p95); hiding the scrollers' fade mask did nothing; <code>content-visibility</code> per hunk recovered most of it.</p>
<p><b>Fix</b> (branch <code>perf/changes-sticky-gutters</code>, 1 file + test): the numbers are static until their hunk is scrolled sideways; one passive capture-phase scroll listener on the review root toggles <code>.rscroll--scrolled</code>, and only there they stick. At <code>scrollLeft 0</code> a static number sits exactly where a sticky one did, so nothing moves; scroll a long line and its numbers still stay put. Same server, client without/with the change, 4×, 8 samples each:</p>
<table><thead><tr><th></th><th>before median / p95</th><th>after median / p95</th></tr></thead><tbody>
<tr><td>Keystroke → paint</td><td>134 / 384 ms</td><td><b>50 / 78 ms</b></td></tr>
<tr><td>INP while typing</td><td>116 / 120 ms</td><td>72 / 96 ms</td></tr>
<tr><td>Send a prompt, until settled</td><td>1,632 / 1,770 ms</td><td>1,156 / 1,698 ms</td></tr>
<tr><td>Long tasks while 4 sessions stream (per 6 s)</td><td>1,131 / 1,738 ms</td><td><b>165 / 529 ms</b></td></tr>
<tr><td>Keystroke p95 while 4 sessions stream</td><td>174 / 407 ms</td><td>74 / 142 ms</td></tr></tbody></table>`,
      images: [
        { path: join(assets, "base-changes.png"), alt: "v0.60.0: Changes is a file list (138 nodes)" },
        { path: join(assets, "fix-panel-light.png"), alt: "main with the fix: the review panel, a hunk scrolled sideways keeps its numbers" },
        { path: join(assets, "fix-panel-dark.png"), alt: "The same, dark" },
      ],
    },
    {
      title: "2 · The Changes review renders in full on open (left to the Changes session)",
      body: `<p><b>Opening a session</b> whose folder has changes: 1,399 → 1,851 ms at 4× (+32%; settled +69%), busy CPU 1,961 → 2,799 ms. Profile diff, main vs v0.60.0: <code>ChangesReview.vue</code> mount +103 ms, <code>use-file-diff-contents.ts</code> +41 ms, <code>diff-parser.ts</code> +8 ms, and +371 ms of native DOM/layout work for the thousands of review nodes. Going back to Sessions (+32%) and cold start to a conversation (+30%) pay the same at 4×. Removing the sticky gutters doesn't change these (what-if: 1,248 vs 1,170 ms): it's the eager render itself. At 1× these are hidden by main's other gains.</p><p><b>Picking the Changes tab</b>: 21 → 142 ms at 1×, 85 → 777 ms at 4×, with 122 ms of long tasks where v0.60.0 had none. The panel now renders the whole review (every hunk) on open rather than a file list. The Changes session ("Changes panel sluggish / won't load") owns the root cause; it also found that with Changes open on a 353-file session, Ctrl+F queued every other API call for ~71 s behind the browser's 6-connection limit. Its PR (branch <code>fix/changes-sluggish</code>) caps file fetches at 4 per machine and shares one <code>/diffs</code> on open. A suggestion for it: render a file's hunks when the file scrolls into view (or is expanded), so the panel's first paint is the file list, as in v0.60.0.</p>`,
    },
    {
      title: "3 · Mounting a long conversation is the biggest cost left (not a regression)",
      body: `<p>Switching to a 60-turn session takes 400 ms at 1× and ~3 s at 4×; returning to Sessions from another view costs the same, because the conversation mounts again. v0.60.0 is no faster (582 ms / 3.2 s). In the 4× profile (2.7 s busy) the top app frames are each bubble's <code>rendered</code> computed, <code>client/src/components/session/MessageBubble.vue:107</code> (markdown-it, ~425 ms inclusive), and highlight.js at ~270 ms self: every bubble re-parses its markdown and re-highlights its code on every mount, with no cache. After the first render, <code>client/src/stores/file-links.ts</code> resolves the paths the text names, and each bubble that names a file renders its markdown again (both versions).</p>
<p><b>Proposal</b> (a design choice, not built): (a) a small module-level cache of rendered HTML keyed by message id and text, so revisiting a session or coming back to Sessions skips markdown and highlighting (cheap, likely the biggest win); (b) highlight code blocks when they scroll into view; (c) render file links as a DOM pass after the HTML is in, not by re-rendering markdown. <code>client/src/components/session/DiffView.vue:211</code> also gives every line a sticky number, the same pattern as cause 1, in the file diff view (older than v0.60.0); the same fix applies.</p>`,
    },
    {
      title: "Checked and ruled out",
      body: `<ul>
<li><b>Whole-list re-renders on SignalR events:</b> four sessions streaming 60 updates each while you read an idle session (no diff open) cost 0 ms of long tasks on both versions; watching a streaming conversation, 0 ms median. The session list doesn't re-render per event.</li>
<li><b>A tab that slows over the day (leaks):</b> 120 session switches in one tab: heap 31 → 38 MB after GC, DOM nodes and listeners back to their baseline (1,477 listeners), switch time flat (long 323–443 ms, short 135–188 ms). Same shape on v0.60.0.</li>
<li><b>Unvirtualized long lists while scrolling:</b> 3 s of wheel scrolling through a 60-turn conversation holds 60 fps at 1× on both; at 4× main drops fewer frames (71 vs 84) with fewer long tasks (1.0 vs 1.6 s).</li>
<li><b>Too many requests on navigation:</b> opening a session makes ~22 API requests on both versions (a lot, but not new); the New session page 2 (3 at 4×).</li>
<li><b>New session page:</b> +17% at 1× (90 → 106 ms) but −11% at 4×: within noise.</li>
<li><b>Bundle:</b> JS decoded at cold start 2.58 → 2.79 MB (+8%), 232 → 238 requests; cold start is still 38% faster.</li></ul>`,
    },
  ],
  method: `
<p><b>Data.</b> Each build gets its own scratch Fleet in test-harness mode (own profile folder, databases, port, 127.0.0.1), seeded through the API with the same invented data: 6 git projects, 150 sessions (3 of 60 turns with 4 to 10 tool calls each, 12 of 10 turns, the rest short), markdown answers with code blocks and tables, tool calls with output and diffs; four sessions primed to stream 60 updates each. The long session's folder gets 10 edited files and one new one (as the Changes scenario does). Not covered: a second machine, real harness traffic, the desktop shell.</p>
<p><b>Timing.</b> Headless Chromium 134 via Playwright, 1440×900. Each interaction is timed from the real input's <code>timeStamp</code> (pointerdown/keydown) to the frame after the expected content is on screen ("shown"), and to 300 ms of DOM quiet ("settled"); keystrokes from keydown to the next frame; long tasks, long animation frames and Event Timing from PerformanceObservers; requests in the window. Clicks go to coordinates resolved beforehand, so the harness's own work never lands in a measurement.</p>
<p><b>Runs.</b> 5 runs × 2 rounds per build, builds alternating each round (another session was benchmarking on the same machine), at 1× and at 4× Chrome CPU throttling (a slower laptop; it magnifies costs that are a few ms here). Median and p95 of 10 samples. A CPU profile per scenario (source-mapped to <code>src/…:line</code>) and Chrome traces for typing.</p>`,
  rerun: `
<pre>cd tests/snappiness
bun install; bun x playwright install chromium     # once
bun run.ts --refs v0.60.0,HEAD --throttle 1,4      # builds both, seeds, measures, writes compare.md (~70 min)
bun run.ts --refs HEAD --only typing,switchLong --runs 3   # a quick look at this checkout</pre>
<p>The kit is on branch <code>snappiness/check</code> (<code>tests/snappiness</code>, README inside). It also has <code>attribute.ts</code> (profile → file:line), <code>trace-typing.ts</code> (where a keystroke's time goes), <code>soak.ts</code> (does a tab age) and <code>--css</code> what-if runs.</p>`,
  ci: `
<p><b>Proposal: a snappiness budget job on PRs that touch <code>client/</code>.</b> Build the SPA and API (already done for E2E), seed one scratch Fleet (~2 min), run five scenarios at 4× (typing with a diff open, switchLong, openSession, sendPrompt, streamingList), 5 runs each (~4 min). About <b>10 CI minutes per PR</b>. Budgets: fixed thresholds with generous headroom (runner CPUs vary ±30%), e.g. keystroke → paint p95 ≤ 120 ms, 0 long tasks while typing, switchLong median ≤ 4 s, streaming long tasks ≤ 400 ms per 6 s; plus counts that don't vary with CPU (API requests on session open ≤ 25, sticky elements in the Changes panel ≤ 200).</p>
<p><b>And nightly:</b> the full A/B against the last release at 1× and 4× (~70 min, off the PR path), posting <code>compare.md</code> as an artifact, so slow drifts show up within a day.</p>`,
  ask: `
<ol>
<li><b>Open and merge the fix PR.</b> taron has no GitHub API login (pushes go over SSH and <code>gh</code> isn't set up), so I couldn't open it. The branch <code>perf/changes-sticky-gutters</code> is rebased on main with its PR body in <code>mockups/changes-sticky-gutters/PR.md</code>: <code>gh pr create --head perf/changes-sticky-gutters --title "perf(changes): typing stays quick beside an open diff; line numbers stick only in a hunk scrolled sideways" --body-file mockups/changes-sticky-gutters/PR.md</code></li>
<li><b>CI budget:</b> per-PR job (~10 min), nightly A/B only (~70 min/night), or both?</li>
<li><b>Long conversations:</b> worth building the rendered-markdown cache (cause 3)? It's the largest remaining cost, though not a regression.</li></ol>`,
};

writeFileSync(out, JSON.stringify(findings, null, 2));
console.log(`wrote ${out}`);
