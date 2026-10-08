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
- It worked against the real scratch Fleet first time: `LiveHubTest` (androidHostTest, OkHttp engine) handshakes,
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
