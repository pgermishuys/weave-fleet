import { computed, onBeforeUnmount, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref } from "vue";
import { isSessionRecapEvent, type SessionRecapPayload } from "@/lib/domain-events";
import { setSessionFocus, useWeaveSocket } from "@/composables/use-weave-socket";
import { DESKTOP_NOTIFICATIONS_PREFERENCE_KEY } from "@/composables/use-session-notifications";
import { useMachineTarget } from "@/lib/machine-target";
import { usePreferencesStore } from "@/stores/preferences";

/** The Settings → General switch for session recaps. Off unless turned on. */
export const SESSION_RECAP_PREFERENCE_KEY = "SessionRecap";

/** Window focus flickers when you click into a canvas iframe; wait for it to settle. */
const FOCUS_SETTLE_MS = 250;

/**
 * Whether you're looking at this tab: it's visible and has focus. A focused
 * canvas iframe counts, since `document.hasFocus()` includes nested documents.
 */
function isLooking(): boolean {
  return document.visibilityState === "visible" && document.hasFocus();
}

/**
 * The open session's recap, and whether you're looking at it. Two things wait on
 * that: Fleet writes a recap a few minutes after a turn ends while no tab is
 * looking at the session, and it sends a desktop notification when a session you
 * left needs you. So this tells the server when this tab is visible and focused
 * on the session's machine, as long as Session recap is on for that machine or desktop notifications are on here.
 */
export function useSessionRecap(
  sessionId: MaybeRefOrGetter<string | null | undefined>,
): Readonly<Ref<SessionRecapPayload | null>> {
  const machine = useMachineTarget();
  // Recaps are written on the session's machine, from its Session recap switch. Desktop notifications are this
  // app's own setting, kept on the live machine (that's what Settings → General and the notifier read).
  const sessionPreferences = usePreferencesStore(machine);
  const livePreferences = usePreferencesStore();
  sessionPreferences.ensureLoaded();
  livePreferences.ensureLoaded();
  const { subscribeV2 } = useWeaveSocket(machine);
  const recap = shallowRef<SessionRecapPayload | null>(null);
  const enabled = computed(() =>
    sessionPreferences.get(SESSION_RECAP_PREFERENCE_KEY, "false") === "true"
    || livePreferences.get(DESKTOP_NOTIFICATIONS_PREFERENCE_KEY, "false") === "true");

  // The session the server has a subscription for, and what it was last told.
  let subscribedId: string | null = null;
  let reported: { sessionId: string; focused: boolean } | null = null;
  let settleTimer: ReturnType<typeof setTimeout> | null = null;

  function report(): void {
    const id = subscribedId;
    if (!id || !enabled.value) return;

    const focused = isLooking();
    if (reported?.sessionId === id && reported.focused === focused) return;

    reported = { sessionId: id, focused };
    setSessionFocus(machine, id, focused);
  }

  function reportSoon(): void {
    if (settleTimer !== null) clearTimeout(settleTimer);
    settleTimer = setTimeout(() => {
      settleTimer = null;
      report();
    }, FOCUS_SETTLE_MS);
  }

  watch(
    () => toValue(sessionId) ?? "",
    (id, _previous, onCleanup) => {
      recap.value = null;
      if (!id) return;

      const unsubscribe = subscribeV2(
        `session:${id}`,
        (snapshot) => {
          recap.value = snapshot.recap?.text ? snapshot.recap : null;
          // A new subscription, or one restored after a reconnect: the server has forgotten this tab.
          subscribedId = id;
          reported = null;
          report();
        },
        (event) => {
          if (!isSessionRecapEvent(event) || event.payload.sessionId !== id) return;
          recap.value = event.payload.text ? event.payload : null;
        },
      );

      onCleanup(() => {
        unsubscribe();
        if (reported?.sessionId === id && reported.focused) setSessionFocus(machine, id, false);
        subscribedId = null;
        reported = null;
      });
    },
    { immediate: true },
  );

  watch(enabled, (on) => {
    if (on) {
      report();
    } else if (reported?.focused) {
      setSessionFocus(machine, reported.sessionId, false);
      reported = null;
    }
  });

  document.addEventListener("visibilitychange", reportSoon);
  window.addEventListener("focus", reportSoon);
  window.addEventListener("blur", reportSoon);

  onBeforeUnmount(() => {
    document.removeEventListener("visibilitychange", reportSoon);
    window.removeEventListener("focus", reportSoon);
    window.removeEventListener("blur", reportSoon);
    if (settleTimer !== null) clearTimeout(settleTimer);
  });

  return recap;
}
