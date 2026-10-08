# KMP spike: running notes

What it takes to build a truly native Fleet client (Kotlin Multiplatform logic, Jetpack Compose on Android, SwiftUI
on iPhone). Kept as I went, newest findings appended at the end of each section.

## Versions picked (checked on Maven Central / Google Maven, 2026-10-08)

- Kotlin 2.4.21, Gradle 9.8.1 (released the day before; worked first time with AGP 9.4.1 + KGP 2.4.21).
- AGP 9.4.1. AGP 9 changes the KMP setup: `com.android.library` + `kotlin.multiplatform` is deprecated, the shared
  module uses `com.android.kotlin.multiplatform.library` with an `android { }` block inside `kotlin { }` (one variant,
  no build types, host tests opt-in with `withHostTest {}`, task `testAndroidHostTest`). The app module applies only
  `com.android.application` + the Compose compiler plugin: AGP 9 has Kotlin built in, `kotlin-android` is gone.
- Compose BOM 2026.09.00 (Compose 1.12.1), activity-compose 1.13.0, core-ktx 1.19.1. **These need compileSdk 37**:
  the installed `platforms;android-36` failed `checkDebugAarMetadata` with 16 issues. Installed
  `platforms;android-37.0` with sdkmanager (no sudo) and set compileSdk 37, targetSdk 36 (the emulator image is 36).
- Ktor 3.6.0 (OkHttp engine on Android, Darwin on iOS), kotlinx.serialization 1.11.0, coroutines 1.11.0.
- No Material: the Android UI is `compose.foundation` + `compose.ui` only (BasicText, BasicTextField, LazyColumn,
  Canvas-drawn icons). The look is Fleet's tokens, not Material's.

## Shared code (commonMain)

### SignalR client — the key finding

Microsoft's SignalR Java client is JVM-only, so it can't run on iOS. I wrote a minimal one over Ktor WebSockets:
JSON hub protocol v1 only, WebSockets only, skipNegotiation first with a fallback to
`POST /hubs/session-events/negotiate?negotiateVersion=1`, handshake, record-separator framing (with a buffer for
messages split across frames), invocations with ids and completions (results/errors), server invocations dispatched
to handlers, pings every 15 s, 30 s silence = dead, close messages, reconnect with backoff (0/2/5/10/30 s) and an
`onConnected` hook to resubscribe.

- **~280 non-blank lines of Kotlin** (`signalr/SignalRProtocol.kt` + `signalr/HubConnection.kt`), versus 3,448
  non-blank lines of JS in `@microsoft/signalr` 10.0.0's ESM build (which also does SSE/long polling, MessagePack,
  streaming, stateful reconnect...). The parts Fleet uses are small.
- It worked against the real scratch Fleet the first time: `LiveHubTest` (androidHostTest, OkHttp engine) handshakes,
  calls `SubscribeToSessionsTopicAsync` and `SubscribeToSessionAsync`, and builds the conversation from the real
  SessionSnapshot — on both the skipNegotiation path and the negotiate path.
- Fleet's hub sends `Event(topic, eventId ?? 0, data)`: eventId 0 means "none" (the TS client passes it through).
- Not done: stateful reconnect / ack-based replay (Fleet resubscribes and takes a fresh snapshot instead, as the web
  client does), MessagePack, streaming invocations, transports other than WebSockets.

### Reducer port

Ported from `client/src/lib/domain-event-reducer.ts`, `event-state.ts`, `phone/fold-steps.ts` (+ `groupTools`) and
`phone/dock-state.ts` (`pendingQuestion`):

- Ported events: snapshot messages (`createSessionStreamState`), `message.created`, `message.updated`,
  `user.prompt.committed`, `message.part.updated` (text, reasoning, tool with field-by-field state merge, file),
  `message.part.delta.streamed` (text field, offset-aware de-duplication), `turn.started`, `turn.ended`,
  `turn.failed` (failure on the latest reply or a message of its own), `session.idled`,
  `delegation.created/updated/completed` (for status), `activity_status` (sub-agent waiting → "waiting_input"),
  plus `permission.asked` / `permission.replied` in the session store, and `activity_status`,
  `session_notification`, `session.started/deleted/archived` on the global `sessions` topic.
- Skipped: `work.*` (running-work strip), `context.updated` (context meter), cost/tokens and `step-finish`
  accumulation, compaction parts and summaries, slash-command and shell-command messages, `session.recap`,
  `session.queue`, `session.retry`, canvases, terminals, files, browser steps, diff line counts and step details,
  `confirmSentPrompt` (optimistic sends — the phone waits for `user.prompt.committed`).
- Kotlin data classes make "nothing changed, return the same list" fall out of `==`, where the TS lists 13 flags.
- Loosely typed harness payloads (`state: unknown`) stay `JsonObject`; small `str()/child()/num()` helpers replace
  `typeof x === "string"` checks.
- Unit tests in commonTest (11 reducer + fold tests, 8 SignalR framing tests) run on the JVM here and on Kotlin/Native
  in macOS CI.

### Kotlin → Swift surface

Kept deliberately simple: no Flow and no generics cross into Swift. Each store has `current()` plus
`watch { state in }` returning a `WatchHandle` (renamed from `Cancellable`, which would clash with Combine's), and
actions are fire-and-forget methods with optional `(String?) -> Unit` callbacks. `IosFleet` is the single entry object;
it also gives Swift helpers to discriminate the sealed `PhoneItem`/`PhoneBlock` (they arrive as Objective-C protocols,
so Swift can't switch over them exhaustively). SKIE would make this nicer (sealed → Swift enums, Flow → AsyncSequence)
but wasn't needed.

## Android

- Pair (typed URL + 8-char code, or a pasted `…/pair#p=` link), Sessions (Needs you cards with Allow once / Deny or
  answer choices, then Working, then Recent), Session (user bubbles, Markdown-ish text, folded tool rows
  `Read path ✓`, working line, docked permission / question / composer).
- Token storage: an AES-GCM key in the Android Keystore + ciphertext in SharedPreferences (~60 lines).
  `androidx.security:security-crypto` (EncryptedSharedPreferences) is deprecated, so I didn't use it.
- Debug-only cleartext to 127.0.0.1 / 10.0.2.2 via a `src/debug` network security config that overrides main's.
- Notifications: a "Needs you" channel; permission asks get Allow once / Deny actions, questions get the first two
  choices as buttons plus a RemoteInput inline reply. Actions go to a BroadcastReceiver that reads the token from the
  Keystore and calls REST itself (`goAsync()` + coroutine), then replaces the notification with the outcome.
- Live Update: an ongoing "Working" notification per working session with `Notification.ProgressStyle`
  (indeterminate) and a chronometer. Promotion: on compileSdk 37 the builder has `setRequestPromotedOngoing` /
  `setShortCriticalText`, but those arrived after API 36.0, so on the API 36 image they'd crash — I set the extras
  they set (`android.requestPromotedOngoing`, `android.shortCriticalText`) instead. Needs
  `POST_PROMOTED_NOTIFICATIONS`. Whether the system promotes it: see "Verification".
- Big gap: notifications only fire while the app's process is alive and connected to the hub. Fleet pushes to phones
  with Web Push only (`PushSubscriptionRecord`: "webpush now; apns or fcm later"). A real native app needs an FCM /
  APNs gateway in Fleet (or a relay), plus `session_notification`'s question requestId (questions don't carry one,
  so the app reads it from a snapshot).

## iPhone (written on Linux, not compiled here)

- Kotlin/Native Apple targets don't build on Linux (Gradle skips them), so none of the Swift or iosMain code has been
  compiled yet. It'll first meet a compiler on the macOS runner.
- XcodeGen `project.yml`: app target (links the static `FleetShared.xcframework`, bundles the fonts, `UIAppFonts`,
  `NSSupportsLiveActivities`, `NSAllowsLocalNetworking`) + Widget Extension target. The ActivityAttributes struct is
  shared between them as Swift; nothing Kotlin goes into the extension.
- Keychain from Kotlin needs CoreFoundation by hand (`CFDictionaryCreateMutable`, `CFBridgingRetain`), because a
  Kotlin Map can't hold `CFStringRef` keys. That's the most error-prone file to have written blind.
- Live Activity: started/updated locally from the hub's session list (pushType nil). Without APNs push updates it
  can't change state while the app is suspended; the elapsed time still ticks (system-drawn from `startedAt`).
- Notification actions: `UNNotificationCategory` with Allow once (`.authenticationRequired`) / Deny (`.destructive`),
  and a `UNTextInputNotificationAction` for questions, answered in `AppDelegate` through the shared client.

## Verification (2026-10-08)

- `./gradlew :androidApp:assembleDebug` and `:androidApp:assembleRelease` (R8) pass.
- `./gradlew :shared:testAndroidHostTest`: 22 tests pass. 11 reducer/fold tests and 8 SignalR framing tests, plus 3
  live tests against the scratch Fleet on 127.0.0.1:5431:
  - `LiveHubTest` ×2: handshake → `SubscribeToSessionsTopicAsync` → `SubscribeToSessionAsync` → a real snapshot
    folded by the reducer, skipping negotiate and through `negotiate?negotiateVersion=1`.
  - `LiveSessionTest`: the whole shared stack as the apps use it (FleetClient + SessionController on one thread):
    sends a prompt and sees the reply stream in (working mid-reply, then idle); `ask-me` docks a question with
    SQLite/Redis and `answerDocked("SQLite")` answers it; `run-tests` docks a permission ask (kind shell, `ls -la &&
    echo tests passed`) and Allow once runs it; with focus off, the hub sends a `session_notification`
    (kind permission, with requestId) and replying over REST from outside the session works (the Android
    notification action's path); idle for 40 s (past the server's 30 s timeout), then another reply on the same
    connection.
- Not run: the emulator. `/dev/kvm` is `root:kvm 0660` and this user isn't in `kvm`, so no Android screenshots, no
  cold-start time and no check of whether Android 16 actually promotes the Live Update. The UI has only been compiled,
  never seen on a screen.
- Not compiled: everything iOS (iosMain Kotlin, SwiftUI, widget). It first meets a compiler on the macOS runner.
- Configuration works with no Android SDK at all (ANDROID_HOME unset, no local.properties): the iOS CI job only needs
  JDK 17 + Xcode + XcodeGen.

## Measurements

Lines of code (non-blank, comments included):

| Area | Kotlin / Swift | TS original |
|---|---|---|
| SignalR client (protocol + connection) | 277 Kotlin | `@microsoft/signalr` 10.0.0 ESM: 3,448 JS (a library; the app wrapper `use-signalr-socket.ts` is 656 more) |
| Conversation reducer + phone folding | 614 Kotlin | 1,121 TS (`domain-event-reducer.ts` + `event-state.ts` + `phone/fold-steps.ts`) + 134 (`dock-state.ts`, `question-types.ts`); the port skips several parts (see above) |
| REST client + models + JSON helpers | 260 Kotlin | (generated OpenAPI types on the web) |
| State holders (FleetClient, SessionController, Pairing) | 443 Kotlin | — |
| Platform (Keystore, Keychain, engines, Swift entry) | 191 Kotlin | — |
| Shared tests | 358 Kotlin | — |
| Android UI (Compose, 6 files) | 953 Kotlin | — |
| Android app glue + notifications + receiver | 340 Kotlin | — |
| iPhone UI + app delegate + Live Activity manager | 774 Swift | — |
| iPhone widget + shared ActivityAttributes | 87 Swift | — |

Builds (4-core machine shared with another Gradle build, `-Xmx2g`, `workers.max=2`):

- First-ever `assembleDebug` (downloads, cold daemons): ~4 min; first `testAndroidHostTest`: 6 min (mostly downloads).
- Clean `assembleDebug` (warm daemon, deps cached, `--no-build-cache`): **51 s**.
- Incremental `assembleDebug` after editing a Compose file: **10 s**. After editing a shared commonMain file: **16 s**.
- `assembleRelease` with R8 + resource shrinking: 3 min 9 s.
- APK size: debug **15.2 MB**; release (R8) **2.6 MB**, of which 1.38 MB dex and ~1.06 MB the six font files.
- Cold start on an emulator: not measured (no KVM).

## Pain points and surprises

- **SignalR has no multiplatform client.** It's small to write yourself (~280 lines) because Fleet only needs JSON over
  WebSockets, and it held up against the real server. But it's now yours to maintain: reconnect, keep-alives,
  invocation timeouts, and any future protocol use (stateful reconnect, streaming).
- **The newest AndroidX wants compileSdk 37**, and the stated toolchain had 36. One `sdkmanager` call fixed it.
- **AGP 9 reshuffled KMP**: there's a new library plugin, `android {}` lives inside `kotlin {}`, there are no build
  types in the shared module, and `kotlin-android` is gone from apps. The setup in older guides no longer applies.
- **Live Update APIs straddle versions**: `ProgressStyle` is API 36, but `setRequestPromotedOngoing` /
  `setShortCriticalText` only exist from 36.1 (in the android-37 jar, not android-36's). On a 36.0 device you set the
  extras by hand. Whether the system promotes the notification is still untested (no emulator).
- **No background delivery without Fleet changes.** Both apps only get `session_notification` while running. A shipped
  app needs FCM/APNs in Fleet (the push model already plans for it) and ideally a question's requestId in the
  notification. iOS suspends apps in seconds, so on iPhone the notification path is mostly theoretical until APNs.
- **Everything iOS needs a Mac**: compiling iosMain, the XCFramework, the Swift, the widget, the simulator, and Live
  Activities. On Linux I could only write it carefully and keep the Kotlin↔Swift surface small. Expect a round of
  compile fixes on the first macOS run, most likely in the Keychain CoreFoundation code and in Swift's view of Kotlin
  types (KotlinLong, enums, sealed interfaces as protocols).
- **The Kotlin↔Swift surface takes deliberate design**: no Flow, no generics, no suspend calls from Swift; I used
  callbacks and `current()` snapshots, plus helper functions to tell sealed types apart. That's about 30 lines of
  Kotlin, and it shapes the API.
- **Two native UIs, two times the UI work**: 953 lines of Compose plus 774 lines of SwiftUI draw the same three screens. The
  shared part (~1,800 lines) is the logic both would otherwise duplicate. You also get two kinds of icon: drawn
  Canvas icons on Android, SF Symbols on iPhone.
- **Fleet's own suppression applies to native too**: SessionNotifier skips sessions someone is watching, so the
  apps must call `SetSessionFocusAsync(false)` when backgrounded (done: lifecycle ON_STOP / scenePhase).
- Compose without Material is perfectly workable (BasicText/BasicTextField/Canvas) and makes the Fleet look easy;
  you give up Material's text field affordances (selection handles style, error states) and write small components.
