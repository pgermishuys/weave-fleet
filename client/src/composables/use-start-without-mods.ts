import { computed } from "vue";
import { PowerOff } from "lucide-vue-next";
import { useModsStore } from "@/stores/mods";
import { useNoticesStore } from "@/stores/notices";

/**
 * "Start without mods" / "Turn mods back on": the menu item the command palette and the Help menu share. It exists
 * only while the Mods switch is on; the banner (`ModsSafeModeBanner`) is what shows the result.
 */
export function useStartWithoutMods() {
  const mods = useModsStore();
  const notices = useNoticesStore();

  const available = computed(() => mods.isSwitchedOn);
  const label = computed(() => (mods.safeMode ? "Turn mods back on" : "Start without mods"));

  async function toggle(): Promise<void> {
    try {
      await mods.setSafeMode(!mods.safeMode);
    } catch (error) {
      // The banner only follows a success, so a refusal needs saying somewhere.
      notices.post({
        id: `mods-safe-mode-failed-${Date.now()}`,
        title: "Couldn't change mods",
        body: error instanceof Error ? error.message : "Try again in a moment.",
        icon: PowerOff,
        tone: "warn",
        expiresMs: 20_000,
      });
    }
  }

  return { available, label, toggle };
}
