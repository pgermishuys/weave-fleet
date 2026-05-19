<script setup lang="ts">
import type { RegisteredSettingsSection } from "@/plugins/slots";
import { computed } from "vue";
import { Settings2 } from "lucide-vue-next";
import AppearanceSection from "@/components/settings/AppearanceSection.vue";
import ConfigOverviewSection from "@/components/settings/ConfigOverviewSection.vue";
import FeaturesSection from "@/components/settings/FeaturesSection.vue";
import SystemSection from "@/components/settings/SystemSection.vue";
import CredentialsSection from "@/components/settings/CredentialsSection.vue";
import SkillsSection from "@/components/settings/SkillsSection.vue";
import NuCodeSection from "@/components/settings/NuCodeSection.vue";
import WorkspaceSection from "@/components/settings/WorkspaceSection.vue";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { usePluginRuntime } from "@/plugins/composable";
import { getSettingsSections } from "@/plugins/slots";

interface DecoratedSettingsSection extends RegisteredSettingsSection {
  displayName: string;
}

const pluginRuntime = usePluginRuntime();
const { activeSection } = useSettingsNav();

const pluginSections = computed<readonly DecoratedSettingsSection[]>(() => {
  const descriptorsById = new Map(
    pluginRuntime.descriptors.value.map((descriptor) => [descriptor.id, descriptor.displayName]),
  );

  return getSettingsSections(pluginRuntime.manifests.value).map((section) => ({
    ...section,
    displayName: descriptorsById.get(section.pluginId) ?? section.title,
  }));
});
</script>

<template>
  <section class="mx-auto grid max-w-3xl gap-6">
    <div class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
      <div class="flex items-start gap-3">
        <div class="rounded-btn border border-border bg-main-bg p-2 text-text">
          <Settings2
            :size="18"
            aria-hidden="true"
          />
        </div>
        <div class="space-y-2">
          <h1 class="text-2xl font-semibold tracking-tight text-text">
            Settings
          </h1>
          <p class="text-sm text-muted">
            Manage credentials, workspace preferences, appearance, skills, system details, and plugin-provided settings.
          </p>
        </div>
      </div>
    </div>

    <WorkspaceSection v-if="activeSection === 'workspace'" />

    <CredentialsSection v-else-if="activeSection === 'credentials'" />

    <template
      v-else-if="activeSection === 'appearance'"
    >
      <AppearanceSection />
    </template>

    <SkillsSection v-else-if="activeSection === 'skills'" />

    <FeaturesSection v-else-if="activeSection === 'features'" />

    <NuCodeSection v-else-if="activeSection === 'nucode'" />

    <section
      v-else-if="activeSection === 'plugins'"
      class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
    >
      <div class="flex flex-col gap-1">
        <h2 class="text-lg font-semibold text-text">
          Plugin settings
        </h2>
        <p class="text-sm text-muted">
          Extra sections registered by installed plugins appear here automatically.
        </p>
      </div>

      <div
        v-if="pluginSections.length === 0"
        class="mt-5 rounded-card border border-dashed border-border p-6 text-center"
      >
        <p class="text-sm font-medium text-text">
          No plugin settings available
        </p>
        <p class="mt-1 text-xs text-muted">
          Registered plugins can contribute settings sections through <code>getSettingsSections()</code>.
        </p>
      </div>

      <div
        v-else
        class="mt-5 grid gap-4"
      >
        <article
          v-for="section in pluginSections"
          :key="section.id"
          class="rounded-card border border-border bg-main-bg p-4"
        >
          <div class="mb-4 flex items-start gap-3">
            <div class="rounded-btn border border-border bg-card-bg p-2 text-text">
              <component
                :is="section.icon"
                v-if="section.icon"
                :size="16"
                aria-hidden="true"
              />
              <Settings2
                v-else
                :size="16"
                aria-hidden="true"
              />
            </div>

            <div>
              <h3 class="text-sm font-semibold text-text">
                {{ section.displayName }}
              </h3>
              <p class="mt-1 text-xs text-muted">
                {{ section.title }}
              </p>
            </div>
          </div>

          <component :is="section.component" />
        </article>
      </div>
    </section>

    <template v-else-if="activeSection === 'system'">
      <SystemSection />
      <ConfigOverviewSection />
    </template>
  </section>
</template>
