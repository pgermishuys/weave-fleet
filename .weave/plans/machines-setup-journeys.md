# Machines setup journeys: build plan

Status: approved by the user on 19 November 2026. Proposal page (open it in a browser):
`mockups/machines-setup-journeys/index.html`. It has the model, all 15 journeys (J1–J15) with mockups, and the
decisions. This file is the build checklist. Tick pieces off here as they merge.

## Demo sentence

From scratch, in any order, someone can set up the desktop app, one or more machines with no screen (`fleet node`) and
the phone app. Each screen connects once and then sees every machine. Nobody types an address or copies a link out of
a terminal, and every terminal step prints the next one.

## The model (use these words everywhere)

- **Your Fleet** is the machines you've connected together. **Every machine keeps the list**: there's no single home
  machine.
- A **machine** runs Fleet: the desktop app runs it with a window, `fleet node` runs it without one.
- The phone, a browser and the desktop app's window look at your Fleet. Connect one to *any* machine and it sees all of
  them. Add a machine from *any* of them and every one sees it. Remove a phone or forget a machine from any of them and
  it's gone everywhere.
- In the UI it's "machine", "your phone", "this browser", "the desktop app". Never "node" (keep the `fleet node`
  command) and never "computer", except for the desk as against the phone.

## Decisions (user, 19 Nov 2026)

1. Every machine keeps the list ("your Fleet"), not one home machine. **Yes.**
2. A machine with no screen can connect phones (drop the 409 in `DeviceEndpoints.cs:54-57`). **Yes.**
3. Waiting machines announce themselves on Tailscale and the home network. Any connected phone or desktop app lists them
   under **Found** and adds one with a matching code. Pasting a pairing link and scanning the terminal's QR code stay as
   fallbacks. **Yes.**
4. The desktop app can let others reach it: a second listener on Tailscale and the home network that always asks for a
   key, while the window keeps loopback sign-in tied to the loopback listener. It stays off until "Add your phone" asks.
   **Yes.**
5. The desktop app uses a Fleet already running on the same computer (one computer, one machine). **Yes.**
6. When the phone can't reach a machine: **A**. Say so ("Out of reach from this phone: atlas is only on Tailscale"),
   offer one "Get Tailscale" or "Turn on Tailscale" button, and keep it apart from "Off since 08:40". Warn when adding a
   Tailscale-only machine while the phone isn't on Tailscale. Relaying through another machine goes on the Later list.
7. No address field by default: pairing codes carry every address, and typing one moves under "Use my own address".
   **Yes.**

## Found while reading the code (19 Nov)

- The desktop app binds 127.0.0.1 with no token (`desktop/src/server.ts:20-41`), so no phone or other machine can reach
  it. The fixes it suggests are CLI flags (`MachinesSection.vue` loopback note, `lib/phone-readiness.ts:66`).
- The phone code carries one address (`lib/pairing.ts` `choosePhoneBaseUrl`), which is why "Which address will the
  phone open?" exists (`AddPhonePanel.vue`).
  - The native app may use http on Tailscale and private-LAN addresses after an id check (`mobile/src/fleet/routes-core.ts`).
  - Only the phone web app needs https.
- The native phone app pairs each machine separately and gets no device grants. Only the web phone does:
  `client/src/lib/phone/grants.ts`, `POST /api/machines/{id}/device-grant`.
- `install.sh` ends with "Then start it with: fleet" and never mentions `fleet node up`. The README still says
  `fleet node --host 0.0.0.0`.
- `fleet node up --ssh-only` contradicts itself (`launcher.sh:639`, `NodePairCommand.cs:70`).
- The docs use port 2113, while the CLI and the app default to 6262. #1073 fixed the SSH tunnel; check what's left.
- **Likely broken (J8, not yet tried):** the desktop app and `fleet node` both default to `~/.weave`. The app's
  `findRunningFleet` opens the node's loopback URL from `fleet.instance.json`, but a node serves no web app and wants a
  key.
- `fleet node up` doesn't check for a harness.

## Pieces

Each piece is one PR in its own fresh session. Sizes are rough. "J" numbers are the journeys on the proposal page.

- [x] **0. Over SSH port bug.** Merged as #1073.
- [x] **1. J8: desktop app on a computer already running `fleet node`.** (#1076) First reproduce it (Windows: the node's
  "Fleet node" scheduled task, then the desktop app). Then make the app use that Fleet: read its key file as the same
  user, show the web app itself, and show the "Fleet already runs on this computer" screen. S–M.
- [x] **2. Words and next steps.** (#1075)
  - `install.sh` ends with the next step for both kinds of machine.
  - `--ssh-only` text; README; docs port; `docs/phone.md` covers the native app.
  - A one-line "what's a machine" on Settings → Machines and in the welcome wizard's last step, with "Add your phone".
  - The phone's pair screen copy.
  - S.
- [x] **3. Desktop app reachable (decision 4).** (#1077)
  - Second listener: always asks for a key; loopback bypass only on the loopback listener.
  - A test that a forwarded-for-loopback request on the network listener still needs a key.
  - The "Let others reach studio" dialog: Tailscale and home network / home network only / my own address, showing the
    addresses Fleet found.
  - The desktop app keeps the choice.
  - J2. M.
- [x] **4. Codes carry every address; machines with no screen connect phones (decisions 2, 7).** (#1080)
  - Pairing payload with every address; the phone tries each one with the id check.
  - Drop the 409.
  - `fleet node up` and `fleet node pair` print a terminal QR code plus the code, and `fleet node up` checks Tailscale
    and harnesses.
  - "Your phone will reach studio" lines replace the three-item readiness checklist.
  - The address field moves under "Use my own address".
  - J2, J6. M.
- [x] **5. Your Fleet: every machine keeps the list (decision 1).** (#1079, #1082, #1081, #1087) The core, and the largest piece.
  - Start with a thin slice behind a Labs switch, live-checked end to end: a phone connects to hangar, atlas is added
    through hangar, the phone sees atlas.
  - Then: the roster is copied to every machine, a machine can introduce another (key exchange), a screen connected to
    any machine gets keys for all, the native phone app takes grants and gets a "joined your Fleet" notification, and
    removal and forget reach every machine, including offline ones when they return.
  - Split into 2–3 PRs: server roster and introductions; phone app grants and notification; removal.
  - J5, J9, J10, J12–J15. L.
- [x] **6. Found (decision 3).** (#1084 announce, Found and add; second PR: wants-to-join prompt and the desktop first run)
  - A waiting machine (`fleet node up` / `fleet node pair`) announces itself on Tailscale (share discovery with the
    Teams spike / `MachineCandidates` so they don't fork) and on the home network.
  - Add a machine → Found is the default tab, with a code match.
  - "X wants to join your Fleet" on any connected phone or desktop app.
  - The desktop app's first run finds your Fleet and joins it, then asks "Where should new sessions run?".
  - Needs piece 5. J4, J7, J9, J10, J13. M–L.
- [x] **7. On a new server.** (#PR) `install.sh --join '<address>#CODE'`, and a one-time 30-minute join code from Add a
  machine → On a new server. Needs pieces 5 and 6. J5. M.
- [x] **8. Away from home (decision 6A).**
  - Tailscale checks on both ends, with "Turn on" for the listener.
  - The phone's reach rows and messages.
  - The warning when adding a Tailscale-only machine.
  - Needs pieces 3 and 4. J3. M.
- [x] **9. Machines page.** (this PR) "Your machines" plus "Phones and browsers", a reach line per machine, and the advanced parts
  (token, addresses, own address) on each machine's page. It replaces "Let other devices reach This machine". Needs
  piece 5. M.
- [x] **10. Phone first (J11).** The phone app's "I don't have Fleet yet" with "Send me the link" / "Share the line".
  The website download page "Where will Fleet run?" lives in the weave-website repo: in a weave-website PR (to follow).
  S–M.

Later list: native system push (APNs/FCM) for the phone app's "joined your Fleet" and "wants to join" notices; today they're a banner inside the app (user, 19 Nov 2026); relaying through another machine (decision 6B); challenge-response before sending a key to a fallback
address (already on the list).

## Order

Wave 1, no dependencies (at most 3 sessions at once): 1, 2, 3. Start 5's thin slice as soon as a slot is free, since
everything after waits on it. Then 4. Then 6, 8 and 9. Then 7 and 10.

## Every piece

- Live-check it in each setup it touches: the desktop app alone; a node plus the desktop app on the same machine;
  several machines; browser or phone only. Use scratch Fleets with a scratch HOME. Never run the Fleet API with the real
  HOME.
- Phone screens: `gh workflow run mobile.yml --ref <branch>`, then `gh run download <id> -n mobile-android-shots`
  (Android ~30 min, iOS ~60).
- A real-app screenshot in the PR description from the moment it's opened, committed under `mockups/<topic>/`.
  Invented data only.
- Main is protected:
  - Required checks are "CI passed" and "Defect record"; a fix… PR needs the lines Found by, Cause, Shipped in, Guard.
  - Never `--admin`. Wait with `gh run watch <run id> --interval 60`.
  - Rebase onto origin/main; merge once green.
- AGENTS.md design rules: shared UI pieces, `PageHeader`, `ErrorNote`, `confirmAction`, theme tokens, the words lint
  (`bun run lint`). Use bun, not npm.
