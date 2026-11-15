// What the UI crawl visits: every screen, in every variant, against the client in Vite mock mode.
// Add a screen here when you add a route or a view worth a look; `setup` runs after the page loads and should throw
// when it can't reach what it means to show (a renamed button), so the night goes red instead of quietly
// screenshotting the wrong page.

/** Preferences that switch every Labs feature on (mock mode reads them from GET /api/preferences). */
export const LABS_ON = {
  Jobs: "true",
  Workflows: "true",
  "features.board.enabled": "true",
  "features.mission-control.enabled": "true",
  Mods: "true",
  SessionMessages: "true",
  SessionRecap: "true",
  Memory: "true",
  NewSessionStarters: "true",
  UsageLimitsChip: "true",
  LiveMachines: "true",
};

/** The axe rules a theme can change. */
const THEME_RULES = ["color-contrast", "link-in-text-block"];

/** Theme, window size and Labs. `phone` variants are 390 px wide: sideways overflow there is a gate. */
export const VARIANTS = {
  "dark-desk-labsoff": { theme: "dark", w: 1440, h: 900, labs: false },
  "dark-desk": { theme: "dark", w: 1440, h: 900, labs: true },
  "light-desk": { theme: "light", w: 1440, h: 900, labs: true },
  "dark-phone": { theme: "dark", w: 390, h: 844, labs: true, phone: true },
  "light-phone": { theme: "light", w: 390, h: 844, labs: true, phone: true },
  // Fleet's theme against the opposite operating-system colour scheme: only with --variants=…
  "dark-desk-oslight": { theme: "dark", os: "light", w: 1440, h: 900, labs: true, onRequest: true },
  "light-desk-osdark": { theme: "light", os: "dark", w: 1440, h: 900, labs: true, onRequest: true },
  // The other eight themes, desktop with Labs on, each under its own operating-system scheme: the "themes" set, a
  // second pass with its own baseline (axe-baseline-themes.json). Dark and light are already in the nightly set. Only
  // axe's colour rules run there: the rest see the same markup in every theme, and the nightly set already counts them.
  ...Object.fromEntries(
    [
      ["weave-classic", "dark"],
      ["black", "dark"],
      ["nord", "dark"],
      ["dracula", "dark"],
      ["solarized-dark", "dark"],
      ["solarized-light", "light"],
      ["monokai", "dark"],
      ["github-dark", "dark"],
    ].map(([theme, os]) => [`${theme}-desk`, { theme, os, w: 1440, h: 900, labs: true, set: "themes", axeRules: THEME_RULES }]),
  ),
};

/** The variants a crawl of `set` visits: "nightly" (the five above) or "themes". On-request variants belong to none. */
export const variantsOf = (set = "nightly") =>
  Object.keys(VARIANTS).filter((n) => !VARIANTS[n].onRequest && (VARIANTS[n].set ?? "nightly") === set);

const SETTINGS = {
  general: "General",
  appearance: "Appearance",
  harnesses: "Harnesses",
  permissions: "What agents can do",
  skills: "Skills & tools",
  memory: "Memory",
  folders: "Folders",
  machines: "Machines",
  labs: "Labs",
  updates: "Updates & about",
};

async function openSettingsSection(page, label) {
  // On a phone the settings menu sits in the drawer behind "Open menu".
  const menu = page.getByRole("button", { name: "Open menu" });
  if (await menu.isVisible().catch(() => false)) {
    await menu.click();
    await page.waitForTimeout(400);
  }
  const item = page.locator(".settings-nav__item", { hasText: label }).first();
  await item.waitFor({ state: "visible", timeout: 5000 }).catch(() => {
    throw new Error(`setup: no settings menu item "${label}"`);
  });
  await item.click();
  await page.waitForTimeout(300);
}

/**
 * Each screen: `id`, `path`, and optionally `waitFor` (a selector that must appear) and `setup(page)`. Every screen gets
 * the faked SignalR hub; mock-session-1 renders the conversation below.
 */
export const SCREENS = [
  { id: "sessions-home", path: "/" },
  { id: "session-conversation", path: "/sessions/mock-session-1", waitFor: ".activity-message--assistant" },
  { id: "session-new", path: "/sessions/new" },
  { id: "fleet", path: "/fleet" },
  { id: "board", path: "/board" },
  { id: "workflows", path: "/workflows" },
  { id: "jobs", path: "/jobs" },
  { id: "automations", path: "/automations" },
  ...["", "daily", "projects", "models", "sessions"].map((s) => ({
    id: `analytics-${s || "overview"}`,
    path: `/analytics${s ? "/" + s : ""}`,
  })),
  ...Object.entries(SETTINGS).map(([id, label]) => ({
    id: `settings-${id}`,
    path: "/settings",
    setup: (page) => openSettingsSection(page, label),
  })),
  { id: "settings-plugins", path: "/settings/plugins" },
  { id: "settings-plugin-github", path: "/settings/plugins/builtin.github" },
  { id: "github", path: "/github" },
  { id: "welcome", path: "/welcome" },
  { id: "repositories", path: "/repositories" },
  { id: "login", path: "/login" },
  { id: "pair", path: "/pair" },
  { id: "phone", path: "/phone" },
  { id: "phone-new", path: "/phone/new" },
  { id: "not-found", path: "/no-such-page" },
  { id: "palette", path: "/", setup: (page) => page.keyboard.press("Control+k") },
  { id: "palette-shortcuts", path: "/", setup: (page) => page.keyboard.press("Control+/") },
  // The shared confirm (confirmAction), as Delete session opens it from the session's More actions.
  {
    id: "confirm-delete-session",
    path: "/sessions/mock-session-1",
    setup: async (page) => {
      await page.locator("[data-testid='session-more-actions']").click();
      await page.locator("[data-testid='session-delete-button']").click();
      await page.locator("[data-testid='confirm-dialog']").waitFor({ state: "visible", timeout: 5000 }).catch(() => {
        throw new Error("setup: Delete session didn't open the confirm");
      });
    },
  },
];

// A rich but invented conversation for the faked hub: text, tools (done, failed, running) and a question.
const SESSION = "mock-session-1";

export function sessionSnapshot() {
  const now = Date.now();
  const info = (id, role, created) => ({
    id,
    role,
    sessionID: SESSION,
    agent: role === "assistant" ? "build" : null,
    modelID: role === "assistant" ? "claude-sonnet-5-5" : null,
    parentID: null,
    time: { created, completed: created + 4000 },
    cost: role === "assistant" ? 0.012 : null,
    tokens: null,
  });
  const text = (id, messageID, t) => ({ id, sessionID: SESSION, messageID, type: "text", text: t });
  const tool = (id, messageID, name, status, input, output, error) => ({
    type: "tool",
    id,
    sessionID: SESSION,
    messageID,
    tool: name,
    callID: `call-${id}`,
    state: {
      type: status,
      status,
      input,
      output,
      error,
      metadata: null,
      time: { start: now - 60000, end: status === "running" ? null : now - 58000 },
    },
  });
  return {
    session: { id: SESSION, title: "Tidy the invoice export", status: "idle" },
    messages: [
      {
        info: info("m1", "user", now - 300000),
        parts: [text("p1", "m1", "The invoice export drops the last row when the total is zero. Can you find out why and fix it?")],
      },
      {
        info: info("m2", "assistant", now - 290000),
        parts: [
          text("p2", "m2", "I'll look at the exporter first, then the tests."),
          tool("t1", "m2", "read", "completed", { filePath: "src/export/invoice.ts" },
            "export function toRows(items) {\n  return items.filter(i => i.total).map(toRow)\n}"),
          tool("t2", "m2", "grep", "completed", { pattern: "toRows", path: "src" },
            "src/export/invoice.ts:12\nsrc/export/__tests__/invoice.test.ts:4"),
          tool("t3", "m2", "bash", "error", { command: "bun test src/export" }, null,
            "1 failing: keeps zero-total rows\nexpected 3 rows, got 2"),
          tool("t4", "m2", "edit", "completed",
            { filePath: "src/export/invoice.ts", oldString: "items.filter(i => i.total)", newString: "items" }, "Edited 1 line"),
          text("p3", "m2",
            "Found it: `toRows` filters out falsy totals, so a **zero** total is dropped.\n\n| Case | Before | After |\n|---|---|---|\n| total 0 | dropped | kept |\n| total 12.50 | kept | kept |\n\n```ts\nexport function toRows(items) {\n  return items.map(toRow)\n}\n```"),
        ],
      },
      {
        info: info("m3", "user", now - 200000),
        parts: [text("p4", "m3", "Great. Also run the full suite and check the CSV header.")],
      },
      {
        info: info("m4", "assistant", now - 190000),
        parts: [
          tool("t5", "m4", "bash", "running", { command: "bun test" }, null),
          text("p5", "m4", "Running the suite now. Which header spelling do you want: `Total (ZAR)` or `Total`?"),
        ],
      },
    ],
    delegations: [],
    activityStatus: "busy",
    lastEventId: 12,
    hasMore: false,
    cursor: null,
    isPartial: false,
  };
}
