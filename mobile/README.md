# Fleet for phones

The native Fleet app (Expo SDK 58, React Native, TypeScript) for iPhone and Android. It pairs with a Fleet you run,
shows its sessions, and lets you read, answer and approve from the phone. It looks like Fleet on both platforms: the
colours come from the desktop's tokens, the fonts are Inter and JetBrains Mono, and nothing is drawn by the
platform's own widgets.

## Layout

- `src/theme/`: colours and fonts. `tokens.generated.ts` is generated from `client/src/assets/main.css`.
- `src/kit/`: the components every screen is built from. `fleet://kit` (or a long-press on the Fleet mark) opens the
  Kit, which shows each one in each state, in light and dark.
- `src/fleet/`: pairing, the connection to a machine, the session stream.
- `src/app/`: screens (Expo Router).
- `app.config.ts`, `eas.json`, `plugins/`: app config, EAS build profiles, and the walk-through's cleartext plugin.
- `e2e/`: the scratch Fleet and the Maestro walk-through.

The conversation logic isn't ported: `metro.config.js` lets the app import the web client's `client/src/lib` files
(the event reducer, tool-row folding, wording) as `@fleet/...`.

## Run it

Use `bun`, never npm. Node 22. The client's modules must be installed too (`cd ../client && npm ci`), since the app
type-checks the shared files.

```sh
bun install
bunx tsc --noEmit
bun test src
bun run tokens:check               # fails when tokens.generated.ts is behind main.css; `bun run tokens` fixes it
bunx expo start --web --port 8103  # the quickest look: the Kit renders in a browser, in both schemes
```

Pairing doesn't work in a browser (CORS); the native app has no CORS.

Android (JDK 17 and the Android SDK; `android/` is generated, never edit it or commit it):

```sh
CI=1 bunx expo prebuild -p android
cd android && ./gradlew assembleRelease -PreactNativeArchitectures=x86_64   # one Gradle at a time
./gradlew --stop
```

iOS needs a Mac. CI builds it (`.github/workflows/mobile.yml`); locally `bunx expo prebuild -p ios` and open the
workspace in Xcode.

## The walk-through

`.github/workflows/mobile.yml` builds the app on Android and iOS (with `FLEET_E2E=1`), starts a scratch Fleet and
drives the app with [Maestro](https://maestro.mobile.dev): pair, then every screen once in light and once in dark.
The screenshots are the artifacts `mobile-android-shots` and `mobile-ios-shots`, in `<platform>-light/` and
`<platform>-dark/`.

- `e2e/start-fleet.sh` starts Fleet on 127.0.0.1:5431 with a scripted model (`e2e/fakellm.py`), one session titled
  "Fix pagination off-by-one", "ask before changes" on, and a pairing code. It makes its own scratch `HOME`. Never
  point it at your real one.
- `e2e/flows/pair.yaml` and `walk.yaml` are the flows; `e2e/walk-all.sh` runs them and switches the appearance.
- `FLEET_E2E=1` at prebuild time lets the app talk plain http to `localhost` and `127.0.0.1` only (Android network
  security config, iOS `NSAllowsLocalNetworking`). Without it the app is https only.

The checks job (tokens, typecheck, tests) runs on every change to `mobile/**`, `client/src/lib/**`, the tool-card
component and `main.css`.

## Install the preview build on an Android phone

The `preview` build is a standalone app that updates itself over the air. The Expo project is `@weavefleets/fleet`
(its id is in `app.config.ts`), and the repo has the `EXPO_TOKEN` secret, so CI can publish updates.

1. Get an Expo token for the `weavefleets` account (or `bunx eas-cli login`), and in `mobile/`:
   `EXPO_TOKEN=… bunx eas-cli build --profile preview --platform android`.
   Wait for the build (about 15 minutes on the free queue).
2. EAS prints an install link and a QR code. On the phone, open the link (or scan the QR), download the APK, and when
   Android asks, allow installs from that browser (Settings, Apps, Special access, Install unknown apps).
3. Open Fleet and pair with the code from Settings, Machines, Add a phone on your Fleet.

Over-the-air updates: every push to `main` (or a `mobile/**` branch) runs `eas update` for the `preview` and `development` channels. The app checks on launch and downloads in the background, so close and reopen it twice: the first
launch fetches the update, the second runs it. A change to native code (a new package, a plugin) changes the runtime
version; that needs a new `eas build`, and old builds keep running their last compatible update.

## Licences

`tsconfig.json` and the build scaffolding come from the Expo template (MIT, Copyright (c) 2015-present 650
Industries, Inc. (Expo)); see `LICENSE`. Inter and JetBrains Mono come from `@expo-google-fonts/*` under the SIL Open
Font License.
