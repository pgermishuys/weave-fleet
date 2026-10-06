# Fleet on your phone: native feel (mockups first)

## The ask
Make the phone app (PR #377, this branch `fleet/how-remote-control-work-claude-code`) feel as close to a native
mobile app as a PWA can. **This stage is mockups only, for the user to approve.** Don't change app code until the
user approves a direction. Don't push and don't open PRs.

## Where things are
- Phone app code: `client/src/components/phone/` (inbox, pairing, setup, answered), `client/src/components/phone/session/`
  (session view, composer, sheets), `client/src/components/phone/new/` (New session), routes `client/src/routes/phone*.tsx`,
  `client/src/routes/pair.tsx`. Design tokens: `client/src/assets/main.css` (and the files it imports).
- What it looks like today: `mockups/phone-app/shots/*.png` (light and dark, 390×844 @2x). The original mockups the build
  followed: `mockups/phone-app/index.html`, `mockups/phone-app/session.html`. Plan and progress notes:
  `.weave/plans/phone-app.md`. User docs: `docs/phone.md`.

## Where the mockups go and how the user sees them
- Put everything in `mockups/phone-native/`. A static server already serves that folder at
  **http://192.168.1.13:8137/** (LAN; the user opens it on their real phone). Replace the placeholder `index.html` with a
  landing page linking each mockup. Relative links only. No build step: plain HTML/CSS/JS files.
- The server runs detached (`python3 -m http.server 8137 --bind 192.168.1.13`). If it's gone
  (`curl -s http://192.168.1.13:8137/`), start it again the same way with `setsid nohup … & disown` so it outlives the
  session. Serve only `mockups/phone-native/`, never the repo root.
- The user will try these **on a real phone**, so they must be genuinely interactive and touch-driven, not pictures:
  sheets that drag, swipe-back, buttons with pressed states, the keyboard case. Make them work in iOS Safari and Android
  Chrome at phone width (also opened as a Home Screen app if possible: include a manifest and `apple-mobile-web-app-*` meta).
- Build from Fleet's real CSS tokens (copy the values from `client/src/assets/main.css`), so what's approved is buildable.
  Light and dark both.

## What "native" should mean here (research it; this list is a starting point)
Study how iOS and Android apps do it (Claude Code mobile, Linear, GitHub Mobile, Slack, iOS Settings/Mail, Material 3)
and be specific. Candidates:
- Navigation: iOS large titles that collapse on scroll; push/pop page transitions; edge swipe back; tab bar that behaves
  natively (re-tap scrolls to top); no web-looking back chevrons.
- Sheets with detents (medium/large), drag to dismiss with velocity, rubber-banding, dimmed backdrop, grabber.
- Touch: instant pressed states, no grey tap highlight, no text selection on controls, 44pt targets, no 300 ms feel,
  `overscroll-behavior`, momentum scrolling, pull to refresh on the inbox.
- Keyboard: content and the primary button stay above the iOS keyboard (`visualViewport`), composer grows, Enter behaviour.
- Safe areas, standalone status bar colour (`theme-color`, light and dark), splash, no white flash on launch.
- Loading: skeletons instead of spinners and rows that jump in; optimistic updates (Allow once flips instantly).
- Type and spacing at native sizes (17pt body, SF/Roboto system font, `-apple-system`), dynamic type friendly.
- Motion: short, spring-like, and honouring `prefers-reduced-motion`. Haptics where the web allows (Android
  `navigator.vibrate`; iOS has none, say so).
- Swipe actions on inbox rows (e.g. swipe to archive / allow) if it fits.

## Known rough edges to fix in the mockups (from a review of the screenshots)
1. New session page reads as a plain settings form: a desktop resize handle on the text box, a dead gap above Start,
   Start isn't kept above the iOS keyboard, Agent/Model rows pop in late and shift the layout, faint back chevron.
2. Notification setup looks like a desktop panel: smaller type than other screens and two "Done" buttons.
3. Permission choice "Don't ask again for `dotnet test *`" wraps mid-command, leaving `*` alone (docked and in More…).
4. Durations have no days ("19154h 33m" for a long-stuck session).
5. Inbox header: muted "2 machines" reads like a stray label beside the + and bell icons; washed-out disabled send
   button in the Terminal sheet.
6. Nothing has been tried on a real phone: safe areas, status bar, keyboard and touch are unverified.

## How to work (the user's standing preferences)
- The user responds to visual, interactive mockups built from the real CSS, ideally before/after. Design the screens as
  a flow (inbox → ask → session → new session → setup), not isolated pictures.
- Render and look at every mockup yourself before showing it (Playwright Chromium kit:
  `/home/pgermishuys/source/weave-fleet/.poc-runtime/pw/node_modules/playwright-core`, see
  `~/.cache/fleet-phone/pw/*.mjs` for examples; use a 390×844 viewport, isMobile, hasTouch). Check light and dark.
- Keep to the ask: native feel for the existing phone screens. Propose new features separately, don't fold them in.
- When the mockups are ready, reply with the URL, what each mockup shows, the decisions you need from the user (offer
  options with a recommendation), and what can't be done natively in a PWA (be honest).
- Commit the mockups on this branch (conventional commits, ending with
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`), but don't push.

## Machine rules
- Always `bun`/`bunx`, never npm/npx. `. ~/.cache/fleet-phone/env.sh` puts Node 22 and bun on PATH and sets TMPDIR.
- Never run the Fleet API, or tests that boot it, with the real HOME (it can delete `~/.weave/fleet.db`). Mockups don't
  need the API at all. Never run anything under `~/.weave/fleet/bin`.
- This machine has 7 GB of RAM: don't run the .NET Api test suite and vitest at the same time.
