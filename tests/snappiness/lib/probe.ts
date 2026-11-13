/**
 * The in-page probe, installed with addInitScript before any app code runs.
 *
 * - Long tasks (>50 ms), long animation frames (with script attribution) and Event Timing entries are
 *   buffered from page start.
 * - The time an interaction starts is the timeStamp of its first pointerdown/keydown (capture phase), so it
 *   includes input queueing delay, not just handler time.
 * - arm(pred) polls a predicate once per animation frame. "shown" is the frame after the predicate first
 *   holds (the frame that painted it); "settled" is when the DOM has been quiet for settleMs after that
 *   (async data finished rendering), capped at maxMs.
 */
export const PROBE = String.raw`(() => {
  const s = (window.__snap = { lt: [], loaf: [], ev: [], keys: [], lastInput: null, cur: null });
  const obs = (type, fn, extra) => { try { new PerformanceObserver((l) => l.getEntries().forEach(fn)).observe({ type, buffered: true, ...(extra || {}) }); } catch {} };
  obs("longtask", (e) => s.lt.push({ start: e.startTime, dur: e.duration }));
  obs("long-animation-frame", (e) => s.loaf.push({
    start: e.startTime, dur: e.duration, blocking: e.blockingDuration, render: e.renderStart ? e.startTime + e.duration - e.renderStart : 0,
    styleLayout: e.styleAndLayoutStart ? e.startTime + e.duration - e.styleAndLayoutStart : 0,
    scripts: (e.scripts || []).map((x) => ({ src: x.sourceURL, fn: x.sourceFunctionName, pos: x.sourceCharPosition, inv: x.invoker, type: x.invokerType, dur: x.duration, layout: x.forcedStyleAndLayoutDuration })),
  }));
  obs("event", (e) => s.ev.push({ name: e.name, start: e.startTime, dur: e.duration, ps: e.processingStart, pe: e.processingEnd, id: e.interactionId }), { durationThreshold: 16 });
  for (const t of ["pointerdown", "keydown"]) addEventListener(t, (e) => { if (s.lastInput == null) s.lastInput = e.timeStamp; }, { capture: true });
  // Keystroke to next paint: rAF fires before the frame paints; the task after it runs once the frame is out.
  addEventListener("keydown", (e) => {
    if (!s.trackKeys) return;
    const t0 = e.timeStamp;
    requestAnimationFrame(() => { const ch = new MessageChannel(); ch.port1.onmessage = () => s.keys.push(performance.now() - t0); ch.port2.postMessage(0); });
  }, { capture: true });

  s.arm = (predSrc, settleMs, maxMs, t0) => {
    const pred = new Function("return (" + predSrc + ")()");
    const st = (s.cur = { t0: t0 ?? performance.now(), shown: null, painted: null, lastMut: null, settled: null, timeout: false });
    if (t0 == null) s.lastInput = null;
    const mo = new MutationObserver(() => { st.lastMut = performance.now(); });
    const start = () => mo.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
    if (document.documentElement) start();
    st.promise = new Promise((res) => {
      const tick = () => {
        const now = performance.now();
        if (st.shown == null) { let ok = false; try { ok = !!pred(); } catch {} if (ok) st.shown = now; }
        else if (st.painted == null) st.painted = now;
        if (st.painted != null) {
          const quiet = Math.max(st.painted, st.lastMut ?? 0);
          if (now - quiet >= settleMs || now - st.t0 > maxMs) { mo.disconnect(); st.settled = Math.min(quiet, st.t0 + maxMs); res(); return; }
        } else if (now - st.t0 > maxMs + 15000) { mo.disconnect(); st.timeout = true; res(); return; }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
  };

  s.collect = async () => {
    const st = s.cur;
    await st.promise;
    // From the real input (pointerdown/keydown timeStamp) when there was one after arming; else from arming.
    const start = s.lastInput != null && s.lastInput >= st.t0 ? s.lastInput : st.t0;
    const end = st.settled ?? performance.now();
    const within = (x) => x.start + x.dur > start && x.start < end;
    const lt = s.lt.filter(within);
    const loaf = s.loaf.filter(within);
    const ev = s.ev.filter((e) => e.start >= start - 5 && e.start <= end);
    return {
      timeout: st.timeout,
      shownMs: st.painted != null ? st.painted - start : null,
      settledMs: st.settled != null ? st.settled - start : null,
      fromInput: s.lastInput != null && s.lastInput >= st.t0,
      longTasks: lt.length,
      longTaskMs: lt.reduce((a, x) => a + x.dur, 0),
      maxLongTaskMs: lt.reduce((a, x) => Math.max(a, x.dur), 0),
      blockingMs: lt.reduce((a, x) => a + Math.max(0, x.dur - 50), 0),
      loaf,
      maxEventMs: ev.reduce((a, x) => Math.max(a, x.dur), 0),
      heapMB: performance.memory ? performance.memory.usedJSHeapSize / 1048576 : null,
      domNodes: document.getElementsByTagName("*").length,
    };
  };

  // Frame recorder for scrolling and streaming: every rAF interval while it runs.
  s.frames = (ms) => new Promise((res) => {
    const t0 = performance.now(); const gaps = []; let last = t0;
    const tick = (now) => { gaps.push(now - last); last = now; if (now - t0 < ms) requestAnimationFrame(tick); else res({ start: t0, end: now, gaps }); };
    requestAnimationFrame(tick);
  });
  s.window = (start, end) => {
    const within = (x) => x.start + x.dur > start && x.start < end;
    const lt = s.lt.filter(within);
    return { longTasks: lt.length, longTaskMs: lt.reduce((a, x) => a + x.dur, 0), maxLongTaskMs: lt.reduce((a, x) => Math.max(a, x.dur), 0), loaf: s.loaf.filter(within) };
  };
})();`;
