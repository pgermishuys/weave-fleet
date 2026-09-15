# Automations rework: Stages 1–3

Proposal and mockup: https://claude.ai/code/artifact/edf2ed3d-0535-4022-8e70-17d6de283a81. Stage 0 (fixes, no new UI) is
the first three commits on this branch.

## Decisions (2026-09-15)

- A run happens in a **new worktree** by default. "Current checkout" stays as a choice per automation.
- Keep the **five events** that fire.
- **Model, Agent, Max per hour, Timeout** leave the UI. The columns stay, so nothing is lost.
- A scheduled run missed while Fleet was off **runs once on start if it is less than 3 hours late**. Otherwise it is
  recorded as Skipped.

## Server

1. Migration 034: `automations.isolation` (`worktree` | `existing`; NULL = made before this, runs as before),
   `automations.base_branch`, and an `automation_runs` table (trigger, scheduled_for, started_at, status, session,
   error).
2. A one-off trigger: `triggerType = "once"`, `triggerConfig = "2026-09-21T09:00"` read in the automation's zone.
   Once it has run or been skipped, it switches itself off.
3. `AutomationRunService`: the one way a run starts (schedule, catch-up, once, Run now, event). It records the run,
   skips when "skip while the last run is going" is on and the last run's session is still busy, starts the session,
   and writes down the session or the error.
4. Scheduler: per automation, the latest occurrence since the last recorded one (or since it was switched on). On
   time: run. Up to 3 h late: run, as a catch-up. Later: record Skipped. Survives restarts because the watermark is
   in `automation_runs`.
5. Where a run happens: a worktree of the folder (branch `fleet/auto-<name>-<yyyyMMdd-HHmm>` from the chosen base),
   the folder as it is, or a scratch folder when there is no folder. Old automations keep their old behaviour.
6. `same_session` target: later runs prompt the session the last run started.
7. API: `nextRunAt` and `lastRun` on each automation, `GET /{id}/runs`, Run now returns the run it started, and
   `GET /draft-from-session/{sessionId}` for "Repeat on a schedule…".

## Client

1. `lib/automation-schedule.ts`: the sentence parser from the mockup, labels, cron, next run, the prompt without the
   schedule words.
2. One composer for create and edit: highlighted schedule, When menu (presets, once, time, custom cron, events),
   Folder / Workspace / Base chips from the session composer, runs-in chip, "…" (name, skip while running), the line
   under the box.
3. The automation page: header with switch, next run, Run now and a menu; the runs list; the composer underneath.
4. Sidebar rows like session rows: next run, Running, Failed or Off.
5. "Repeat on a schedule…" in a session's context menu.
