/**
 * Installing Fleet as an app on Android. Chrome offers the install once (`beforeinstallprompt`), as the page loads,
 * so it's caught from the start (main.ts) and kept until the setup screen shows an Install button. iPhone has no such
 * event: the setup screen shows the Share → Add to Home Screen steps instead.
 */
import { shallowRef } from "vue";

interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
}

/** Chrome's offer to install, while there is one. */
export const installPrompt = shallowRef<InstallPromptEvent | null>(null);
/** Fleet was installed during this visit. */
export const installed = shallowRef(false);

let listening = false;

export function captureInstallPrompt(target: Window = window): void {
  if (listening || typeof target.addEventListener !== "function") return;
  listening = true;
  target.addEventListener("beforeinstallprompt", (event) => {
    // Keep Chrome's own mini-bar away; the setup screen offers it at the right moment.
    event.preventDefault();
    installPrompt.value = event as InstallPromptEvent;
  });
  target.addEventListener("appinstalled", () => {
    installed.value = true;
    installPrompt.value = null;
  });
}

/** Shows Chrome's install dialog. True when the person installed Fleet. */
export async function promptInstall(): Promise<boolean> {
  const offer = installPrompt.value;
  if (!offer) return false;
  installPrompt.value = null;
  await offer.prompt();
  const choice = await offer.userChoice;
  if (choice.outcome === "accepted") installed.value = true;
  return choice.outcome === "accepted";
}
