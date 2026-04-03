"use client";

import { useTheme, ALL_THEMES, THEME_LABELS, type Theme } from "@/contexts/theme-context";
import { cn } from "@/lib/utils";
import { Check } from "lucide-react";

const THEME_SWATCHES: Record<Theme, { bg: string; card: string; accent: string; text: string }> = {
  default:          { bg: "#0F172A", card: "#1E293B", accent: "#A855F7", text: "#F8FAFC" },
  black:            { bg: "#000000", card: "#0A0A0A", accent: "#A855F7", text: "#FAFAFA" },
  light:            { bg: "#FFFFFF", card: "#F1F5F9", accent: "#9333EA", text: "#0F172A" },
  nord:             { bg: "#2E3440", card: "#3B4252", accent: "#88C0D0", text: "#ECEFF4" },
  dracula:          { bg: "#282A36", card: "#343746", accent: "#BD93F9", text: "#F8F8F2" },
  "solarized-dark": { bg: "#002B36", card: "#073642", accent: "#268BD2", text: "#FDF6E3" },
  "solarized-light":{ bg: "#FDF6E3", card: "#EEE8D5", accent: "#268BD2", text: "#073642" },
  monokai:          { bg: "#272822", card: "#3E3D32", accent: "#A6E22E", text: "#F8F8F2" },
  "github-dark":    { bg: "#0D1117", card: "#161B22", accent: "#58A6FF", text: "#E6EDF3" },
};

export function ThemeSwitcher() {
  const { theme, setTheme } = useTheme();

  return (
    <div className="grid grid-cols-3 gap-3">
      {ALL_THEMES.map((t) => {
        const isActive = theme === t;
        const colors = THEME_SWATCHES[t];
        return (
          <button
            key={t}
            onClick={() => setTheme(t)}
            className={cn(
              "relative flex flex-col items-center gap-2 rounded-lg border-2 p-3 transition-all",
              "hover:border-primary/50",
              isActive
                ? "border-primary bg-primary/5"
                : "border-border bg-card"
            )}
          >
            {/* Mini swatch preview */}
            <div
              className="w-full aspect-[4/3] rounded-md overflow-hidden border border-border/50"
              style={{ backgroundColor: colors.bg }}
            >
              <div className="p-1.5 h-full flex flex-col gap-1">
                {/* Mini "sidebar" + "content" layout */}
                <div className="flex gap-1 flex-1">
                  <div
                    className="w-1/3 rounded-sm"
                    style={{ backgroundColor: colors.card }}
                  />
                  <div className="flex-1 flex flex-col gap-0.5">
                    <div
                      className="h-1.5 w-3/4 rounded-full"
                      style={{ backgroundColor: colors.accent }}
                    />
                    <div
                      className="h-1 w-1/2 rounded-full opacity-50"
                      style={{ backgroundColor: colors.text }}
                    />
                    <div
                      className="h-1 w-2/3 rounded-full opacity-30"
                      style={{ backgroundColor: colors.text }}
                    />
                  </div>
                </div>
              </div>
            </div>

            {/* Label */}
            <span className="text-xs font-medium">{THEME_LABELS[t]}</span>

            {/* Checkmark */}
            {isActive && (
              <div className="absolute top-1.5 right-1.5 h-4 w-4 rounded-full bg-primary flex items-center justify-center">
                <Check className="h-2.5 w-2.5 text-primary-foreground" />
              </div>
            )}
          </button>
        );
      })}
    </div>
  );
}
