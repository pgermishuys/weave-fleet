import { createContext, useContext, useMemo, type ReactNode } from "react";
import { useColorScheme } from "react-native";
import { tokens } from "./tokens.generated";

export type Scheme = "light" | "dark";

type Colors = { [K in keyof typeof tokens.dark]: string };

export interface Theme {
  scheme: Scheme;
  color: Colors;
  font: { regular: string; medium: string; semibold: string; bold: string; mono: string; monoMedium: string };
  radius: { card: number; button: number; panel: number; sheet: number; pill: number };
}

const font: Theme["font"] = {
  regular: "Inter_400Regular",
  medium: "Inter_500Medium",
  semibold: "Inter_600SemiBold",
  bold: "Inter_700Bold",
  mono: "JetBrainsMono_400Regular",
  monoMedium: "JetBrainsMono_500Medium",
};

const radius: Theme["radius"] = { ...tokens.radius, sheet: 16, pill: 999 };

function themeFor(scheme: Scheme): Theme {
  return { scheme, color: tokens[scheme], font, radius };
}

const ThemeContext = createContext<Theme>(themeFor("dark"));

/** `override` forces a scheme (the Kit's toggle); null follows the phone. */
export function ThemeProvider({ override, children }: { override?: Scheme | null; children: ReactNode }) {
  const system = useColorScheme();
  const scheme: Scheme = override ?? (system === "light" ? "light" : "dark");
  const theme = useMemo(() => themeFor(scheme), [scheme]);
  return <ThemeContext.Provider value={theme}>{children}</ThemeContext.Provider>;
}

export function useTheme(): Theme {
  return useContext(ThemeContext);
}
