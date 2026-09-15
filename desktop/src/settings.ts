import * as fs from "node:fs";
import * as path from "node:path";

export interface Bounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** The app's own state, kept in Electron's userData directory. Fleet's settings stay in Fleet. */
export interface DesktopSettings {
  window?: Bounds & { maximized?: boolean };
  /** The port the app's own Fleet used last time, so the UI keeps its local storage (theme, drafts). */
  port?: number;
}

export class SettingsStore {
  private readonly file: string;
  private current: DesktopSettings;

  constructor(userDataDir: string) {
    this.file = path.join(userDataDir, "desktop-settings.json");
    this.current = SettingsStore.read(this.file);
  }

  get(): DesktopSettings {
    return this.current;
  }

  update(patch: Partial<DesktopSettings>): void {
    this.current = { ...this.current, ...patch };
    try {
      fs.mkdirSync(path.dirname(this.file), { recursive: true });
      fs.writeFileSync(this.file, JSON.stringify(this.current, null, 2));
    } catch {
      // Losing the window position isn't worth failing over.
    }
  }

  private static read(file: string): DesktopSettings {
    try {
      const value: unknown = JSON.parse(fs.readFileSync(file, "utf8"));
      return typeof value === "object" && value !== null ? (value as DesktopSettings) : {};
    } catch {
      return {};
    }
  }
}

/**
 * Saved bounds if they still fit on a screen (a monitor may have been unplugged), otherwise null so the window
 * opens centred at its default size.
 */
export function restorableBounds(saved: Bounds | undefined, screens: readonly Bounds[]): Bounds | null {
  if (!saved || saved.width < 400 || saved.height < 300) return null;
  const visible = screens.some((screen) => {
    const overlapX = Math.min(saved.x + saved.width, screen.x + screen.width) - Math.max(saved.x, screen.x);
    const overlapY = Math.min(saved.y + saved.height, screen.y + screen.height) - Math.max(saved.y, screen.y);
    return overlapX >= 100 && overlapY >= 100;
  });
  return visible ? saved : null;
}
