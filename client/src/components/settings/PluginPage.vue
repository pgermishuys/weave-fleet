<script setup lang="ts">
import { computed } from "vue";
import { ChevronRight, Puzzle } from "lucide-vue-next";
import SettingSwitch from "@/components/settings/SettingSwitch.vue";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { setPluginEnabledEverywhere } from "@/plugins/use-plugin-switch";
import { usePluginRuntime } from "@/plugins/composable";
import { describeContributions, getConfigPage, isPluginEnabled } from "@/plugins/slots";

const props = defineProps<{ pluginId: string }>();

// A plugin's own page: its switch, its own settings (the component it contributes as `configPage`) and what it adds.
const pluginRuntime = usePluginRuntime();
const { setActiveSection } = useSettingsNav();

const manifest = computed(() => pluginRuntime.manifests.value.find((m) => m.descriptor.id === props.pluginId));
const enabled = computed(() => isPluginEnabled(props.pluginId));
const page = computed(() => getConfigPage(props.pluginId));
const descriptor = computed(() => manifest.value?.descriptor);
const status = computed(() => pluginRuntime.getStatus(props.pluginId));
const additions = computed(() => (manifest.value ? describeContributions(manifest.value) : []));
const icon = computed(() => manifest.value?.contributions?.configPage?.icon ?? Puzzle);
const name = computed(() => descriptor.value?.displayName ?? props.pluginId);
</script>

<template>
  <section
    class="grid gap-5"
    :data-testid="`plugin-page-${pluginId}`"
  >
    <nav
      class="flex items-center gap-1 text-xs text-muted"
      aria-label="Breadcrumb"
    >
      <span>Settings</span>
      <ChevronRight
        :size="12"
        aria-hidden="true"
      />
      <button
        type="button"
        class="hover:text-text hover:underline"
        @click="setActiveSection('plugins')"
      >
        Plugins
      </button>
      <ChevronRight
        :size="12"
        aria-hidden="true"
      />
      <span class="text-text">{{ name }}</span>
    </nav>

    <p
      v-if="!manifest"
      class="rounded-card border border-dashed border-border bg-card-bg p-6 text-sm text-muted"
      data-testid="plugin-not-found"
    >
      No plugin called "{{ pluginId }}" is installed.
    </p>

    <template v-else>
      <header class="flex items-start gap-3">
        <span class="grid h-10 w-10 shrink-0 place-items-center rounded-btn border border-border bg-card-bg text-text">
          <component
            :is="icon"
            :size="20"
            aria-hidden="true"
          />
        </span>
        <div class="min-w-0 flex-1">
          <h2 class="flex items-center gap-2 text-xl font-semibold tracking-tight text-text">
            {{ name }}
            <span class="rounded-full border border-border px-1.5 text-[0.71429rem] font-semibold text-muted">Built in</span>
          </h2>
          <p class="mt-0.5 text-sm text-muted">
            <span :class="enabled ? 'text-running' : ''">{{ enabled ? "On" : "Off" }}</span>
            <template v-if="enabled && status"> · {{ status.status === "connected" ? "Connected" : status.status === "error" ? "Needs attention" : "Not connected" }}</template>
            <template v-if="descriptor?.description"> · {{ descriptor.description }}</template>
          </p>
        </div>
        <SettingSwitch
          :checked="enabled"
          :aria-label="`${name} on or off`"
          data-testid="plugin-page-switch"
          @click="void setPluginEnabledEverywhere(pluginId, !enabled)"
        />
      </header>

      <template v-if="enabled && page">
        <div class="flex items-baseline justify-between gap-4">
          <h3 class="text-sm font-semibold text-text">
            Settings
          </h3>
          <p class="text-xs text-muted">
            {{ name }}'s own page
          </p>
        </div>
        <component
          :is="page.component"
          :descriptor="descriptor"
          :status="status"
        />
      </template>
      <p
        v-else-if="!enabled"
        class="rounded-card border border-dashed border-border bg-card-bg p-4 text-sm text-muted"
        data-testid="plugin-off-note"
      >
        {{ name }} is off. Its rail icon, settings and pickers are gone until you turn it on.
      </p>

      <div v-if="additions.length > 0">
        <div class="mb-2 flex items-baseline justify-between gap-4">
          <h3 class="text-sm font-semibold text-text">
            Adds to Fleet
          </h3>
          <p class="text-xs text-muted">
            {{ additions.length }} {{ additions.length === 1 ? "thing" : "things" }}
          </p>
        </div>
        <div class="overflow-hidden rounded-card border border-border bg-card-bg">
          <div
            v-for="added in additions"
            :key="`${added.point}:${added.detail}`"
            class="grid grid-cols-[8rem_1fr] gap-3 border-b border-border px-4 py-2.5 text-sm last:border-b-0"
            data-testid="plugin-addition"
          >
            <span class="text-muted">{{ added.area }}</span>
            <span class="text-text">{{ added.detail }} <code class="ml-1 font-mono text-xs text-muted">{{ added.point }}</code></span>
          </div>
        </div>
      </div>

      <div>
        <h3 class="mb-2 text-sm font-semibold text-text">
          Source
        </h3>
        <p class="rounded-card border border-border bg-card-bg px-4 py-2.5 text-sm text-muted">
          Built in to Fleet. It ships with the app and updates with it.
        </p>
      </div>
    </template>
  </section>
</template>
