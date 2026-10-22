// Inter and JetBrains Mono, as desktop Fleet uses them. Pass to expo-font's useFonts. Inter's cv11/ss01 alternates
// (desktop turns them on) need fontVariant support RN doesn't have for custom font features, so the phone skips them.
import { Inter_400Regular, Inter_500Medium, Inter_600SemiBold, Inter_700Bold } from "@expo-google-fonts/inter";
import { JetBrainsMono_400Regular, JetBrainsMono_500Medium } from "@expo-google-fonts/jetbrains-mono";

export const fontAssets: Record<string, number> = {
  Inter_400Regular,
  Inter_500Medium,
  Inter_600SemiBold,
  Inter_700Bold,
  JetBrainsMono_400Regular,
  JetBrainsMono_500Medium,
};
