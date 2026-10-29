<script setup lang="ts">
import { computed, nextTick, watch } from "vue";
import AppearanceSection from "@/components/settings/AppearanceSection.vue";
import BrowserSection from "@/components/settings/BrowserSection.vue";
import CredentialsSection from "@/components/settings/CredentialsSection.vue";
import GeneralSection from "@/components/settings/GeneralSection.vue";
import HarnessesSection from "@/components/settings/HarnessesSection.vue";
import LabsSection from "@/components/settings/LabsSection.vue";
import MachinesSection from "@/components/settings/MachinesSection.vue";
import MemorySection from "@/components/settings/MemorySection.vue";
import PermissionsSection from "@/components/settings/PermissionsSection.vue";
import SkillsToolsPage from "@/components/settings/SkillsToolsPage.vue";
import StorageSection from "@/components/settings/StorageSection.vue";
import SystemSection from "@/components/settings/SystemSection.vue";
import WeaveSection from "@/components/settings/WeaveSection.vue";
import WhatsNewSection from "@/components/settings/WhatsNewSection.vue";
import WorkflowsSection from "@/components/settings/WorkflowsSection.vue";
import WorkspaceSection from "@/components/settings/WorkspaceSection.vue";
import WorktreeNamingSection from "@/components/settings/WorktreeNamingSection.vue";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useMachinesStore } from "@/stores/machines";
import { getConfigPage } from "@/plugins/slots";

const INTEGRATION_PREFIX = "integration:";

/** Each page's own title and one line under it. */
const PAGES: Readonly<Record<string, { title: string; line: string }>> = {
  general: { title: "General", line: "Everyday behaviour." },
  appearance: { title: "Appearance", line: "How Fleet looks." },
  harnesses: { title: "Harnesses", line: "The coding agents Fleet can run, and how each is set up." },
  permissions: { title: "Permissions", line: "What agents may do without asking, and whether they can use a browser." },
  skills: { title: "Skills & tools", line: "What agents can use: skills, tools and, with Mods on, mods." },
  memory: { title: "Memory", line: "What Fleet remembers about each repository." },
  weave: { title: "Weave", line: "The Weave plugin and its config for each harness." },
  workflows: { title: "Workflows", line: "Model roles for workflow steps." },
  folders: { title: "Folders", line: "Where your repositories are, how worktrees are named, and the disk they take." },
  machines: { title: "Machines", line: "The machines you work on, phones and devices, and the access token." },
  labs: { title: "Labs", line: "Experiments. They can change or go away, and each is off until you turn it on." },
  updates: { title: "Updates & about", line: "Fleet's version and updates, and what's new." },
  credentials: { title: "API keys", line: "Credentials for model providers." },
};

const { activeSection, activeAnchor } = useSettingsNav();
// Settings belong to a machine: when another one is live, these are its settings.
const machines = useMachinesStore();

// An add-on's page comes through the plugin contribution point; core never imports the add-on.
const integrationPage = computed(() =>
  activeSection.value.startsWith(INTEGRATION_PREFIX)
    ? getConfigPage(activeSection.value.slice(INTEGRATION_PREFIX.length))
    : undefined,
);

const header = computed(() => {
  if (activeSection.value.startsWith(INTEGRATION_PREFIX)) {
    const page = integrationPage.value;
    return { title: page?.navLabel ?? page?.title ?? "Integration", line: page?.title ?? "" };
  }
  return PAGES[activeSection.value] ?? PAGES.general;
});

// Landing on a group (Folders › Storage, Permissions › Browser) scrolls it into view.
watch(
  [activeSection, activeAnchor],
  async () => {
    if (!activeAnchor.value) return;
    await nextTick();
    document
      .querySelector(`[data-settings-anchor="${CSS.escape(activeAnchor.value)}"]`)
      ?.scrollIntoView({ block: "start", behavior: "smooth" });
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
      </h1>
      <p class="mt-1 text-sm text-muted">
        {{ header.line }}
      </p>
      <p
        v-if="!machines.live.isHome && activeSection !== 'machines'"
        class="mt-2 text-sm text-muted"
        data-testid="settings-machine-note"
      >
        These are the settings of <span class="font-mono font-semibold text-[var(--coral)]">{{ machines.live.name }}</span>, the machine you're working in.
      </p>
    </div>

    <GeneralSection v-if="activeSection === 'general'" />

    <AppearanceSection v-else-if="activeSection === 'appearance'" />

    <HarnessesSection v-else-if="activeSection === 'harnesses'" />

    <template v-else-if="activeSection === 'permissions'">
      <PermissionsSection />
      <div data-settings-anchor="browser">
        <BrowserSection />
      </div>
    </template>

    <SkillsToolsPage v-else-if="activeSection === 'skills'" />

    <MemorySection v-else-if="activeSection === 'memory'" />

    <WeaveSection v-else-if="activeSection === 'weave'" />

    <WorkflowsSection v-else-if="activeSection === 'workflows'" />

    <template v-else-if="activeSection === 'folders'">
      <WorkspaceSection />
      <WorktreeNamingSection />
      <div data-settings-anchor="storage">
        <StorageSection />
      </div>
    </template>

    <MachinesSection v-else-if="activeSection === 'machines'" />

    <LabsSection v-else-if="activeSection === 'labs'" />

    <template v-else-if="activeSection === 'updates'">
      <SystemSection />
      <WhatsNewSection />
    </template>

    <CredentialsSection v-else-if="activeSection === 'credentials'" />

    <component
      :is="integrationPage.component"
      v-else-if="integrationPage"
    />
  </section>
</template>
