# Session recap (Line style)

When a turn ends in a session you aren't looking at, Fleet writes a one-line recap about 3 minutes
later. It uses a throwaway fork of the conversation, so it never enters the agent's history. When
you come back, the line is waiting above the composer, in Claude Code's `※ recap:` style. It goes
away when you send your next message.

Mockup (v2, choose "Line"): https://claude.ai/code/artifact/a921930d-14de-4747-a357-f5845b31fa73

## Decisions already made

- **Style: Line.** One line above the composer, the recap text only. This version has no card,
  timeline, counts or "Details" link.
- **Mechanism: copy Claude Code** (read from the 2.1.270 binary). Write the recap while the prompt
  cache is warm, from a fork that reuses the whole conversation, with one extra user message:
  > The user stepped away and is coming back. Recap in under 40 words, 1-2 plain sentences, no
  > markdown. Lead with the overall goal and current task, then the one next action. Skip
  > root-cause narrative, fix internals, secondary to-dos, and em-dash tangents.
- **Spike results** (OpenCode 1.18.30, Haiku 4.5 on Copilot and OpenRouter):
  - A plain fork gets a full cache hit.
  - Per-prompt `tools:{…:false}` or a `deny` permission drops the tool definitions from the
    request, which misses the cache entirely.
  - A session `permission` of ask-all keeps the hit and stops any tool call before it runs.
  - The cache TTL is 5 minutes: a fork at +4 min hit, at +6.5 min it missed.
  - Fork time: 0.2 s at 50 messages, 2.2 s at 600.
- **Harness-neutral.** Fleet asks the harness for "a one-shot answer off the record". Everything
  OpenCode-specific stays in the adapter.

## Behaviour

- **Trigger:** `TurnEnded` for a session that no one is focused on. Start a timer for
  `Recap:Delay` (default 3 min, must stay under the cache TTL). When it fires, check the skip
  rules and write the recap.
- **Cancel the timer when:** someone focuses the session, a new turn starts, or the session is
  deleted or archived.
- **Skip when:**
  - the user has turned recaps off
  - the session is busy, or has running delegations
  - there are fewer than 3 user messages in the session, or fewer than 2 since the last recap
  - 3 attempts have already failed for this turn
- **Store** the latest recap on the session (text + written-at). **Clear it** when the user sends a
  prompt.
- **Focus** means the session is open, the tab is visible, and the window has focus. The browser
  reports it; a session is "watched" if any connection reports focus.

## Changes

### 1. Harness capability (Domain + OpenCode adapter)
- `HarnessCapabilities.SupportsOffTheRecordPrompt`.
- `IHarnessSession.AskOffTheRecordAsync(string prompt, CancellationToken ct) → string?`: answers
  from the session's full context; doesn't add to its history or run tools.
- OpenCode implementation:
  1. `POST /session/{id}/fork`
  2. `PATCH /session/{fork}` with `title: "fleet-recap"` and
     `permission: [{permission:"*", pattern:"*", action:"ask"}]`
  3. `POST /session/{fork}/message` with the recap prompt. Pass the **parent's last model, agent
     and variant**; a different model misses the cache.
  4. Take the text parts. `DELETE /session/{fork}` in `finally`.

  Time out at 45 s. If the model calls a tool, the call blocks on `permission.asked`; the timeout
  aborts it and deletes the fork, and the tool never runs.
- Startup sweep: delete leftover `fleet-recap` sessions on each pooled instance (after a crash).
- Fork events are already dropped by `SseEventDemultiplexer.RouteEvent` (unbound session ID).
  Add a test so this stays true.

### 2. Recap service (Application + Api)
- `SessionFocusTracker`: in-memory map of session ID → focused connection IDs.
  - New hub method `SetSessionFocusAsync(sessionId, focused)`.
  - Clear a connection's entries on disconnect.
- `SessionRecapService`:
  - Subscribes to `TurnEnded`, `TurnStarted`, `SessionDeleted` and `SessionArchived` through the
    same in-process path `IAutomationEventNotifier` uses.
  - Owns the per-session timers (`TimeProvider`, so tests can use a fake clock).
  - Applies the skip rules, calls the harness, stores the result, and broadcasts it.
- Storage: migration adding `recap_text` and `recap_written_at` to `sessions`. Clear both on prompt
  send (`SessionOrchestrator` send path).
- New domain event `SessionRecapUpdated { SessionId, Text?, WrittenAt? }` (wire type
  `session.recap`). `Text` is null when the recap is cleared.
- `SessionSnapshot` gains `Recap`, so a reload or a second device shows it.
- Setting: the `sessionRecap` preference, **off by default** (user decision 2026-09-13: Copilot
  may bill each recap as a premium request). The service reads the owner's preference and does
  nothing unless it's on: no timers and no forks.

### 3. Client
- `useSessionFocus`: watches the route, `visibilitychange`, and window focus/blur. Debounced
  `SetSessionFocus` calls; re-sends after a SignalR reconnect.
- The domain-event reducer and snapshot load keep `recap` on the session state.
- `RecapLine.vue` above the composer:
  - the recap icon + "recap:" + text, muted
  - hidden while there's an optimistic user message
  - styled like the mockup's `.recap-line`
- Settings page: a "Session recap" toggle, off by default. It explains that each recap is one
  extra request to the session's model.
- Focus reporting only runs when the setting is on.

### 4. Tests and live check
- **Service:** fake clock; the timer fires only while no one is focused, cancels on focus or turn
  start, and applies the skip rules.
- **OpenCode adapter:** the fork → patch → prompt → delete sequence, the fork is deleted on
  timeout, and the model is taken from the parent.
- **Client:** reducer (`session.recap` set and clear), `RecapLine` rendering, and focus reporting.
- **Live:** scratch Fleet with pooled OpenCode on a real model. Blur the tab, wait 3 minutes,
  check the line appears and the fork is gone from `/session`, and check `cache.read` in the
  OpenCode logs.

## Changed while building (2026-09-13)

- **Recap kept in memory, not in the database.** `SessionRecapService` holds it, and the hub adds it
  to the snapshot, so a reload or second device still sees it. A Fleet restart loses it, but after a
  restart the cache is cold anyway. No migration.
- **The sweep runs before each ask, not at startup.** OpenCode lists sessions per directory, so every
  ask first deletes `fleet-recap` forks in its own directory that are older than the 45 s timeout
  plus 30 s. That way it never deletes a fork another ask is still using.
- **Trigger is busy→idle from `HarnessEventRelay`,** not the `TurnEnded` domain event, which flows
  through the outbox. This is the same signal the row status uses.
- **Timer timing is Claude Code's:** 3 min after the turn ended, or 2 s after you leave if that's
  later, and only within 270 s of the turn ending (90% of a 5-minute cache). The setting is checked
  when the timer fires.
- **Prompt counts are in memory,** counting prompts Fleet sent: 3 before the first recap, then 2
  between recaps. They start from 0 after a restart.

## Open questions

- ~~Default on or off?~~ Decided: off by default, with a toggle on the Settings page.
- **Draft in the composer** (a Claude Code skip rule): the client would have to report it along
  with focus. Left out of v1.

## Out of scope

Marker and Dock styles, the "Details" card with timeline and counts, the sidebar, and on-demand
`/recap`.
