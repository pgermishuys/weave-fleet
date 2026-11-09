import { watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import {
  describeUpdatedNotice,
  describeUpdateChip,
  isUpdateRule,
  parseRuleOverrides,
  UPDATE_RULE_DEFAULT_KEY,
  UPDATE_RULE_NOTICE_KEY,
  UPDATE_RULE_OVERRIDES_KEY,
} from "@/lib/fleet-updates";
import { useFleetUpdatesWatch } from "@/composables/use-fleet-updates";
import { isWindowStale, reloadAction } from "@/composables/use-stale-window-notice";
import { HOME_MACHINE_KEY } from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";
import { useNoticesStore } from "@/stores/notices";
import { usePreferencesStore } from "@/stores/preferences";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useWhatsNew } from "@/composables/use-whats-new";

const CHIP_ID = "fleet-updates-waiting";

/**
 * Tells the user once when a machine has updated itself ("falcon updated to 0.59.1"), and
 * keeps ONE chip in the status bar that counts the machines that can update (or says where a rollout is) and opens
 * Settings → Updates & about › Machines.
 */
export function useFleetUpdateNotices(): void {
  const notices = useNoticesStore();
  const machines = useMachinesStore();
  const router = useRouter();
  const { setActiveSection } = useSettingsNav();
  const { openWhatsNew } = useWhatsNew();
  const preferences = usePreferencesStore();
  preferences.ensureLoaded();
  const { waiting, rollout } = useFleetUpdatesWatch();

  /** A machine that restarted itself under a rule nobody picked: the user is told so once, ever. */
  function unpickedRuleNews(key: string, trigger: string): boolean {
    if (trigger !== "idle" && trigger !== "overnight") return false;
    if (!preferences.hasFetched || preferences.get(UPDATE_RULE_NOTICE_KEY, "false") === "true") return false;
    const chosen = isUpdateRule(preferences.get(UPDATE_RULE_DEFAULT_KEY, ""));
    return !chosen && !parseRuleOverrides(preferences.get(UPDATE_RULE_OVERRIDES_KEY, "{}"))[key];
  }

  function openMachines(): void {
    setActiveSection("updates", "machines");
    void router.navigate({ to: "/settings" });
  }

  watch(
    [waiting, rollout],
    ([count, current]) => {
      const chip = describeUpdateChip(count, current);
      if (!chip) {
        notices.remove(CHIP_ID);
        return;
      }
      notices.post({ id: CHIP_ID, title: chip, chip, quiet: true, onChipClick: openMachines });
    },
    { immediate: true },
  );

  // The notice's id names the machine and the version, so each is shown once however often the machine is read again.
  watch(
    [() => machines.entries, () => preferences.hasFetched],
    ([entries]) => {
      for (const entry of entries) {
        const news = describeUpdatedNotice(entry.key, entry.name, entry.info?.version ?? null, entry.info?.update?.lastUpdate, new Date());
        if (!news || notices.has(news.id)) continue;
        // A window that loaded before this update is still running the old version: Reload leads, What's new steps back.
        const toldRule = unpickedRuleNews(entry.key, news.trigger);
        if (toldRule) void preferences.set(UPDATE_RULE_NOTICE_KEY, "true");
        const stale = entry.key === HOME_MACHINE_KEY && isWindowStale(entry.info?.version);
        notices.post({
          id: news.id,
          title: news.title,
          body: toldRule ? `${news.body} Nobody chose an update rule, so it updates itself when nothing is in use. You can change that.` : news.body,
          actions: [
            ...(stale ? [reloadAction()] : []),
            {
              label: "What's new",
              tone: stale ? "quiet" : "primary",
              run: () => {
                openWhatsNew(news.version);
                notices.settle(news.id);
              },
            },
            ...(toldRule
              ? [{ label: "Change the rule", tone: "quiet" as const, run: () => { openMachines(); notices.settle(news.id); } }]
              : []),
            { label: "Dismiss", run: () => notices.settle(news.id) },
          ],
        });
      }
    },
    { immediate: true },
  );
}
