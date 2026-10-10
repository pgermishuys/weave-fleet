import { readonly, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { useSettingsNav } from "@/composables/use-settings-nav";

/** Where What's new was asked to go: a version's notes, or the top of the card. A new object each time, so asking twice scrolls twice. */
export interface WhatsNewRequest {
  version: string | null;
  /** Also open every version after this one up to `version`: the ones an update skipped over. */
  since?: string;
}

const request = shallowRef<WhatsNewRequest | null>(null);

/**
 * Opens What's new: Settings → System, at the version's notes. Every "What's new" in Fleet comes here instead of
 * sending the user to GitHub.
 */
export function useWhatsNew() {
  const router = useRouter();
  const { setActiveSection } = useSettingsNav();

  function openWhatsNew(version?: string | null, options: { since?: string } = {}): void {
    request.value = options.since ? { version: version ?? null, since: options.since } : { version: version ?? null };
    setActiveSection("system");
    void router.navigate({ to: "/settings" });
  }

  return { openWhatsNew };
}

/** For the What's new card: the latest request, and a way to mark it handled. */
export function useWhatsNewRequest() {
  return {
    request: readonly(request),
    clear: () => {
      request.value = null;
    },
  };
}
