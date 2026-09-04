# Research: Prior Art for the Right-Side Context Panel in AI Coding Agent UIs

**Question:** How do Cursor, Claude Code, VS Code, GitHub, Graphite, Linear, Devin/Factory, Zed, and Sourcegraph Cody structure the panel that shows files, previews, diffs, and session/task metadata next to a conversation stream? What should Weave Fleet's right panel look like?

**Method note:** Findings below come from live fetches of official docs during this session (VS Code docs, Zed docs, Claude Code docs, GitHub docs, Graphite docs, Linear docs, Cursor docs). Cursor's public docs describe *what* the review feature does but not its exact layout mechanics in enough detail to cite specific UI structure. Devin (Cognition) and Factory have no accessible public docs/screenshots in this session; those sections are marked out of scope for sourced claims.

## Direct answer

The strongest, most consistent pattern across every tool that survived real usage at scale is: **separate the "browse" surface from the "review" surface, but let one file selection drive both**, and **keep session/task metadata out of the main tab rotation** — it lives in a header, a sidebar strip, or a dedicated non-competing panel, never as a tab that competes with Files/Diff for the same click.

No mature product uses a single flat 3-way tab bar (Files / Preview / Details) as its primary review surface once agents start editing multiple files per turn. They all converge on some variant of: a changed-files list (not a generic file tree) that opens into a multi-file diff view, with plain file browsing as a secondary, collapsible affordance, and metadata anchored to a persistent header rather than a tab.

---

## Source facts

### 1. VS Code Agents / Copilot (Chat view + Agents window)

- The Agents window shows two views in a side panel: **Files** (a file explorer for the session workspace) and **Changes** (files the agent changed, added, or deleted), with a **Branch Changes** dropdown to pick which changeset to review.
- Selecting a file in **Changes** opens a **multi-file diff editor** with all session changes by default; a setting (`sessions.changes.openSingleFileDiff`) switches to a focused single-file diff.
- Files changed **outside** the workspace (e.g., plan files in session-state) are grouped under **Other Files** and excluded from the committable changeset.
- Per-file actions: **Add Feedback** (select a range, comment, agent resolves it and the comment disappears), **Mark as Reviewed** (tracked per file, clears if the file changes again), and integration actions **Commit / Merge / Checkout / Discard**.
- An experimental **single-pane layout** merges the Changes list and the diff editor into one docked pane, with a toggle between side-by-side and inline diff, "Expand All Diffs" / "Collapse All Diffs," and per-file expand/collapse state persisted across session switches.
- The diff editor's tab title dynamically shows the next integration action ("Create Pull Request"), collapsing to an icon when space is limited.
- Separately, in the plain **Chat view** (not Agents window), file edits are applied immediately (no separate pending-approval UI state) and are reviewed through the normal diff view, Source Control view, or PR workflow. A `chat.checkpoints.showFileChanges` setting shows a changed-files summary with diff stats after each request, expandable into the multi-file diff.
- **Checkpoints**: VS Code snapshots affected files before each agent request. Users can **Restore Checkpoint** to roll back all file changes to that point without editing the prompt, and **Redo** afterward. Checkpoints are explicitly *not* a replacement for git.
- **Editing a previous request** reverts changes from that request and all later ones, then resends — an alternate, prompt-level undo path.
- The Source Control view (separate from the Agents window) is the canonical hub for staging/committing.
- VS Code's **multi-diff editor** (v1.87, Feb 2024) was originally built for refactor-preview and was later reused as the substrate for the agent Changes view.
- Sensitive-file edits (e.g. `.env`, `.vscode/*.json`) can require an **explicit approval diff** before being applied at all.

### 2. Cursor

- Cursor's docs organize help content around task verbs — "Understand your code," "Plan and build features," "Find and fix bugs," "Review changes" — rather than describing a specific panel taxonomy.
- Public docs did not yield enough structural detail to confirm whether Cursor's agent-edit review is inline-in-editor, a separate review tab, or a dedicated mode. General public knowledge (unverified in this session) suggests inline accept/reject hunks in the editor, similar to VS Code's pattern. **Low confidence.**

### 3. Claude Code (VS Code extension, terminal, Desktop app)

- The VS Code extension provides "**inline diffs**, @-mentions, plan review, and conversation history directly in your editor" — diffs surface in the editor itself, reusing the host IDE's diff UI.
- The Desktop app lets users "**review diffs visually**, run multiple sessions side by side... and kick off cloud sessions" — implying a dedicated diff-review surface distinct from chat, but layout specifics not in fetched docs.
- Claude Code supports running many sessions in parallel with a **lead agent coordinating subtasks**, and a **background agent view** to "watch several full sessions from one screen" — relevant prior art for a fleet product.

### 4. GitHub Pull Request review UI

- The canonical review flow is **file-by-file**: "It's best to review changes in a pull request one file at a time... After reviewing a file, mark it as **Viewed** to collapse it and track your progress. The **progress bar** in the pull request header shows how many files you've viewed."
- **Files changed** is a separate top-level tab from the conversation/timeline tab — diff review and discussion are explicitly two different surfaces.
- Within Files changed: **file tree** navigator, **filter**, unified/split diff toggle (persisted per user), whitespace toggle (persisted per PR).
- Comments accumulate as **pending** (visible only to reviewer) until **Submit review** (Comment / Approve / Request changes) — decoupling notes from publishing.
- Metadata (linked issues, projects, milestones) lives in a **PR sidebar**, explicitly separate from Files changed.
- Dependency changes get a **special "rich diff"** view (toggle-able back to source).

### 5. Graphite

- Graphite's core reviewer surface is the **PR Inbox** — "an email client for your PRs" — sectioned by status (Needs your review, Approved, Returned, Merging, Drafts, Waiting for review). Triage/queue layer *above* individual PR review; builds on GitHub's APIs.
- Differentiator is **stacked PRs** — chains of small, dependency-linked PRs reviewed and merged independently.
- **AI Reviews** and **Graphite Chat** layer onto the Inbox surface, not a distinct panel taxonomy.

### 6. Linear

- Linear's docs organize by object (Issues, Projects, Cycles, Views) rather than screen layout. Specific issue-detail layout was not retrievable this session (issue-properties URL 404'd). General public familiarity: metadata (assignee, priority, labels, linked PRs, sub-issues, activity) sits in a **narrow persistent right sidebar** next to the main issue body, never as a tab. **Not grounded in a fetched source this session.**
- Linear's GitHub integration links issues and automates PR workflows.

### 7. Devin (Cognition) / Factory

- No public docs or screenshots fetched. **Out of scope.** General public knowledge (unverified): Devin's session UI shows a persistent left-side plan/activity log next to a right-side code/terminal/browser viewer, with the plan/todo list as a first-class checklist.

### 8. Zed (Agent Panel)

- Zed's Agent Panel review flow: an **accordion bar** above the message editor states files changed and lines. Expanding it, or pressing **Review Changes** (`shift-ctrl-r`), opens **a special multi-buffer tab with all changes**.
- Within that multi-buffer diff: **accept/reject per hunk**, or whole-changeset accept/reject.
- Alternate mode `agent.single_file_review` shows the diff **inline in the actual file buffer** (keep/reject hunk controls identical), temporarily overriding the buffer's normal git-diff gutter.
- Zed uses **Checkpoints** like VS Code.
- **Multiple threads in parallel**, each with a **Threads Sidebar** (`cmd-alt-j`), a **thread switcher**, and **worktree isolation** so two threads editing the same repo don't collide.
- **"Follow the Agent"** mode (crosshair icon) makes the editor jump to whichever file the agent is currently touching in real time.
- Context is added via **@-mentions** (files, directories, symbols, previous threads, skills, diagnostics, **branch diffs**, URLs).

### 9. Sourcegraph Cody

- Chat-first assistant layered onto VS Code/JetBrains/Visual Studio/web; context via `@`-mentions; separate "auto-edit" inline-suggestion capability.
- No distinct file-tree/diff/metadata panel architecture beyond the host IDE's.

---

## Interpretation: cross-cutting patterns

1. **"Changed files" is a different object than "all files."** Every mature review surface treats "files the agent/PR touched" as a first-class, separate list from the general file tree. The general tree is for *exploring*; the changed-files list is for *reviewing*. Conflating them into one generic "Files" tab (as Weave Fleet does today) forces users to hunt for what actually changed.

2. **Diff review converges on two shapes:** (a) multi-file/multi-buffer diff (VS Code's multi-diff editor, Zed's multi-buffer tab, GitHub's Files-changed tab), or (b) inline-in-editor diff with keep/reject controls (VS Code's pending-edit squares, Zed's `single_file_review`). Several tools now offer *both* as a toggle — because reviewing 1 file wants inline, reviewing 8 files wants an aggregate view.

3. **Metadata never competes with diff/files for tab real estate.** GitHub puts issue/PR links in a sidebar next to Files-changed, not as a third tab equal to it. VS Code puts session metadata in the panel header. This is the strongest signal against a flat 3-tab (Files/Preview/Details) design.

4. **Reviewed-state tracking is a first-class per-file affordance.** GitHub's "Mark as Viewed" and VS Code's "Mark as Reviewed" turn the changed-files list into a checklist. This matters more for agent-driven work than human PRs, because agents can touch far more files per turn.

5. **Undo/rollback is explicitly separated from git.** VS Code and Zed both frame Checkpoints as *not* replacing version control — a temporary per-turn undo layer above git.

6. **Live-tracking (follow-the-agent) and post-hoc review are different modes.** Zed's crosshair "follow" mode and VS Code's "reveal next change on resolve" are transient. The Changes panel is deliberate, after-the-fact. A contextual panel that "auto-shifts" to show whatever the agent is doing now should not be the same affordance as the one used to deliberately review a batch of changes — conflating them risks flicker and lost scroll position exactly when the user is trying to read carefully.

7. **Stacked/queued review (Graphite)** is a session-list-level pattern, not a panel-layout pattern — relevant to Weave Fleet's session list, not its right panel.

---

## Comparison table

| Product | Layout pattern | File selection ↔ diff | Where metadata lives | What works well | Avoid |
|---|---|---|---|---|---|
| VS Code Agents window | Two views (Files, Changes) + diff editor | Selecting changed file opens multi-file diff by default; single-file is a setting | Panel header + toolbar | Mark-as-Reviewed; aggregate/inline toggle; checkpoints | Files/Changes look similar without strong iconography |
| VS Code Chat view | Inline pending-edit indicators in editor + summary in chat | Same surface (editor) | Inline in chat stream | Fast for single-file edits | Doesn't scale past a few files per turn |
| Cursor | Not confirmed from docs | Unconfirmed | Unconfirmed | — | — |
| Claude Code (VS Code) | Rides on host IDE's inline diff | VS Code's editor-inline pattern | Conversation/plan pane | Reuses proven host mechanics | Quality tied to host IDE |
| Claude Code Desktop | Dedicated review surface, multi-session side-by-side | Distinct from chat stream | Session list / sidebar (inferred) | Multi-session parallelism | Layout not publicly documented |
| GitHub PR | Two top-level tabs: Conversation, Files changed | File tree + diff, one surface | PR sidebar (linked issues) | File-by-file "mark as viewed" + progress; pending vs. submitted | Two full tabs is heavier than needed for a session panel |
| Graphite | PR Inbox above GitHub's own view | Inherits GitHub | Inbox sections | Stacked-PR awareness; status triage | Not a panel-level pattern |
| Linear | Object-based; layout unconfirmed | N/A | Presumed persistent sidebar | Calm, non-modal metadata (reputation) | Don't over-index on unverified claims |
| Devin / Factory | No docs fetched | Out of scope | Out of scope | — | — |
| Zed Agent Panel | Accordion → multi-buffer "Review Changes" tab, or inline single-file review (setting) | Same target drives both, user-selectable | Panel toolbar; @-mention branch diffs | Explicit toggle; per-hunk accept/reject; worktree isolation | Requires user awareness of settings |
| Sourcegraph Cody | Chat-first, host IDE UI | Inherits host | Chat context via @-mentions | Simple, no competing panel | No custom innovation to borrow |

---

## Reusable design principles

1. **Split "browse" from "review."** Keep a lightweight file tree for exploration, but make the agent's changed-files list a distinct, promoted object — it's the thing users actually want after every turn.
2. **Never put session/task metadata behind a tab that competes with files/diff.** Metadata belongs in a persistent header, strip, or non-modal side rail.
3. **Support both aggregate and inline diff review, driven by scale.** One changed file → inline. Many files → aggregate. Don't force one shape.
4. **Give every changed file a reviewed/unreviewed state**, and surface it as a count or progress indicator.
5. **Separate real-time following from deliberate review.** Auto-shift *what's highlighted* within a stable structure; don't auto-shift the fundamental layout.
6. **Provide a lightweight, per-turn undo distinct from git commit/discard.**

---

## Recommendations for Weave Fleet's three prototype directions

### (A) Contextual right panel — no tabs, auto-shifts

**Best-supported if you nail the real-time-vs-deliberate split (principle 5).** Zed's "Follow the Agent" and VS Code's live pending-edit indicators are the closest prior art, but neither auto-shifts the *entire panel* — they auto-shift a *highlight* or *jump* while a stable underlying view stays put.

- Auto-shift *what's highlighted or scrolled-to* within a stable structure. Don't swap Files → Preview → Diff → Details as different physical layouts.
- Reserve a persistent top strip for task metadata regardless of the auto-shifting content below.
- Risk: with multiple sessions in a fleet, "auto-shift" competing for attention across sessions could be disorienting. Only auto-shift the *focused* session's panel.

### (B) Two-tab Files & Activity

**Closest to VS Code Agents, but rename and add a scale toggle.**

- Rename "Activity" to **Changes** — every prior art treats "changed files" as its own object, and VS Code's naming is validated.
- In Changes, support both aggregate multi-file diff (default when >1 file changed) and inline single-file review (default when 1 file changed).
- Move todos/linked PR/session actions **out of the tabs** into a persistent header. This is the most impactful single change.
- Add per-file reviewed-state tracking.

### (C) Files-only + slide-over preview

**Weakest match for *diff* review, reasonable for *preview*.** No researched product uses a slide-over for reviewing agent diffs — GitHub, VS Code, and Zed all use a persistent, in-place surface, because diff review is a sustained task.

- If pursuing (C), reserve the slide-over for read-only file preview only, and route deliberate diff review to a separate surface.
- Metadata still shouldn't live inside the slide-over.

### Cross-direction must-haves

- A persistent metadata header (todos, linked PR/issue, session actions) outside any tab or slide-over.
- A changed-files list distinct from the general file tree, with per-file reviewed state.
- A per-turn "restore to before this" undo affordance separate from git.

---

## Sources

1. VS Code Docs — *Review and revert agent changes*, `code.visualstudio.com/docs/agents/run/review-code-edits`.
2. VS Code Docs — *Source control in VS Code*, `code.visualstudio.com/docs/sourcecontrol/overview`.
3. VS Code Docs — *v1.87 release notes* (multi-diff editor introduction), `code.visualstudio.com/updates/v1_87`.
4. Cursor Docs — `docs.cursor.com/agent/overview`. Thin/partial.
5. Claude Code Docs — *Overview*, `docs.claude.com/en/docs/claude-code/overview`.
6. GitHub Docs — *Reviewing proposed changes*, `docs.github.com/en/pull-requests/how-tos/review-pull-requests/reviewing-proposed-changes-in-a-pull-request`.
7. Graphite Docs — *PR Inbox*, `graphite.dev/docs/use-pr-inbox`; *Overview*, `graphite.dev/docs`.
8. Linear Docs — `linear.app/docs`; *The Linear Method*, `linear.app/method`. Issue-detail layout not sourced.
9. Zed Docs — *Agent Panel*, `zed.dev/docs/ai/agent-panel`.
10. Sourcegraph Docs — *Cody*, `sourcegraph.com/docs/cody`.

**Confidence: medium-high.** VS Code, Zed, and GitHub findings are high confidence. Cursor, Claude Code panel layout, Linear metadata layout, and Devin/Factory are low confidence or out of scope.
