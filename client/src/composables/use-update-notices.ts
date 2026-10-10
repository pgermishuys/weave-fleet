import { onMounted, onUnmounted, watch } from "vue";
import { ExternalLink, RotateCw, Sparkles, TriangleAlert } from "lucide-vue-next";
import { api } from "@/api/client";
import { useReleaseNotes } from "@/composables/use-release-notes";
import { useUpdateStatus, type UpdateStatus } from "@/composables/use-update-status";
import { useWhatsNew } from "@/composables/use-whats-new";
import { getDesktopBridge, releaseNotesUrl, type DesktopUpdateState } from "@/lib/desktop";
import { changeSummary, compareVersions, parseReleaseNotes, whatsNewSummary, type WhatsNewSummary } from "@/lib/release-notes";
import { useNoticesStore, type Notice } from "@/stores/notices";

/** How long "Updated to …" stays in the status bar after Fleet restarts into a new version, when there are no notes. */
export const UPDATED_CHIP_MS = 12_000;
/** How long What's new stays before it settles: there's a list to read. */
export const WHATS_NEW_HOLD_MS = 20_000;
const LAST_VERSION_KEY = "weave:last-version";

const APP = "update:app:";
const SERVER = "update:server:";

function parts(version: string): number[] {
  return version.replace(/^v/, "").split(/[.+-]/).slice(0, 3).map((part) => Number.parseInt(part, 10) || 0);
}

/** Whether `next` is a newer release than `previous` (major.minor.patch; pre-release tags ignored). */
export function isNewerVersion(next: string, previous: string): boolean {
  const a = parts(next);
  const b = parts(previous);
  for (let i = 0; i < 3; i++) {
    if ((a[i] ?? 0) !== (b[i] ?? 0)) return (a[i] ?? 0) > (b[i] ?? 0);
  }
  return false;
}

function readLastVersion(source: string): string | null {
  try {
    return localStorage.getItem(`${LAST_VERSION_KEY}:${source}`);
  } catch {
    return null;
  }
}

function writeLastVersion(source: string, version: string): void {
  try {
    localStorage.setItem(`${LAST_VERSION_KEY}:${source}`, version);
  } catch {
    // localStorage unavailable: no "Updated to" chip next time, nothing else.
  }
}

async function workingSessions(): Promise<number> {
  try {
    const { data } = await api.GET("/api/desktop/status");
    const count = (data as { workingSessions?: unknown } | undefined)?.workingSessions;
    return typeof count === "number" ? count : 0;
  } catch {
    return 0;
  }
}

/** "0.49" for 0.49.0; a patch release keeps its patch (0.47.1). */
function shortVersion(version: string): string {
  return version.replace(/^v/, "").replace(/^(\d+\.\d+)\.0$/, "$1");
}

/** "0.48.0 and 0.49.0", or "0.45.0 to 0.49.0" for more. Releases come newest first. */
function versionList(versions: string[]): string {
  const [newest, oldest] = [versions[0], versions[versions.length - 1]];
  return versions.length === 2 ? `${oldest} and ${newest}` : `${oldest} to ${newest}`;
}

function formatDay(iso: string | null): string {
  if (!iso) return "";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

/** What the What's new card says: its title, the line under it, and what it leaves out. */
function whatsNewCopy(summary: WhatsNewSummary, previous: string, current: string): { title: string; body: string; more?: string } {
  const versions = summary.releases.map((release) => release.version);
  const left = summary.more;
  if (versions.length <= 1) {
    const day = formatDay(summary.releases[0]?.publishedAt ?? null);
    const more = left === 0 ? undefined : summary.moreAreFixes ? `+${left} more ${left === 1 ? "fix" : "fixes"}` : `+${left} more`;
    return { title: `What's new in Fleet ${shortVersion(current)}`, body: `Updated from ${previous}${day ? ` · ${day}` : ""}`, more };
  }
  const list = versionList(versions);
  return {
    title: `What's new since Fleet ${previous}`,
    body: `${versions.length} updates · ${list}`,
    more: left === 0 ? undefined : `+${left} more in ${list}`,
  };
}

/**
 * Tells the user once, quietly, that a new Fleet is ready, from whichever updates this Fleet: the desktop app's own
 * updater, or the server's for the `fleet` CLI. The card settles into a status bar chip that stays until the update
 * is installed. After a restart into a new version, What's new opens once as a card with the changes most worth a
 * look; without notes to show, "Updated to …" shows in the status bar for a few seconds instead.
 */
export function useUpdateNotices(): void {
  const notices = useNoticesStore();
  const bridge = getDesktopBridge();
  const { updateStatus } = useUpdateStatus();
  let stopListening: (() => void) | undefined;
  let stopShowUpdate: (() => void) | undefined;
  let appNoticeId: string | null = null;

  const { openWhatsNew } = useWhatsNew();
  const releaseNotes = useReleaseNotes();
  /** "3 new, 4 fixed" for each version whose notes Fleet has, for the link on its update card. */
  const counts = new Map<string, string>();
  // Opens What's new in Settings → System; the href is only for a middle-click or a copied link.
  const whatsNew = (version: string) => {
    const count = counts.get(version);
    return { label: count ? `What's new: ${count}` : "What's new", href: releaseNotesUrl(version), run: () => openWhatsNew(version) };
  };

  /** The saved notes, loaded once more if they don't have this version yet. */
  async function notesFor(version: string) {
    const has = () => releaseNotes.releases.value.some((release) => compareVersions(release.version, version) === 0);
    if (!has()) await releaseNotes.load();
    return releaseNotes.releases.value;
  }

  /** Puts the count of what's new on an update card's link, once the notes are in. */
  async function addCount(id: string, version: string): Promise<void> {
    const release = (await notesFor(version)).find((item) => compareVersions(item.version, version) === 0);
    const count = release ? changeSummary(parseReleaseNotes(release.body)) : "";
    if (!count) return;
    counts.set(version, count);
    const notice = notices.notices.find((item) => item.id === id);
    // A card that's asking something (sessions are working) has no link: it gets the count when it's back.
    if (notice?.link) notices.post({ ...notice, link: whatsNew(version) });
  }

  // ── The desktop app's updates ──────────────────────────────────────────
  function appReady(version: string): Notice {
    const id = `${APP}${version}`;
    return {
      id,
      title: `Fleet ${version} is ready`,
      body: "Installs when you restart.",
      link: whatsNew(version),
      chip: `Fleet ${version} ready`,
      actions: [
        { label: "Restart", icon: RotateCw, tone: "primary", run: () => restart(version) },
        { label: "Later", run: () => notices.settle(id) },
      ],
    };
  }

  function appAvailable(version: string): Notice {
    const id = `${APP}${version}`;
    return {
      id,
      title: `Fleet ${version} is out`,
      body: "Download it and install it over this one.",
      link: whatsNew(version),
      chip: `Fleet ${version} available`,
      actions: [
        {
          label: "Download",
          icon: ExternalLink,
          tone: "primary",
          run: async () => {
            await bridge?.installUpdate();
            notices.settle(id);
          },
        },
        { label: "Later", run: () => notices.settle(id) },
      ],
    };
  }

  async function install(id: string, confirmed: boolean): Promise<void> {
    await bridge?.installUpdate({ confirmed });
    // Still here: the app didn't restart (it asked and was told no, or the update went away).
    notices.settle(id);
  }

  async function restart(version: string): Promise<void> {
    const id = `${APP}${version}`;
    const working = await workingSessions();
    if (working === 0) {
      // The app checks again itself, so a session that started in between still gets asked about.
      await install(id, false);
      return;
    }
    const ready = appReady(version);
    notices.update(id, {
      tone: "warn",
      icon: TriangleAlert,
      title: working === 1 ? "1 session is working" : `${working} sessions are working`,
      body: "Restarting stops them. They pick up again on your next prompt.",
      link: undefined,
      actions: [
        { label: "Restart anyway", tone: "danger", run: () => install(id, true) },
        { label: "Cancel", run: () => notices.update(id, { ...ready, tone: undefined, icon: undefined }) },
      ],
    });
  }

  function syncApp(state: DesktopUpdateState | null): void {
    if (!state) return;
    const version = state.version;
    if (state.status === "ready" && version) showApp(appReady(version));
    else if (state.status === "available" && state.mode === "notify" && version) showApp(appAvailable(version));
    else if (state.status === "idle" || state.status === "off") {
      notices.removeWhere(APP);
      appNoticeId = null;
    }
    // checking, downloading and error leave what's there: a failed re-check doesn't take back a ready update.
  }

  function showApp(notice: Notice): void {
    notices.removeWhere(APP, notice.id);
    if (!notices.has(notice.id)) {
      notices.post(notice);
      void addCount(notice.id, notice.id.slice(APP.length));
    }
    appNoticeId = notice.id;
  }

  // ── The server's updates (the `fleet` CLI) ─────────────────────────────
  function syncServer(status: UpdateStatus | null): void {
    if (!status || status.status === "managed") return;
    if (status.status === "staged" && status.latestVersion) {
      const version = status.latestVersion;
      const id = `${SERVER}${version}`;
      notices.removeWhere(SERVER, id);
      if (notices.has(id)) return;
      notices.post({
        id,
        title: `Fleet ${version} is ready`,
        body: "It installs the next time you start Fleet.",
        link: whatsNew(version),
        chip: `Fleet ${version} ready`,
        actions: [{ label: "Got it", run: () => notices.settle(id) }],
      });
      void addCount(id, version);
    } else if (status.status === "uptodate") {
      notices.removeWhere(SERVER);
    }
  }

  // ── What's new after a restart into a new version ──────────────────────
  // Each device keeps the version it last ran, so each shows What's new once.
  async function noteRunningVersion(source: "app" | "server", version: string): Promise<void> {
    const previous = readLastVersion(source);
    writeLastVersion(source, version);
    if (!previous || !isNewerVersion(version, previous)) return;
    const summary = whatsNewSummary(await notesFor(version), previous, version);
    if (summary) postWhatsNew(source, summary, previous, version);
    else postUpdated(source, version);
  }

  function postWhatsNew(source: "app" | "server", summary: WhatsNewSummary, previous: string, version: string): void {
    const id = `whats-new:${source}:${version}`;
    // Skipped versions: What's new opens every one of them, not only the newest.
    const open = () => openWhatsNew(version, summary.releases.length > 1 ? { since: previous } : {});
    notices.post({
      id,
      icon: Sparkles,
      ...whatsNewCopy(summary, previous, version),
      items: summary.items.map((item) => ({ text: item.text, label: item.scope, kind: item.kind })),
      chip: `What's new in ${shortVersion(version)}`,
      holdMs: WHATS_NEW_HOLD_MS,
      onChipClick: open,
      actions: [
        {
          label: "See all changes",
          tone: "primary",
          run: () => {
            open();
            notices.settle(id);
          },
        },
        { label: "Close", run: () => notices.settle(id) },
      ],
    });
  }

  /** No notes to show (offline, or not published yet): only a quiet "Updated to …" that goes away. */
  function postUpdated(source: "app" | "server", version: string): void {
    notices.post({
      id: `updated:${source}:${version}`,
      title: `Updated to Fleet ${version}. See what's new.`,
      chip: `Updated to ${version}`,
      quiet: true,
      expiresMs: UPDATED_CHIP_MS,
      onChipClick: () => openWhatsNew(version),
    });
  }

  watch(
    updateStatus,
    (status) => {
      syncServer(status);
      // A Fleet the app runs has the app's version, which the app's own source reports.
      if (status && status.status !== "managed" && status.currentVersion && !bridge) void noteRunningVersion("server", status.currentVersion);
    },
    { immediate: true },
  );

  onMounted(async () => {
    if (!bridge) return;
    void noteRunningVersion("app", bridge.version);
    stopListening = bridge.onUpdateState(syncApp);
    stopShowUpdate = bridge.onShowUpdate?.(() => {
      if (appNoticeId) notices.reopen(appNoticeId);
    });
    syncApp(await bridge.getUpdateState());
  });

  onUnmounted(() => {
    stopListening?.();
    stopShowUpdate?.();
  });
}
