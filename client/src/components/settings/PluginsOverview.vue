<script setup lang="ts">
import { computed } from "vue";
import { Puzzle } from "lucide-vue-next";
import SettingSwitch from "@/components/settings/SettingSwitch.vue";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { setPluginEnabledEverywhere } from "@/plugins/use-plugin-switch";
import { usePluginRuntime } from "@/plugins/composable";
import { describeContributions, getConfigPage, isPluginEnabled } from "@/plugins/slots";

// Everything here is read from the plugin registry and each plugin's contribution points; no plugin is named.
const pluginRuntime = usePluginRuntime();
const { setActiveSection } = useSettingsNav();

const rows = computed(() =>
  pluginRuntime.manifests.value
    .filter((manifest) => !manifest.descriptor.alwaysOn)
    .map((manifest) => {
      const id = manifest.descriptor.id;
      const enabled = isPluginEnabled(id);
      const page = getConfigPage(id);
      return {
        id,
        name: manifest.descriptor.displayName,
        description: manifest.descriptor.description ?? "",
        icon: manifest.contributions?.configPage?.icon ?? Puzzle,
        enabled,
        // The areas it adds to, once each, whether or not it is on: that is what turning it on would add.
        areas: [...new Set(describeContributions(manifest).map((added) => added.area))],
        // A plugin's page is open while it is on and has settings; a plugin that is off is still reachable to switch it on.
        hasPage: Boolean(page) || Boolean(manifest.contributions?.configPage),
      };
    }),
);
</script>

<template>
  <section
    class="grid gap-3"
    data-testid="plugins-overview"
  >
    <div class="flex items-baseline justify-between gap-4">
      <h2 class="text-sm font-semibold text-text">
        All plugins
      </h2>
      <p class="text-xs text-muted">
        Each switch adds or removes everything below
      </p>
    </div>

    <div class="overflow-hidden rounded-card border border-border bg-card-bg">
      <p
        v-if="rows.length === 0"
        class="p-4 text-sm text-muted"
      >
        No plugins are installed.
      </p>
      <article
        v-for="row in rows"
        :key="row.id"
        class="flex items-start gap-3 border-b border-border p-4 last:border-b-0"
        :data-plugin-row="row.id"
      >
        <span class="grid h-9 w-9 shrink-0 place-items-center rounded-btn border border-border bg-main-bg text-text">
          <component
            :is="row.icon"
            :size="18"
            aria-hidden="true"
          />
        </span>
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="text-sm font-semibold text-text hover:underline disabled:no-underline"
              :disabled="!row.hasPage"
              @click="setActiveSection(`plugin:${row.id}`)"
            >
              {{ row.name }}
            </button>
            <span class="rounded-full border border-border px-1.5 text-[0.71429rem] font-semibold text-muted">Built in</span>
          </div>
          <p
            v-if="row.description"
            class="mt-0.5 text-sm text-muted"
          >
            {{ row.description }}
          </p>
          <div
            v-if="row.areas.length > 0"
            class="mt-2 flex flex-wrap items-center gap-1.5"
            :aria-label="`${row.name} adds to`"
          >
            <span class="text-xs text-muted">Adds to</span>
            <span
              v-for="area in row.areas"
              :key="area"
              class="rounded-btn border border-border bg-main-bg px-1.5 py-0.5 text-xs text-text"
              data-testid="plugin-adds-chip"
            >{{ area }}</span>
          </div>
          <p class="mt-2 text-xs text-muted">
            Built in to Fleet
          </p>
        </div>
        <SettingSwitch
          :checked="row.enabled"
          :aria-label="`${row.name} on or off`"
          :data-testid="`plugin-switch-${row.id}`"
          @click="void setPluginEnabledEverywhere(row.id, !row.enabled)"
        />
      </article>
    </div>
  </section>
</template>
