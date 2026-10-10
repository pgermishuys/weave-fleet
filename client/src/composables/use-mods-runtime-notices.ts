import { onMounted, onUnmounted, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { CircleArrowUp, Download } from "lucide-vue-next";
import { onDomainEvent } from "@/composables/on-domain-event";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { liveTarget } from "@/lib/machine-target";
import { failureCause, megabytes, type ModsRuntimeView } from "@/lib/mods-runtime";
import { useModsRuntimeStore } from "@/stores/mods-runtime";
import { useNoticesStore } from "@/stores/notices";

const SECURITY_FAILED = "mods-runtime-security-failed:";
const OWN_UNSAFE = "mods-runtime-own-unsafe:";
const DRAFT_WAITING = "mods-runtime-draft-waiting";

/** A notice nobody saw within this long goes away; Settings → Features still says it. */
const NOTICE_EXPIRES_MS = 30 * 60_000;

/**
 * The Bun that mods run on, said wherever the user is (not only in Settings, where the Mods row already says it):
 * a security fix that couldn't be installed, a Bun of the user's own that needs one, and a mod waiting for a Bun
 * that's still downloading. Also keeps the runtime store following the pushed events.
 */
export function useModsRuntimeNotices(): void {
  const store = useModsRuntimeStore();
  const notices = useNoticesStore();
  const router = useRouter();
  const { setActiveSection } = useSettingsNav();
  let stopDrafts: (() => void) | null = null;

  const openSettings = {
    label: "Open Settings",
    run: () => {
      setActiveSection("features");
      void router.navigate({ to: "/settings" });
    },
  };

  function securityFailed(view: ModsRuntimeView): void {
    const { job, bun } = view;
    if (job?.kind === "security" && job.phase === "failed" && job.reason !== "cancelled" && bun) {
      const id = `${SECURITY_FAILED}${job.version}`;
      notices.removeWhere(SECURITY_FAILED, id);
      notices.post({
        id,
        title: "Bun needs a security fix",
        body: `Mods run on Bun ${bun.version}, which has a security problem fixed in ${job.version}. ${failureCause(job)}`,
        icon: CircleArrowUp,
        tone: "warn",
        chip: "Bun security fix",
        expiresMs: NOTICE_EXPIRES_MS,
        actions: [{ label: "Retry", tone: "primary", run: async () => void (await store.install()) }, openSettings],
      });
    } else {
      notices.removeWhere(SECURITY_FAILED);
    }
  }

  function ownUnsafe(view: ModsRuntimeView): void {
    const { bun } = view;
    if (bun?.source === "configured" && !bun.safe) {
      const id = `${OWN_UNSAFE}${bun.version}`;
      notices.removeWhere(OWN_UNSAFE, id);
      notices.post({
        id,
        title: "Your Bun needs a security fix",
        body: bun.message ?? `Bun ${bun.version} has a security problem. Run bun upgrade to update it.`,
        icon: CircleArrowUp,
        tone: "warn",
        chip: "Bun security fix",
        actions: [openSettings],
      });
    } else {
      notices.removeWhere(OWN_UNSAFE);
    }
  }

  /** A mod is waiting for the first Bun: say how far the download is, or drop the notice when it's over. */
  function draftWaiting(view: ModsRuntimeView, announce: boolean): void {
    const { job } = view;
    const waiting = job?.kind === "install" && store.isInstalling;
    if (!waiting || !job) {
      notices.remove(DRAFT_WAITING);
      return;
    }
    if (!announce && !notices.has(DRAFT_WAITING)) return;
    const total = megabytes(job.bytesTotal ?? view.release.size);
    notices.post({
      id: DRAFT_WAITING,
      title: "Your mod starts when Bun is installed",
      body: `Fleet is still downloading Bun ${job.version} (${megabytes(job.bytesReceived)} of ${total} MB). The mod starts as soon as it's done.`,
      icon: Download,
      actions: [openSettings],
    });
  }

  watch(
    () => store.view,
    (view) => {
      if (!view) return;
      securityFailed(view);
      ownUnsafe(view);
      draftWaiting(view, false);
    },
    { immediate: true },
  );

  onMounted(() => {
    store.listen();
    void store.load();
    // A draft (an agent's mod waiting for review) is what asks for a running Bun.
    stopDrafts = onDomainEvent(liveTarget(), "sessions", "mods.changed", (event) => {
      if (event.payload?.sessionId && store.view) draftWaiting(store.view, true);
    });
  });

  onUnmounted(() => {
    stopDrafts?.();
    store.stop();
  });
}
