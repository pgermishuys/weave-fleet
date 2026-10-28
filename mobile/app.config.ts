// Fleet's phone app. FLEET_E2E=1 builds the walk-through variant: the only build that may talk plain http, and only
// to this device itself (the scratch Fleet the walk-through starts). Every other build is https only.
import type { ExpoConfig } from "expo/config";

const e2e = process.env.FLEET_E2E === "1";

// The Expo project (expo.dev, account weavefleets) that builds the app and serves its over-the-air updates.
const EAS_PROJECT_ID = "887a9ea8-49ad-4a4f-bdf3-b923a3194a0e";

const config: ExpoConfig = {
  name: "Fleet",
  slug: "fleet",
  owner: "weavefleets",
  scheme: "fleet",
  version: "0.1.0",
  orientation: "portrait",
  icon: "./assets/icon.png",
  userInterfaceStyle: "automatic",
  backgroundColor: "#0d0d10",
  ios: {
    supportsTablet: false,
    bundleIdentifier: "io.tryweave.fleet",
  },
  android: {
    package: "io.tryweave.fleet",
    adaptiveIcon: {
      backgroundColor: "#0d0d10",
      foregroundImage: "./assets/android-icon-foreground.png",
      backgroundImage: "./assets/android-icon-background.png",
      monochromeImage: "./assets/android-icon-monochrome.png",
    },
    predictiveBackGestureEnabled: false,
  },
  web: { bundler: "metro", output: "single" },
  plugins: [
    "expo-router",
    "expo-secure-store",
    "expo-font",
    "expo-dev-client",
    "expo-updates",
    ["expo-camera", { cameraPermission: "Fleet scans the pairing code your computer shows.", recordAudioAndroid: false }],
    ...(e2e ? ["./plugins/with-local-cleartext.ts"] : []),
  ],
  // An update only reaches builds whose native side matches it.
  runtimeVersion: { policy: "fingerprint" },
  // The walk-through build runs its own JS, never an update published from another commit.
  updates: { url: `https://u.expo.dev/${EAS_PROJECT_ID}`, checkAutomatically: "ON_LOAD", enabled: !e2e },
  extra: { eas: { projectId: EAS_PROJECT_ID } },
  experiments: { typedRoutes: false },
};

export default config;
