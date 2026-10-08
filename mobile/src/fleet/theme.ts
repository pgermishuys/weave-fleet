// Fleet's own tokens (client/src/assets/main.css), so the app looks like Fleet on both platforms rather than like
// iOS or Material.
import { useColorScheme } from "react-native";

const dark = {
  bg: "#0d0d10",
  panel: "#141418",
  card: "#1b1b20",
  border: "rgba(255,255,255,0.075)",
  text: "#e8e8ec",
  muted: "#8e8e9a",
  accent: "#6366f1",
  accentDim: "rgba(99,102,241,0.15)",
  onAccent: "#ffffff",
  running: "#22c55e",
  waiting: "#f59e0b",
  waitingDim: "rgba(245,158,11,0.10)",
  error: "#ef4444",
  complete: "#3b82f6",
};

const light: typeof dark = {
  bg: "#F4F2EF",
  panel: "#FFFFFF",
  card: "#FFFFFF",
  border: "rgba(26,25,24,0.09)",
  text: "#1A1918",
  muted: "#6E6A65",
  accent: "#5B6EC7",
  accentDim: "rgba(91,110,199,0.12)",
  onAccent: "#ffffff",
  running: "#16a34a",
  waiting: "#d97706",
  waitingDim: "rgba(217,119,6,0.08)",
  error: "#dc2626",
  complete: "#2563eb",
};

export type Palette = typeof dark;

export function usePalette(): Palette {
  return useColorScheme() === "light" ? light : dark;
}

export const font = {
  regular: "Inter_400Regular",
  medium: "Inter_500Medium",
  semibold: "Inter_600SemiBold",
  mono: "JetBrainsMono_400Regular",
};

export const radius = { card: 10, button: 8, panel: 12 };
export const ROW = 44;
