import { computed, shallowRef } from "vue";
import { readyRegistration } from "@/composables/use-service-worker";
import { readCredentialsSync } from "@/lib/device-credentials";
import { pushState, readPushEnvironment, type PushState } from "@/lib/push/capabilities";
import type { PushKind } from "@/lib/push/payload";
import {
  deleteSubscription,
  fetchVapidKey,
  lookupSubscription,
  saveSubscription,
  sendTestPush,
  subscribeBrowser,
  type PushChoices,
  type PushTarget,
} from "@/lib/push/subscribe";

/** What a phone gets unless it chooses otherwise: everything, quiet at the desk. */
export const DEFAULT_CHOICES: PushChoices = {
  kinds: ["permission", "question", "finished", "failed", "workflow"],
  quietWhenDesk: true,
};

const ENDPOINT_KEY = "weave:push-endpoint";

/** Home: this page's own origin, with the phone's device token when it has one. */
function homeTarget(): PushTarget {
  return { baseUrl: "", token: readCredentialsSync()?.token ?? null };
}

function rememberEndpoint(endpoint: string | null): void {
  try {
    if (endpoint) localStorage.setItem(ENDPOINT_KEY, endpoint);
    else localStorage.removeItem(ENDPOINT_KEY);
  } catch {
    // Without storage the next check just re-saves.
  }
}

function rememberedEndpoint(): string | null {
  try {
    return localStorage.getItem(ENDPOINT_KEY);
  } catch {
    return null;
  }
}

/**
 * This browser's notifications from its home machine: whether it can have them, whether they're on, which kinds,
 * and the actions the setup screen offers. Turning on must happen in a click handler: iOS ignores a permission
 * request that isn't.
 */
export function usePushSubscription() {
  const state = shallowRef<PushState>(pushState(readPushEnvironment()));
  const subscribed = shallowRef(false);
  const choices = shallowRef<PushChoices>({ ...DEFAULT_CHOICES });
  const busy = shallowRef(false);
  const error = shallowRef<string | null>(null);
  const permissionRevoked = shallowRef(false);

  async function currentSubscription(): Promise<PushSubscription | null> {
    const registration = await readyRegistration();
    return registration ? await registration.pushManager.getSubscription() : null;
  }

  /**
   * Reads what home has for this browser, and repairs drift: a subscription the browser rotated or home forgot is
   * saved again, keeping the choices. Called when the app opens and when it comes back on screen.
   */
  async function refresh(): Promise<void> {
    state.value = pushState(readPushEnvironment());
    if (state.value !== "ready" && state.value !== "denied") return;

    const remembered = rememberedEndpoint();
    if (state.value === "denied") {
      subscribed.value = false;
      permissionRevoked.value = remembered !== null;
      return;
    }

    try {
      const subscription = await currentSubscription();
      if (!subscription) {
        subscribed.value = false;
        // Was on, and the browser dropped it while permission stays granted: subscribe again quietly.
        permissionRevoked.value = remembered !== null && Notification.permission !== "granted";
        if (remembered && Notification.permission === "granted") await resubscribe(remembered);
        return;
      }

      const target = homeTarget();
      const saved = await lookupSubscription(target, subscription.endpoint);
      if (saved) {
        choices.value = { kinds: saved.kinds as PushKind[], quietWhenDesk: saved.quietWhenDesk };
      } else {
        // Home doesn't know this endpoint (rotated, or removed after failures): save it, carrying the old choices.
        const restored = await saveSubscription(target, subscription.toJSON(), remembered === subscription.endpoint ? choices.value : {}, remembered);
        choices.value = { kinds: restored.kinds as PushKind[], quietWhenDesk: restored.quietWhenDesk };
      }
      rememberEndpoint(subscription.endpoint);
      subscribed.value = true;
      permissionRevoked.value = false;
    } catch (failure) {
      error.value = failure instanceof Error ? failure.message : String(failure);
    }
  }

  async function resubscribe(previousEndpoint: string): Promise<void> {
    const registration = await readyRegistration();
    if (!registration) return;
    const target = homeTarget();
    const subscription = await subscribeBrowser(registration, await fetchVapidKey(target));
    const saved = await saveSubscription(target, subscription.toJSON(), {}, previousEndpoint);
    choices.value = { kinds: saved.kinds as PushKind[], quietWhenDesk: saved.quietWhenDesk };
    rememberEndpoint(subscription.endpoint);
    subscribed.value = true;
  }

  /** Asks for permission, subscribes and saves the choices. Call from a click. */
  async function turnOn(next: PushChoices = choices.value): Promise<boolean> {
    busy.value = true;
    error.value = null;
    try {
      const permission = await Notification.requestPermission();
      if (permission !== "granted") {
        state.value = permission === "denied" ? "denied" : state.value;
        error.value = permission === "denied" ? null : "Notifications weren't allowed.";
        return false;
      }

      const registration = await readyRegistration();
      if (!registration) throw new Error("Fleet's service worker isn't running here. Reload the page and try again.");
      const target = homeTarget();
      const existing = await registration.pushManager.getSubscription();
      const subscription = existing ?? await subscribeBrowser(registration, await fetchVapidKey(target));
      const saved = await saveSubscription(target, subscription.toJSON(), next);
      choices.value = { kinds: saved.kinds as PushKind[], quietWhenDesk: saved.quietWhenDesk };
      rememberEndpoint(subscription.endpoint);
      subscribed.value = true;
      permissionRevoked.value = false;
      return true;
    } catch (failure) {
      error.value = failure instanceof Error ? failure.message : String(failure);
      return false;
    } finally {
      busy.value = false;
    }
  }

  /** Saves new choices for the subscription that's on. */
  async function updateChoices(next: PushChoices): Promise<void> {
    choices.value = next;
    if (!subscribed.value) return;
    error.value = null;
    try {
      const subscription = await currentSubscription();
      if (subscription) await saveSubscription(homeTarget(), subscription.toJSON(), next);
    } catch (failure) {
      error.value = failure instanceof Error ? failure.message : String(failure);
    }
  }

  async function sendTest(): Promise<string | null> {
    error.value = null;
    try {
      const subscription = await currentSubscription();
      if (!subscription) return null;
      return await sendTestPush(homeTarget(), subscription.endpoint);
    } catch (failure) {
      error.value = failure instanceof Error ? failure.message : String(failure);
      return null;
    }
  }

  async function turnOff(): Promise<void> {
    busy.value = true;
    try {
      const subscription = await currentSubscription();
      if (subscription) {
        await deleteSubscription(homeTarget(), subscription.endpoint);
        await subscription.unsubscribe();
      }
      rememberEndpoint(null);
      subscribed.value = false;
    } catch (failure) {
      error.value = failure instanceof Error ? failure.message : String(failure);
    } finally {
      busy.value = false;
    }
  }

  const canTurnOn = computed(() => state.value === "ready");

  return { state, subscribed, choices, busy, error, permissionRevoked, canTurnOn, refresh, turnOn, updateChoices, sendTest, turnOff };
}
