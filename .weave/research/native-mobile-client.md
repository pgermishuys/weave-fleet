# What it takes to build a real native mobile client for Fleet

Experiment, 2026-10-08. Branch `experiment/native-mobile` (pushed), worktree branch
`fleet/experiment-what-take-build-real-native`. Two working spikes against a throwaway Fleet with a scripted model:

- `mobile/`: **React Native (Expo SDK 57)**. One UI in TypeScript; the conversation logic is the web client's own
  code, imported unchanged.
- `mobile-kmp/`: **Kotlin Multiplatform**. Shared Kotlin logic; native screens in **Jetpack Compose** (Android) and
  **SwiftUI** (iPhone).

Both pair with Fleet's existing one-time code, keep the device key in the Keychain/Keystore, list sessions, follow a
session live over the SignalR hub, send prompts, Allow once / Deny commands, answer questions, notify with action
buttons, and show an iOS Live Activity. Screenshots: `~/.cache/fleet-native/gallery/index.html`.

## How it was checked

- **iOS**: `.github/workflows/native-mobile-experiment.yml` on GitHub's `macos-26` runner (Xcode 26, iPhone 17 Pro
  simulator). Each job builds Fleet, starts it with a scripted model (`mobile/e2e/start-fleet.sh`, "Ask before
  changes" on), builds the app and drives it with [Maestro](https://maestro.mobile.dev) (`mobile/e2e/flows/walk.yaml`,
  `mobile-kmp/e2e/walk.yaml`): pair → sessions → conversation → command approval → question → long task → Home
  (Live Activity) → ask while backgrounded.
- **Android**: both apps build (debug and release) on Linux; the Kotlin shared tests (22, including live hub and
  permission tests against a running Fleet) pass on the JVM. **Neither app has been seen on an Android screen yet**:
  the emulator needs `/dev/kvm` access (`sudo setfacl -m u:$USER:rw /dev/kvm`).
- **React Native logic** also ran in its web build under Playwright against the same Fleet (pair, stream, dock,
  answer). That checks the logic, not native rendering.

## Results

| | Kotlin Multiplatform + Compose/SwiftUI | React Native (Expo) |
|---|---|---|
| iOS walk-through | **Passed end to end** (runs #4–#9) | **Passed end to end** (runs #8, #9) |
| iOS Live Activity | **Works**: Dynamic Island green while working, amber when it needs you, updated in the background | **Not seen** in runs #8–#9, even started in the foreground; iOS logged a widget "content load failed" in run #5. `expo-widgets` (JSX → SwiftUI) unproven; a hand-written Swift extension (as in the Kotlin app) is the fallback |
| Ask while backgrounded (iOS) | No banner: the app still counted as watching (see 2 below); island turned amber | **Native banner** ("Wants to run ls -la …"); long-press shows **Allow once / Deny**. Answering from it is unproven (the test's tap missed, then a sequencing bug in the flow) |
| iOS clean CI build | ~9 min: Kotlin/Native tests 4.6, framework 1, Xcode (Debug) 3.4 | ~36 min: prebuild 1.5, Xcode (Release) 33–36 |
| Android release app | **2.6 MB** (R8; 1.4 MB code, 1.1 MB fonts) | 84.5 MB for 2 ABIs, no minify (~26 MB per phone via the store; 53 MB is native libraries) |
| Android build, local | clean 51 s, incremental 10–16 s | clean ~10 min (first one also downloads the NDK) |
| Conversation logic | **Ported**: 614 Kotlin lines from 1,121 TS (skips running-work, context meter) | **Shared**: ~2,900 lines from `client/src/lib` unchanged, via Metro and two small stand-ins |
| SignalR | Hand-written, 277 lines (Microsoft's client is JVM-only) | Microsoft's official JS client |
| Screens | Written twice: Compose 953 + SwiftUI 774 lines | Written once (~1,100 lines app code in total) |
| Native extras | Plain Swift/Kotlin: ActivityKit, `UNTextInputNotificationAction`, RemoteInput, Android 16 Live Update | `expo-notifications` (actions + text reply), `expo-widgets` (Live Activity as JSX → SwiftUI); no Android Live Update without a native module |
| Look | Fleet tokens, Inter, JetBrains Mono, dark | Fleet tokens, Inter, JetBrains Mono, light + dark |

## What it takes, whichever way

These are needed for any native app, and are most of the real work.

1. **Push through Apple and Google.** Today Fleet only does Web Push. Both spikes notify only while the app runs;
   iOS suspends a backgrounded app within seconds, so on a real phone asks would mostly go unnoticed. Needs:
   - an `apns`/`fcm` channel behind the existing `IPushSender` (`src/WeaveFleet.Application/Push/PushModels.cs`;
     the `push_subscriptions.channel` column already exists; `PUT /api/push/subscriptions` only accepts `webpush`);
   - **a relay**, because the APNs key and FCM service account belong to whoever publishes the app, not to each
     self-hosted Fleet. Options: a small Weave-run gateway (tryweave.io) that holds the keys and forwards opaque
     payloads, or Expo's push service (Expo holds the keys; React Native only). Payloads stay tiny (machine,
     session, kind, request id); anything sensitive is fetched by the app over the tailnet.
   - Live Activity updates while suspended also come from APNs (`liveactivity` push type, per-activity token).
2. **"Watching" has to expire.** Fleet skips notifications for a session someone is watching
   (`SessionFocusTracker`). The Kotlin app marks itself watching and un-marks on backgrounding, but iOS can suspend it
   before that call goes out, so Fleet keeps thinking the phone is looking. Seen on the simulator: the ask came,
   the island turned amber, no banner. The React Native app never marks itself as watching, so it got banners in the
   background, but also a "Finished its turn" banner while looking at the session. Both are wrong in opposite ways. Fix: a heartbeat/expiry for phone focus, or `beginBackgroundTask` around the
   call. (`DeskPresenceTracker` already does heartbeat + expiry for desktop presence.)
3. **Question notifications need the question.** `session_notification` carries a `requestId` only for
   permissions; both apps fetch a snapshot to find the pending question. Add `requestId` (and the choices) for
   questions.
4. **Accounts, signing, distribution.** Apple Developer Program ($99/yr) for TestFlight/App Store, APNs and
   real-device installs; Google Play Console ($25 once) or sideloaded APKs; a Firebase project for FCM. App review
   for an app that controls coding agents on your own machines needs a clear story and a demo Fleet for reviewers.
5. **macOS CI.** Every iOS build needs a Mac. GitHub's `macos-26` runners work; a React Native Release build costs
   ~38 min of macOS minutes per run.
6. **Reachability stays as it is.** Pairing, device keys, grants and Tailscale all worked unchanged. Native apps
   aren't subject to CORS; plain-http Fleets need an ATS exception on iOS (debug only in a real app; production
   should use `tailscale serve` https).

## What each approach costs and buys

**React Native (Expo)**
- Buys: the conversation behaves exactly like the desktop's, because it *is* the desktop's code; one set of screens;
  new event types work on the phone the day the web client handles them; Expo's config plugins give Live
  Activities and notification actions without writing Swift.
- Costs: big Android app (~26 MB vs 2.6 MB); slow iOS builds; a yearly Expo SDK upgrade treadmill; native extras go
  through library wrappers with their own limits (here: `start()` is a synchronous `Activity.request` on the JS
  thread; no Android Live Update). Text selection, long streaming Markdown and scrolling performance are untested on devices.

**Kotlin Multiplatform + Compose/SwiftUI**
- Buys: truly native screens and behaviour on both; small, fast app; iOS extras are plain ActivityKit/UserNotifications
  code; the iOS app passed the full walk-through first time, and the Swift and Kotlin/Native code compiled on its
  first build.
- Costs: a second copy of the conversation logic that must track the TypeScript one (mitigate with shared JSON
  fixtures run through both reducers in CI); our own SignalR client; every screen built twice; all iOS work needs
  a Mac.

## What's keepable

- **Either way**: the native extras (Swift Live Activity + notification categories; Android Notifier with
  RemoteInput and Live Update), the pairing/key-storage flow, the Maestro walk-throughs and the throwaway-Fleet
  starter (`mobile/e2e`).
- **Kotlin path**: the shared Kotlin core (~2,000 lines: SignalR, REST, models, reducer port, controllers, Keychain/
  Keystore), its tests, and both UIs as a starting point.
- **React Native path**: almost all of `mobile/`; replace the stopgap Markdown renderer.
- Throwaway: debug defaults pointing at 127.0.0.1:5431, plain-http allowances, the `fleetkmp://` scheme.

## Open decisions

1. React Native (share the desktop's logic) or Kotlin Multiplatform (native screens, logic twice)?
2. Push relay: a Weave-run gateway, or Expo's service (React Native only)? Either way, who holds the Apple/Google keys?
3. Distribution: TestFlight/App Store and Play, or sideload/enterprise first?
4. Phone UI scope for v1: the PWA's screens (inbox, sessions, session, new session), or less?

## Next steps if this goes ahead

1. Server: `apns`/`fcm` push channel + relay; focus heartbeat for phones; question `requestId` in notifications.
2. Android on a screen (emulator with KVM, then the user's phone) for both apps.
3. Pick the approach; for Kotlin, add the shared reducer fixtures before porting more events.
4. Real-device checks: background asks, Live Activity updates via push, keyboard and long conversations.

## Gotchas found on the way

- iOS only starts a Live Activity from the foreground (`Activity.request` fails in the background; remote start needs
  a push-to-start token).
- On GitHub's macOS runners OpenCode can take ~25 s to make its first model call; UI waits in the walk-throughs are
  90 s.
- Maestro on iOS: `hideKeyboard` fails, typing sometimes drops characters (retry and assert the field), React Native
  pressable rows read as one accessibility label (match with `.*text.*`), and SpringBoard's notification menu can't
  always be tapped by text.
- Two Gradle builds at once on this 14 GB machine got one OOM-killed; the processes share the real Fleet service's
  cgroup, so keep one at a time and `./gradlew --stop` after.
- Fleet's pairing limit (5 typed codes a minute) kicks in quickly while iterating; it's working as designed.
- Waiting for text that's already further up the conversation ("All 42 tests pass") passes at once; wait for something
  new (a count, or the composer coming back) instead.

## Still unproven

- Answering an ask from the notification's Allow once (both apps). The React Native buttons show; the tap isn't exercised.
- The React Native Live Activity via `expo-widgets`.
- Anything on an Android screen (both apps build; the emulator needs `/dev/kvm`).
- Real devices: background delivery (needs APNs/FCM), keyboard behaviour, long conversations, text selection.
