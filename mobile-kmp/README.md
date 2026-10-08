# Weave Fleet — Kotlin Multiplatform spike

A native Fleet client for Android and iPhone: shared Kotlin for everything that isn't drawing (REST, a hand-written
SignalR client, the conversation reducer ported from the web client, state holders), Jetpack Compose on Android and
SwiftUI on iPhone. An experiment, not a product: see `NOTES.md` for what it cost and what it found.

```
mobile-kmp/
  shared/        Kotlin Multiplatform: commonMain (models, Ktor REST, SignalR, reducer, stores), androidMain, iosMain
  androidApp/    Jetpack Compose (foundation only, no Material), notifications, Live Update
  iosApp/        SwiftUI app + Widget Extension (Live Activity), generated with XcodeGen
  fonts/         Inter + JetBrains Mono (OFL), used by both apps
  shots/         Emulator screenshots
```

Versions: Kotlin 2.4.21, AGP 9.4.1 (`com.android.kotlin.multiplatform.library` for `shared`), Gradle 9.8.1,
Compose BOM 2026.09.00, Ktor 3.6.0, kotlinx.serialization 1.11.0, coroutines 1.11.0, compileSdk 37, minSdk 26,
JDK 17.

## Android (Linux or macOS)

```sh
. ~/.cache/fleet-native/android-env.sh      # JAVA_HOME (17), ANDROID_HOME (needs platforms;android-37.0)
cd mobile-kmp
./gradlew :shared:testAndroidHostTest       # reducer + SignalR framing tests on the JVM
./gradlew :androidApp:assembleDebug         # androidApp/build/outputs/apk/debug/androidApp-debug.apk
./gradlew --stop                            # the daemons hold ~3.5 GB between them
```

Live test of the SignalR client against a running Fleet (skipped unless the variables are set):

```sh
FLEET_LIVE_URL=http://127.0.0.1:5431 FLEET_LIVE_TOKEN=... FLEET_LIVE_SESSION=<session id> \
  ./gradlew :shared:testAndroidHostTest --tests '*LiveHubTest*' --rerun
```

Run it on an emulator against a Fleet on the same machine:

```sh
adb install -r androidApp/build/outputs/apk/debug/androidApp-debug.apk
adb reverse tcp:5431 tcp:5431               # the app talks to http://127.0.0.1:5431 (debug allows cleartext there)
adb shell pm grant io.tryweave.fleet.kmp android.permission.POST_NOTIFICATIONS
adb shell am start -W -n io.tryweave.fleet.kmp/io.tryweave.fleet.android.MainActivity
```

Get a pairing code from Fleet (owner token): `POST /api/machine/pairing` with `{"baseUrl":"http://127.0.0.1:5431"}`,
then type the `manualCode` (`XXXX-XXXX`) into the app's Pair screen.

## iPhone (macOS only)

Kotlin/Native's Apple targets only build on macOS with Xcode. On a macOS runner (Xcode 16+, JDK 17, XcodeGen):

```sh
cd mobile-kmp
./gradlew :shared:assembleFleetSharedDebugXCFramework
#   -> shared/build/XCFrameworks/debug/FleetShared.xcframework (iosArm64 + iosSimulatorArm64, static)
# (just the simulator slice, faster: ./gradlew :shared:linkDebugFrameworkIosSimulatorArm64)
./gradlew :shared:iosSimulatorArm64Test     # the same commonTest suite, on Kotlin/Native

cd iosApp
brew install xcodegen                       # if needed
xcodegen generate                           # writes FleetKMP.xcodeproj from project.yml
xcodebuild -project FleetKMP.xcodeproj -scheme FleetKMP -configuration Debug \
  -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath build CODE_SIGNING_ALLOWED=NO build
```

A GitHub Actions job is sketched in `ci/ios-build.yml` (copy it into `.github/workflows/` to use it).

To run it: boot a simulator, `xcrun simctl install booted build/Build/Products/Debug-iphonesimulator/FleetKMP.app`,
`xcrun simctl launch booted io.tryweave.fleet.kmp`. The simulator reaches a Fleet on the same Mac at
`http://127.0.0.1:<port>` (`NSAllowsArbitraryLoads`, spike only).

What the iPhone app has: Pair / Sessions / Session screens in Fleet's look (SwiftUI), the token in the Keychain
(Kotlin, `KeychainSecureStore`), notification categories with Allow once / Deny and a text-input answer handled in
`AppDelegate` through the shared client, and a Widget Extension with an ActivityKit Live Activity (lock screen +
Dynamic Island: title, Working / Needs you, elapsed time) started and updated from the hub.
