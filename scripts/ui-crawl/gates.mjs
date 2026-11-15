// The crawl's gates: each finds, in one screen's record from crawl.json, the findings that make the crawl red. Shared by
// report.mjs (the verdict) and self-test.mjs (which checks a throwing screen fails the gates it should).
export const GATES = {
  pageErrors: { label: "Page errors", find: (r) => r.pageErrors },
  // A screen that drew an error boundary's fallback (Fleet's route error page or the router's own) instead of itself.
  errorPages: { label: "Error pages", find: (r) => r.errorPages ?? [] },
  consoleErrors: {
    label: "Console errors",
    find: (r) => r.console.filter((c) => c.type === "error" && !c.mockGap).map((c) => (c.url ? `${c.text} (${c.url})` : c.text)),
  },
  failedRequests: {
    label: "Failed requests",
    find: (r) => r.failed.map((f) => `${f.method ?? ""} ${f.url} ${f.status ?? f.why ?? ""}`.trim()),
  },
  phoneOverflow: {
    label: "Sideways overflow at 390 px",
    find: (r) => {
      if (!r.phone || !r.overflow) return [];
      const o = r.overflow;
      if (!o.pageOverflows && !o.offenderCount) return [];
      return [`page ${o.pageW}px in a ${o.vw}px window; ${o.offenderCount} element${o.offenderCount === 1 ? "" : "s"} past the edge`,
        ...o.offenders.map((x) => `<${x.tag} class="${x.cls}"> right ${x.right}px ${x.text ? `“${x.text}”` : ""}`.trim())];
    },
  },
};
