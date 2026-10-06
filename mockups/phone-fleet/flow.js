// The Fleet phone flow in Fleet's own look: inbox → ask → session → new session → notifications → pairing.
// Behaviour comes from kit.js (carried over from the native-feel round); the markup uses desktop Fleet's components.
import { setupEnv, setupViewport, setupPress, createNav, openSheet, sheetPages, largeTitle, pullToRefresh, swipeRow, toast, banner, autogrow, haptic, animateTo, h, prefs, isIOSWebKit, isStandalone, closeTop } from "./kit.js";

setupEnv();
const viewport = setupViewport();
setupPress();
const params = new URLSearchParams(location.search);
const docked = params.get("docked") || localStorage.getItem("fleet-mock-docked") || "compact";

// ── Icons: lucide, as desktop Fleet uses (2px stroke, round) ─────────
const I = (d, extra = "") => `<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" ${extra}>${d}</svg>`;
const ic = {
  plus: I('<path d="M5 12h14M12 5v14"/>'),
  bell: I('<path d="M10.27 21a2 2 0 0 0 3.46 0"/><path d="M3.26 15.33A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.67C19.41 13.96 18 12.5 18 8A6 6 0 0 0 6 8c0 4.5-1.41 5.96-2.74 7.33"/>'),
  inbox: I('<path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/>'),
  chat: I('<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>'),
  server: I('<rect width="20" height="8" x="2" y="2" rx="2"/><rect width="20" height="8" x="2" y="14" rx="2"/><path d="M6 6h.01M6 18h.01"/>'),
  monitor: I('<rect width="20" height="14" x="2" y="3" rx="2"/><path d="M8 21h8M12 17v4"/>'),
  back: I('<path d="m15 18-6-6 6-6"/>'),
  chevR: I('<path d="m9 18 6-6-6-6"/>'),
  chevD: I('<path d="m6 9 6 6 6-6"/>'),
  more: I('<circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/><circle cx="5" cy="12" r="1"/>'),
  up: I('<path d="m5 12 7-7 7 7M12 19V5"/>'),
  check: I('<path d="M20 6 9 17l-5-5"/>'),
  x: I('<path d="M18 6 6 18M6 6l12 12"/>'),
  archive: I('<rect width="20" height="5" x="2" y="3" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8M10 12h4"/>'),
  undo: I('<path d="M9 14 4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11"/>'),
  terminal: I('<path d="m7 11 2-2-2-2M11 13h4"/><rect width="18" height="18" x="3" y="3" rx="2"/>'),
  prompt: I('<path d="m4 17 6-6-6-6M12 19h8"/>'),
  diff: I('<path d="M12 3v14M5 10h14M5 21h14"/>'),
  file: I('<path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/>'),
  fileText: I('<path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4M10 9H8M16 13H8M16 17H8"/>'),
  side: I('<path d="M7.9 20A9 9 0 1 0 4 16.1L2 22Z"/>'),
  fork: I('<circle cx="12" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><circle cx="18" cy="6" r="3"/><path d="M18 9v2c0 .6-.4 1-1 1H7c-.6 0-1-.4-1-1V9M12 12v3"/>'),
  pencil: I('<path d="M21.17 6.81a1 1 0 0 0-3.99-3.99L3.84 16.17a2 2 0 0 0-.5.83l-1.32 4.35a.5.5 0 0 0 .62.62l4.35-1.32a2 2 0 0 0 .83-.5zM15 5l4 4"/>'),
  laptop: I('<path d="M18 5a2 2 0 0 1 2 2v8.53l1.24 2.48A1 1 0 0 1 20.35 19.5H3.65a1 1 0 0 1-.89-1.49L4 15.53V7a2 2 0 0 1 2-2zM20 15.5H4"/>'),
  image: I('<rect width="18" height="18" x="3" y="3" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-3.09-3.09a2 2 0 0 0-2.82 0L6 21"/>'),
  camera: I('<path d="M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3z"/><circle cx="12" cy="13" r="3"/>'),
  search: I('<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>'),
  share: I('<path d="M4 12v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8M16 6l-4-4-4 4M12 2v13"/>'),
  shield: I('<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="M12 8v4M12 16h.01"/>'),
  help: I('<circle cx="12" cy="12" r="10"/><path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3M12 17h.01"/>'),
  folder: I('<path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/>'),
  branch: I('<path d="M6 3v12"/><circle cx="18" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M18 9a9 9 0 0 1-9 9"/>'),
  cpu: I('<rect width="16" height="16" x="4" y="4" rx="2"/><rect width="6" height="6" x="9" y="9" rx="1"/><path d="M15 2v2M15 20v2M2 15h2M2 9h2M20 15h2M20 9h2M9 2v2M9 20v2"/>'),
  clip: I('<path d="m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l8.57-8.57A4 4 0 1 1 18 8.84l-8.59 8.57a2 2 0 0 1-2.83-2.83l8.49-8.48"/>'),
  pr: I('<circle cx="18" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><path d="M13 6h3a2 2 0 0 1 2 2v7M6 9v12"/>', 'stroke-width="2.2"'),
  task: I('<rect width="8" height="8" x="3" y="3" rx="2"/><path d="M7 11v4a2 2 0 0 0 2 2h4"/><rect width="8" height="8" x="13" y="13" rx="2"/>'),
  phone: I('<rect width="14" height="20" x="5" y="2" rx="2" ry="2"/><path d="M12 18h.01"/>'),
  book: I('<path d="M12 7v14M3 18a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h5a4 4 0 0 1 4 4 4 4 0 0 1 4-4h5a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1h-6a3 3 0 0 0-3 3 3 3 0 0 0-3-3z"/>'),
  ext: I('<path d="M15 3h6v6M10 14 21 3M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>'),
  spin: '<svg class="spinner" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="M21 12a9 9 0 1 1-6.22-8.56"/></svg>',
};
const g = (k) => k === "working" ? '<span class="glyph glyph--working" aria-label="Working"><i></i><i></i><i></i><i></i></span>' : `<span class="glyph glyph--${k}"></span>`;
const svgc = (svg, cls) => svg.replace("<svg ", `<svg class="${cls}" `);

// ── Mock data ────────────────────────────────────────────────────────
const now = Date.now();
const min = 60e3, hr = 60 * min, day = 24 * hr;
const CMD = "dotnet test tests/WeaveFleet.IntegrationTests --filter SignalREventContractTests";
const CWD = "~/src/weave-fleet";
// wrap a command only between words, never inside one
const cmdHtml = (c) => c.split(" ").map((w) => `<span class="nobr">${w}</span>`).join(" ");
const S = {
  h1: { title: "Fix flaky SignalR reconnect test", machine: "hangar", folder: "weave-fleet", branch: "fix/flaky-reconnect", state: "permission", at: now - 3 * min, since: now - 73e3,
    convo: [{ t: "user", text: "The reconnect test fails about 1 in 10 runs on CI. Find out why and fix it." },
      { t: "tools", rows: [{ k: "read", d: "tests/WeaveFleet.E2E/SignalRTransportTests.cs" }, { k: "read", d: "src/WeaveFleet.Api/Hubs/SessionHub.cs" }, { k: "grep", pat: "WaitForReconnect(" }] },
      { t: "agent", html: "<p>CI schedules the first reconnect retry at <b>2 s</b>, and the test only waits 2 s, so it loses the race about one run in ten.</p><p>I raised the harness wait to 5 s in <code>TestHarness.cs</code>.</p>" },
      { t: "tools", rows: [{ k: "edit", d: "tests/WeaveFleet.E2E/TestHarness.cs", add: 1, del: 1 }, { k: "task", who: "shuttle", d: "Look for other tests with the same race", run: true }, { k: "bash", d: CMD, wait: true }] }] },
  f1: { title: "Per-device tokens for machines", machine: "falcon", folder: "weave-fleet", branch: "feat/device-tokens", state: "question", at: now - 11 * min, since: now - 11 * min,
    q: "When a removed phone calls in, should it get a plain 401 or a page that says it was removed?",
    opts: [["Plain 401", "The phone just gets 401 Unauthorized"], ["Explain", "A page that says it was removed"]],
    convo: [{ t: "user", text: "Give each paired phone its own token so I can remove one without re-pairing the rest." },
      { t: "tools", rows: [{ k: "read", d: "src/WeaveFleet.Api/Auth/DeviceTokens.cs" }, { k: "edit", d: "src/WeaveFleet.Api/Auth/DeviceTokens.cs", add: 42, del: 9 }, { k: "edit", d: "src/WeaveFleet.Infrastructure/Migrations/049_DeviceTokens.cs", add: 31, del: 0 }], more: 3 },
      { t: "agent", html: "<p>Tokens are per device now, and removing one revokes only that device. One thing I need from you:</p>" }] },
  f2: { title: "Composer references refactor", machine: "falcon", folder: "weave-fleet", branch: "refactor/references", state: "working", at: now - 361e3, since: now - 361e3,
    convo: [{ t: "user", text: "Split the composer's @ references into their own module and keep the pills working." },
      { t: "agent", html: "<p>Moving the picker and pill rendering into <code>references/</code>. The pills read from the same store, so nothing changes for the composer.</p>" },
      { t: "tools", rows: [{ k: "read", d: "client/src/components/session/Composer.vue" }, { k: "edit", d: "client/src/references/pills.ts", add: 88, del: 0 }], more: 7 }] },
  h3: { title: "Migrate the harness cache", machine: "hangar", folder: "weave-fleet", branch: "chore/harness-cache", state: "working", at: now - (13 * day + 6 * hr + 12 * min), since: now - (13 * day + 6 * hr + 12 * min),
    convo: [{ t: "user", text: "Move the harness cache to the new table and drop the old one." },
      { t: "tools", rows: [{ k: "read", d: "src/WeaveFleet.Infrastructure/HarnessCache.cs" }, { k: "read", d: "src/WeaveFleet.Infrastructure/Migrations/047.cs" }, { k: "bash", d: "dotnet ef migrations add HarnessCacheTable", run: true }] }] },
  h2: { title: "Bump OpenCode to 1.14", machine: "hangar", folder: "weave-fleet", branch: "chore/opencode-1.14", state: "finished", at: now - 22 * min, pr: { n: 412, kind: "draft" },
    convo: [{ t: "user", text: "Bump OpenCode to 1.14 and fix whatever breaks." },
      { t: "tools", rows: [{ k: "edit", d: "client/package.json", add: 1, del: 1 }, { k: "bash", d: "bun run test", r: "2 failed" }, { k: "edit", d: "client/src/lib/opencode-events.ts", add: 3, del: 3 }] },
      { t: "agent", html: "<p>Bumped to 1.14.2. Two tests listened for the old event name; both pass now.</p>" },
      { t: "since", text: "Since you looked" },
      { t: "user", text: "Skip the E2E run, just open the PR" },
      { t: "tools", rows: [{ k: "bash", d: "gh pr create --draft --fill", r: "#412" }] },
      { t: "agent", html: "<p>Opened draft pull request <b>#412</b>. E2E passed on CI as well.</p>" }] },
  h4: { title: "Readable bad requests", machine: "hangar", folder: "weave-fleet", branch: "fix/readable-400", state: "finished", at: now - 5 * hr, pr: { n: 351, kind: "merged" }, convo: [{ t: "user", text: "Empty 400s should name the field." }] },
  f3: { title: "Pi model switch", machine: "falcon", folder: "weave-fleet", branch: "feat/pi-model", state: "finished", at: now - 30 * hr, convo: [{ t: "user", text: "Let Pi switch models mid-session." }] },
};
const archived = new Set();

export function dur(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  const hh = Math.floor(m / 60);
  if (hh < 24) return `${hh}h ${m % 60}m`;
  return `${Math.floor(hh / 24)}d ${hh % 24}h`;
}
// compact, as the desktop session list writes times
const short = (t) => { const m = Math.floor((Date.now() - t) / min); if (m < 1) return "now"; if (m < 60) return `${m}m`; const hh = Math.floor(m / 60); if (hh < 24) return `${hh}h`; const d = Math.floor(hh / 24); return d < 7 ? `${d}d` : `${Math.floor(d / 7)}w`; };
const ago = (t) => { const m = Math.floor((Date.now() - t) / min); if (m < 1) return "just now"; if (m < 60) return `${m} min ago`; const hh = Math.floor(m / 60); if (hh < 24) return `${hh} h ago`; return hh < 48 ? "yesterday" : `${Math.floor(hh / 24)} days ago`; };
const isAsk = (s) => s.state === "permission" || s.state === "question";
const glyphOf = (s) => isAsk(s) ? "waiting" : s.state === "working" || s.state === "starting" ? "working" : s.state === "failed" ? "error" : "quiet";
const needsCount = () => Object.keys(S).filter((k) => !archived.has(k) && isAsk(S[k])).length;
const prChip = (pr) => pr ? `<span class="pr pr--${pr.kind}">${ic.pr}#${pr.n}</span>` : "";

// ── Root: bar, panel with three pages, the rail along the bottom ─────
const stage = document.getElementById("stage");
const rootScreen = h(`<section class="screen" id="root"></section>`);
stage.appendChild(rootScreen);
const nav = createNav(stage, rootScreen);

const tabsDef = [["inbox", "Needs you", ic.inbox], ["sessions", "Sessions", ic.chat], ["machines", "Machines", ic.server]];
const heads = {
  inbox: `<h1>Needs you</h1><p data-online><span class="mdot"></span>hangar<span class="mdot" style="margin-left:6px"></span>falcon<span>· online</span></p>`,
  sessions: `<h1>Sessions</h1><p>7 sessions on 2 machines</p>`,
  machines: `<h1>Machines</h1><p>2 online · this phone is paired with hangar</p>`,
};
rootScreen.innerHTML = `
  <header class="bar">
    <img class="bar__logo" src="icons/weave-logo.png" alt="Fleet" style="object-fit:contain">
    <span class="bar__title" data-bar-title>Needs you</span>
    <span class="bar__spacer"></span>
    <button class="icon-btn" data-act="notify" aria-label="Notifications">${ic.bell}</button>
    <button class="btn btn--outline btn--sm" data-act="new" style="margin-left:2px">${ic.plus}New session</button>
  </header>
  <div class="panel">
    ${tabsDef.map(([id]) => `
      <div class="tabpage" data-tabpage="${id}" ${id === "inbox" ? "" : "hidden"}>
        <div class="ptr"></div>
        <div class="scroller"><div class="scroller__inner">
          <div class="page-head">${heads[id]}</div>
          <div class="tab-content"></div>
        </div></div>
      </div>`).join("")}
  </div>
  <nav class="tabbar">
    ${tabsDef.map(([id, name, icon], i) => `<button class="tab ${i === 0 ? "is-active" : ""}" data-tab="${id}"><span class="tab__ic">${icon}${id === "inbox" ? `<span class="count" data-badge></span>` : ""}</span><span>${name}</span></button>`).join("")}
  </nav>`;

const bar = rootScreen.querySelector(".bar");
const tabPage = (id) => rootScreen.querySelector(`[data-tabpage="${id}"]`);
for (const [id] of tabsDef) largeTitle(tabPage(id).querySelector(".scroller"), { classList: { toggle: (c, on) => { if (id === currentTab) bar.classList.toggle(c, on); } } }, tabPage(id).querySelector(".page-head"));

// ── Inbox ────────────────────────────────────────────────────────────
const askHead = (id, right) => {
  const s = S[id];
  const perm = s.state === "permission";
  return `<div class="pcard__head">${svgc(perm ? ic.shield : ic.help, "pcard__icon")}<span class="pcard__title">${perm ? "Run a command" : "Question"}</span>${right}</div>`;
};
const askCard = (id) => {
  const s = S[id];
  const head = askHead(id, `<span class="pcard__meta"><span class="from">${s.machine}</span>${short(s.at)}</span>`);
  const open = `<button class="pcard__open" data-act="open" data-id="${id}"><div class="pcard__session"><span>${s.title}</span>${ic.chevR}</div></button>`;
  if (s.state === "permission")
    return `<article class="pcard" data-ask="${id}">${head}${open}<div class="cmd cmd--one"><span class="cmd__p">$ </span>${CMD}</div>
      <div class="btns"><button class="btn btn--primary" data-act="allow" data-id="${id}">Allow once</button><button class="btn btn--outline" data-act="ask-more" data-id="${id}">More…</button></div></article>`;
  return `<article class="pcard" data-ask="${id}">${head}${open}<p class="pcard__q">${s.q}</p>
      <div class="btns btns--q">${s.opts.map(([o]) => `<button class="btn btn--outline" data-act="answer" data-id="${id}" data-answer="${o}">${o}</button>`).join("")}<button class="btn btn--outline" data-act="ask-more" data-id="${id}">More…</button></div></article>`;
};
const rowMeta = (s) => {
  if (isAsk(s)) return `<span class="srow__m srow__m--waiting">Needs you</span>`;
  if (s.state === "working" || s.state === "starting") return `<span class="srow__m" data-tick="${s.id}">${dur(Date.now() - s.since)}</span>`;
  return `<span class="srow__m">${prChip(s.pr)}${short(s.at)}</span>`;
};
const sessionRow = (id, where = "machine") => {
  const s = S[id]; s.id = id;
  const quiet = s.state === "finished";
  return `<div class="row-wrap" data-row="${id}"><div class="swipe-actions"><button class="swipe-act" aria-label="Archive">${ic.archive}<span>Archive</span></button></div>
    <button class="srow ${quiet ? "srow--quiet" : ""}" data-act="open" data-id="${id}"><span class="srow__g">${g(glyphOf(s))}</span><div class="srow__main"><div class="srow__t">${s.title}</div><div class="srow__s">${where === "folder" ? s.folder : s.machine} · ${s.state === "starting" ? "starting…" : s.branch}</div></div>${rowMeta(s)}</button></div>`;
};
const live = (st) => Object.keys(S).filter((k) => !archived.has(k) && st.includes(S[k].state));
const skeleton = () => `
  <div class="asks">${[0, 1].map(() => `<div class="pcard" style="background:var(--panel);border-color:var(--border)"><span class="sk" style="width:42%"></span><div style="margin:14px 0 4px"><span class="sk" style="width:76%;height:1em"></span></div><div class="cmd" style="border-color:transparent;background:var(--tint-3)"><span class="sk" style="width:88%"></span></div><div class="btns"><span class="sk" style="height:44px;border-radius:10px"></span><span class="sk" style="height:44px;border-radius:10px"></span></div></div>`).join("")}</div>
  <div class="section-h"><span class="sk" style="width:76px"></span></div>
  <div class="rows">${[0, 1].map(() => `<div class="srow"><span class="srow__g"><span class="sk" style="width:10px;height:10px;border-radius:3px"></span></span><div class="srow__main"><span class="sk" style="width:68%"></span><div style="margin-top:8px"><span class="sk" style="width:44%;height:.75em"></span></div></div></div>`).join("")}</div>`;

function renderInbox() {
  const c = tabPage("inbox").querySelector(".tab-content");
  const asks = live(["permission", "question"]);
  const working = live(["working", "starting"]);
  const finished = live(["finished"]).sort((a, b) => S[b].at - S[a].at).slice(0, 3);
  c.innerHTML = `
    ${asks.length ? `<div class="asks">${asks.map(askCard).join("")}</div>` : `<div class="empty fade-in">${svgc(ic.check, "")}<span>Nothing needs you right now.</span></div>`}
    ${working.length ? `<h2 class="section-h">Working <small>${working.length}</small></h2><div class="rows">${working.map((id) => sessionRow(id)).join("")}</div>` : ""}
    ${finished.length ? `<h2 class="section-h">Finished <small>${finished.length}</small><button class="section-h__act pressable" data-act="tab" data-tab="sessions">All sessions</button></h2><div class="rows">${finished.map((id) => sessionRow(id)).join("")}</div>` : ""}`;
  c.querySelector(".empty svg")?.setAttribute("style", "width:16px;height:16px;color:var(--running)");
  wireRows(c);
  updateBadge();
}
const folded = new Set();
function renderSessions() {
  const c = tabPage("sessions").querySelector(".tab-content");
  const by = (m) => Object.keys(S).filter((k) => !archived.has(k) && S[k].machine === m).sort((a, b) => (isAsk(S[b]) - isAsk(S[a])) || S[b].at - S[a].at);
  c.innerHTML = `
    <div style="padding:6px 12px 2px"><label class="field">${ic.search}<input type="search" placeholder="Filter sessions" data-search enterkeyhint="search"></label></div>
    ${["hangar", "falcon"].map((m) => `<button class="mhead pressable ${folded.has(m) ? "is-folded" : ""}" data-act="fold" data-m="${m}">${svgc(ic.chevD, "mhead__chev")}<span class="mdot"></span>${m}${m === "hangar" ? '<span class="tag tag--live" style="margin-left:4px">live</span>' : ""}<small>${by(m).length}</small></button>
      <div class="rows ${folded.has(m) ? "is-folded" : ""}">${by(m).map((id) => sessionRow(id, "folder")).join("")}</div>`).join("")}
    <p class="foot" style="margin-top:14px;padding:0 0 0 0">Swipe a session left to archive it.</p>
    <div class="card" style="margin-top:18px"><button class="set set--accent" data-act="toast" data-text="Opens the full Fleet in the browser"><div class="set__main"><div class="set__t">Open the full Fleet</div></div>${svgc(ic.ext, "set__chev")}</button></div>`;
  wireRows(c);
  c.querySelector("[data-search]").addEventListener("input", (e) => {
    const q = e.target.value.toLowerCase();
    c.querySelectorAll("[data-row]").forEach((r) => (r.hidden = !S[r.dataset.row].title.toLowerCase().includes(q)));
  });
}
function renderMachines() {
  const c = tabPage("machines").querySelector(".tab-content");
  const m = (name, os, n, icon, live) => `<button class="set" data-act="toast" data-text="Shows ${name}'s sessions"><span class="mdot"></span><div class="set__main"><div class="set__t" style="display:flex;align-items:center;gap:6px">${name}<span class="tag">${os}</span>${live ? '<span class="tag tag--live">live</span>' : ""}</div><div class="set__s">${n} sessions · ${live ? "this phone's home" : "reached through hangar"}</div></div>${svgc(ic.chevR, "set__chev")}</button>`;
  c.innerHTML = `
    <div class="card" style="margin-top:8px">${m("hangar", "linux", 4, ic.monitor, true)}${m("falcon", "macos", 3, ic.laptop, false)}</div>
    <p class="foot">Add machines from Fleet on a computer, in Settings&nbsp;→&nbsp;Machines.</p>
    <div class="label">This phone</div>
    <div class="card">
      <button class="set" data-act="notify">${svgc(ic.bell, "set__ic")}<div class="set__main"><div class="set__t">Notifications</div></div><span class="set__v" data-notif-value>Off</span>${svgc(ic.chevR, "set__chev")}</button>
      <button class="set" data-act="pair">${svgc(ic.phone, "set__ic")}<div class="set__main"><div class="set__t">Pair with another machine</div></div>${svgc(ic.chevR, "set__chev")}</button>
      <a class="set" href="index.html" style="text-decoration:none;color:inherit">${svgc(ic.book, "set__ic")}<div class="set__main"><div class="set__t">About these mockups</div></div>${svgc(ic.chevR, "set__chev")}</a>
    </div>`;
}
function wireRows(c) {
  c.querySelectorAll(".row-wrap").forEach((w) => swipeRow(w, {
    onCommit: (wrap) => {
      const id = wrap.dataset.row;
      archived.add(id);
      haptic("success");
      rerenderLists();
      toast(`Archived “${S[id].title}”`, { label: "Undo", icon: ic.undo.replace("<svg ", '<svg width="14" height="14" '), onTap: () => { archived.delete(id); rerenderLists(); } });
    },
  }));
}
function rerenderLists() { renderInbox(); renderSessions(); }
function updateBadge() {
  const n = needsCount();
  const b = rootScreen.querySelector("[data-badge]");
  b.textContent = n; b.hidden = n === 0;
  document.title = n ? `(${n}) Fleet` : "Fleet";
}

const inboxContent = tabPage("inbox").querySelector(".tab-content");
if (params.get("skeleton") === "only") { inboxContent.innerHTML = skeleton(); }
else if (params.get("skeleton") !== "0" && !location.hash.slice(1)) {
  inboxContent.innerHTML = skeleton();
  setTimeout(() => { renderInbox(); inboxContent.classList.add("fade-in"); }, 900);
} else renderInbox();
renderSessions();
renderMachines();
updateBadge();

pullToRefresh(tabPage("inbox").querySelector(".scroller"), tabPage("inbox").querySelector(".ptr"), async () => {
  await new Promise((r) => setTimeout(r, 1100));
  S.h1.at = Date.now() - 20e3;
  renderInbox();
  tabPage("inbox").querySelector("[data-online]").innerHTML = `<span class="mdot"></span>hangar<span class="mdot" style="margin-left:6px"></span>falcon<span>· updated just now</span>`;
});

// Tabs: instant switch; tapping the current tab scrolls to the top
let currentTab = "inbox";
function switchTab(id, user = false) {
  const scroller = tabPage(id).querySelector(".scroller");
  if (id === currentTab) { scroller.scrollTo({ top: 0, behavior: "smooth" }); return; }
  if (user) haptic("light");
  tabPage(currentTab).hidden = true;
  tabPage(id).hidden = false;
  rootScreen.querySelectorAll(".tab").forEach((t) => t.classList.toggle("is-active", t.dataset.tab === id));
  currentTab = id;
  rootScreen.querySelector("[data-bar-title]").textContent = tabsDef.find((t) => t[0] === id)[1];
  bar.classList.toggle("is-scrolled", scroller.scrollTop > tabPage(id).querySelector(".page-head").offsetHeight - 20);
}
rootScreen.querySelectorAll(".tab").forEach((t) => t.addEventListener("click", () => switchTab(t.dataset.tab, true)));

// ── Session screen ───────────────────────────────────────────────────
const toolDef = { read: [ic.fileText, "Read"], grep: [ic.search, "Grep"], edit: [ic.pencil, "Edit"], bash: [ic.prompt, "Bash"], task: [ic.task, ""] };
const toolRow = (r, i) => {
  const [icon, label] = toolDef[r.k];
  let right;
  if (r.run) right = r.k === "task" ? g("working") : g("running");
  else if (r.wait) right = `<span style="color:var(--waiting);font-family:var(--font);font-weight:600">Needs you</span>`;
  else if (r.add !== undefined) right = `<span class="add">+${r.add}</span><span class="del">−${r.del}</span>`;
  else if (r.r) right = `<span style="color:${/fail/.test(r.r) ? "var(--error)" : "var(--muted)"}">${r.r}</span>`;
  else right = svgc(ic.check, "tool__ok");
  const detail = r.pat ? `<span class="tool__d"><span class="tool__pat">${r.pat}</span></span>` : `<span class="tool__d">${r.d}</span>`;
  return `<button class="tool ${r.who ? "tool--who" : ""}" data-act="step" data-i="${i}">${svgc(icon, "tool__ic")}<span class="tool__l">${r.who ?? label}</span>${detail}<span class="tool__r">${right}</span></button>`;
};
const convoItem = (it) => {
  switch (it.t) {
    case "user": return `<div class="umsg">${it.text}</div>`;
    case "agent": return `<div class="amsg">${it.html}</div>`;
    case "tools": return `<div class="tools">${it.rows.map(toolRow).join("")}${it.more ? `<button class="tool tool--more" data-act="steps">${svgc(ic.chevD, "tool__ic")}<span class="tool__l">${it.more} more steps</span></button>` : ""}</div>`;
    case "since": return `<div class="since">${it.text}</div>`;
  }
};
const statusLine = (id) => {
  const s = S[id];
  if (isAsk(s)) return `${g("waiting")}<span class="is-waiting">Needs you</span><span>· ${s.machine} · ${s.folder}</span>`;
  if (s.state === "working") return `${g("working")}<span>Working · <span data-tick="${id}">${dur(Date.now() - s.since)}</span> · ${s.machine}</span>`;
  if (s.state === "starting") return `${g("working")}<span>Starting on ${s.machine}…</span>`;
  return `${g("quiet")}<span>Finished ${ago(s.at)} · ${s.machine}</span>`;
};
function dockedAsk(id) {
  const s = S[id];
  const later = `<button class="later pressable" data-act="later">Later</button>`;
  if (s.state === "permission") {
    const full = docked === "full";
    return `<div class="pcard docked">${askHead(id, later)}
      <div class="cmd ${full ? "" : "clamp3"}">${full ? `<span class="cmd__cwd">${CWD}</span>` : ""}<span class="cmd__p">$ </span>${cmdHtml(CMD)}</div>
      ${full ? `<div class="choices" style="margin-top:10px">${permChoices(id, true)}</div>` : `<div class="btns"><button class="btn btn--primary" data-act="allow" data-id="${id}">Allow once</button><button class="btn btn--outline" data-act="ask-more" data-id="${id}">More…</button></div>`}</div>`;
  }
  if (s.state === "question")
    return `<div class="pcard docked">${askHead(id, later)}<p class="pcard__q" style="margin-top:8px">${s.q}</p>
      <div class="btns btns--q">${s.opts.map(([o]) => `<button class="btn btn--outline" data-act="answer" data-id="${id}" data-answer="${o}">${o}</button>`).join("")}<button class="btn btn--outline" data-act="ask-more" data-id="${id}">More…</button></div></div>`;
  return "";
}
const screens = {};
function sessionScreen(id) {
  const s = S[id];
  const el = h(`<section class="screen screen--full screen--session" data-session="${id}">
    <div class="panel">
      <header class="shead">
        <button class="icon-btn icon-btn--text" data-act="back" aria-label="Back">${ic.back}</button>
        <div class="shead__main"><div class="shead__t">${s.title}</div><div class="shead__s" data-status>${statusLine(id)}</div></div>
        <button class="icon-btn" data-act="menu" data-id="${id}" aria-label="Session menu">${ic.more}</button>
      </header>
      <div class="scroller"><div class="scroller__inner convo">
        ${s.convo.map(convoItem).join("")}
        <div data-tail>${tail(id)}</div>
      </div></div>
      <div class="dock">
        <div data-docked>${dockedAsk(id)}</div>
        <div class="frame"><textarea rows="1" placeholder="Message ${s.machine}…" aria-label="Message"></textarea>
          <div class="frame__bar"><button class="icon-btn" data-act="plus" aria-label="Add">${ic.plus}</button>
            <button class="sel" data-act="toast" data-text="Picks the agent">Loom${ic.chevD}</button><button class="sel" data-act="toast" data-text="Picks the model">sonnet-5${ic.chevD}</button>
            <button class="send" data-act="send" data-id="${id}" disabled aria-label="Send">${ic.up}</button></div></div>
      </div>
    </div>
  </section>`);
  const sc = el.querySelector(".scroller"), dock = el.querySelector(".dock");
  new ResizeObserver(() => {
    const atBottom = sc.scrollHeight - sc.scrollTop - sc.clientHeight < 40;
    sc.style.paddingBottom = dock.offsetHeight + 12 + "px";
    if (atBottom) sc.scrollTop = sc.scrollHeight;
  }).observe(dock);
  const ta = el.querySelector("textarea"), send = el.querySelector(".send");
  autogrow(ta, (v) => (send.disabled = !v.trim()));
  ta.addEventListener("focus", () => setTimeout(() => (sc.scrollTop = sc.scrollHeight), 300));
  screens[id] = el;
  return el;
}
const tail = (id) => {
  const s = S[id];
  if (s.state === "working" || s.state === "starting") return `<div class="working">${g("working")}<span class="working__word">${s.state === "starting" ? `Starting on ${s.machine}` : "Working"}</span>${s.state === "starting" ? "" : `<span class="working__t">· <span data-tick="${id}">${dur(Date.now() - s.since)}</span></span>`}</div>`;
  if (isAsk(s)) return `<div class="working working--waiting">${g("waiting")}Waiting for you</div>`;
  return "";
};
function refreshSession(id) {
  const el = screens[id];
  if (!el?.isConnected) return;
  el.querySelector("[data-status]").innerHTML = statusLine(id);
  el.querySelector("[data-tail]").innerHTML = tail(id);
}
async function openSession(id, { animate = true } = {}) {
  const el = sessionScreen(id);
  await nav.push(el, { animate });
  el.querySelector(".scroller").scrollTop = 1e6;
}
function appendConvo(id, item) {
  S[id].convo.push(item);
  const el = screens[id];
  if (!el?.isConnected) return;
  const node = h(convoItem(item));
  node.classList.add("fade-in");
  el.querySelector("[data-tail]").before(node);
  const sc = el.querySelector(".scroller");
  sc.scrollTo({ top: sc.scrollHeight, behavior: "smooth" });
  return node;
}

// Allow once: the button flips at once (optimistic); the agent carries on
async function allow(id, btn) {
  haptic("success");
  if (btn) { btn.classList.add("is-done"); btn.innerHTML = btn.classList.contains("choice") ? btn.innerHTML : `${ic.check.replace("<svg ", '<svg width="18" height="18" ')}Allowed`; }
  const card = rootScreen.querySelector(`[data-ask="${id}"]`);
  const dockedEl = screens[id]?.querySelector(".docked");
  S[id].state = "working"; S[id].since = Date.now();
  const wait = S[id].convo.flatMap((c) => c.rows ?? []).find((r) => r.wait);
  if (wait) { delete wait.wait; wait.run = true; }
  screens[id]?.querySelectorAll(".tool").forEach((t) => { if (t.textContent.includes("Needs you")) t.querySelector(".tool__r").innerHTML = g("running"); });
  refreshSession(id);
  updateBadge();
  if (dockedEl) { await new Promise((r) => setTimeout(r, btn?.closest(".docked") ? 380 : 0)); dockedEl.classList.add("is-gone"); }
  if (card) { await new Promise((r) => setTimeout(r, 450)); await collapse(card); }
  renderInbox(); renderSessions();
  setTimeout(() => {
    if (wait) { delete wait.run; wait.r = "40 passed"; }
    screens[id]?.querySelectorAll(".tool").forEach((t) => { if (t.querySelector(".glyph--running")) t.querySelector(".tool__r").innerHTML = `<span style="color:var(--muted)">40 passed</span>`; });
    appendConvo(id, { t: "agent", html: "<p>All 40 contract tests pass, five runs in a row. Want me to open a pull request?</p>" });
    S[id].state = "finished"; S[id].at = Date.now();
    refreshSession(id); renderInbox(); renderSessions();
  }, 2600);
}
async function answer(id, text, btn) {
  haptic("success");
  if (btn && !btn.classList.contains("choice")) { btn.classList.add("is-done"); btn.innerHTML = `${ic.check.replace("<svg ", '<svg width="18" height="18" ')}${text}`; }
  const card = rootScreen.querySelector(`[data-ask="${id}"]`);
  S[id].state = "working"; S[id].since = Date.now();
  setTimeout(() => screens[id]?.querySelector(".docked")?.classList.add("is-gone"), 300);
  appendConvo(id, { t: "user", text });
  refreshSession(id); updateBadge();
  if (card) { await new Promise((r) => setTimeout(r, 450)); await collapse(card); }
  renderInbox(); renderSessions();
  setTimeout(() => { appendConvo(id, { t: "agent", html: text === "Explain" ? "<p>A removed phone now gets a short page that says it was removed and how to pair it again.</p>" : "<p>A removed phone now gets a plain 401.</p>" }); S[id].state = "finished"; S[id].at = Date.now(); refreshSession(id); renderInbox(); renderSessions(); }, 2200);
}
async function collapse(el) {
  el.style.overflow = "hidden";
  el.style.height = el.offsetHeight + "px";
  await animateTo(el, { height: "0px", opacity: "0", marginBottom: "-10px", paddingTop: "0px", paddingBottom: "0px", borderWidth: "0px" }, 320);
}
function sendMessage(id) {
  const el = screens[id];
  const ta = el.querySelector(".dock textarea");
  const text = ta.value.trim();
  if (!text) return;
  haptic("light");
  ta.value = ""; ta.dispatchEvent(new Event("input"));
  const b = appendConvo(id, { t: "user", text });
  b?.classList.add("is-sending");
  setTimeout(() => b?.classList.remove("is-sending"), 350);
  S[id].state = "working"; S[id].since = Date.now();
  el.querySelector(".docked")?.classList.add("is-gone");
  refreshSession(id); renderInbox(); renderSessions();
  setTimeout(() => {
    appendConvo(id, { t: "agent", html: "<p>On it. I'll keep the change small and tell you when the tests pass.</p>" });
    S[id].state = "finished"; S[id].at = Date.now(); refreshSession(id); renderInbox(); renderSessions();
  }, 2000);
}

viewport.onResize(() => {
  const a = document.activeElement;
  if (a?.closest?.(".sheet__body")) setTimeout(() => (a.closest(".frame") || a).scrollIntoView({ block: "nearest" }), 80);
});
setInterval(() => document.querySelectorAll("[data-tick]").forEach((n) => { const s = S[n.dataset.tick]; if (s) n.textContent = dur(Date.now() - s.since); }), 1000);

// ── Sheets ───────────────────────────────────────────────────────────
const closeBtn = `<button class="icon-btn" data-act="sheet-close" aria-label="Close">${ic.x}</button>`;
let activeSheet = null;
function sheet(html, opts = {}) {
  const el = h(`<div>${html}</div>`);
  const api = openSheet({ el, viewport, ...opts, onClose: () => { if (activeSheet === api) activeSheet = null; opts.onClose?.(); } });
  activeSheet = api;
  return api;
}
const sheetHead = (title, sub, right = closeBtn) => `<div class="sheet__head"><h2>${title}${sub ? `<span class="sub">${sub}</span>` : ""}</h2>${right}</div>`;
const permChoices = (id, inDock = false) => `
  <button class="choice choice--primary" data-act="allow" data-id="${id}" ${inDock ? "" : "data-close"}><span class="choice__n">1</span><span class="choice__main"><span class="choice__t">Allow once</span></span></button>
  <button class="choice" data-act="allow-always" data-id="${id}"><span class="choice__n">2</span><span class="choice__main"><span class="choice__t">Don't ask again for <span class="inline-code">dotnet test *</span></span></span><span class="choice__note">this session</span></button>
  <button class="choice" data-act="${inDock ? "ask-more-deny" : "deny-open"}" data-id="${id}"><span class="choice__n">3</span><span class="choice__main"><span class="choice__t">Deny, and tell the agent what to do instead…</span></span></button>`;

function askSheet(id, { deny = false } = {}) {
  const s = S[id];
  if (s.state === "question") return questionSheet(id);
  const sh = sheet(`
    ${sheetHead("Run a command", `${s.machine} · ${s.title}`)}
    <div class="sheet__body"><div class="sheet__pad">
      <div class="cmd" style="margin-top:0"><span class="cmd__cwd">${CWD}</span><span class="cmd__p">$ </span>${cmdHtml(CMD)}</div>
      <div class="choices" style="margin-top:12px">${permChoices(id)}</div>
      <div class="deny-box" hidden style="margin-top:10px"><div class="frame"><textarea rows="2" placeholder="Run only the reconnect test" aria-label="What to do instead"></textarea>
        <div class="frame__bar"><span class="sel sel--note">Tells the agent, then denies</span><button class="send" data-act="deny-send" data-id="${id}" disabled aria-label="Send">${ic.up}</button></div></div></div>
      <div class="choices-hint"><span>The agent waits until you answer.</span><button class="pressable" data-act="toast" data-text="Opens Settings → Permissions on your computer" style="color:var(--accent);font-weight:500">Permission settings</button></div>
    </div>
    <button class="link" data-act="open" data-id="${id}" data-close style="margin-top:10px">Open the session</button>
    </div>`, { detents: ["medium", "large"], initial: "medium" });
  const ta = sh.el.querySelector(".deny-box textarea");
  autogrow(ta, (v) => (sh.el.querySelector('[data-act="deny-send"]').disabled = !v.trim()));
  if (deny) setTimeout(() => sh.el.querySelector('[data-act="deny-open"]').click(), 500);
  return sh;
}
function questionSheet(id) {
  const s = S[id];
  const sh = sheet(`
    ${sheetHead("Question", `${s.machine} · ${s.title}`)}
    <div class="sheet__body"><div class="sheet__pad">
      <p style="font-size:var(--t-body);line-height:1.5;margin:0 0 14px">${s.q}</p>
      <div class="choices">${s.opts.map(([o, d], i) => `<button class="choice" data-act="answer" data-id="${id}" data-answer="${o}" data-close><span class="choice__n">${i + 1}</span><span class="choice__main"><span class="choice__t">${o}</span><span class="choice__s" style="display:block">${d}</span></span></button>`).join("")}
        <div class="frame" style="margin-top:2px"><textarea rows="1" placeholder="Something else, in your own words…" aria-label="Your answer"></textarea>
          <div class="frame__bar"><button class="send" data-act="answer-own" data-id="${id}" disabled aria-label="Send">${ic.up}</button></div></div></div>
    </div>
    <button class="link" data-act="open" data-id="${id}" data-close style="margin-top:12px">Open the session</button>
    </div>`, { detents: ["medium", "large"], initial: "medium" });
  const ta = sh.el.querySelector("textarea");
  autogrow(ta, (v) => (sh.el.querySelector('[data-act="answer-own"]').disabled = !v.trim()));
  ta.addEventListener("focus", () => sh.expand());
}
const mi = (act, icon, label, hint = "", cls = "", id = "") => `<button class="mi ${cls}" data-act="${act}" data-id="${id}">${icon}<span>${label}</span>${hint ? `<span class="mi__hint">${hint}</span>` : ""}</button>`;
function menuSheet(id) {
  sheet(`<div class="menu">
      ${mi("changes", ic.diff, "Changes", "3 files", "", id)}${mi("toast", ic.file, "Files", "", "", id)}${mi("toast", ic.side, "Side question", "/btw", "", id)}${mi("terminal", ic.prompt, "Run a command", "!", "", id)}
      <hr>${mi("toast", ic.fork, "Fork from here", "", "", id)}${mi("toast", ic.pencil, "Rename", "", "", id)}${mi("toast", ic.laptop, "Open on my computer", "", "", id)}
      <hr>${mi("archive", ic.archive, "Archive", "", "", id)}
    </div>`, { floating: true });
}
function plusSheet(id) {
  sheet(`<div class="menu">
      ${mi("toast", ic.image, "Photo or screenshot")}${mi("toast", ic.camera, "Camera")}${mi("toast", ic.clip, "File from the folder", "@")}
      <hr>${mi("terminal", ic.prompt, "Run a command", "!", "", id)}${mi("toast", ic.side, "Side question", "/btw")}
    </div>`, { floating: true });
}
function terminalSheet(id) {
  const s = S[id];
  const sh = sheet(`
    ${sheetHead("Run a command", `in ${CWD} on ${s.machine}`)}
    <div class="sheet__body" style="padding-bottom:16px"><div class="sheet__pad">
      <div class="frame frame--term"><div class="frame__top"><span class="term__bang">!</span><textarea rows="1" placeholder="git log --oneline -3" aria-label="Command" autocapitalize="off" autocorrect="off" spellcheck="false" enterkeyhint="go"></textarea></div>
        <div class="frame__bar"><span class="sel sel--note">Runs without a model turn</span><button class="send" data-act="term-send" data-id="${id}" disabled aria-label="Run">${ic.up}</button></div></div>
    </div>
    <div class="card" style="margin-top:14px"><button class="set" data-act="toast" data-text="Sent a link to ${s.machine}">${svgc(ic.terminal, "set__ic")}<div class="set__main"><div class="set__t">Open a terminal on ${s.machine}</div><div class="set__s">Sends a link to the computer</div></div>${svgc(ic.chevR, "set__chev")}</button></div>
    </div>`);
  const ta = sh.el.querySelector("textarea");
  autogrow(ta, (v) => (sh.el.querySelector(".send").disabled = !v.trim()));
  ta.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); sh.el.querySelector(".send").click(); } });
  setTimeout(() => ta.focus({ preventScroll: true }), 0);
}
function stepSheet(el) {
  const tool = el.closest(".tool");
  const label = tool.querySelector(".tool__l").textContent, d = tool.querySelector(".tool__d").textContent;
  const isEdit = label === "Edit";
  const body = isEdit
    ? `<div class="cmd" style="padding:0;overflow:hidden;font-size:12.5px"><div style="padding:8px 12px;color:var(--muted);font-size:12px;border-bottom:1px solid var(--border)">${d.split("/").pop()} <span class="add">+1</span> <span class="del">−1</span></div>
        <div style="padding:6px 0;white-space:pre;overflow-x:auto"><span style="display:block;padding:0 12px;color:var(--muted)">@@ -41,7 +41,7 @@</span><span style="display:block;padding:0 12px">    var hub = await StartHubAsync();</span><span style="display:block;padding:0 12px;background:color-mix(in srgb,var(--error) 10%,transparent)">-   var wait = TimeSpan.FromSeconds(2);</span><span style="display:block;padding:0 12px;background:color-mix(in srgb,var(--running) 10%,transparent)">+   var wait = TimeSpan.FromSeconds(5);</span><span style="display:block;padding:0 12px">    await hub.WaitForReconnect(wait);</span></div></div>`
    : `<div class="cmd" style="white-space:pre-wrap;word-break:break-word;font-size:12.5px;line-height:1.6"><span class="cmd__p">${label === "Bash" ? "$ " : ""}</span>${d}\n<span style="color:var(--muted)">${label === "Read" ? "└ 214 lines" : label === "Grep" ? "└ 6 matches in 3 files" : label === "Bash" ? "└ Passed!  40 tests, 0 failed (12.4 s)" : "└ running on shuttle"}</span></div>`;
  sheet(`${sheetHead(label || "Delegated", label ? d.split("/").pop() : d)}<div class="sheet__body"><div class="sheet__pad">${body}</div></div>`, { detents: ["medium", "large"], initial: "medium" });
}
function stepsSheet() {
  const rows = [{ k: "read", d: "src/WeaveFleet.Api/Auth/PairingEndpoints.cs" }, { k: "grep", pat: "DeviceToken" }, { k: "read", d: "client/src/lib/device-credentials.ts" }];
  sheet(`${sheetHead("3 more steps", "")}<div class="sheet__body"><div class="sheet__pad"><div class="tools">${rows.map(toolRow).join("")}</div></div></div>`, { detents: ["medium", "large"], initial: "medium" });
}
function changesSheet() {
  const files = [["tests/WeaveFleet.E2E/TestHarness.cs", 1, 1], ["tests/WeaveFleet.E2E/SignalRTransportTests.cs", 4, 2], ["docs/testing.md", 3, 0]];
  sheet(`${sheetHead("Changes", "3 files in this session's worktree")}
    <div class="sheet__body"><div class="card">${files.map(([f, a, d]) => `<button class="set" data-act="toast" data-text="Opens the diff">${svgc(ic.file, "set__ic")}<div class="set__main"><div class="set__t">${f.split("/").pop()}</div><div class="set__s mono" style="font-size:12px">${f.split("/").slice(0, -1).join("/")}</div></div><span class="mono" style="font-size:12.5px"><span class="add">+${a}</span> <span class="del">−${d}</span></span></button>`).join("")}</div></div>`,
  { detents: ["medium", "large"], initial: "medium" });
}

// ── New session: the desktop's new-session composer, chips under the box ──
const NS = { machine: "hangar", folder: "weave-fleet", where: "New worktree", harness: "OpenCode 2", agent: "Loom", model: "sonnet-5", loading: true };
const pickers = {
  machine: ["Machine", ic.monitor, [["hangar", "Linux · this phone's home"], ["falcon", "macOS · through hangar"]]],
  folder: ["Folder", ic.folder, [["weave-fleet", "~/src/weave-fleet"], ["weave", "~/src/weave"], ["tryweave.io", "~/src/tryweave.io"], ["dotfiles", "~/dotfiles"]]],
  where: ["Where", ic.branch, [["New worktree", "Its own branch and folder, so it can't trip over other sessions"], ["This folder", "Works straight in weave-fleet, on the current branch"]]],
  harness: ["Harness", ic.cpu, [["OpenCode 2", "Recommended"], ["OpenCode", ""], ["Claude Code", ""], ["Pi", ""]]],
  agent: ["Agent", "", [["Loom", "Plans, then delegates"], ["Tapestry", "Runs a plan step by step"], ["Shuttle", "Quick, focused changes"], ["Default", "The harness's own default"]]],
  model: ["Model", "", [["sonnet-5", "claude-sonnet-5 · default"], ["opus-5-5", "claude-opus-5-5"], ["gpt-5.5", ""], ["gemini-3-pro", ""]]],
};
const nsChip = (k) => `<button class="chip" data-act="pick" data-key="${k}">${pickers[k][1]}<span data-ns="${k}">${NS[k]}</span>${svgc(ic.chevD, "chev")}</button>`;
const nsSel = (k) => `<button class="sel" data-act="pick" data-key="${k}"><span data-ns="${k}">${NS.loading ? '<span class="sk" style="width:44px"></span>' : NS[k]}</span>${ic.chevD}</button>`;
const nsCaption = () => `Runs on <b>${NS.machine}</b>, ${NS.where === "New worktree" ? "in a new worktree of" : "straight in"} <b>${NS.folder}</b>, with ${NS.harness}.`;
function newSessionSheet({ focus = true } = {}) {
  NS.loading = true;
  const sh = sheet(`
    <div class="sheet-pages"><div class="sheet-page">
      ${sheetHead("New session", "")}
      <div class="sheet__body">
        <div class="sheet__pad">
          <div class="frame"><textarea data-prompt rows="4" placeholder="Describe the task, or ask a question…" aria-label="What should the agent do?" enterkeyhint="enter"></textarea>
            <div class="frame__bar"><button class="icon-btn" data-act="toast" data-text="Attach a photo or file" aria-label="Attach">${ic.clip}</button>${nsSel("agent")}${nsSel("model")}</div></div>
          <div class="chips" style="margin-top:12px">${["machine", "folder", "where", "harness"].map(nsChip).join("")}</div>
          <p class="foot" style="margin:10px 2px 0" data-ns-foot>${nsCaption()}</p>
        </div>
      </div>
      <div class="sheet__foot"><button class="btn btn--primary btn--block" data-act="start" disabled>${ic.up}<span data-start-label>Start on ${NS.machine}</span></button></div>
    </div></div>`, { detents: ["large"] });
  const ta = sh.el.querySelector("[data-prompt]");
  autogrow(ta, (v) => sh.el.querySelectorAll('[data-act="start"]').forEach((b) => (b.disabled = !v.trim())));
  ta.style.minHeight = 24 * 4 + 14 + "px";
  if (focus) ta.focus({ preventScroll: true });
  loadAgentModel(sh.el);
  sh.pages = sheetPages(sh.el.querySelector(".sheet-pages"));
  return sh;
}
function loadAgentModel(el, ms = 750) {
  NS.loading = true;
  el.querySelectorAll('.sel [data-ns="agent"],.sel [data-ns="model"]').forEach((n) => (n.innerHTML = '<span class="sk" style="width:44px"></span>'));
  setTimeout(() => { NS.loading = false; el.querySelectorAll('.sel [data-ns="agent"],.sel [data-ns="model"]').forEach((n) => { n.textContent = NS[n.dataset.ns]; n.classList.add("fade-in"); }); }, ms);
}
function pickerPage(sh, key) {
  const [title, , opts] = pickers[key];
  const page = h(`<div>
    <div class="sheet__head" style="padding-left:8px"><button class="icon-btn icon-btn--text" data-act="picker-back" aria-label="Back">${ic.back}</button><h2>${title}</h2></div>
    <div class="sheet__body">
      ${key === "folder" ? `<div class="sheet__pad" style="margin-bottom:12px"><label class="field">${ic.search}<input type="search" placeholder="Search folders on ${NS.machine}"></label></div>` : ""}
      <div class="card">${opts.map(([v, d]) => `<button class="set" data-act="picked" data-key="${key}" data-val="${v}"><div class="set__main"><div class="set__t">${v}</div>${d ? `<div class="set__s">${d}</div>` : ""}</div>${NS[key] === v ? svgc(ic.check, "set__check") : '<span style="width:18px"></span>'}</button>`).join("")}</div>
    </div></div>`);
  sh.pages.push(page);
}
async function startSession(sh, btn) {
  const prompt = sh.el.querySelector("[data-prompt]").value.trim();
  if (!prompt) return;
  haptic("success");
  btn.innerHTML = `${ic.spin}<span>Starting…</span>`;
  btn.disabled = true;
  await new Promise((r) => setTimeout(r, 650));
  const id = "n" + Date.now();
  S[id] = { title: prompt.length > 42 ? prompt.slice(0, 40) + "…" : prompt, machine: NS.machine, folder: NS.folder, branch: "new-session", state: "starting", at: Date.now(), since: Date.now(), convo: [{ t: "user", text: prompt }] };
  await sh.close();
  rerenderLists();
  openSession(id);
  setTimeout(() => { S[id].state = "working"; S[id].since = Date.now(); refreshSession(id); rerenderLists(); }, 1500);
}

// ── Notifications: a Settings page at touch size ─────────────────────
const notif = { on: false, kinds: { needs: true, questions: true, finished: true, failed: true }, quiet: true };
function notifySheet({ on } = {}) {
  if (on !== undefined) notif.on = on;
  const sh = sheet(`${sheetHead("Notifications", "", `<button class="btn btn--primary btn--sm" data-act="sheet-close" style="margin-right:4px">Done</button>`)}<div class="sheet__body" data-notify-body></div>`, { detents: ["large"] });
  renderNotify(sh.el.querySelector("[data-notify-body]"));
}
function renderNotify(body) {
  const needsHomeScreen = prefs.platform === "ios" && !isStandalone;
  if (!notif.on) {
    const step = (n, t) => `<div class="set"><span class="choice__n">${n}</span><div class="set__main"><div class="set__t">${t}</div></div></div>`;
    body.innerHTML = `<div class="hero">
        <img src="icons/apple-touch-icon.png" alt="">
        <h3>Know when an agent needs you</h3>
        <p>Fleet taps you on the shoulder when a command waits for your approval, an agent asks a question, or a session finishes.</p>
      </div>
      ${needsHomeScreen ? `<div class="label">On iPhone, add Fleet to your Home Screen first</div>
      <div class="card">${step(1, `Tap Share ${svgc(ic.share, "").replace("<svg ", '<svg style="display:inline;width:15px;height:15px;vertical-align:-2px;color:var(--accent)" ')} in Safari`)}${step(2, "Choose Add to Home Screen")}${step(3, "Open Fleet from the Home Screen")}</div>
      <p class="foot">iOS only lets web apps send notifications once they're on the Home Screen.</p>` : ""}
      <div class="sheet__pad" style="margin-top:22px"><button class="btn btn--primary btn--block" data-act="notify-on">Turn on notifications</button></div>`;
    return;
  }
  const t = (k, title, sub) => `<button class="set" data-act="toggle" data-k="${k}"><div class="set__main"><div class="set__t">${title}</div><div class="set__s">${sub}</div></div><span class="switch ${(k === "quiet" ? notif.quiet : notif.kinds[k]) ? "is-on" : ""}" role="switch"></span></button>`;
  body.innerHTML = `
    <div class="card fade-in"><div class="set"><span class="done-mark" style="width:30px;height:30px;margin:0">${ic.check.replace("<svg ", '<svg style="width:16px;height:16px" ')}</span><div class="set__main"><div class="set__t">On for this phone</div><div class="set__s">${prefs.platform === "android" ? "Android · Chrome" : "iPhone · Home Screen app"}</div></div></div></div>
    <div class="label">Tell me when</div>
    <div class="card fade-in">${t("needs", "Something needs me", "A command or an edit waits for my approval")}${t("questions", "An agent asks", "A question I need to answer")}${t("finished", "A session finishes", "Its turn ended")}${t("failed", "A session fails", "It stopped with an error")}</div>
    <div class="card fade-in" style="margin-top:12px">${t("quiet", "Quiet at my desk", "Skip the phone while Fleet is open on a computer")}</div>
    <div class="sheet__pad" style="margin-top:18px"><button class="btn btn--outline btn--block" data-act="notify-test">${ic.bell.replace("<svg ", '<svg style="width:17px;height:17px" ')}Send a test notification</button></div>
    <p class="foot" data-test-note>It arrives like a real one, so you can see how it looks.</p>`;
}

// ── Pairing and "answered" pages (opened from a QR code / a notification) ──
function pairScreen() {
  const el = h(`<section class="screen screen--full" data-pair>
    <div class="panel"><div class="center-page">
      <img src="icons/weave-logo.png" alt="Fleet" style="width:72px;height:34px;object-fit:contain;margin:0 auto">
      <div style="text-align:center;margin-top:18px"><span class="from" style="height:26px;padding:0 10px;font-size:13px;gap:6px">${ic.monitor.replace("<svg ", '<svg style="width:14px;height:14px" ')}hangar · Linux</span></div>
      <h1 style="font-size:22px;font-weight:600;letter-spacing:-0.01em;text-align:center;margin:14px 0 8px;line-height:1.25">Connect this phone to hangar?</h1>
      <p style="text-align:center;color:var(--muted);font-size:15px;line-height:1.5;margin:0 6px">This phone gets its own key to hangar. It can start sessions, answer agents and read their work. Remove it any time in Settings&nbsp;→&nbsp;Machines.</p>
      <div class="label" style="margin:26px 2px 8px">Name this phone</div>
      <input class="input" value="${prefs.platform === "android" ? "Pixel 9" : "Pieter's iPhone"}" aria-label="Name this phone">
      <div class="actions"><button class="btn btn--primary btn--block" data-act="connect">Connect</button><button class="btn btn--ghost btn--block" data-act="back" style="color:var(--muted);font-weight:400">Not you? Close this page.</button></div>
    </div></div></section>`);
  return el;
}
function answeredScreen() {
  return h(`<section class="screen screen--full" data-answered>
    <div class="panel"><div class="center-page" style="text-align:center">
      <div class="done-mark">${ic.check}</div>
      <h1 style="font-size:22px;font-weight:600;letter-spacing:-0.01em;margin:18px 0 6px">Allowed. hangar carries on.</h1>
      <p style="color:var(--muted);font-size:15px;margin:0">It ran once; it'll ask again next time.</p>
      <div class="cmd cmd--one" style="margin:18px 0 0;text-align:left"><span class="cmd__p">$ </span>${CMD}</div>
      <div class="actions"><button class="btn btn--primary btn--block" data-act="answered-open">Open the session</button><button class="btn btn--outline btn--block" data-act="back">Back to Needs you</button></div>
    </div></div></section>`);
}

// ── Actions (one delegated click handler) ────────────────────────────
const actions = {
  back: () => closeTop(),
  tab: (el) => switchTab(el.dataset.tab, true),
  "sheet-close": () => activeSheet?.close(),
  open: (el) => openSession(el.dataset.id),
  allow: (el) => allow(el.dataset.id, el.closest(".sheet") ? null : el),
  "allow-always": (el) => { const id = el.dataset.id; if (el.closest(".sheet")) activeSheet?.close(); else el.classList.add("is-done"); toast("Won't ask again for dotnet test * in this session"); allow(id); },
  answer: (el) => answer(el.dataset.id, el.dataset.answer, el.closest(".sheet") ? null : el),
  "answer-own": (el) => { const v = el.closest(".frame").querySelector("textarea").value.trim(); activeSheet?.close(); answer(el.dataset.id, v); },
  "ask-more": (el) => askSheet(el.dataset.id),
  "ask-more-deny": (el) => askSheet(el.dataset.id, { deny: true }),
  "deny-open": (el) => { const box = el.closest(".sheet").querySelector(".deny-box"); box.hidden = false; el.classList.add("is-selected"); activeSheet?.expand(); box.querySelector("textarea").focus({ preventScroll: true }); setTimeout(() => box.scrollIntoView({ block: "nearest", behavior: "smooth" }), 350); },
  "deny-send": (el) => { const v = el.closest(".frame").querySelector("textarea").value.trim(); activeSheet?.close(); answer(el.dataset.id, `Denied: ${v}`); },
  later: (el) => {
    const d = el.closest(".docked");
    d.classList.add("is-gone");
    const pill = h(`<button class="waiting-pill pressable" data-act="unlater">${g("waiting")}1 waiting · Review</button>`);
    setTimeout(() => { d.parentElement.prepend(pill); pill.classList.add("fade-in"); }, 250);
  },
  unlater: (el) => { const p = el.parentElement; el.remove(); p.querySelector(".docked")?.classList.remove("is-gone"); },
  send: (el) => sendMessage(el.dataset.id),
  menu: (el) => menuSheet(el.dataset.id),
  plus: (el) => plusSheet(el.closest("[data-session]").dataset.session),
  step: (el) => stepSheet(el),
  steps: () => stepsSheet(),
  fold: (el) => { const m = el.dataset.m; folded.has(m) ? folded.delete(m) : folded.add(m); el.classList.toggle("is-folded"); el.nextElementSibling.classList.toggle("is-folded"); haptic("light"); },
  changes: () => { activeSheet?.close(); setTimeout(changesSheet, 260); },
  terminal: (el) => { const id = el.dataset.id || el.closest("[data-session]")?.dataset.session || "h2"; activeSheet?.close(); setTimeout(() => terminalSheet(id), 260); },
  "term-send": (el) => { const ta = el.closest(".frame").querySelector("textarea"); const v = ta.value.trim(); const id = el.dataset.id; activeSheet?.close(); appendConvo(id, { t: "tools", rows: [{ k: "bash", d: v, r: "exit 0" }] }); },
  archive: (el) => { const id = el.dataset.id; activeSheet?.close(); setTimeout(() => { archived.add(id); rerenderLists(); closeTop(); toast(`Archived “${S[id].title}”`, { label: "Undo", icon: ic.undo.replace("<svg ", '<svg width="14" height="14" '), onTap: () => { archived.delete(id); rerenderLists(); } }); }, 280); },
  new: () => newSessionSheet(),
  pick: (el) => activeSheet?.pages && pickerPage(activeSheet, el.dataset.key),
  "picker-back": () => activeSheet?.pages.pop(),
  picked: (el) => {
    const k = el.dataset.key, v = el.dataset.val;
    haptic("light");
    NS[k] = v;
    el.closest(".card").querySelectorAll(".set").forEach((r) => (r.lastElementChild.outerHTML = r === el ? svgc(ic.check, "set__check") : '<span style="width:18px"></span>'));
    const root = activeSheet.el;
    root.querySelectorAll(`[data-ns="${k}"]`).forEach((n) => (n.textContent = v));
    root.querySelector("[data-start-label]").textContent = `Start on ${NS.machine}`;
    root.querySelector("[data-ns-foot]").innerHTML = nsCaption();
    if (k === "harness" || k === "machine") { NS.agent = "Default"; NS.model = "Default"; loadAgentModel(root); }
    setTimeout(() => activeSheet.pages.pop(), 180);
  },
  start: (el) => startSession(activeSheet, el),
  notify: () => notifySheet(),
  "notify-on": (el) => {
    el.innerHTML = `${ic.spin}<span>Asking…</span>`;
    setTimeout(() => { notif.on = true; haptic("success"); renderNotify(el.closest("[data-notify-body]")); document.querySelector("[data-notif-value]").textContent = "On"; }, 700);
  },
  toggle: (el) => {
    const k = el.dataset.k;
    if (k === "quiet") notif.quiet = !notif.quiet; else notif.kinds[k] = !notif.kinds[k];
    el.querySelector(".switch").classList.toggle("is-on");
    haptic("light");
  },
  "notify-test": (el) => {
    const note = el.closest(".sheet").querySelector("[data-test-note]");
    note.textContent = "Sent. It should arrive in a few seconds.";
    setTimeout(() => banner({ title: "hangar needs you", body: `Fix flaky SignalR reconnect test wants to run ${CMD.slice(0, 22)}…`, onTap: () => { activeSheet?.close(); setTimeout(() => openSession("h1"), 300); } }), 1400);
  },
  pair: () => nav.push(pairScreen()),
  connect: (el) => { el.innerHTML = `${ic.spin}<span>Connecting…</span>`; el.disabled = true; setTimeout(() => { haptic("success"); closeTop(); toast("Connected to hangar"); }, 900); },
  "answered-open": () => { closeTop(); setTimeout(() => openSession("h1"), 380); },
  toast: (el) => toast(el.dataset.text || "Not part of this mockup"),
};
document.addEventListener("click", (e) => {
  const el = e.target.closest("[data-act]");
  if (!el || el.disabled) return;
  if (el.tagName === "A") e.preventDefault();
  const f = actions[el.dataset.act];
  if (!f) return;
  f(el, e);
  if (el.hasAttribute("data-close") && activeSheet && el.dataset.act !== "allow-always") activeSheet.close();
});

// ── Deep links: flow.html#ask, #session, #new, #notify … ─────────────
const deep = location.hash.slice(1);
history.replaceState({ d: 0 }, "", location.pathname + location.search);
const waitFrame = () => new Promise((r) => setTimeout(r, 30));
(async () => {
  await waitFrame();
  switch (deep) {
    case "sessions": switchTab("sessions"); break;
    case "machines": switchTab("machines"); break;
    case "ask": askSheet("h1"); break;
    case "ask-deny": askSheet("h1", { deny: true }); break;
    case "question": askSheet("f1"); break;
    case "session": await openSession("h1", { animate: false }); break;
    case "session-question": await openSession("f1", { animate: false }); break;
    case "session-working": await openSession("h3", { animate: false }); break;
    case "session-finished": await openSession("h2", { animate: false }); break;
    case "menu": await openSession("h2", { animate: false }); menuSheet("h2"); break;
    case "terminal": await openSession("h2", { animate: false }); terminalSheet("h2"); break;
    case "changes": await openSession("h2", { animate: false }); changesSheet(); break;
    case "step": await openSession("h1", { animate: false }); stepSheet(screens.h1.querySelectorAll(".tool")[3]); break;
    case "new": newSessionSheet({ focus: false }); break;
    case "new-filled": { const sh = newSessionSheet({ focus: false }); const ta = sh.el.querySelector("[data-prompt]"); ta.value = "The reconnect test fails about 1 in 10 runs on CI. Find out why and fix it."; ta.dispatchEvent(new Event("input")); break; }
    case "new-picker": { const sh = newSessionSheet({ focus: false }); setTimeout(() => pickerPage(sh, "where"), 500); break; }
    case "notify": notifySheet({ on: false }); break;
    case "notify-on": notifySheet({ on: true }); break;
    case "pair": await nav.push(pairScreen(), { animate: false }); break;
    case "answered": await nav.push(answeredScreen(), { animate: false }); break;
  }
  const kb = +params.get("kb");
  if (kb) window.__fakeKeyboard(kb);
})();
window.__flow = { S, dur, switchTab, openSession, toast, actions };
