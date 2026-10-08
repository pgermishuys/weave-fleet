# Fleet native client: React Native (Expo) spike

An experiment, not a product: what it takes to build a real iPhone and Android app for Fleet. The other half of the
experiment, Kotlin Multiplatform with native screens, is in `../mobile-kmp`. Findings are in
`../.weave/research/native-mobile-client.md`.

## What it does

- **Pair** with the code from Settings → Machines → Add a phone (typed, or the QR scanned with the camera). The device
  key lives in the iOS Keychain or Android Keystore-backed storage (`expo-secure-store`), not browser storage.
- **Sessions**: waiting on you, working, recent; kept live by the hub's `sessions` topic.
- **A session**: the live conversation over SignalR, with folded tool rows, and a dock that is the composer, Allow
  once / Deny for a pending command, or a question's choices.
- **Notifications** while the app runs: Allow once / Deny on a permission ask, a question's choices plus a typed
  answer, answered from the notification without opening the app.
- **iOS Live Activity** (`src/widgets/SessionActivity.tsx`, via `expo-widgets`): lock screen and Dynamic Island
  while a session works or waits on you.

## Fleet's own logic, unchanged

The app doesn't port the conversation logic; it runs the web client's. `metro.config.js` adds `../client/src` to
Metro, resolves the web client's `@/` imports inside it, and swaps two browser-only imports for stand-ins in
`src/shims`. The app imports them as `@fleet/...`:

- `lib/domain-event-reducer.ts` (with `event-state`, `running-work`, `delegation-state`, `context-usage`,
  `shell-commands`): snapshot + events → messages.
- `lib/phone/fold-steps.ts`, `dock-state.ts`, `asks.ts`, `time.ts`: the phone web app's tool rows, dock and wording.

That's 15 files and about 2,900 lines.

## Run it

Use `bun`, never npm. Node 22.

```sh
bun install
bunx tsc --noEmit                 # typechecks the shared web-client files too (needs client/node_modules)
bunx expo start --web --port 8099 # quickest check; CORS blocks pairing in a browser, the native app has no CORS
```

Android (JDK 17 and the Android SDK; `android/` is generated, don't edit it):

```sh
CI=1 bunx expo prebuild -p android
cd android && ./gradlew assembleRelease   # app/build/outputs/apk/release/app-release.apk
```

iOS needs a Mac: `bunx expo prebuild -p ios`, then build `ios/*.xcworkspace` in Xcode, or let
`.github/workflows/native-mobile-experiment.yml` do it on a macOS runner.

## Walk-through against a throwaway Fleet

`e2e/start-fleet.sh` starts Fleet on 127.0.0.1:5431 with a scripted model (`e2e/fakellm.py`), one session, "Ask before
changes" on, and a pairing code. It writes OpenCode config under `$HOME`, so it's for CI runners and scratch machines
only. `e2e/flows/walk.yaml` is a [Maestro](https://maestro.mobile.dev) flow for both platforms:

```sh
maestro test -e FLEET_URL=http://127.0.0.1:5431 -e PAIR_CODE=XXXX-XXXX e2e/flows/walk.yaml
```

The scripted model answers `run-tests` with a command, `ask-me` with a question, and `slow-task` with a 45 s command.

## Licences

The Expo template files (`app.json`, `tsconfig.json`, `assets/`) are MIT, Copyright (c) 2015-present 650 Industries,
Inc. (Expo); see `LICENSE`. Inter and JetBrains Mono come from `@expo-google-fonts/*` under the SIL Open Font License.
