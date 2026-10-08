import { Inter_400Regular, Inter_500Medium, Inter_600SemiBold } from "@expo-google-fonts/inter";
import { JetBrainsMono_400Regular } from "@expo-google-fonts/jetbrains-mono";
import { useFonts } from "expo-font";
import { Stack } from "expo-router";
import { StatusBar } from "expo-status-bar";
import { useEffect, useState } from "react";
import { View } from "react-native";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { loadCredentials } from "~/fleet/credentials";
import { trackActivity } from "~/fleet/activity-tracker";
import { hub } from "~/fleet/hub";
import { listenForResponses, notifyFromFleet, setupNotifications } from "~/fleet/notifications";
import { usePalette } from "~/fleet/theme";
import type { SessionNotificationPayload } from "@fleet/lib/domain-events";

export default function RootLayout() {
  const p = usePalette();
  const [fontsLoaded] = useFonts({ Inter_400Regular, Inter_500Medium, Inter_600SemiBold, JetBrainsMono_400Regular });
  const [credentialsLoaded, setCredentialsLoaded] = useState(false);

  useEffect(() => {
    void loadCredentials().then(() => setCredentialsLoaded(true));
  }, []);

  // Asks from any session become native notifications while the app runs.
  useEffect(() => {
    if (!credentialsLoaded) return;
    void setupNotifications();
    const stopResponses = listenForResponses();
    const stopEvents = hub.subscribeSessions((event) => {
      if (event.type === "session_notification") void notifyFromFleet(event.payload as SessionNotificationPayload);
      trackActivity(event);
    });
    return () => {
      stopResponses();
      stopEvents();
    };
  }, [credentialsLoaded]);

  if (!fontsLoaded || !credentialsLoaded) return <View style={{ flex: 1, backgroundColor: p.bg }} />;

  return (
    <SafeAreaProvider>
      <StatusBar style="auto" />
      <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: p.bg }, animation: "default" }} />
    </SafeAreaProvider>
  );
}
