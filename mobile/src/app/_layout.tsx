import { useFonts } from "expo-font";
import { Stack } from "expo-router";
import { StatusBar } from "expo-status-bar";
import * as SplashScreen from "expo-splash-screen";
import { useEffect } from "react";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { useForegroundConnections } from "~/fleet/hooks";
import { machines } from "~/fleet/machines";
import { fontAssets } from "~/theme/fonts";
import { ThemeProvider, useTheme } from "~/theme";

void SplashScreen.preventAutoHideAsync().catch(() => {});

function Shell() {
  const { color, scheme } = useTheme();
  useForegroundConnections();
  return (
    <>
      <StatusBar style={scheme === "dark" ? "light" : "dark"} />
      <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: color.chrome } }} />
    </>
  );
}

export default function RootLayout() {
  const [fontsReady] = useFonts(fontAssets);
  useEffect(() => void machines.load().catch(() => {}), []);
  useEffect(() => {
    if (fontsReady) void SplashScreen.hideAsync().catch(() => {});
  }, [fontsReady]);
  if (!fontsReady) return null;
  return (
    <SafeAreaProvider>
      <ThemeProvider>
        <Shell />
      </ThemeProvider>
    </SafeAreaProvider>
  );
}
