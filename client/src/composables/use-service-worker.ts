import { shallowRef } from "vue";

/**
 * Registers Fleet's service worker (/sw.js) and follows its "navigate" messages: when a notification is tapped and
 * Fleet is already open, the worker focuses that window and tells it where to go.
 *
 * Only in a secure context (https, or localhost) and only in a production build: the dev server has no sw.js
 * unless it was built. Set VITE_SW_DEV=1 to register it from `vite` anyway (after `bun run build`).
 */
const registration = shallowRef<ServiceWorkerRegistration | null>(null);
let started = false;

export function serviceWorkerEnabled(): boolean {
  if (typeof window === "undefined" || !("serviceWorker" in navigator) || !window.isSecureContext) return false;
  return import.meta.env.PROD || import.meta.env.VITE_SW_DEV === "1";
}

/** Registers the worker once; `navigate` is called with a same-origin path when a tapped notification asks. */
export function startServiceWorker(navigate: (path: string) => void): void {
  if (started || !serviceWorkerEnabled()) return;
  started = true;

  navigator.serviceWorker.addEventListener("message", (event: MessageEvent) => {
    const data = event.data as { type?: string; url?: string } | null;
    if (data?.type !== "navigate" || typeof data.url !== "string") return;
    if (!data.url.startsWith("/") || data.url.startsWith("//")) return;
    navigate(data.url);
  });

  navigator.serviceWorker.register("/sw.js", { scope: "/" })
    .then((registered) => {
      registration.value = registered;
    })
    .catch((error: unknown) => {
      console.warn("[fleet] service worker registration failed", error);
    });
}

/** The worker's registration once it's ready; null where there is none. */
export async function readyRegistration(): Promise<ServiceWorkerRegistration | null> {
  if (!serviceWorkerEnabled()) return null;
  return await navigator.serviceWorker.ready;
}

export function useServiceWorker() {
  return { registration, readyRegistration };
}
