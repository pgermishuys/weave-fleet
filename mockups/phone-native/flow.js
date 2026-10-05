// The Fleet phone flow, mocked: inbox → ask → session → new session → notification setup.
import { setupEnv, setupViewport, setupPress, createNav, openSheet, sheetPages, largeTitle, pullToRefresh, swipeRow, toast, banner, autogrow, haptic, animateTo, h, prefs, isIOSWebKit, isStandalone, closeTop } from "./kit.js";

setupEnv();
const viewport = setupViewport();
setupPress();
const params = new URLSearchParams(location.search);

// ── Icons (24px, 2px stroke, round: the same family as the app's lucide icons) ──
const I = (d, extra = "") => `<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" ${extra}>${d}</svg>`;
const ic = {
  plus: I('<path d="M12 5v14M5 12h14"/>'),
  bell: I('<path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9"/><path d="M10.3 21a1.94 1.94 0 0 0 3.4 0"/>'),
  inbox: I('<path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/>'),
  chat: I('<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>'),
  monitor: I('<rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/>'),
  back: I('<path d="m15 18-6-6 6-6"/>', 'stroke-width="2.4"'),
  arrowLeft: I('<path d="M19 12H5M12 19l-7-7 7-7"/>'),
  chevR: '<svg class="row__chev" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="m9 18 6-6-6-6"/></svg>',
  more: I('<circle cx="5" cy="12" r="1.3" fill="currentColor"/><circle cx="12" cy="12" r="1.3" fill="currentColor"/><circle cx="19" cy="12" r="1.3" fill="currentColor"/>'),
  up: I('<path d="M12 19V5M5 12l7-7 7 7"/>', 'stroke-width="2.6"'),
  check: I('<path d="M20 6 9 17l-5-5"/>', 'stroke-width="2.6"'),
  x: I('<path d="M18 6 6 18M6 6l12 12"/>', 'stroke-width="2.4"'),
  archive: I('<rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8M10 12h4"/>'),
  terminal: I('<path d="m4 17 6-6-6-6M12 19h8"/>'),
  diff: I('<path d="M12 3v14M5 10h14M5 21h14"/>'),
  file: I('<path d="M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5L14.5 2z"/><path d="M14 2v6h6"/>'),
  side: I('<path d="M7.9 20A9 9 0 1 0 4 16.1L2 22z"/>'),
  fork: I('<circle cx="6" cy="6" r="3"/><circle cx="18" cy="6" r="3"/><circle cx="12" cy="18" r="3"/><path d="M6 9v1a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V9M12 12v3"/>'),
  pencil: I('<path d="M17 3a2.85 2.85 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5z"/>'),
  laptop: I('<path d="M20 16V7a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v9m16 0H4m16 0 1.28 2.55A1 1 0 0 1 20.38 20H3.62a1 1 0 0 1-.9-1.45L4 16"/>'),
  image: I('<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-3.09-3.09a2 2 0 0 0-2.82 0L6 21"/>'),
  camera: I('<path d="M14.5 4h-5L7 7H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-3z"/><circle cx="12" cy="13" r="3"/>'),
  search: I('<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>'),
  read: I('<path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2zM22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>'),
  spin: '<svg class="spinner" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="M21 12a9 9 0 1 1-6.22-8.56"/></svg>',
  share: I('<path d="M4 12v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8M16 6l-4-4-4 4M12 2v13"/>'),
};
const machineSvg = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></svg>';
const backBtn = () => `<button class="navbtn glass" data-act="back" aria-label="Back">${prefs.platform === "android" ? ic.arrowLeft : ic.back}</button>`;

// ── Mock data ────────────────────────────────────────────────────────
const now = Date.now();
const min = 60e3, hr = 60 * min, day = 24 * hr;
const CMD = "dotnet test tests/WeaveFleet.IntegrationTests --filter SignalREventContractTests";
// wrap a command only between words, never inside one (no "--" / "filter" split, no lone "*")
const cmdHtml = (c) => c.split(" ").map((w) => `<span class="nobr">${w}</span>`).join(" ");
const S = {
  h1: { title: "Fix flaky SignalR reconnect test", machine: "hangar", state: "permission", at: now - 3 * min, since: now - 73e3,
    convo: [{ t: "user", text: "The reconnect test fails about 1 in 10 runs on CI. Find out why and fix it." },
      { t: "agent", html: "CI schedules the first reconnect retry at <b>2 s</b>, and the test only waits 2 s, so it loses the race about one run in ten. I raised the harness wait to 5 s." },
      { t: "step", text: "Read 2 files · edited 1 · searched 1", act: "steps" },
      { t: "deleg", who: "shuttle", text: "Look for other tests with the same race" },
      { t: "step", text: "Ran 1 command", act: "steps" }] },
  f1: { title: "Per-device tokens for machines", machine: "falcon", state: "question", at: now - 11 * min, since: now - 11 * min,
    q: "When a removed phone calls in, should it get a plain 401 or a page that says it was removed?",
    opts: [["Plain 401", "The phone just gets 401 Unauthorized"], ["Explain", "A page that says it was removed"]],
    convo: [{ t: "user", text: "Give each paired phone its own token so I can remove one without re-pairing the rest." },
      { t: "agent", html: "Tokens are per device now and removing one revokes only that device. One thing I need from you:" },
      { t: "step", text: "Read 6 files · edited 4", act: "steps" }] },
  f2: { title: "Composer references refactor", machine: "falcon", state: "working", at: now - 361e3, since: now - 361e3,
    convo: [{ t: "user", text: "Split the composer's @ references into their own module and keep the pills working." },
      { t: "agent", html: "Moving the picker and pill rendering into <code>references/</code>. The pills read from the same store, so nothing changes for the composer." },
      { t: "step", text: "Read 9 files · edited 5", act: "steps" }] },
  h3: { title: "Migrate the harness cache", machine: "hangar", state: "working", at: now - (13 * day + 6 * hr + 12 * min), since: now - (13 * day + 6 * hr + 12 * min),
    convo: [{ t: "user", text: "Move the harness cache to the new table and drop the old one." },
      { t: "step", text: "Read 3 files", act: "steps" }] },
  h2: { title: "Bump OpenCode to 1.14", machine: "hangar", state: "finished", at: now - 22 * min,
    convo: [{ t: "user", text: "Bump OpenCode to 1.14 and fix whatever breaks." },
      { t: "agent", html: "Bumped to 1.14.2. Two tests listened for the old event name; both pass now." },
      { t: "step", text: "Read 3 files · edited 2 · ran 2 commands", act: "steps" },
      { t: "since", text: "Since you looked at 1:37 PM" },
      { t: "user", text: "Skip the E2E run, just open the PR" },
      { t: "agent", html: "E2E passes too. I opened a draft pull request." },
      { t: "step", text: "Ran 1 command", act: "steps" }] },
  h4: { title: "Readable bad requests", machine: "hangar", state: "finished", at: now - 5 * hr, convo: [{ t: "user", text: "Empty 400s should name the field." }] },
  f3: { title: "Pi model switch", machine: "falcon", state: "finished", at: now - 30 * hr, convo: [{ t: "user", text: "Let Pi switch models mid-session." }] },
};
const archived = new Set();

// durations: seconds → minutes → hours → days, so a stuck session reads "13d 6h", not "19154h 33m"
export function dur(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  const hh = Math.floor(m / 60);
  if (hh < 24) return `${hh}h ${m % 60}m`;
  return `${Math.floor(hh / 24)}d ${hh % 24}h`;
}
const ago = (t) => { const m = Math.floor((Date.now() - t) / min); if (m < 1) return "just now"; if (m < 60) return `${m} min ago`; const hh = Math.floor(m / 60); if (hh < 24) return `${hh} h ago`; return hh < 48 ? "yesterday" : `${Math.floor(hh / 24)} days ago`; };
const statusLine = (id) => {
  const s = S[id];
  if (s.state === "permission" || s.state === "question") return `<span class="dot dot--waiting"></span>${s.machine} · Needs you`;
  if (s.state === "working") return `<span class="dot dot--running"></span>${s.machine} · Working · <span data-tick="${id}">${dur(Date.now() - s.since)}</span>`;
  if (s.state === "starting") return `<span class="dot dot--running"></span>Starting on ${s.machine}…`;
  return `<span class="dot dot--done"></span>${s.machine} · Finished ${ago(s.at)}`;
};
const needsCount = () => Object.keys(S).filter((k) => !archived.has(k) && (S[k].state === "permission" || S[k].state === "question")).length;

// ── Root screen: three tabs, a floating tab bar ─────────────────────
const stage = document.getElementById("stage");
const rootScreen = h(`<section class="screen" id="root"></section>`);
stage.appendChild(rootScreen);
const nav = createNav(stage, rootScreen);

const tabsDef = [["inbox", "Needs you", ic.inbox], ["sessions", "Sessions", ic.chat], ["machines", "Machines", ic.monitor]];
rootScreen.innerHTML = `
  ${tabsDef.map(([id, name]) => `
    <div class="tabpage" data-tabpage="${id}" ${id === "inbox" ? "" : "hidden"}>
      <header class="navbar">
        <div class="navbar__title"><span>${name}</span></div>
        <div class="navbar__spacer"></div>
        ${id === "inbox" ? `<div class="navbtn-group glass"><button class="navbtn navbtn--plusbtn" data-act="new" aria-label="New session">${ic.plus}</button><button class="navbtn" data-act="notify" aria-label="Notifications">${ic.bell}</button></div>` : ""}
        ${id === "sessions" ? `<button class="navbtn glass navbtn--plusbtn" data-act="new" aria-label="New session">${ic.plus}</button>` : ""}
      </header>
      <div class="ptr"></div>
      <div class="scroller"><div class="scroller__inner">
        <div class="large-title"><h1>${name}</h1>${id === "inbox" ? `<p data-updated><span class="dot" style="width:8px;height:8px;background:var(--running)"></span>hangar and falcon online</p>` : ""}</div>
        <div class="tab-content"></div>
      </div></div>
    </div>`).join("")}
  <button class="fab" data-act="new">${ic.plus}<span>New session</span></button>
  <nav class="tabbar glass">
    ${tabsDef.map(([id, name, icon], i) => `<button class="tab ${i === 0 ? "is-active" : ""}" data-tab="${id}"><span class="tab__ic">${icon}</span><span>${name}</span>${id === "inbox" ? `<span class="badge badge--amber" data-badge></span>` : ""}</button>`).join("")}
  </nav>`;

const tabPage = (id) => rootScreen.querySelector(`[data-tabpage="${id}"]`);
const titles = {};
for (const [id] of tabsDef) {
  const p = tabPage(id);
  titles[id] = largeTitle(p.querySelector(".scroller"), p.querySelector(".navbar"), p.querySelector(".large-title"));
}

// ── Inbox ────────────────────────────────────────────────────────────
const askCard = (id) => {
  const s = S[id];
  const head = `<button class="ask__open" data-act="open" data-id="${id}">
      <div class="ask__meta"><span class="machine">${machineSvg}${s.machine}</span>· ${ago(s.at)}</div>
      <div class="ask__title">${s.title}</div>
      <div class="ask__what">${s.state === "permission" ? "Wants to run" : `<b>Asked:</b> ${s.q}`}</div></button>`;
  if (s.state === "permission")
    return `<article class="ask" data-ask="${id}">${head}<div class="code code--one">${CMD}</div>
      <div class="btns"><button class="btn btn--primary" data-act="allow" data-id="${id}">Allow once</button><button class="btn" data-act="ask-more" data-id="${id}">More…</button></div></article>`;
  return `<article class="ask" data-ask="${id}">${head}
      <div class="btns btns--q">${s.opts.map(([o]) => `<button class="btn" data-act="answer" data-id="${id}" data-answer="${o}">${o}</button>`).join("")}<button class="btn" data-act="ask-more" data-id="${id}">More…</button></div></article>`;
};
const sessionRow = (id, sub) => {
  const s = S[id];
  const dot = { working: "dot--running", starting: "dot--running", permission: "dot--waiting", question: "dot--waiting", finished: "dot--done" }[s.state];
  return `<div class="row-wrap" data-row="${id}"><div class="swipe-actions"><button class="swipe-act swipe-act--archive" aria-label="Archive">${ic.archive}<span>Archive</span></button></div>
    <button class="row" data-act="open" data-id="${id}" style="--sep-left:44px"><span class="dot ${dot}"></span><div class="row__main"><div class="row__title">${s.title}</div><div class="row__sub">${sub ?? statusSub(id)}</div></div></button></div>`;
};
const statusSub = (id) => {
  const s = S[id];
  if (s.state === "working" || s.state === "starting") return `${s.machine} · Working · <span data-tick="${id}">${dur(Date.now() - s.since)}</span>`;
  if (s.state === "permission" || s.state === "question") return `${s.machine} · Needs you · ${ago(s.at)}`;
  return `${s.machine} · ${ago(s.at)}`;
};
const live = (st) => Object.keys(S).filter((k) => !archived.has(k) && st.includes(S[k].state));
const skeleton = () => `
  ${[0, 1].map(() => `<div class="ask" style="box-shadow:none"><span class="sk" style="width:38%"></span><div style="margin:12px 0 8px"><span class="sk" style="width:72%;height:1.05em"></span></div><span class="sk" style="width:30%"></span><div style="margin-top:12px"><span class="sk" style="width:100%;height:38px;border-radius:10px"></span></div><div class="btns"><span class="sk" style="height:46px;border-radius:23px"></span><span class="sk" style="height:46px;border-radius:23px"></span></div></div>`).join("")}
  <div class="section-h"><span class="sk" style="width:90px;height:1em"></span></div>
  <div class="group">${[0, 1].map(() => `<div class="row"><span class="dot" style="background:var(--fill-strong)"></span><div class="row__main"><span class="sk" style="width:70%"></span><div style="margin-top:7px"><span class="sk" style="width:45%;height:.8em"></span></div></div></div>`).join("")}</div>`;

function renderInbox() {
  const c = tabPage("inbox").querySelector(".tab-content");
  const asks = live(["permission", "question"]);
  const working = live(["working", "starting"]);
  const finished = live(["finished"]).sort((a, b) => S[b].at - S[a].at).slice(0, 2);
  c.innerHTML = `
    ${asks.map(askCard).join("")}
    ${asks.length === 0 ? `<div class="group-f fade-in" style="margin:6px 20px 8px;font-size:var(--t-sub)">Nothing needs you right now.</div>` : ""}
    ${working.length ? `<h2 class="section-h">Working <small>${working.length}</small></h2><div class="group">${working.map((id) => sessionRow(id)).join("")}</div>` : ""}
    ${finished.length ? `<h2 class="section-h" style="margin-top:22px">Finished <small>${finished.length}</small></h2><div class="group">${finished.map((id) => sessionRow(id)).join("")}</div>` : ""}`;
  wireRows(c);
  updateBadge();
}
function renderSessions() {
  const c = tabPage("sessions").querySelector(".tab-content");
  const ids = Object.keys(S).filter((k) => !archived.has(k)).sort((a, b) => S[b].at - S[a].at);
  const today = ids.filter((k) => Date.now() - S[k].at < day), earlier = ids.filter((k) => Date.now() - S[k].at >= day);
  c.innerHTML = `
    <div style="padding:0 16px 14px"><label class="search glass-field">${ic.search}<input type="search" placeholder="Search sessions" data-search enterkeyhint="search"></label></div>
    <div data-list>
    ${today.length ? `<div class="group-h">Today</div><div class="group">${today.map((id) => sessionRow(id)).join("")}</div>` : ""}
    ${earlier.length ? `<div class="group-h">Earlier</div><div class="group">${earlier.map((id) => sessionRow(id)).join("")}</div>` : ""}
    </div>
    <div class="group" style="margin-top:22px"><a class="row row--accent" href="#" data-act="toast" data-text="Opens the full Fleet in the browser"><div class="row__main"><div class="row__title">Open the full Fleet</div></div>${ic.chevR}</a></div>
    <div class="group-f">Swipe a session left to archive it.</div>`;
  wireRows(c);
  c.querySelector("[data-search]").addEventListener("input", (e) => {
    const q = e.target.value.toLowerCase();
    c.querySelectorAll("[data-row]").forEach((r) => (r.hidden = !S[r.dataset.row].title.toLowerCase().includes(q)));
  });
}
function renderMachines() {
  const c = tabPage("machines").querySelector(".tab-content");
  c.innerHTML = `
    <div class="group">
      <div class="row" style="--sep-left:62px"><span class="row__icon" style="background:var(--accent)">${ic.monitor}</span><div class="row__main"><div class="row__title">hangar</div><div class="row__sub">Linux · 4 sessions</div></div><span class="dot" style="background:var(--running)"></span></div>
      <div class="row" style="--sep-left:62px"><span class="row__icon" style="background:var(--queued)">${ic.laptop}</span><div class="row__main"><div class="row__title">falcon</div><div class="row__sub">macOS · 3 sessions</div></div><span class="dot" style="background:var(--running)"></span></div>
    </div>
    <div class="group-f">Add machines from Fleet on a computer, in Settings → Machines.</div>
    <div class="group" style="margin-top:22px">
      <button class="row" data-act="notify" style="--sep-left:62px"><span class="row__icon" style="background:var(--error)">${ic.bell}</span><div class="row__main"><div class="row__title">Notifications</div></div><span class="row__value" data-notif-value>Off</span>${ic.chevR}</button>
      <a class="row" href="index.html" style="--sep-left:62px"><span class="row__icon" style="background:var(--muted)">${ic.read}</span><div class="row__main"><div class="row__title">About these mockups</div></div>${ic.chevR}</a>
    </div>`;
}
function wireRows(c) {
  c.querySelectorAll(".row-wrap").forEach((w) => swipeRow(w, {
    onCommit: (wrap) => {
      const id = wrap.dataset.row;
      archived.add(id);
      haptic("success");
      rerenderLists();
      toast("Archived", { label: "Undo", onTap: () => { archived.delete(id); rerenderLists(); } });
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

// skeleton first (fixed heights, so nothing jumps), then content fades in
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
  await new Promise((r) => setTimeout(r, 1000));
  S.h1.at = Date.now() - 20e3;
  renderInbox();
  const p = tabPage("inbox").querySelector("[data-updated]");
  p.innerHTML = `<span class="dot" style="width:8px;height:8px;background:var(--running)"></span>hangar and falcon online · updated just now`;
}, titles.inbox);

// Tabs: instant switch (iOS) or a quick fade-through (Android); tapping the current tab scrolls to the top
let currentTab = "inbox";
function switchTab(id, user = false) {
  const scroller = tabPage(id).querySelector(".scroller");
  if (id === currentTab) { scroller.scrollTo({ top: 0, behavior: "smooth" }); return; }
  if (user) haptic("light");
  tabPage(currentTab).hidden = true;
  tabPage(id).hidden = false;
  if (prefs.platform === "android") { tabPage(id).style.opacity = 0; animateTo(tabPage(id), { opacity: "1" }, 180, "ease-out"); }
  rootScreen.querySelectorAll(".tab").forEach((t) => t.classList.toggle("is-active", t.dataset.tab === id));
  rootScreen.querySelector(".fab").style.display = id === "machines" ? "none" : "";
  currentTab = id;
}
rootScreen.querySelectorAll(".tab").forEach((t) => t.addEventListener("click", () => switchTab(t.dataset.tab, true)));
// Android: the floating button shrinks to an icon as you scroll down
for (const [id] of tabsDef) {
  let last = 0;
  tabPage(id).querySelector(".scroller").addEventListener("scroll", (e) => {
    const y = e.target.scrollTop;
    rootScreen.querySelector(".fab").classList.toggle("is-small", y > last && y > 40);
    last = y;
  }, { passive: true });
}

// ── Session screen ───────────────────────────────────────────────────
const convoItem = (it) => {
  switch (it.t) {
    case "user": return `<div class="bubble">${it.text}</div>`;
    case "agent": return `<div class="agent"><p>${it.html}</p></div>`;
    case "step": return `<button class="step" data-act="${it.act ?? ""}">${it.spin ? ic.spin : ""}<span class="step__t">${it.text}</span>${it.spin ? "" : ic.chevR}</button>`;
    case "deleg": return `<button class="step" data-act="toast" data-text="Opens shuttle's delegated session"><span class="step__who">${it.who}</span><span class="step__t">${it.text}</span>${it.done ? '<span class="step__done">Done</span>' : ic.spin}</button>`;
    case "since": return `<div class="since">${it.text}</div>`;
  }
};
function dockedAsk(id) {
  const s = S[id];
  if (s.state === "permission")
    return `<div class="docked-ask"><div class="docked-ask__h"><span class="dot dot--waiting"></span>Wants to run a command<button class="later pressable" data-act="later">Later</button></div>
      <div class="code code--wrap clamp3"><span class="prompt nobr">~/src/weave-fleet $</span> ${cmdHtml(CMD)}</div>
      <div class="btns"><button class="btn btn--primary" data-act="allow" data-id="${id}">Allow once</button><button class="btn" data-act="ask-more" data-id="${id}">More…</button></div></div>`;
  if (s.state === "question")
    return `<div class="docked-ask"><div class="docked-ask__h"><span class="dot dot--waiting"></span>Asked you<button class="later pressable" data-act="later">Later</button></div>
      <div style="font-size:var(--t-body);line-height:1.38">${s.q}</div>
      <div class="btns btns--q">${s.opts.map(([o]) => `<button class="btn" data-act="answer" data-id="${id}" data-answer="${o}">${o}</button>`).join("")}<button class="btn" data-act="ask-more" data-id="${id}">More…</button></div></div>`;
  return "";
}
const screens = {};
function sessionScreen(id) {
  const s = S[id];
  const el = h(`<section class="screen" data-session="${id}">
    <header class="navbar">
      ${backBtn()}
      <div class="sess-head"><div class="sess-head__t">${s.title}</div><div class="sess-head__s" data-status>${statusLine(id)}</div></div>
      <button class="navbtn glass" data-act="menu" data-id="${id}" aria-label="Session menu">${ic.more}</button>
    </header>
    <div class="scroller"><div class="scroller__inner convo">
      ${s.convo.map(convoItem).join("")}
      <div data-tail>${tail(id)}</div>
    </div></div>
    <div class="dock">
      <div data-docked>${dockedAsk(id)}</div>
      <div class="composer glass"><button class="composer__plus" data-act="plus" aria-label="Add">${ic.plus}</button>
        <textarea rows="1" placeholder="Message" aria-label="Message"></textarea>
        <button class="send" data-act="send" data-id="${id}" disabled aria-label="Send">${ic.up}</button></div>
    </div>
  </section>`);
  const sc = el.querySelector(".scroller"), dock = el.querySelector(".dock");
  const navbar = el.querySelector(".navbar");
  sc.addEventListener("scroll", () => navbar.classList.toggle("is-scrolled", sc.scrollTop > 4), { passive: true });
  // the conversation always clears the dock, whatever its height (docked ask, five-line composer, keyboard)
  new ResizeObserver(() => {
    const atBottom = sc.scrollHeight - sc.scrollTop - sc.clientHeight < 40;
    sc.style.paddingBottom = dock.offsetHeight + 16 + "px";
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
  if (s.state === "working" || s.state === "starting") return `<div class="working"><span class="typing"><i></i><i></i><i></i></span>${s.state === "starting" ? `Starting on ${s.machine}…` : `Working · <span data-tick="${id}">${dur(Date.now() - s.since)}</span>`}</div>`;
  if (s.state === "permission" || s.state === "question") return `<div class="working" style="color:var(--idle)">Waiting for you</div>`;
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
  if (btn) { btn.classList.add("is-done"); btn.innerHTML = `${ic.check}Allowed`; }
  const card = rootScreen.querySelector(`[data-ask="${id}"]`);
  const docked = screens[id]?.querySelector(".docked-ask");
  S[id].state = "working"; S[id].since = Date.now();
  refreshSession(id);
  updateBadge();
  if (docked) { docked.classList.add("is-gone"); }
  if (card) { await new Promise((r) => setTimeout(r, 450)); await collapse(card); }
  renderInbox(); renderSessions();
  const step = appendConvo(id, { t: "step", text: `Running ${CMD.slice(0, 26)}…`, spin: true });
  setTimeout(() => {
    S[id].convo.pop(); step?.remove();
    appendConvo(id, { t: "step", text: "Ran 2 commands", act: "steps" });
    appendConvo(id, { t: "agent", html: "All 40 contract tests pass, five runs in a row. Want me to open a pull request?" });
    S[id].state = "finished"; S[id].at = Date.now();
    refreshSession(id); renderInbox(); renderSessions();
  }, 2600);
}
async function answer(id, text, btn) {
  haptic("success");
  if (btn) { btn.classList.add("is-done"); btn.innerHTML = `${ic.check}${text}`; }
  const card = rootScreen.querySelector(`[data-ask="${id}"]`);
  S[id].state = "working"; S[id].since = Date.now();
  screens[id]?.querySelector(".docked-ask")?.classList.add("is-gone");
  appendConvo(id, { t: "user", text });
  refreshSession(id); updateBadge();
  if (card) { await new Promise((r) => setTimeout(r, 450)); await collapse(card); }
  renderInbox(); renderSessions();
  setTimeout(() => { appendConvo(id, { t: "agent", html: text === "Explain" ? "A removed phone now gets a short page that says it was removed and how to pair again." : "A removed phone now gets a plain 401." }); S[id].state = "finished"; S[id].at = Date.now(); refreshSession(id); renderInbox(); renderSessions(); }, 2200);
}
async function collapse(el) {
  el.style.overflow = "hidden";
  el.style.height = el.offsetHeight + "px";
  await animateTo(el, { height: "0px", opacity: "0", marginBottom: "0px", paddingTop: "0px", paddingBottom: "0px" }, 320);
}
function sendMessage(id) {
  const el = screens[id];
  const ta = el.querySelector("textarea");
  const text = ta.value.trim();
  if (!text) return;
  haptic("light");
  ta.value = ""; ta.dispatchEvent(new Event("input"));
  const b = appendConvo(id, { t: "user", text });
  b?.classList.add("is-sending");
  setTimeout(() => b?.classList.remove("is-sending"), 350);
  S[id].state = "working"; S[id].since = Date.now();
  el.querySelector(".docked-ask")?.classList.add("is-gone");
  refreshSession(id); renderInbox(); renderSessions();
  setTimeout(() => {
    appendConvo(id, { t: "agent", html: "On it. I'll keep the change small and tell you when the tests pass." });
    S[id].state = "finished"; S[id].at = Date.now(); refreshSession(id); renderInbox(); renderSessions();
  }, 2000);
}

// keep the focused field in sight when the keyboard changes the space a sheet has
viewport.onResize(() => {
  const a = document.activeElement;
  if (a?.closest?.(".sheet__body")) setTimeout(() => (a.closest(".composer,.prompt-card") || a).scrollIntoView({ block: "nearest" }), 80);
});

// live durations
setInterval(() => document.querySelectorAll("[data-tick]").forEach((n) => { const s = S[n.dataset.tick]; if (s) n.textContent = dur(Date.now() - s.since); }), 1000);

// ── Sheets ───────────────────────────────────────────────────────────
const closeBtn = `<button class="navbtn glass" data-act="sheet-close" aria-label="Close">${ic.x}</button>`;
let activeSheet = null;
function sheet(html, opts = {}) {
  const el = h(`<div>${html}</div>`);
  const api = openSheet({ el, viewport, ...opts, onClose: () => { if (activeSheet === api) activeSheet = null; opts.onClose?.(); } });
  activeSheet = api;
  return api;
}

function askSheet(id) {
  const s = S[id];
  if (s.state === "question") return questionSheet(id);
  const sh = sheet(`
    <div class="sheet__head"><h2>Run a command</h2><span class="navbar__spacer"></span>${closeBtn}</div>
    <div class="sheet__body"><div class="sheet__pad">
      <div class="ask__meta"><span class="machine">${machineSvg}${s.machine}</span>· ${s.title}</div>
      <div class="code code--wrap" style="margin:10px 0 16px"><span class="prompt nobr">~/src/weave-fleet $</span> ${cmdHtml(CMD)}</div>
      <button class="btn btn--primary btn--big" data-act="allow" data-id="${id}" data-close>Allow once</button>
    </div>
    <div class="group" style="margin-top:16px">
      <button class="row" data-act="allow-always" data-id="${id}"><div class="row__main"><div class="row__title">Always allow in this session</div><div class="row__sub">Commands that start with <span class="chip-code">dotnet test</span></div></div></button>
      <button class="row" data-act="deny-open"><div class="row__main"><div class="row__title" style="color:var(--error)">Deny</div><div class="row__sub">and tell the agent what to do instead</div></div>${ic.chevR}</button>
    </div>
    <div class="deny-box" hidden>
      <div class="group-h">Tell the agent what to do instead</div>
      <div class="sheet__pad"><div class="composer glass-field"><textarea rows="2" placeholder="Run only the reconnect test" aria-label="What to do instead"></textarea><button class="send" data-act="deny-send" data-id="${id}" disabled aria-label="Send">${ic.up}</button></div></div>
    </div>
    <button class="link-btn" data-act="open" data-id="${id}" data-close style="margin-top:12px">Open the session</button>
    </div>`, { detents: ["medium", "large"], initial: "medium" });
  const ta = sh.el.querySelector(".deny-box textarea");
  autogrow(ta, (v) => (sh.el.querySelector('[data-act="deny-send"]').disabled = !v.trim()));
  return sh;
}
function questionSheet(id) {
  const s = S[id];
  const sh = sheet(`
    <div class="sheet__head"><h2>Question</h2><span class="navbar__spacer"></span>${closeBtn}</div>
    <div class="sheet__body"><div class="sheet__pad">
      <div class="ask__meta"><span class="machine">${machineSvg}${s.machine}</span>· ${s.title}</div>
      <p style="font-size:var(--t-body);line-height:1.4;margin:10px 0 16px">${s.q}</p></div>
    <div class="group">${s.opts.map(([o, d]) => `<button class="row" data-act="answer" data-id="${id}" data-answer="${o}" data-close><div class="row__main"><div class="row__title">${o}</div><div class="row__sub">${d}</div></div></button>`).join("")}</div>
    <div class="group-h">Or answer in your own words</div>
    <div class="sheet__pad"><div class="composer glass-field"><textarea rows="1" placeholder="Your answer" aria-label="Your answer"></textarea><button class="send" data-act="answer-own" data-id="${id}" disabled aria-label="Send">${ic.up}</button></div></div>
    <button class="link-btn" data-act="open" data-id="${id}" data-close style="margin-top:12px">Open the session</button>
    </div>`, { detents: ["medium", "large"], initial: "medium" });
  const ta = sh.el.querySelector("textarea");
  autogrow(ta, (v) => (sh.el.querySelector('[data-act="answer-own"]').disabled = !v.trim()));
  ta.addEventListener("focus", () => sh.expand());
}
function menuSheet(id) {
  const row = (act, icon, label, value = "", cls = "") => `<button class="row ${cls}" data-act="${act}" data-id="${id}" style="--sep-left:56px"><span class="row__icon row__icon--plain">${icon}</span><div class="row__main"><div class="row__title">${label}</div></div>${value ? `<span class="row__value">${value}</span>` : ""}</button>`;
  sheet(`
    <div class="sheet__body" style="padding-bottom:14px">
      <div class="group">${row("changes", ic.diff, "Changes", "3 files")}${row("toast", ic.file, "Files")}${row("toast", ic.side, "Side question", "/btw")}${row("terminal", ic.terminal, "Run a command")}</div>
      <div class="group">${row("toast", ic.fork, "Fork from here")}${row("toast", ic.pencil, "Rename")}${row("toast", ic.laptop, "Open on my computer")}</div>
      <div class="group">${row("archive", ic.archive, "Archive", "", "row--danger")}</div>
    </div>`, { floating: true });
}
function plusSheet(id) {
  const row = (act, icon, label, value = "") => `<button class="row" data-act="${act}" data-id="${id}" style="--sep-left:56px"><span class="row__icon row__icon--plain">${icon}</span><div class="row__main"><div class="row__title">${label}</div></div>${value ? `<span class="row__value">${value}</span>` : ""}</button>`;
  sheet(`<div class="sheet__body" style="padding-bottom:14px">
      <div class="group">${row("toast", ic.image, "Photo or screenshot")}${row("toast", ic.camera, "Camera")}${row("toast", ic.file, "File from the folder")}</div>
      <div class="group">${row("terminal", ic.terminal, "Run a command", "!")}${row("toast", ic.side, "Side question", "/btw")}</div></div>`, { floating: true });
}
function terminalSheet(id) {
  const sh = sheet(`
    <div class="sheet__head"><h2>Run a command</h2><span class="navbar__spacer"></span>${closeBtn}</div>
    <div class="sheet__body" style="padding-bottom:16px"><div class="sheet__pad">
      <div class="composer glass-field term"><span class="term__bang">!</span><textarea rows="1" placeholder="git log --oneline -3" aria-label="Command" autocapitalize="off" autocorrect="off" spellcheck="false" enterkeyhint="go"></textarea><button class="send" data-act="term-send" data-id="${id}" disabled aria-label="Run">${ic.up}</button></div>
      <p class="group-f" style="margin:8px 4px 0">Runs in the session's folder, with no model turn. The output shows in the conversation.</p>
    </div>
    <div class="group" style="margin-top:18px"><button class="row" data-act="toast" data-text="Sent a link to hangar"><div class="row__main"><div class="row__title">Open a terminal on hangar</div><div class="row__sub">Sends a link to the computer</div></div>${ic.chevR}</button></div>
    </div>`);
  const ta = sh.el.querySelector("textarea");
  autogrow(ta, (v) => (sh.el.querySelector(".send").disabled = !v.trim()));
  ta.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); sh.el.querySelector(".send").click(); } });
  setTimeout(() => ta.focus({ preventScroll: true }), 0);
}
function stepsSheet() {
  const rows = [["Read", "tests/WeaveFleet.E2E/SignalRTransportTests.cs"], ["Read", "src/WeaveFleet.Api/Hubs/SessionHub.cs"], ["Searched", "WaitForReconnect(", "6 matches"], ["Edited", "tests/WeaveFleet.E2E/TestHarness.cs", "+1 −1"],
    ["Delegated", "shuttle: other tests with the same race"], ["Ran", "dotnet test --filter SignalRTransportTests", "40 passed"], ["Read", "client/src/composables/use-signalr-socket.ts"], ["Searched", "withAutomaticReconnect", "2 matches"], ["Read", ".github/workflows/ci.yml"], ["Ran", CMD, "waiting"]];
  sheet(`<div class="sheet__head"><h2>Steps</h2><span class="navbar__spacer"></span>${closeBtn}</div>
    <div class="sheet__body"><div class="group">${rows.map(([v, t, n]) => `<button class="row" data-act="toast" data-text="Opens the step's detail"><div class="row__main"><div class="row__sub" style="margin:0 0 2px;font-size:var(--t-foot)">${v}${n ? ` · ${n}` : ""}</div><div class="row__title mono" style="font-size:.85rem">${t}</div></div></button>`).join("")}</div></div>`,
  { detents: ["medium", "large"], initial: "medium" });
}
function changesSheet() {
  const files = [["tests/WeaveFleet.E2E/TestHarness.cs", 1, 1], ["tests/WeaveFleet.E2E/SignalRTransportTests.cs", 4, 2], ["docs/testing.md", 3, 0]];
  sheet(`<div class="sheet__head"><h2>Changes</h2><span class="navbar__spacer"></span>${closeBtn}</div>
    <div class="sheet__body"><div class="group">${files.map(([f, a, d]) => `<button class="row" data-act="toast" data-text="Opens the diff"><div class="row__main"><div class="row__title mono" style="font-size:.85rem">${f.split("/").pop()}</div><div class="row__sub">${f.split("/").slice(0, -1).join("/")}</div></div><span class="mono" style="font-size:.8rem"><span style="color:var(--running)">+${a}</span> <span style="color:var(--error)">−${d}</span></span>${ic.chevR}</button>`).join("")}</div>
    <div class="group-f">3 files changed in this session's worktree.</div></div>`, { detents: ["medium", "large"], initial: "medium" });
}

// ── New session ──────────────────────────────────────────────────────
const NS = { machine: "hangar", folder: "weave-fleet", where: "New worktree", harness: "OpenCode 2", agent: "Default", model: "Default", loading: true };
const pickers = {
  machine: ["Machine", [["hangar", "Linux · this phone's home"], ["falcon", "macOS"]]],
  folder: ["Folder", [["weave-fleet", "~/src/weave-fleet"], ["weave", "~/src/weave"], ["tryweave.io", "~/src/tryweave.io"], ["dotfiles", "~/dotfiles"]]],
  where: ["Where", [["New worktree", "Its own branch and folder, so it can't trip over other sessions"], ["This folder", "Works straight in weave-fleet, on the current branch"]]],
  harness: ["Harness", [["OpenCode 2", ""], ["OpenCode", ""], ["Claude Code", ""], ["Pi", ""]]],
  agent: ["Agent", [["Default", "The harness's own default"], ["Loom", "Plans, then delegates"], ["Tapestry", "Runs a plan step by step"], ["Shuttle", "Quick, focused changes"]]],
  model: ["Model", [["Default", "claude-sonnet-5"], ["claude-opus-5-5", ""], ["gpt-5.5", ""], ["gemini-3-pro", ""]]],
};
function newSessionSheet({ focus = true } = {}) {
  NS.loading = true;
  const topStart = prefs.start === "top";
  const nsRow = (k, last) => `<button class="row" data-act="pick" data-key="${k}"><div class="row__main"><div class="row__title">${pickers[k][0]}</div></div><span class="row__value" data-ns="${k}">${(k === "agent" || k === "model") && NS.loading ? '<span class="sk" style="width:64px"></span>' : NS[k]}</span>${ic.chevR}</button>`;
  const sh = sheet(`
    <div class="sheet-pages"><div class="sheet-page">
      <div class="sheet__head"><button class="navbtn glass navbtn--text" data-act="sheet-close">Cancel</button><h2>New session</h2><span class="navbar__spacer"></span>${topStart ? `<button class="navbtn navbtn--text navbtn--primary" data-act="start" disabled>Start</button>` : ""}</div>
      <div class="sheet__body">
        <div class="sheet__pad"><div class="prompt-card"><textarea data-prompt rows="4" placeholder="What should the agent do?" aria-label="What should the agent do?" enterkeyhint="enter"></textarea></div></div>
        <div class="group-h">Where it runs</div>
        <div class="group">${["machine", "folder", "where"].map((k) => nsRow(k)).join("")}</div>
        <div class="group-h">Who does it</div>
        <div class="group">${["harness", "agent", "model"].map((k) => nsRow(k)).join("")}</div>
        <div class="group-f" data-ns-foot>Agent and model come from ${NS.harness} on ${NS.machine}.</div>
      </div>
      ${topStart ? "" : `<div class="sheet__foot"><button class="btn btn--primary btn--big" data-act="start" disabled>${ic.up}<span data-start-label>Start on ${NS.machine}</span></button></div>`}
    </div></div>`, { detents: ["large"], recess: true });
  const ta = sh.el.querySelector("[data-prompt]");
  autogrow(ta, (v) => sh.el.querySelectorAll('[data-act="start"]').forEach((b) => (b.disabled = !v.trim())));
  ta.style.minHeight = 22 * 4 + 18 + "px";
  if (focus) ta.focus({ preventScroll: true }); // inside the tap, so iOS raises the keyboard with the sheet
  loadAgentModel(sh.el);
  sh.pages = sheetPages(sh.el.querySelector(".sheet-pages"));
  return sh;
}
function loadAgentModel(el, ms = 750) {
  NS.loading = true;
  el.querySelectorAll('[data-ns="agent"],[data-ns="model"]').forEach((n) => (n.innerHTML = '<span class="sk" style="width:64px"></span>'));
  setTimeout(() => { NS.loading = false; el.querySelectorAll('[data-ns="agent"],[data-ns="model"]').forEach((n) => { n.textContent = NS[n.dataset.ns]; n.classList.add("fade-in"); }); }, ms);
}
function pickerPage(sh, key) {
  const [title, opts] = pickers[key];
  const page = h(`<div>
    <div class="sheet__head"><button class="navbtn glass" data-act="picker-back" aria-label="Back">${prefs.platform === "android" ? ic.arrowLeft : ic.back}</button><h2>${title}</h2></div>
    <div class="sheet__body">
      ${key === "folder" ? `<div class="sheet__pad" style="margin-bottom:14px"><label class="search glass-field">${ic.search}<input type="search" placeholder="Search folders on ${NS.machine}"></label></div>` : ""}
      <div class="group">${opts.map(([v, d]) => `<button class="row" data-act="picked" data-key="${key}" data-val="${v}"><div class="row__main"><div class="row__title">${v}</div>${d ? `<div class="row__sub" style="white-space:normal">${d}</div>` : ""}</div>${NS[key] === v ? `<span class="row__check">${ic.check}</span>` : '<span style="width:24px"></span>'}</button>`).join("")}</div>
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
  S[id] = { title: prompt.length > 42 ? prompt.slice(0, 40) + "…" : prompt, machine: NS.machine, state: "starting", at: Date.now(), since: Date.now(), convo: [{ t: "user", text: prompt }] };
  await sh.close();
  rerenderLists();
  openSession(id);
  setTimeout(() => { S[id].state = "working"; S[id].since = Date.now(); refreshSession(id); rerenderLists(); }, 1500);
}

// ── Notification setup ───────────────────────────────────────────────
const notif = { on: false, kinds: { needs: true, questions: true, finished: true, failed: true }, quiet: true };
function notifySheet({ on } = {}) {
  if (on !== undefined) notif.on = on;
  const sh = sheet(`<div class="sheet__head"><h2>Notifications</h2><span class="navbar__spacer"></span><button class="navbtn glass navbtn--text navbtn--accent" data-act="sheet-close">Done</button></div><div class="sheet__body" data-notify-body></div>`, { detents: ["large"], recess: true });
  renderNotify(sh.el.querySelector("[data-notify-body]"));
}
function renderNotify(body) {
  const needsHomeScreen = prefs.platform === "ios" && !isStandalone;
  if (!notif.on) {
    body.innerHTML = `<div class="sheet__pad notify-hero">
        <img src="icons/apple-touch-icon.png" alt="" width="72" height="72">
        <h3>Know when an agent needs you</h3>
        <p>Fleet taps you on the shoulder when a command waits for your approval, an agent asks a question, or a session finishes.</p>
      </div>
      ${needsHomeScreen ? `<div class="group-h">On iPhone, first add Fleet to your Home Screen</div>
      <div class="group">
        <div class="row" style="--sep-left:56px"><span class="row__icon row__icon--plain">${ic.share}</span><div class="row__main"><div class="row__title row__title--wrap">Tap Share in Safari</div></div></div>
        <div class="row" style="--sep-left:56px"><span class="row__icon row__icon--plain">${ic.plus}</span><div class="row__main"><div class="row__title row__title--wrap">Choose Add to Home Screen</div></div></div>
        <div class="row" style="--sep-left:56px"><span class="row__icon row__icon--plain"><img src="icons/apple-touch-icon.png" alt="" width="24" height="24" style="border-radius:6px"></span><div class="row__main"><div class="row__title row__title--wrap">Open Fleet from the Home Screen</div></div></div>
      </div><div class="group-f">iOS only lets web apps send notifications once they're on the Home Screen.</div>` : ""}
      <div class="sheet__pad" style="margin-top:24px"><button class="btn btn--primary btn--big" data-act="notify-on">Turn on notifications</button></div>`;
    return;
  }
  const t = (k, title, sub) => `<button class="row" data-act="toggle" data-k="${k}"><div class="row__main"><div class="row__title">${title}</div><div class="row__sub" style="white-space:normal">${sub}</div></div><span class="switch ${(k === "quiet" ? notif.quiet : notif.kinds[k]) ? "is-on" : ""}" role="switch"></span></button>`;
  body.innerHTML = `
    <div class="group fade-in"><div class="row"><span class="row__icon" style="background:var(--running)">${ic.check}</span><div class="row__main"><div class="row__title">On for this phone</div><div class="row__sub">${prefs.platform === "android" ? "Android · Chrome" : "iPhone · Home Screen app"}</div></div></div></div>
    <div class="group-h">Tell me when</div>
    <div class="group fade-in">${t("needs", "Something needs me", "A command or edit waits for my approval")}${t("questions", "An agent asks", "A question I need to answer")}${t("finished", "A session finishes", "Its turn ended")}${t("failed", "A session fails", "It stopped with an error")}</div>
    <div class="group fade-in" style="margin-top:22px">${t("quiet", "Quiet at my desk", "Skip the phone while Fleet is open on a computer")}</div>
    <div class="group" style="margin-top:22px"><button class="row row--accent" data-act="notify-test"><div class="row__main"><div class="row__title">Send a test notification</div></div></button></div>
    <div class="group-f" data-test-note>It arrives like a real one, so you can see how it looks.</div>`;
}

// ── Actions (one delegated click handler) ────────────────────────────
const actions = {
  back: () => closeTop(),
  "sheet-close": () => activeSheet?.close(),
  open: (el) => openSession(el.dataset.id),
  allow: (el) => allow(el.dataset.id, el.closest(".sheet") ? null : el),
  "allow-always": (el) => { toast("Won't ask again for dotnet test *"); activeSheet?.close(); allow(el.dataset.id); },
  answer: (el) => answer(el.dataset.id, el.dataset.answer, el.closest(".sheet") ? null : el),
  "answer-own": (el) => { const v = el.closest(".composer").querySelector("textarea").value.trim(); activeSheet?.close(); answer(el.dataset.id, v); },
  "ask-more": (el) => askSheet(el.dataset.id),
  "deny-open": (el) => { const box = el.closest(".sheet").querySelector(".deny-box"); box.hidden = false; activeSheet?.expand(); box.querySelector("textarea").focus({ preventScroll: true }); setTimeout(() => box.scrollIntoView({ block: "nearest", behavior: "smooth" }), 350); },
  "deny-send": (el) => { const v = el.closest(".composer").querySelector("textarea").value.trim(); activeSheet?.close(); answer(el.dataset.id, `Denied: ${v}`); },
  later: (el) => {
    const d = el.closest(".docked-ask");
    const id = el.closest("[data-session]").dataset.session;
    d.classList.add("is-gone");
    const pill = h(`<button class="waiting-pill glass pressable" data-act="unlater"><span class="dot dot--waiting"></span>1 waiting · Review</button>`);
    setTimeout(() => { d.parentElement.prepend(pill); pill.classList.add("fade-in"); }, 250);
  },
  unlater: (el) => { const p = el.parentElement; el.remove(); p.querySelector(".docked-ask")?.classList.remove("is-gone"); },
  send: (el) => sendMessage(el.dataset.id),
  menu: (el) => menuSheet(el.dataset.id),
  plus: (el) => plusSheet(el.closest("[data-session]").dataset.session),
  steps: () => stepsSheet(),
  changes: () => { activeSheet?.close(); setTimeout(changesSheet, 260); },
  terminal: (el) => { const id = el.dataset.id; activeSheet?.close(); setTimeout(() => terminalSheet(id), 260); },
  "term-send": (el) => { const ta = el.closest(".composer").querySelector("textarea"); const v = ta.value.trim(); const id = el.dataset.id; activeSheet?.close(); appendConvo(id, { t: "step", text: `! ${v}`, act: "steps" }); },
  archive: (el) => { const id = el.dataset.id; activeSheet?.close(); setTimeout(() => { archived.add(id); rerenderLists(); closeTop(); toast("Archived", { label: "Undo", onTap: () => { archived.delete(id); rerenderLists(); } }); }, 280); },
  new: () => newSessionSheet(),
  pick: (el) => activeSheet?.pages && pickerPage(activeSheet, el.dataset.key),
  "picker-back": () => activeSheet?.pages.pop(),
  picked: (el) => {
    const k = el.dataset.key, v = el.dataset.val;
    haptic("light");
    NS[k] = v;
    el.closest(".group").querySelectorAll(".row").forEach((r) => (r.querySelector(".row__check, span:last-child").outerHTML = r === el ? `<span class="row__check">${ic.check}</span>` : '<span style="width:24px"></span>'));
    const root = activeSheet.el;
    root.querySelector(`[data-ns="${k}"]`).textContent = v;
    root.querySelector("[data-start-label]") && (root.querySelector("[data-start-label]").textContent = `Start on ${NS.machine}`);
    root.querySelector("[data-ns-foot]").textContent = `Agent and model come from ${NS.harness} on ${NS.machine}.`;
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
    case "ask-deny": askSheet("h1"); setTimeout(() => document.querySelector('[data-act="deny-open"]').click(), 600); break;
    case "question": askSheet("f1"); break;
    case "session": await openSession("h1", { animate: false }); break;
    case "session-question": await openSession("f1", { animate: false }); break;
    case "session-working": await openSession("h3", { animate: false }); break;
    case "session-finished": await openSession("h2", { animate: false }); break;
    case "menu": await openSession("h2", { animate: false }); menuSheet("h2"); break;
    case "terminal": await openSession("h2", { animate: false }); terminalSheet("h2"); break;
    case "steps": await openSession("h1", { animate: false }); stepsSheet(); break;
    case "new": newSessionSheet({ focus: false }); break;
    case "new-filled": { const sh = newSessionSheet({ focus: false }); const ta = sh.el.querySelector("[data-prompt]"); ta.value = "The reconnect test fails about 1 in 10 runs on CI. Find out why and fix it."; ta.dispatchEvent(new Event("input")); break; }
    case "new-picker": { const sh = newSessionSheet({ focus: false }); setTimeout(() => pickerPage(sh, "where"), 500); break; }
    case "notify": notifySheet({ on: false }); break;
    case "notify-on": notifySheet({ on: true }); break;
    case "banner": setTimeout(() => actions["notify-test"]({ closest: () => ({ querySelector: () => ({}) }) }), 0); break;
  }
  const kb = +params.get("kb");
  if (kb) window.__fakeKeyboard(kb);
})();
window.__flow = { S, dur, switchTab, openSession };
