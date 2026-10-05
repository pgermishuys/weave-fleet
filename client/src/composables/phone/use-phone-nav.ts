import { useRouter } from "@tanstack/vue-router";
import { getActiveMachine } from "@/lib/machines";
import { sheetHistorySettled } from "@/lib/phone/sheet-history";

/**
 * Moving between the phone's screens as a stack: the inbox at the bottom, sessions pushed over it. PhoneStack keeps
 * `stack.depth` (how many sessions this page pushed over the inbox) as the address changes, so Back pops what was
 * pushed and, for a session opened straight from a link or a notification, goes to the inbox instead of leaving.
 */
export const stack = {
  depth: 0,
  /** Set by a swipe back as it lets go, so the pop carries on from the finger's position at its speed. */
  popFrom: null as { fromX: number; velocity: number } | null,
};

export function usePhoneNav() {
  const router = useRouter();

  async function openSession(machineId: string, sessionId: string, options: { replace?: boolean } = {}): Promise<void> {
    await sheetHistorySettled();
    await router.navigate({ to: "/phone/s/$machineId/$sessionId", params: { machineId, sessionId }, replace: options.replace });
  }

  async function back(): Promise<void> {
    await sheetHistorySettled();
    // A session on another machine was opened with a page load there; going back loads home again.
    if (getActiveMachine()) {
      window.location.assign("/phone");
      return;
    }
    if (stack.depth > 0) {
      window.history.back();
      return;
    }
    await router.navigate({ to: "/phone", replace: true });
  }

  /** Closes a screen that is a route of its own (New session, Notifications): back if this page opened it. */
  async function closeRoute(fromApp: boolean): Promise<void> {
    if (fromApp) window.history.back();
    else await router.navigate({ to: "/phone", replace: true });
  }

  return { openSession, back, closeRoute };
}
