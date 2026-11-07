<script setup lang="ts">
import { computed, nextTick, watch } from "vue";
import MachineScope from "@/components/layout/MachineScope.vue";
import { Button } from "@/components/ui/button";
import AppearanceSection from "@/components/settings/AppearanceSection.vue";
import CredentialsSection from "@/components/settings/CredentialsSection.vue";
import GeneralSection from "@/components/settings/GeneralSection.vue";
import HarnessesSection from "@/components/settings/HarnessesSection.vue";
import LabsSection from "@/components/settings/LabsSection.vue";
import MachinesSection from "@/components/settings/MachinesSection.vue";
import MemorySection from "@/components/settings/MemorySection.vue";
import PermissionsSection from "@/components/settings/PermissionsSection.vue";
import SkillsToolsPage from "@/components/settings/SkillsToolsPage.vue";
import StorageSection from "@/components/settings/StorageSection.vue";
import UpdatesSection from "@/components/settings/UpdatesSection.vue";
import WorkspaceSection from "@/components/settings/WorkspaceSection.vue";
import WorktreeNamingSection from "@/components/settings/WorktreeNamingSection.vue";
import { useSettingsMachine } from "@/composables/use-settings-machine";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useMachinesStore } from "@/stores/machines";
import PluginPage from "@/components/settings/PluginPage.vue";
import PluginsOverview from "@/components/settings/PluginsOverview.vue";
import { pluginManifests } from "@/plugins/registry";

const PLUGIN_PREFIX = "plugin:";

/** Each page's own title and one line under it. */
const PAGES: Readonly<Record<string, { title: string; line: string }>> = {
  general: { title: "General", line: "Everyday behaviour." },
  appearance: { title: "Appearance", line: "How Fleet looks." },
  harnesses: { title: "Harnesses", line: "The runtimes Fleet drives sessions with. Everything about one harness is on its tab." },
  permissions: { title: "What agents can do", line: "Edits, commands, messages and other machines: one setting each, with its exceptions." },
  skills: { title: "Skills & tools", line: "What agents can use: skills, tools and, with Mods on, mods." },
  memory: { title: "Memory", line: "What Fleet remembers about each repository." },
  folders: { title: "Folders", line: "Where your repositories are, how worktrees are named, and the disk they take." },
  machines: { title: "Machines", line: "The machines you work on, phones and devices, and the access token." },
  labs: { title: "Labs", line: "Experiments. They can change or go away, and each is off until you turn it on." },
  updates: { title: "Updates & about", line: "Every version and update: Fleet, harnesses and machines, and what's new." },
  plugins: { title: "Plugins", line: "Add-ons that put things in Fleet: rail icons, settings pages and pickers. Turn one off and everything it adds goes with it." },
  credentials: { title: "API keys", line: "Credentials for model providers." },
};

const { activeSection, activeAnchor } = useSettingsNav();
// Settings belong to a machine: when another one is live, these are its settings.
const machines = useMachinesStore();
// With "Switch machines without reloading" on, Settings shows the machine you're on, or the one picked in the menu.
const settingsMachine = useSettingsMachine();

/**
 * Pages that ask the machine Settings shows. The rest ask the live machine, so for another machine they say so rather
 * than show the live machine's settings as its own. Appearance and Machines belong to this browser, not a machine.
 */
const MACHINE_PAGES: readonly string[] = ["harnesses", "folders"];
const BROWSER_PAGES: readonly string[] = ["appearance", "machines"];
const isBrowserPage = computed(() => BROWSER_PAGES.includes(activeSection.value) || activeSection.value === "plugins" || pluginId.value !== null);
const comesLater = computed(() =>
  settingsMachine.enabled.value && settingsMachine.isOther.value && !isBrowserPage.value && !MACHINE_PAGES.includes(activeSection.value));

// A plugin's page is drawn from the plugin registry; core never imports the plugin.
const pluginId = computed(() =>
  activeSection.value.startsWith(PLUGIN_PREFIX) ? activeSection.value.slice(PLUGIN_PREFIX.length) : null,
);

const header = computed(() => {
  if (pluginId.value) {
    const descriptor = pluginManifests.get(pluginId.value)?.descriptor;
    return { title: descriptor?.displayName ?? "Plugin", line: descriptor?.description ?? "" };
  }
  return PAGES[activeSection.value] ?? PAGES.general;
});

// These pages read the anchor themselves (Harnesses picks a tab, Skills & tools picks tools or mods), so they keep it.
const OWN_ANCHOR_PAGES: readonly string[] = ["harnesses", "skills"];

// Landing on a group (Folders › Storage, What agents can do › Browser) scrolls it into view, once: the anchor is spent after.
watch(
  [activeSection, activeAnchor],
  async () => {
    if (!activeAnchor.value) return;
    await nextTick();
    if (!activeAnchor.value) return;
    const target = document.querySelector(`[data-settings-anchor="${CSS.escape(activeAnchor.value)}"], [data-setting-group="${CSS.escape(activeAnchor.value)}"], [data-setting-row="${CSS.escape(activeAnchor.value)}"]`);
    if (!target) return;
    target.scrollIntoView({ block: "start", behavior: "smooth" });
    // Left alone when the group isn't there yet, so one that appears later still gets scrolled to.
    if (!OWN_ANCHOR_PAGES.includes(activeSection.value)) activeAnchor.value = null;
  },
  { flush: "post", immediate: true },
);
</script>

<template>
  <section class="grid grid-cols-1 gap-6">
    <div>
      <h1
        class="text-2xl font-semibold tracking-tight text-text"
        data-testid="settings-page-title"
      >
        {{ header.title }}
        <span
          v-if="settingsMachine.enabled.value"
          class="settings-machine-chip"
          :class="{ 'settings-machine-chip--browser': isBrowserPage }"
          data-testid="settings-machine-chip"
        >{{ isBrowserPage ? "This browser" : settingsMachine.entry.value.name }}</span>
      </h1>
      <p class="mt-1 text-sm text-muted">
        {{ header.line }}
      </p>
      <p
        v-if="!settingsMachine.enabled.value && !machines.live.isHome && activeSection !== 'machines'"
        class="mt-2 text-sm text-muted"
        data-testid="settings-machine-note"
      >
        These are the settings of <span class="font-mono font-semibold text-[var(--coral)]">{{ machines.live.name }}</span>, the machine you're working in.
      </p>
    </div>

    <MachineScope
      :key="settingsMachine.key.value"
      :target="settingsMachine.target.value"
    >
      <div
        class="settings-machine-pane grid grid-cols-1 gap-6"
        data-testid="settings-machine-pane"
      >
        <div
          v-if="comesLater"
          class="flex flex-wrap items-center gap-3 rounded-btn border border-border px-4 py-3 text-sm text-muted"
          data-testid="settings-machine-later"
        >
          <span>
            {{ header.title }} on <span class="font-mono font-semibold text-[var(--coral)]">{{ settingsMachine.entry.value.name }}</span> comes in a later step.
          </span>
          <Button
            variant="outline"
            size="sm"
            data-testid="settings-machine-show-live"
            @click="settingsMachine.pick(machines.liveKey)"
          >
            Show {{ machines.live.name }}'s {{ header.title }}
          </Button>
        </div>

      <GeneralSection v-else-if="activeSection === 'general'" />

      <AppearanceSection v-else-if="activeSection === 'appearance'" />

      <HarnessesSection v-else-if="activeSection === 'harnesses'" />

      <PermissionsSection v-else-if="activeSection === 'permissions'" />

      <SkillsToolsPage v-else-if="activeSection === 'skills'" />

      <MemorySection v-else-if="activeSection === 'memory'" />

      <template v-else-if="activeSection === 'folders'">
        <WorkspaceSection />
        <WorktreeNamingSection />
        <div data-settings-anchor="storage">
          <StorageSection />
        </div>
      </template>

      <MachinesSection v-else-if="activeSection === 'machines'" />

      <LabsSection v-else-if="activeSection === 'labs'" />

      <UpdatesSection v-else-if="activeSection === 'updates'" />

      <CredentialsSection v-else-if="activeSection === 'credentials'" />

      <PluginsOverview v-else-if="activeSection === 'plugins'" />

      <PluginPage
        v-else-if="pluginId"
        :plugin-id="pluginId"
      />
      </div>
    </MachineScope>
  </section>
</template>

<style scoped>
.settings-machine-chip { display: inline-block; margin-left: 10px; padding: 1px 6px; border-radius: 4px; vertical-align: middle; font-family: var(--font-mono, ui-monospace, monospace); font-size: 0.78571rem; font-weight: 600; letter-spacing: 0; color: var(--coral); background: color-mix(in srgb, var(--coral) 14%, transparent); }
.settings-machine-chip--browser { color: var(--muted); background: color-mix(in srgb, var(--text) 8%, transparent); }
.settings-machine-pane { animation: settings-machine-fade 180ms ease-out; }
@keyframes settings-machine-fade { from { opacity: 0; } to { opacity: 1; } }
@media (prefers-reduced-motion: reduce) { .settings-machine-pane { animation: none; } }
</style>
