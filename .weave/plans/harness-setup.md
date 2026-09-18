# Harness setup: install and update harnesses from Fleet

Someone who installs Fleet without OpenCode or Claude Code gets no help: a local install never mentions the
harness, and the first message fails. This plan adds a first-run wizard, a home-page banner, and an update
button. Proposal and mockups: https://claude.ai/artifact/K5CXP4kJH9xaZd8zpV7hiC (prior art: t3code's
`WelcomeWizard.tsx`, `providerMaintenance.ts`).

## Decisions (2026-09-18)

- **Wording:** screens say *harness*, as Settings does. *Agent* already means OpenCode's agents (the picker,
  `@` autocomplete, the Turns canvas). The wizard explains the word once; buttons name the product
  ("Install OpenCode").
- **First run:** a wizard (welcome → harnesses → ready) on the first launch of a local Fleet, only when no
  harness is ready. If it's skipped, the dashboard shows a banner until a harness is ready; the banner
  reopens the wizard at the harness step. Cloud mode keeps its wizard.
- **Install:** Fleet types the vendor's installer into a terminal and the user presses Enter. Fleet never
  runs it on its own. macOS/Linux: `curl -fsSL https://opencode.ai/install | bash`,
  `curl -fsSL https://claude.ai/install.sh | bash`. Windows: `winget install --id SST.opencode --exact`,
  `irm https://claude.ai/install.ps1 | iex`.
- **Updates:** a button, never automatic. Fleet waits until no session is working.
- **Windows updates:** `opencode upgrade` doesn't handle winget installs (anomalyco/opencode#30026, closed
  as not planned), so for those Fleet runs `winget upgrade --id SST.opencode --exact` itself.
- **Minimum OpenCode version:** yes; set from the oldest version that passes the pooled OpenCode tests.
- **No downloads by Fleet:** both vendors ship installers that update themselves.

## Stage 1 — say what's wrong, and find new installs

- [x] Harness status is a state (`ready`, `not-installed`, `sign-in-required`, `not-working`) with version
      and executable path (`HarnessStates`, `HarnessAvailability`, `HarnessInfo`).
- [x] `HarnessProbe` finds the executable and runs `--version` with a 10-second timeout; every runtime
      uses it. Claude Code adds its `auth status` check.
- [x] `ExecutableResolver` searches PATH, then the harness's install folders (`OpenCodeExecutable`:
      `$OPENCODE_INSTALL_DIR`, `$XDG_BIN_DIR`, `~/.opencode/bin`), then common user folders
      (`~/.local/bin`, `~/bin`, Homebrew on macOS, winget links, npm and Scoop on Windows). Fleet read
      PATH at launch, so without this a harness installed while Fleet runs isn't found until a restart.
- [x] Settings → Harnesses shows the state, the reason, version and path, and a "Check again" button.
- [x] The new-session composer says why no harness is ready and won't send.

## Stage 2 — first-run wizard, home banner, install terminal

- [x] The setup terminal: `/api/setup/terminals` starts a shell in the user's home folder, outside any
      session, local mode only; opening one ends the last one (`TerminalService.CreateSetupAsync`).
- [x] Each runtime declares its install and sign-in commands per platform (`IHarnessRuntime.GetSetup`,
      `HarnessSetup` on `/api/harnesses`); the client shows what the harness says.
- [x] `HarnessSetupWizard.vue` (welcome, harnesses, ready) opens on first launch when no harness is ready;
      `harnessSetup.done` records that it was finished or skipped. `HarnessSetupRows.vue` types the command
      into the terminal (`TerminalView` `initialInput`) and checks the harnesses every 3 s while it's open.
- [x] Dashboard banner while no harness is ready; it, the composer notice and Settings open the wizard at
      the harness step (cloud mode: the composer still points to Settings).
- [x] Continue turns on a ready harness that's off (Claude Code starts off).

## Stage 3 — updates

- [ ] Hourly latest-version check (npm registry: `opencode-ai`, `@anthropic-ai/claude-code`), with a
      setting to turn it off.
- [ ] Update button in Settings; runs `opencode upgrade <version>`, `claude update`, or
      `winget upgrade --id SST.opencode --exact` for winget installs, once no session is working, with a
      5-minute timeout. Checks the version afterwards; shows the command to copy when it fails.
- [ ] Restart idle pooled OpenCode servers after an update (`OpenCodeHarnessPoolRecycler`).
- [ ] Minimum OpenCode version: below it the harness is "Update needed" and new sessions can't use it.

## Found along the way

- On 2026-09-18 `/tmp` held 280 hidden 13 MB native libraries (`/tmp/.<hash>-00000000.so`, 1.5 GB, dating
  back ten days); `lsof` showed a running `opencode` holding one, so OpenCode likely extracts one per start
  and never removes it. Fleet starts OpenCode often. They filled the per-user `/tmp` quota and broke test
  runs with "disk I/O error". Not confirmed further.
- `opencode serve` never updates itself; only OpenCode's TUI runs its auto-update.
