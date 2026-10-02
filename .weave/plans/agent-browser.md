# Agent browser: agents use the page, Fleet is the browser

Approved 2026-10-02 ("yes, build it"). Spike, mockups and evidence: branch `spike/opencode2-browser-plugin`,
`docs/spikes/opencode2-browser-plugin/`, artifact https://claude.ai/artifact/WTGjVDAVqNUEVkA9iZjRqv.
Builds on PR #358 (`DenyBrowser` rule); this branch (`feat/agent-browser`) starts from it.

## Decisions

- On by default, limited: the session's own pages (apps it started, its browser/page canvases), page scripts
  (`evaluate`) off, uploads refused. Settings → Browser changes them.
- Agent's view (live picture of the agent's tab) and the step list are in v1.
- No per-session switch in v1. Claude Code and Pi: off, with the reason.
- Native, then Fleet, then off: OpenCode 2 uses its own browser plugin with Fleet attached as the browser;
  OpenCode gets two Fleet tools (`fleet_browser_read`, `fleet_browser_act`).
- OpenCode 2 specifics stop at its adapter; the browser service is harness-neutral.

## Shape

- **Application/Browser/AgentBrowser**: `IAgentBrowser` (typed actions → typed results), `AgentBrowserSettings`
  (preferences `AgentBrowser.Enabled|Pages|Scripts`), `AgentBrowserPolicy` (which addresses a session's tab may
  load), step recording (`browser.step` event + store), errors in words the agent can act on.
- **Infrastructure/Browser**: `ChromeHost` (one headless Chrome shared by screenshots and agent tabs; idle quit only
  when no tab is open), `CdpConnection` gains event subscriptions, `AgentBrowser` (CDP implementation: tabs,
  accessibility snapshot + refs, input, screenshot, console/failed requests/dialogs buffers, Fetch-based navigation
  policy, idle close).
- **OpenCode 2 adapter**: `OpenCode2BrowserAttachments` — control events from the server's existing event stream,
  one pending `attach` per session while enabled, rules swap `DenyBrowser` → none while attached, plugin JSON ↔
  typed actions, `preview` → page canvas, `tabs.open` with focus → browser canvas for that page.
- **OpenCode adapter**: `fleet_browser_read`, `fleet_browser_act` in `opencode/fleet/fleet-canvas.ts` via the
  canvas bridge; hidden (session permission deny) when off.
- **Client**: steps card on `execute` / `fleet_browser_*` calls; BrowserCanvas Your view / Agent's view (frame
  polling + ring + caption + banner); Settings → Browser; capability `SupportsAgentBrowser`.
- **Skills**: `fleet-run`, `fleet-debug` lines; `fleet_browser_screenshot` description (both plugins).

## Stages (tick as done)

- [ ] A1 CdpConnection events + ChromeHost shared by screenshotter
- [ ] A2 AgentBrowser core (typed actions, CDP impl, policy, settings)
- [ ] A3 Steps: domain event, store, API (tabs, steps, frame)
- [ ] B  OpenCode 2 attachment
- [ ] C  OpenCode tools
- [ ] D  Client: steps card, Agent's view, Settings → Browser
- [ ] E  Skill + description edits
- [ ] F  Tests, live checks, screenshots, PR
