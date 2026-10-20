/**
 * How much of each provider account's usage the user has used (`GET /api/usage/limits`), read by the server from the
 * sign-ins their harnesses already hold. One entry per provider and account, never per harness. The server pushes the
 * same body as `usage.limits` on the sessions topic. Tokens never reach the browser.
 */
import {
  USAGE_WARNING_THRESHOLD,
  type UsageLimitStatus,
  type UsageLimitWindow,
  windowLabel,
  windowShortLabel,
} from "@/lib/usage-limits";

export const USAGE_LIMITS_EVENT = "usage.limits";
/** Preference: the whole feature, off until the user turns it on. */
export const USAGE_LIMITS_PREFERENCE_KEY = "UsageLimits";
/** Preference: "false" hides the status bar chip (the feature stays on). */
export const USAGE_LIMITS_CHIP_PREFERENCE_KEY = "UsageLimitsChip";

export type ProviderUsageState = "ok" | "not_allowed" | "no_window" | "unreadable" | "signed_out" | "unsupported";

const STATES: readonly ProviderUsageState[] = ["ok", "not_allowed", "no_window", "unreadable", "signed_out", "unsupported"];

export interface ProviderCredits {
  remaining: number | null;
  limit: number | null;
  used: number | null;
}

export interface ProviderUsage {
  /** "claude", "codex", "openrouter", "opencode2". */
  provider: string;
  /** Plan or account to tell two sign-ins apart; display only. */
  account: string | null;
  name: string;
  state: ProviderUsageState;
  /** Where the sign-in was read from: "Claude Code", "OpenCode". */
  source: string | null;
  harnesses: string[];
  /** Epoch milliseconds Fleet last asked the provider. */
  readAt: number | null;
  windows: UsageLimitWindow[];
  credits: ProviderCredits | null;
}

export interface UsageLimits {
  enabled: boolean;
  providers: ProviderUsage[];
}

const num = (value: unknown): number | null => (typeof value === "number" && Number.isFinite(value) ? value : null);
const time = (value: unknown): number | null => {
  const at = typeof value === "string" ? Date.parse(value) : Number.NaN;
  return Number.isFinite(at) ? at : null;
};

/** The body from the wire, or null when it doesn't read. Entries that don't read are left out. */
export function toUsageLimits(raw: unknown): UsageLimits | null {
  if (!raw || typeof raw !== "object") return null;
  const wire = raw as Record<string, unknown>;
  if (typeof wire.enabled !== "boolean" || !Array.isArray(wire.providers)) return null;
  return {
    enabled: wire.enabled,
    providers: wire.providers.map(toProvider).filter((provider): provider is ProviderUsage => provider !== null),
  };
}

function toProvider(raw: unknown): ProviderUsage | null {
  if (!raw || typeof raw !== "object") return null;
  const wire = raw as Record<string, unknown>;
  if (typeof wire.provider !== "string") return null;
  const credits = wire.credits && typeof wire.credits === "object" ? (wire.credits as Record<string, unknown>) : null;
  return {
    provider: wire.provider,
    account: typeof wire.account === "string" ? wire.account : null,
    name: typeof wire.name === "string" && wire.name ? wire.name : wire.provider,
    // A state this client doesn't know reads as unreadable rather than as a lie.
    state: STATES.includes(wire.state as ProviderUsageState) ? (wire.state as ProviderUsageState) : "unreadable",
    source: typeof wire.source === "string" ? wire.source : null,
    harnesses: Array.isArray(wire.harnesses) ? wire.harnesses.filter((h): h is string => typeof h === "string") : [],
    readAt: time(wire.readAt),
    windows: Array.isArray(wire.windows)
      ? wire.windows.map(toWindow).filter((window): window is UsageLimitWindow => window !== null)
      : [],
    credits: credits ? { remaining: num(credits.remaining), limit: num(credits.limit), used: num(credits.used) } : null,
  };
}

function toWindow(raw: unknown): UsageLimitWindow | null {
  if (!raw || typeof raw !== "object") return null;
  const wire = raw as Record<string, unknown>;
  if (typeof wire.window !== "string") return null;
  const utilization = num(wire.utilization);
  const windowSeconds = num(wire.windowSeconds);
  const status: UsageLimitStatus = wire.status === "rejected" || wire.status === "warning" ? wire.status : "allowed";
  return {
    window: wire.window,
    utilization: utilization === null ? null : Math.min(1, Math.max(0, utilization)),
    resetsAt: time(wire.resetsAt),
    windowSeconds: windowSeconds !== null && windowSeconds > 0 ? windowSeconds : null,
    status,
  };
}

/** Labels for Codex's windows, on top of the ones in `usage-limits`. */
export function providerWindowLabel(window: string): string {
  if (window === "primary") return "Primary limit";
  if (window === "secondary") return "Secondary limit";
  return windowLabel(window);
}

export function providerWindowShortLabel(window: string): string {
  if (window === "primary") return "primary";
  if (window === "secondary") return "secondary";
  return windowShortLabel(window);
}

/** 0 to 100, a used-up window counting as full. */
export function windowPercent(window: UsageLimitWindow): number {
  return window.status === "rejected" ? 100 : Math.round((window.utilization ?? 0) * 100);
}

const WINDOW_ORDER = ["five_hour", "primary", "seven_day", "secondary", "seven_day_opus", "seven_day_sonnet"];

/** Windows that haven't reset yet, the shortest first. */
function liveWindows(provider: ProviderUsage, now: number): UsageLimitWindow[] {
  const rank = (window: string) => {
    const index = WINDOW_ORDER.indexOf(window);
    return index === -1 ? WINDOW_ORDER.length : index;
  };
  return provider.windows
    .filter((window) => window.resetsAt === null || window.resetsAt > now)
    .sort((a, b) => rank(a.window) - rank(b.window) || a.window.localeCompare(b.window));
}

export interface TightestWindow {
  provider: ProviderUsage;
  window: UsageLimitWindow;
}

/** The fullest window of any readable provider: what the status bar chip shows. Null when none is readable. */
export function tightestWindow(providers: readonly ProviderUsage[], now: number): TightestWindow | null {
  let best: TightestWindow | null = null;
  for (const provider of providers) {
    if (provider.state !== "ok") continue;
    for (const window of liveWindows(provider, now))
      if (!best || windowPercent(window) > windowPercent(best.window)) best = { provider, window };
  }
  return best;
}

/** Amber from 80%, red when used up, quiet below. */
export function usageTone(window: UsageLimitWindow): "quiet" | "warn" | "used" {
  if (window.status === "rejected") return "used";
  return windowPercent(window) >= USAGE_WARNING_THRESHOLD * 100 ? "warn" : "quiet";
}

export function providerWindows(provider: ProviderUsage, now: number): UsageLimitWindow[] {
  return liveWindows(provider, now);
}

/** Share of the window gone by now (0 to 1), or null when the window's length or reset time isn't known. */
export function expectedShare(window: UsageLimitWindow, now: number): number | null {
  const length = window.windowSeconds;
  if (!length || length <= 0 || window.resetsAt === null) return null;
  const span = length * 1000;
  return Math.min(1, Math.max(0, (now - (window.resetsAt - span)) / span));
}

const MIN_AHEAD_SECONDS = 15 * 60;
const MIN_ELAPSED_SHARE = 0.05;

/** "45 min", "2 h", "1 d 3 h". */
export function formatDuration(seconds: number): string {
  const minutes = Math.max(1, Math.round(seconds / 60));
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h`;
  const days = Math.floor(hours / 24);
  const rest = hours % 24;
  return rest ? `${days} d ${rest} h` : `${days} d`;
}

export interface PaceNote {
  aheadBySeconds: number;
  text: string;
}

/**
 * Set only when the user is over an even pace and, at this rate, would run out at least 15 minutes before the window
 * resets. Quiet for a window under 5% gone, a used-up one and one with no length.
 */
export function paceNote(window: UsageLimitWindow, now: number): PaceNote | null {
  const share = expectedShare(window, now);
  const u = window.utilization;
  if (share === null || u === null || window.windowSeconds == null || window.resetsAt === null) return null;
  if (u <= 0 || u >= 1 || window.status === "rejected" || u <= share || share < MIN_ELAPSED_SHARE) return null;
  const elapsed = share * window.windowSeconds;
  const remaining = (window.resetsAt - now) / 1000;
  const aheadBySeconds = remaining - (elapsed * (1 - u)) / u;
  if (aheadBySeconds < MIN_AHEAD_SECONDS) return null;
  return {
    aheadBySeconds,
    text: `Ahead of pace: at this rate it runs out about ${formatDuration(aheadBySeconds)} before it resets`,
  };
}
