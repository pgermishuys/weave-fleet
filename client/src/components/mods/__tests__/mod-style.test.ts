import { describe, expect, it } from "vitest";
import { MOD_COLOR_ROLES, MOD_ICON_NAMES, MOD_SPACES, MOD_TONES } from "@/lib/mods/types";
import { MOD_ICONS } from "@/components/mods/mod-icons";
import { roleColor, spacePx, toneColor } from "@/components/mods/mod-style";

describe("mod-style", () => {
  it("maps each colour role to a theme variable", () => {
    expect(Object.fromEntries(MOD_COLOR_ROLES.map((role) => [role, roleColor(role)]))).toEqual({
      text: "var(--text)",
      muted: "var(--muted)",
      accent: "var(--accent)",
      good: "var(--running)",
      warn: "var(--idle)",
      bad: "var(--error)",
    });
  });

  it("maps each tone, neutral to muted", () => {
    expect(Object.fromEntries(MOD_TONES.map((tone) => [tone, toneColor(tone)]))).toEqual({
      good: "var(--running)",
      warn: "var(--idle)",
      bad: "var(--error)",
      neutral: "var(--muted)",
      accent: "var(--accent)",
    });
  });

  it("returns nothing for an unknown role", () => {
    expect(roleColor("pink")).toBeUndefined();
    expect(roleColor(3)).toBeUndefined();
  });

  it("spaces in 4 px steps", () => {
    expect(MOD_SPACES.map((n) => spacePx(n))).toEqual(["0px", "4px", "8px", "12px", "16px", "24px", "32px"]);
    expect(spacePx("x")).toBeUndefined();
  });

  it("maps every icon name to a component", () => {
    for (const name of MOD_ICON_NAMES) expect(MOD_ICONS[name], name).toBeTruthy();
    expect(Object.keys(MOD_ICONS).sort()).toEqual([...MOD_ICON_NAMES].sort());
  });
});
