import { onMounted, onUnmounted, watch } from "vue";
import { ExternalLink, RotateCw, TriangleAlert } from "lucide-vue-next";
import { api } from "@/api/client";
import { useUpdateStatus, type UpdateStatus } from "@/composables/use-update-status";
import { getDesktopBridge, releaseNotesUrl, type DesktopUpdateState } from "@/lib/desktop";
import { useNoticesStore, type Notice } from "@/stores/notices";

/** How long "Updated to …" stays in the status bar after Fleet restarts into a new version. */
export const UPDATED_CHIP_MS = 12_000;
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

/**
 * Tells the user once, quietly, that a new Fleet is ready, from whichever updates this Fleet: the desktop app's own
 * updater, or the server's for the `fleet` CLI. The card settles into a status bar chip that stays until the update
 * is installed. After a restart into a new version, "Updated to …" shows in the status bar for a few seconds.
 */
export function useUpdateNotices(): void {
  const notices = useNoticesStore();
  const bridge = getDesktopBridge();
  const { updateStatus } = useUpdateStatus();
  let stopListening: (() => void) | undefined;
  let stopShowUpdate: (() => void) | undefined;
  let appNoticeId: string | null = null;

  const whatsNew = (version: string) => ({ label: "What's new", href: releaseNotesUrl(version) });

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
    if (!notices.has(notice.id)) notices.post(notice);
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
    } else if (status.status === "uptodate") {
      notices.removeWhere(SERVER);
    }
  }

  // ── "Updated to …" after a restart into a new version ──────────────────
  function noteRunningVersion(source: "app" | "server", version: string): void {
    const previous = readLastVersion(source);
    writeLastVersion(source, version);
    if (!previous || !isNewerVersion(version, previous)) return;
    const url = releaseNotesUrl(version);
    notices.post({
      id: `updated:${source}:${version}`,
      title: `Updated to Fleet ${version}. See what's new.`,
      chip: `Updated to ${version}`,
      quiet: true,
      expiresMs: UPDATED_CHIP_MS,
      onChipClick: () => window.open(url, "_blank", "noopener,noreferrer"),
    });
  }

  watch(
    updateStatus,
    (status) => {
      syncServer(status);
      // A Fleet the app runs has the app's version, which the app's own source reports.
      if (status && status.status !== "managed" && status.currentVersion && !bridge) noteRunningVersion("server", status.currentVersion);
    },
    { immediate: true },
  );

  onMounted(async () => {
    if (!bridge) return;
    noteRunningVersion("app", bridge.version);
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
