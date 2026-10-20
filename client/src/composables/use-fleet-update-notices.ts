import { watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { describeUpdatedNotice, describeUpdateChip } from "@/lib/fleet-updates";
import { useFleetUpdatesWatch } from "@/composables/use-fleet-updates";
import { useMachinesStore } from "@/stores/machines";
import { useNoticesStore } from "@/stores/notices";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useWhatsNew } from "@/composables/use-whats-new";

const CHIP_ID = "fleet-updates-waiting";

/**
 * With the Updates switch on: tells the user once when a machine has updated itself ("falcon updated to 0.59.1"), and
 * keeps a chip in the status bar that counts the Fleets that can update and opens Settings → Updates.
 */
export function useFleetUpdateNotices(): void {
  const notices = useNoticesStore();
  const machines = useMachinesStore();
  const router = useRouter();
  const { setActiveSection } = useSettingsNav();
  const { openWhatsNew } = useWhatsNew();
  const { enabled, waiting } = useFleetUpdatesWatch();

  function openUpdates(): void {
    setActiveSection("updates");
    void router.navigate({ to: "/settings" });
  }

  watch(
    [enabled, waiting],
    ([on, count]) => {
      const chip = on ? describeUpdateChip(count) : null;
      if (!chip) {
        notices.remove(CHIP_ID);
        return;
      }
      notices.post({ id: CHIP_ID, title: chip, chip, quiet: true, onChipClick: openUpdates });
    },
    { immediate: true },
  );

  // The notice's id names the machine and the version, so each is shown once however often the machine is read again.
  watch(
    [enabled, () => machines.entries],
    ([on, entries]) => {
      if (!on) return;
      for (const entry of entries) {
        const news = describeUpdatedNotice(entry.key, entry.name, entry.info?.version ?? null, entry.info?.update?.lastUpdate, new Date());
        if (!news || notices.has(news.id)) continue;
        notices.post({
          id: news.id,
          title: news.title,
          body: news.body,
          actions: [
            {
              label: "What's new",
              tone: "primary",
              run: () => {
                openWhatsNew(news.version);
                notices.settle(news.id);
              },
            },
            { label: "Dismiss", run: () => notices.settle(news.id) },
          ],
        });
      }
    },
    { immediate: true },
  );
}
