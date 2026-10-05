// Fleet phone mockups: the native-feel kit. Plain JS, no build step.
// Everything here is a candidate for client/src/components/phone once a direction is approved.

const root = document.documentElement;
const qs = new URLSearchParams(location.search);
const store = (k, d) => qs.get(k) ?? localStorage.getItem("fleet-mock-" + k) ?? d;

export const isIOSWebKit = /iP(hone|ad|od)/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
export const isStandalone = matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
export const reducedMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
export const prefs = {};

// ── Theme, platform, font ────────────────────────────────────────────
export function setupEnv() {
  const themePref = store("theme", "auto");
  const platPref = store("platform", "auto");
  prefs.theme = themePref;
  prefs.platform = platPref === "auto" ? (/Android/i.test(navigator.userAgent) ? "android" : "ios") : platPref;
  prefs.font = store("font", "system");
  prefs.start = store("start", "bottom");
  const mq = matchMedia("(prefers-color-scheme: dark)");
  const apply = () => {
    const t = themePref === "auto" ? (mq.matches ? "dark" : "light") : themePref;
    root.dataset.theme = t;
    setStatusColor();
  };
  mq.addEventListener?.("change", apply);
  apply();
  root.dataset.platform = prefs.platform;
  root.dataset.font = prefs.font;
  if (isStandalone) root.classList.add("standalone");
  // screenshots only: desktop Chromium has no notch, so ?safe=59,34 stands in for an iPhone's safe areas
  const safe = qs.get("safe");
  if (safe) {
    const [t, b] = safe.split(",").map(Number);
    root.style.setProperty("--safe-top", t + "px");
    root.style.setProperty("--safe-bottom", b + "px");
    if (t > 0) addEventListener("DOMContentLoaded", () => {
      const s = document.createElement("div");
      s.className = "fake-status";
      s.style.height = t + "px";
      s.innerHTML = '<span>9:41</span><span class="fake-status__isl"></span><span>●●● ▮</span>';
      document.body.appendChild(s);
    });
  }
}

let recessed = 0;
export function setStatusColor() {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (!meta) return;
  const bg = getComputedStyle(root).getPropertyValue("--main-bg").trim();
  meta.content = recessed > 0.5 ? "#000000" : bg;
}

// ── Keyboard: the app frame follows the visual viewport ─────────────
export function setupViewport() {
  const vv = window.visualViewport;
  const app = document.getElementById("app");
  let fake = 0;
  const update = () => {
    const h = fake ? window.innerHeight - fake : vv ? vv.height : window.innerHeight;
    const top = fake ? 0 : vv ? vv.offsetTop : 0;
    const kb = fake || Math.max(0, window.innerHeight - h);
    root.style.setProperty("--vvh", h + "px");
    root.style.setProperty("--vvtop", top + "px");
    root.style.setProperty("--kb", kb + "px");
    root.classList.toggle("kb-open", kb > 80);
    // the large-title layout can't scroll the page itself; undo any page scroll iOS makes
    if (!fake && window.scrollY) window.scrollTo(0, 0);
    listeners.forEach((f) => f(h));
  };
  const listeners = new Set();
  vv?.addEventListener("resize", update);
  vv?.addEventListener("scroll", update);
  window.addEventListener("resize", update);
  update();
  // screenshots only: a fake keyboard of a given height, as a real one would shrink the visual viewport
  window.__fakeKeyboard = (h) => {
    fake = h;
    let el = document.querySelector(".fake-kb");
    if (h && !el) { el = document.createElement("div"); el.className = "fake-kb"; el.textContent = "KEYBOARD"; document.body.appendChild(el); }
    if (!h && el) el.remove();
    update();
  };
  return { onResize: (f) => listeners.add(f), height: () => (fake ? window.innerHeight - fake : vv ? vv.height : window.innerHeight) };
}

// ── Haptics ──────────────────────────────────────────────────────────
// Android: the Vibration API. iOS has none; Safari 18+ ticks when a native <input switch> toggles, so a hidden one is
// clicked from inside the user's gesture. That is a hack, not an API, and may stop working.
let iosSwitch;
export function haptic(kind = "light") {
  if (navigator.vibrate) { navigator.vibrate(kind === "success" ? [12, 60, 18] : kind === "heavy" ? 22 : 9); return; }
  if (!isIOSWebKit) return;
  if (!iosSwitch) {
    iosSwitch = document.createElement("label");
    iosSwitch.style.cssText = "position:fixed;left:-100px;top:0;opacity:0;pointer-events:none";
    iosSwitch.innerHTML = '<input type="checkbox" switch>';
    document.body.appendChild(iosSwitch);
  }
  iosSwitch.click();
}

// ── Pressed states: instant on buttons, slightly delayed on list rows (so a scroll doesn't flash them) ──
const PRESS = ".btn,.navbtn,.tab,.row,.step,.send,.composer__plus,.link-btn,.ask__open,.fab,.pressable";
const DELAYED = ".row,.step,.ask__open";
export function setupPress() {
  let cur = null, timer = 0, sx = 0, sy = 0, addedAt = 0, ripple = null;
  const add = (el, x, y) => {
    el.classList.add("is-pressed"); addedAt = performance.now();
    if (root.dataset.platform === "android" && getComputedStyle(el).position !== "static") {
      const r = el.getBoundingClientRect();
      const size = Math.hypot(Math.max(x - r.left, r.right - x), Math.max(y - r.top, r.bottom - y)) * 2;
      ripple = document.createElement("span"); ripple.className = "ripple";
      Object.assign(ripple.style, { width: size + "px", height: size + "px", left: x - r.left - size / 2 + "px", top: y - r.top - size / 2 + "px" });
      if (getComputedStyle(el).overflow === "visible") el.style.overflow = "hidden";
      el.appendChild(ripple);
    }
  };
  const release = (quick) => {
    clearTimeout(timer);
    const el = cur; cur = null;
    if (!el) return;
    if (quick && !el.classList.contains("is-pressed")) add(el, sx, sy);
    const rp = ripple; ripple = null;
    const wait = Math.max(0, 110 - (performance.now() - addedAt));
    setTimeout(() => { el.classList.remove("is-pressed"); if (rp) { rp.classList.add("is-out"); setTimeout(() => rp.remove(), 500); } }, wait);
  };
  const start = (e, x, y) => {
    const el = e.target.closest(PRESS);
    if (!el || el.disabled) return;
    cur = el; sx = x; sy = y;
    if (el.matches(DELAYED)) timer = setTimeout(() => cur === el && add(el, x, y), 70);
    else add(el, x, y);
  };
  document.addEventListener("touchstart", (e) => start(e, e.touches[0].clientX, e.touches[0].clientY), { passive: true });
  document.addEventListener("touchmove", (e) => {
    if (!cur) return;
    const t = e.touches[0];
    if (Math.hypot(t.clientX - sx, t.clientY - sy) > 10) { clearTimeout(timer); const el = cur; cur = null; el.classList.remove("is-pressed"); ripple?.remove(); ripple = null; }
  }, { passive: true });
  document.addEventListener("touchend", () => release(true), { passive: true });
  document.addEventListener("touchcancel", () => release(false), { passive: true });
  document.addEventListener("pointerdown", (e) => { if (e.pointerType === "mouse") start(e, e.clientX, e.clientY); });
  document.addEventListener("pointerup", (e) => { if (e.pointerType === "mouse") release(true); });
}

// ── Small animation helper ───────────────────────────────────────────
export function animateTo(el, styles, ms, ease = "var(--ease)") {
  return new Promise((res) => {
    if (reducedMotion()) ms = Math.min(ms, 120);
    el.style.transition = Object.keys(styles).map((k) => `${k.replace(/[A-Z]/g, (m) => "-" + m.toLowerCase())} ${ms}ms ${ease}`).join(",");
    void el.offsetWidth;
    Object.assign(el.style, styles);
    setTimeout(() => { el.style.transition = ""; res(); }, ms + 20);
  });
}
const rubber = (d, dim, c = 0.55) => (1 - 1 / ((d * c) / dim + 1)) * dim;
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

// ── History: Android's back gesture and the browser back button close the top layer ──
const layers = [];
let ignorePops = 0;
let lastEdgeTouch = 0;
function pushLayer(layer) { layers.push(layer); history.pushState({ d: layers.length }, ""); }
export function closeTop(opts = {}) {
  const layer = layers.pop();
  if (!layer) return;
  ignorePops++;
  history.back();
  return layer.close(opts);
}
window.addEventListener("popstate", (e) => {
  if (ignorePops > 0) { ignorePops--; return; }
  const d = e.state?.d ?? 0;
  // iOS Safari draws its own edge swipe; don't animate a second time after it
  const animate = !(isIOSWebKit && !isStandalone && performance.now() - lastEdgeTouch < 1200);
  while (layers.length > d) layers.pop().close({ animate });
});
document.addEventListener("touchstart", (e) => { if (e.touches[0].clientX < 24 || e.touches[0].clientX > innerWidth - 24) lastEdgeTouch = performance.now(); }, { passive: true });
export const layerCount = () => layers.length;

// ── Navigation stack: push / pop with parallax, swipe back from anywhere on the page (iOS 26) ──
export function createNav(stage, rootScreen) {
  const stack = [rootScreen];
  const PARALLAX = -0.3, DIM = 0.14;
  const dimOf = (s) => s.querySelector(":scope > .screen__dim") || s.appendChild(Object.assign(document.createElement("div"), { className: "screen__dim" }));

  async function push(screen, { animate = true } = {}) {
    const prev = stack[stack.length - 1];
    screen.classList.add("screen", "is-pushed");
    stage.appendChild(screen);
    stack.push(screen);
    attachSwipeBack(screen);
    pushLayer({ close: (o) => pop(o) });
    if (!animate) { prev.classList.add("is-hidden"); return; }
    if (reducedMotion()) {
      screen.style.opacity = 0;
      await animateTo(screen, { opacity: 1 }, 160, "ease-out");
      prev.classList.add("is-hidden");
      return;
    }
    screen.style.transform = "translateX(100%)";
    const d = dimOf(prev);
    await Promise.all([
      animateTo(screen, { transform: "translateX(0)" }, 440),
      animateTo(prev, { transform: `translateX(${PARALLAX * 100}%)` }, 440),
      animateTo(d, { opacity: DIM }, 440),
    ]);
    prev.classList.add("is-hidden");
  }

  async function pop({ animate = true, fromX = 0, velocity = 0 } = {}) {
    if (stack.length < 2) return;
    const top = stack.pop();
    const prev = stack[stack.length - 1];
    prev.classList.remove("is-hidden");
    const d = dimOf(prev);
    if (!animate) { top.remove(); prev.style.transform = ""; d.style.opacity = 0; return; }
    if (reducedMotion()) { await animateTo(top, { opacity: 0 }, 140, "ease-out"); top.remove(); prev.style.transform = ""; d.style.opacity = 0; return; }
    const w = stage.clientWidth;
    const remaining = w - fromX;
    const ms = clamp(velocity > 0 ? remaining / velocity : 380, 160, 380);
    await Promise.all([
      animateTo(top, { transform: `translateX(${w}px)` }, ms, "var(--ease-out)"),
      animateTo(prev, { transform: "translateX(0)" }, ms, "var(--ease-out)"),
      animateTo(d, { opacity: 0 }, ms, "var(--ease-out)"),
    ]);
    top.remove();
    prev.style.transform = "";
  }

  function attachSwipeBack(screen) {
    let sx = 0, sy = 0, dx = 0, mode = null, samples = [];
    screen.addEventListener("touchstart", (e) => {
      mode = null; dx = 0;
      if (root.dataset.platform !== "ios" || stack[stack.length - 1] !== screen) return;
      const t = e.touches[0];
      // leave the very edge to Safari when it's running as a browser tab
      if (!isStandalone && isIOSWebKit && t.clientX < 22) return;
      if (e.target.closest("textarea,input,.no-swipe-back")) return;
      sx = t.clientX; sy = t.clientY; mode = "maybe"; samples = [[performance.now(), sx]];
    }, { passive: true });
    screen.addEventListener("touchmove", (e) => {
      if (!mode || mode === "no") return;
      const t = e.touches[0];
      const mx = t.clientX - sx, my = t.clientY - sy;
      if (mode === "maybe") {
        if (Math.abs(mx) < 8 && Math.abs(my) < 8) return;
        mode = mx > 0 && Math.abs(mx) > Math.abs(my) * 1.4 ? "drag" : "no";
        if (mode === "no") return;
        sx = t.clientX; // start from here so the page doesn't jump
      }
      e.preventDefault();
      dx = Math.max(0, t.clientX - sx);
      samples.push([performance.now(), t.clientX]); if (samples.length > 5) samples.shift();
      const w = stage.clientWidth, p = dx / w;
      const prev = stack[stack.length - 2];
      prev.classList.remove("is-hidden");
      screen.style.transform = `translateX(${dx}px)`;
      prev.style.transform = `translateX(${PARALLAX * 100 * (1 - p)}%)`;
      dimOf(prev).style.opacity = DIM * (1 - p);
    }, { passive: false });
    const end = () => {
      if (mode !== "drag") { mode = null; return; }
      mode = null;
      const [t0, x0] = samples[0], [t1, x1] = samples[samples.length - 1];
      const v = (x1 - x0) / Math.max(1, t1 - t0); // px per ms
      const w = stage.clientWidth;
      if ((v > 0.35 || dx > w * 0.45) && v > -0.15) {
        haptic("light");
        layers.pop(); ignorePops++; history.back();
        pop({ fromX: dx, velocity: Math.max(v, 1.2) });
      } else {
        const prev = stack[stack.length - 2];
        animateTo(screen, { transform: "translateX(0)" }, 300);
        animateTo(prev, { transform: `translateX(${PARALLAX * 100}%)` }, 300);
        animateTo(dimOf(prev), { opacity: DIM }, 300).then(() => prev.classList.add("is-hidden"));
      }
    };
    screen.addEventListener("touchend", end);
    screen.addEventListener("touchcancel", end);
  }

  return { push, back: () => closeTop(), top: () => stack[stack.length - 1], depth: () => stack.length };
}

// ── Sheets with detents ──────────────────────────────────────────────
// detents: ["fit"] (sized to content) or any of ["medium", "large"]. Drag to resize or dismiss; a flick counts.
export function openSheet({ el, detents = ["fit"], initial, recess: recessWanted = false, floating = false, onClose, viewport, history: useHistory = true }) {
  // the card-stack recess behind a sheet is an iOS idiom; Material sheets just dim
  const recess = recessWanted && root.dataset.platform === "ios";
  const overlays = document.getElementById("overlays");
  const stage = document.getElementById("stage");
  const layer = document.createElement("div");
  layer.className = "sheet-layer";
  const backdrop = document.createElement("div");
  backdrop.className = "sheet-backdrop";
  el.classList.add("sheet");
  if (floating && root.dataset.platform === "ios") el.classList.add("sheet--floating");
  if (!el.querySelector(".sheet__grabber")) el.insertAdjacentHTML("afterbegin", '<div class="sheet__grabber" aria-hidden="true"></div>');
  layer.append(backdrop, el);
  overlays.appendChild(layer);

  let H = 0, offsets = {}, cur = 0, closed = false;
  const body = () => el.querySelector(".sheet-page:last-child .sheet__body") || el.querySelector(".sheet__body");
  const appH = () => document.getElementById("app").clientHeight;
  const topGap = () => (parseFloat(getComputedStyle(root).getPropertyValue("--safe-top")) || 0) + (recess ? 14 : 10);
  const floatGap = () => (el.classList.contains("sheet--floating") ? parseFloat(getComputedStyle(el).bottom) || 8 : 0);

  function layout() {
    const max = appH() - topGap() - floatGap();
    if (detents.includes("fit")) {
      el.style.height = "auto";
      el.style.maxHeight = max + "px";
      H = el.offsetHeight;
      offsets = { fit: 0 };
    } else {
      H = max;
      el.style.height = H + "px";
      el.style.maxHeight = "";
      offsets = { large: 0, medium: Math.round(H - appH() * 0.52) };
    }
  }
  const dismissAt = () => H + floatGap() + 10;
  const sorted = () => detents.map((d) => offsets[d]).sort((a, b) => a - b);
  const lowest = () => Math.max(...sorted());

  function paint(y) {
    cur = y;
    el.style.transform = `translateY(${y}px)`;
    const lo = lowest();
    const o = y <= lo ? 1 : clamp(1 - (y - lo) / (dismissAt() - lo), 0, 1);
    backdrop.style.opacity = o;
    if (recess) {
      const r = clamp(1 - y / H, 0, 1);
      const safeTop = parseFloat(getComputedStyle(root).getPropertyValue("--safe-top")) || 0;
      stage.style.transform = `translateY(${(safeTop + 6) * r}px) scale(${1 - 0.07 * r})`;
      stage.style.borderRadius = `${14 * r}px`;
      stage.style.transformOrigin = "50% 0";
      recessed = r; setStatusColor();
    }
    const b = el.querySelector(".sheet__body");
    if (b) el.classList.toggle("is-below-top", y > sorted()[0] + 1);
  }
  async function snap(y, ms = 420, ease = "var(--ease)") {
    if (reducedMotion()) ms = 140;
    el.style.transition = `transform ${ms}ms ${ease}`;
    backdrop.style.transition = `opacity ${ms}ms ${ease}`;
    if (recess) stage.style.transition = `transform ${ms}ms ${ease}, border-radius ${ms}ms ${ease}`;
    paint(y);
    await new Promise((r) => setTimeout(r, ms + 10));
    el.style.transition = backdrop.style.transition = "";
    if (recess) stage.style.transition = "";
  }

  layout();
  paint(dismissAt());
  void el.offsetWidth;
  const startAt = offsets[initial ?? detents[0]];
  snap(startAt, 480);

  // drag handling
  let sy = 0, startY = 0, dragging = false, decided = false, samples = [];
  el.addEventListener("touchstart", (e) => {
    if (e.target.closest("input,textarea")) { decided = true; dragging = false; return; }
    const t = e.touches[0];
    sy = t.clientY; startY = cur; decided = false; dragging = false; samples = [[performance.now(), sy]];
    el.style.transition = "";
  }, { passive: true });
  el.addEventListener("touchmove", (e) => {
    const t = e.touches[0];
    const dy = t.clientY - sy;
    if (!decided) {
      if (dy === 0) return;
      decided = true;
      const sc = e.target.closest(".sheet__body");
      const atTop = cur <= sorted()[0] + 1;
      if (!sc || !atTop) dragging = true;
      else dragging = dy > 0 && sc.scrollTop <= 0;
      if (e.target.closest(".swipe-x")) dragging = false;
    }
    if (!dragging) return;
    e.preventDefault();
    samples.push([performance.now(), t.clientY]); if (samples.length > 5) samples.shift();
    let y = startY + dy;
    const top = sorted()[0];
    if (y < top) y = top - rubber(top - y, appH() * 0.4);
    paint(y);
  }, { passive: false });
  const end = () => {
    if (!dragging) return;
    dragging = false;
    const [t0, y0] = samples[0], [t1, y1] = samples[samples.length - 1];
    const v = (y1 - y0) / Math.max(1, t1 - t0);
    const projected = cur + v * 220;
    const lo = lowest();
    if (projected > lo + (dismissAt() - lo) * 0.5 || (v > 0.9 && cur > lo - 40)) { haptic("light"); api.close(); return; }
    let best = sorted()[0], bd = Infinity;
    for (const o of sorted()) { const d = Math.abs(o - projected); if (d < bd) { bd = d; best = o; } }
    if (best !== startY) haptic("light");
    snap(best, clamp(Math.abs(best - cur) / Math.max(Math.abs(v), 1.2), 220, 420));
  };
  el.addEventListener("touchend", end);
  el.addEventListener("touchcancel", end);
  backdrop.addEventListener("click", () => api.close());

  viewport?.onResize(() => { if (closed) return; const wasAt = Object.keys(offsets).find((k) => offsets[k] === cur) ?? detents[0]; layout(); paint(offsets[wasAt] ?? 0); });

  const api = {
    el,
    expand: () => detents.includes("large") && snap(offsets.large),
    relayout: () => { const wasFit = cur === 0; layout(); if (wasFit) paint(0); },
    // closing from the UI goes through history so Android's back stays in step
    close: () => (useHistory && layers[layers.length - 1] === layerRec ? closeTop() : doClose()),
  };
  async function doClose({ animate = true } = {}) {
    if (closed) return; closed = true;
    document.activeElement?.blur?.();
    if (animate) await snap(dismissAt(), 320, "cubic-bezier(.4,0,1,1)");
    layer.remove();
    if (recess) { stage.style.transform = ""; stage.style.borderRadius = ""; recessed = 0; setStatusColor(); }
    onClose?.();
  }
  const layerRec = { close: doClose };
  if (useHistory) pushLayer(layerRec);
  return api;
}

// nested pages inside a sheet (pickers): push from the right, swipe or tap back
export function sheetPages(container) {
  const stack = [...container.querySelectorAll(":scope > .sheet-page")];
  return {
    async push(page) {
      const prev = stack[stack.length - 1];
      page.classList.add("sheet-page");
      page.style.transform = "translateX(100%)";
      container.appendChild(page); stack.push(page);
      await Promise.all([animateTo(page, { transform: "translateX(0)" }, 400), animateTo(prev, { transform: "translateX(-30%)", opacity: "0.6" }, 400)]);
    },
    async pop() {
      if (stack.length < 2) return;
      const top = stack.pop(), prev = stack[stack.length - 1];
      await Promise.all([animateTo(top, { transform: "translateX(100%)" }, 340, "var(--ease-out)"), animateTo(prev, { transform: "translateX(0)", opacity: "1" }, 340, "var(--ease-out)")]);
      top.remove();
    },
  };
}

// ── Large title: collapses into the bar, snaps, stretches on overscroll ──
export function largeTitle(scroller, navbar, titleEl) {
  let touching = false, idle = 0;
  const th = () => titleEl.offsetHeight - 6;
  const on = () => {
    const y = scroller.scrollTop;
    navbar.classList.toggle("is-scrolled", y > th() - 10);
    titleEl.style.transform = y < 0 ? `scale(${1 + Math.min(-y, 120) / 900})` : "";
    clearTimeout(idle);
    idle = setTimeout(() => { if (!touching && y > 0 && y < th()) scroller.scrollTo({ top: y < th() / 2 ? 0 : th(), behavior: "smooth" }); }, 160);
  };
  scroller.addEventListener("scroll", on, { passive: true });
  scroller.addEventListener("touchstart", () => (touching = true), { passive: true });
  scroller.addEventListener("touchend", () => { touching = false; on(); }, { passive: true });
  return { stretch: (p) => (titleEl.style.transform = p > 0 ? `scale(${1 + Math.min(p, 120) / 900})` : "") };
}

// ── Pull to refresh ──────────────────────────────────────────────────
// iOS WebKit: read the native rubber band (negative scrollTop). Elsewhere: drive it from touches.
export function pullToRefresh(scroller, ptrEl, onRefresh, title) {
  const THRESH = 72;
  const ios = root.dataset.platform === "ios";
  ptrEl.innerHTML = ios
    ? `<svg class="ptr__ios" viewBox="0 0 28 28">${Array.from({ length: 8 }, (_, i) => `<line x1="14" y1="3.5" x2="14" y2="8.5" transform="rotate(${i * 45} 14 14)" opacity="${0.25 + (i / 8) * 0.75}"/>`).join("")}</svg>`
    : `<div class="ptr__android"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round"><path d="M20 12a8 8 0 1 1-2.34-5.66"/><path d="M20 4v5h-5"/></svg></div>`;
  const spin = ptrEl.firstElementChild;
  const inner = scroller.querySelector(".scroller__inner");
  let refreshing = false, armed = false, pull = 0;

  const show = (p) => {
    pull = p;
    if (ios) {
      const lines = spin.querySelectorAll("line");
      const n = Math.round(clamp(p / THRESH, 0, 1) * 8);
      lines.forEach((l, i) => (l.style.visibility = refreshing || i < n ? "visible" : "hidden"));
      spin.style.opacity = refreshing ? 1 : clamp(p / 30, 0, 1);
      spin.style.transform = refreshing ? "" : `translateY(${Math.max(0, p - 50) * 0.5}px)`;
    } else {
      spin.style.transform = `translateY(${clamp(p, 0, 130) * 0.9}px) rotate(${p * 3}deg)`;
      spin.style.opacity = clamp(p / 40, 0, 1);
    }
    if (p > THRESH && !armed) { armed = true; haptic("light"); }
    if (p < THRESH - 10) armed = false;
  };
  async function trigger() {
    refreshing = true;
    spin.classList.add("is-spinning");
    show(THRESH);
    if (ios) await animateTo(inner, { transform: "translateY(54px)" }, 260, "var(--ease-out)");
    else spin.style.transform = "translateY(70px)";
    await onRefresh();
    spin.classList.remove("is-spinning");
    if (ios) animateTo(inner, { transform: "translateY(0)" }, 380);
    else await animateTo(spin, { transform: "translateY(0) scale(.2)", opacity: "0" }, 260);
    refreshing = false; armed = false; show(0); spin.style.opacity = 0;
  }

  if (isIOSWebKit) {
    scroller.addEventListener("scroll", () => { if (!refreshing && scroller.scrollTop <= 0) show(-scroller.scrollTop); }, { passive: true });
    scroller.addEventListener("touchend", () => { if (!refreshing && -scroller.scrollTop > THRESH) trigger(); }, { passive: true });
    return;
  }
  let sy = 0, tracking = false, active = false;
  scroller.addEventListener("touchstart", (e) => { if (refreshing) return; tracking = scroller.scrollTop <= 0; active = false; sy = e.touches[0].clientY; }, { passive: true });
  scroller.addEventListener("touchmove", (e) => {
    if (!tracking || refreshing) return;
    const dy = e.touches[0].clientY - sy;
    if (dy <= 0 || scroller.scrollTop > 0) { if (active) { show(0); if (ios) inner.style.transform = ""; } active = false; return; }
    active = true;
    e.preventDefault();
    const p = rubber(dy, 260, 0.9);
    show(p);
    if (ios) { inner.style.transform = `translateY(${p}px)`; title?.stretch(p); }
  }, { passive: false });
  scroller.addEventListener("touchend", () => {
    if (!active) return;
    active = tracking = false;
    title?.stretch(0);
    if (pull > THRESH) trigger();
    else { if (ios) animateTo(inner, { transform: "translateY(0)" }, 300); else animateTo(spin, { transform: "translateY(0)", opacity: "0" }, 200); show(0); }
  });
}

// ── Swipe actions on a row (trailing: Archive) ───────────────────────
let openRow = null;
export function swipeRow(wrap, { onCommit }) {
  const row = wrap.querySelector(":scope > .row");
  const acts = wrap.querySelector(".swipe-actions");
  const ACT = 88;
  let sx = 0, sy = 0, base = 0, x = 0, mode = null, samples = [], past = false;
  const set = (v) => { x = v; row.style.transform = `translateX(${v}px)`; acts.style.opacity = v < 0 ? 1 : 0; const a = acts.firstElementChild; a.style.width = Math.max(ACT, -v) + "px"; };
  const close = () => { animateTo(row, { transform: "translateX(0)" }, 320).then(() => (acts.style.opacity = 0)); x = 0; if (openRow === api) openRow = null; };
  wrap.addEventListener("touchstart", (e) => {
    if (openRow && openRow !== api) openRow.close();
    sx = e.touches[0].clientX; sy = e.touches[0].clientY; base = x; mode = "maybe"; past = false; samples = [[performance.now(), sx]];
  }, { passive: true });
  wrap.addEventListener("touchmove", (e) => {
    if (!mode || mode === "no") return;
    const t = e.touches[0], dx = t.clientX - sx, dy = t.clientY - sy;
    if (mode === "maybe") {
      if (Math.abs(dx) < 8 && Math.abs(dy) < 8) return;
      mode = Math.abs(dx) > Math.abs(dy) * 1.2 && (dx < 0 || base < 0) ? "drag" : "no";
      if (mode === "no") return;
      row.classList.remove("is-pressed");
    }
    e.preventDefault();
    samples.push([performance.now(), t.clientX]); if (samples.length > 5) samples.shift();
    let v = Math.min(0, base + dx);
    const w = wrap.clientWidth;
    const full = -w * 0.6;
    if (v < full !== past) { past = v < full; haptic(past ? "heavy" : "light"); }
    set(v);
  }, { passive: false });
  const end = () => {
    if (mode !== "drag") { mode = null; return; }
    mode = null;
    const [t0, x0] = samples[0], [t1, x1] = samples[samples.length - 1];
    const v = (x1 - x0) / Math.max(1, t1 - t0);
    if (past) return commit();
    if (x < -ACT / 2 && v < 0.3) { animateTo(row, { transform: `translateX(${-ACT}px)` }, 300); x = -ACT; openRow = api; }
    else close();
  };
  wrap.addEventListener("touchend", end);
  acts.addEventListener("click", () => commit());
  async function commit() {
    openRow = null;
    await animateTo(row, { transform: `translateX(${-wrap.clientWidth}px)` }, 220, "var(--ease-out)");
    acts.firstElementChild.style.width = "100%";
    const h = wrap.offsetHeight;
    wrap.style.height = h + "px";
    await animateTo(wrap, { height: "0px", opacity: "0" }, 260);
    onCommit?.(wrap);
  }
  const api = { close };
  return api;
}
document.addEventListener("touchstart", (e) => { if (openRow && !e.target.closest(".row-wrap")) openRow.close(); }, { passive: true });

// ── Toast & notification banner ──────────────────────────────────────
export function toast(text, action) {
  const t = document.createElement("div");
  t.className = "toast glass";
  t.innerHTML = `<span>${text}</span>${action ? `<button class="pressable">${action.label}</button>` : ""}`;
  document.getElementById("overlays").appendChild(t);
  requestAnimationFrame(() => requestAnimationFrame(() => t.classList.add("is-in")));
  const hide = () => { t.classList.remove("is-in"); setTimeout(() => t.remove(), 400); };
  t.querySelector("button")?.addEventListener("click", () => { action.onTap(); hide(); });
  setTimeout(hide, 3800);
}

export function banner({ title, body, onTap }) {
  const b = document.createElement("div");
  b.className = "banner glass";
  b.innerHTML = `<img src="icons/apple-touch-icon.png" alt=""><div style="flex:1;min-width:0"><div class="banner__app"><span>FLEET</span><span>now</span></div><div class="banner__t">${title}</div><div class="banner__b">${body}</div></div>`;
  document.getElementById("overlays").appendChild(b);
  haptic("success");
  requestAnimationFrame(() => requestAnimationFrame(() => b.classList.add("is-in")));
  let sy = 0, dy = 0, timer = setTimeout(() => hide(), 6000);
  const hide = () => { b.style.transition = ""; b.classList.remove("is-in"); b.style.transform = ""; setTimeout(() => b.remove(), 500); clearTimeout(timer); };
  b.addEventListener("touchstart", (e) => { sy = e.touches[0].clientY; dy = 0; b.style.transition = "none"; }, { passive: true });
  b.addEventListener("touchmove", (e) => { e.preventDefault(); dy = e.touches[0].clientY - sy; b.style.transform = `translateY(${dy < 0 ? dy : rubber(dy, 80)}px)`; }, { passive: false });
  b.addEventListener("touchend", () => { b.style.transition = ""; if (dy < -30) hide(); else if (Math.abs(dy) < 6) { hide(); onTap?.(); } else b.style.transform = ""; });
  b.addEventListener("click", (e) => { if (e.pointerType === "mouse") { hide(); onTap?.(); } });
  return hide;
}

// ── Composer that grows to five lines ────────────────────────────────
export function autogrow(ta, onChange) {
  const fit = () => { onChange?.(ta.value); if (!ta.isConnected || !ta.offsetParent) return; ta.style.height = "auto"; ta.style.height = Math.min(ta.scrollHeight, 22 * 5 + 18) + "px"; };
  ta.addEventListener("input", fit);
  requestAnimationFrame(fit);
  return fit;
}

export const h = (html) => { const t = document.createElement("template"); t.innerHTML = html.trim(); return t.content.firstElementChild; };
