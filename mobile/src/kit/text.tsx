import { Text as RNText, type TextProps } from "react-native";
import { useTheme } from "~/theme";

export type Tone = "default" | "muted" | "accent" | "error" | "waiting" | "running";
export type Weight = "regular" | "medium" | "semibold" | "bold";

/** `#rrggbb` plus an alpha, for tints of a token colour. */
export function alpha(hex: string, a: number): string {
  return hex + Math.round(a * 255).toString(16).padStart(2, "0");
}

export function Text({
  tone = "default", mono, weight = "regular", size = 15, style, ...rest
}: TextProps & { tone?: Tone; mono?: boolean; weight?: Weight; size?: number }) {
  const { color, font } = useTheme();
  const family = mono ? (weight === "regular" ? font.mono : font.monoMedium) : font[weight];
  const fg = tone === "default" ? color.text : tone === "muted" ? color.muted : color[tone];
  return <RNText {...rest} style={[{ color: fg, fontFamily: family, fontSize: size, lineHeight: Math.round(size * 1.4) }, style]} />;
}
