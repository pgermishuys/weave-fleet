<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, useTemplateRef } from "vue";
import { LoaderCircle, Plus } from "lucide-vue-next";
import type { WeaveHarnessDetection } from "@/api/client";
import { Button } from "@/components/ui/button";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import type { useWeaveConfig } from "@/composables/use-weave-config";
import {
  WEAVE_FLAVORS,
  describeWeaveInstall,
  loadedFlavors,
  outdatedFlavorsOf,
  weaveAddedByFleet,
  weavePill,
} from "@/lib/weave-flavors";

/**
 * One harness's Weave: whether its plugin list loads Weave (with Add Weave and Remove), and whose config Weave reads in
 * Fleet sessions. Shown only for harnesses Fleet can ask about Weave. The config itself is Fleet's, one for all
 * harnesses: Edit asks the page to open its dialog.
 */
const props = defineProps<{
  harnessType: string;
  /** The page's one Weave state, so every tab shares what Fleet last asked the harnesses. */
  weave: ReturnType<typeof useWeaveConfig>;
}>();

const emit = defineEmits<{ editConfig: [] }>();

const { view, error, loading, load, ensureLoaded, changePlugin, changingPlugin, pluginNotices, setPluginNotice } = props.weave;

const detection = computed<WeaveHarnessDetection | undefined>(() =>
  view.value?.harnesses.find((harness) => harness.harnessType === props.harnessType));
const all = computed(() => view.value?.harnesses ?? []);
const flavors = computed(() => loadedFlavors(all.value));
/** Only this harness's: another harness's old Weave is that tab's to mention. */
const outdated = computed(() => outdatedFlavorsOf(detection.value ? [detection.value] : []));
const bothLoaded = computed(() => (detection.value?.installs ?? []).some((install) => install.flavor === "weave")
  && (detection.value?.installs ?? []).some((install) => install.flavor === "legacy"));

/** Some harness's plugin list is being changed: they can share one opencode.json, so every tab waits for it. */
const pluginLocked = computed(() => changingPlugin.value !== null);
const pluginBusy = computed(() => changingPlugin.value === props.harnessType);
const pluginNotice = computed(() => pluginNotices.value[props.harnessType] ?? null);

async function changeWeavePlugin(current: WeaveHarnessDetection, add: boolean): Promise<void> {
  setPluginNotice(current.harnessType, null);
  try {
    const result = await changePlugin(current.harnessType, add);
    setPluginNotice(current.harnessType, { text: result.message, tone: result.loaded ? "ok" : "warn" });
  } catch (caught) {
    setPluginNotice(current.harnessType, {
      text: caught instanceof Error ? caught.message : `Couldn't change ${current.harnessName}'s plugins.`,
      tone: "error",
    });
  }
}

const sourceLabel = computed(() => view.value?.source === "fleet"
  ? "Kept in Fleet. Weave reads it in Fleet sessions."
  : "Weave reads your own file in Fleet sessions.");

// Asking the harnesses can start one, so Fleet asks only once someone looks at Weave, and only if it hasn't answered.
const root = useTemplateRef<HTMLElement>("root");
let watcher: IntersectionObserver | undefined;

onMounted(() => {
  if (view.value) return;
  if (typeof IntersectionObserver === "undefined" || !root.value) {
    ensureLoaded();
    return;
  }
  watcher = new IntersectionObserver((entries) => {
    if (!entries.some((entry) => entry.isIntersecting)) return;
    watcher?.disconnect();
    watcher = undefined;
    ensureLoaded();
  });
  watcher.observe(root.value);
});

onBeforeUnmount(() => watcher?.disconnect());
</script>

<template>
  <!-- Until Fleet has answered, every tab holds the group's place: it's what's watched to know Weave has been seen. -->
  <div
    v-if="!view && !error"
    ref="root"
  >
    <SettingGroup
      id="weave"
      title="Weave"
      hint="Agents, prompts and workflows Weave adds."
    >
      <div
        class="flex items-center gap-2 px-4 py-3 text-sm text-muted"
        role="status"
        data-testid="weave-asking"
      >
        <LoaderCircle
          :size="16"
          class="animate-spin"
          aria-hidden="true"
        />
        <span>Asking your harnesses which plugins they load…</span>
      </div>
    </SettingGroup>
  </div>

  <SettingGroup
    v-else-if="detection || !view"
    id="weave"
    title="Weave"
    hint="Agents, prompts and workflows Weave adds."
  >
    <SettingRow
      v-if="!detection"
      id="weave-error"
      title="Couldn't ask the harnesses about Weave"
      :summary="error ?? ''"
    >
      <template #control>
        <Button
          variant="outline"
          size="sm"
          :disabled="loading"
          @click="load(true)"
        >
          Check again
        </Button>
      </template>
    </SettingRow>

    <SettingRow
      v-else
      id="weave-plugin"
      title="Weave plugin"
      :summary="describeWeaveInstall(detection)"
    >
      <template #badge>
        <span
          class="inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2 py-0.5 text-[11px] font-medium"
          :class="{
            'border-running/35 bg-running/10 text-running': weavePill(detection).tone === 'ok',
            'border-idle/35 bg-idle/10 text-idle': weavePill(detection).tone === 'warn',
            'border-border text-muted': weavePill(detection).tone === 'off',
          }"
        >
          <span
            class="size-1.5 rounded-full bg-current"
            aria-hidden="true"
          />
          {{ weavePill(detection).label }}
        </span>
      </template>
      <template #control>
        <div class="flex items-center gap-2">
          <Button
            variant="ghost"
            size="sm"
            data-testid="weave-recheck"
            :disabled="loading || pluginLocked"
            @click="load(true)"
          >
            <LoaderCircle
              v-if="loading"
              class="animate-spin"
              aria-hidden="true"
            />
            Check again
          </Button>
          <Button
            v-if="detection.addTo"
            variant="outline"
            size="sm"
            :data-testid="`weave-add-${detection.harnessType}`"
            :disabled="pluginLocked"
            @click="changeWeavePlugin(detection, true)"
          >
            <LoaderCircle
              v-if="pluginBusy"
              class="animate-spin"
              aria-hidden="true"
            />
            <Plus
              v-else
              aria-hidden="true"
            />
            {{ pluginBusy ? "Adding…" : "Add Weave" }}
          </Button>
          <Button
            v-else-if="weaveAddedByFleet(detection)"
            variant="ghost"
            size="sm"
            :data-testid="`weave-remove-${detection.harnessType}`"
            :disabled="pluginLocked"
            @click="changeWeavePlugin(detection, false)"
          >
            <LoaderCircle
              v-if="pluginBusy"
              class="animate-spin"
              aria-hidden="true"
            />
            {{ pluginBusy ? "Removing…" : "Remove" }}
          </Button>
        </div>
      </template>
      <template #below>
        <p
          v-if="error"
          class="mt-2 break-words text-xs text-error"
          role="alert"
          data-testid="weave-error"
        >
          Couldn't ask the harnesses again: {{ error }}
        </p>
        <p
          v-if="pluginBusy"
          class="mt-2 text-xs text-muted"
          role="status"
          data-testid="weave-plugin-busy"
        >
          The first time, the harness downloads Weave from npm, which can take a minute.
        </p>
        <p
          v-else-if="pluginNotice"
          class="mt-2 break-words text-xs"
          :class="{
            'text-muted': pluginNotice.tone === 'ok',
            'text-idle': pluginNotice.tone === 'warn',
            'text-error': pluginNotice.tone === 'error',
          }"
          :role="pluginNotice.tone === 'error' ? 'alert' : 'status'"
          data-testid="weave-plugin-notice"
        >
          {{ pluginNotice.text }}
        </p>
        <p
          v-for="flavor in outdated"
          :key="flavor"
          class="mt-2 rounded-btn border border-idle/35 bg-idle/10 px-3 py-2 text-xs text-text"
          data-testid="weave-outdated"
        >
          <span class="font-semibold text-idle">
            Update {{ WEAVE_FLAVORS[flavor].name }} to {{ WEAVE_FLAVORS[flavor].minimum }} to use a config kept in Fleet.
          </span>
          This version only reads <code class="font-mono">{{ WEAVE_FLAVORS[flavor].ownPath }}</code>. Update it in your
          opencode.json plugin list, then check again.
        </p>
        <p
          v-if="bothLoaded"
          class="mt-2 rounded-btn border border-idle/35 bg-idle/10 px-3 py-2 text-xs text-text"
        >
          This harness loads both Weave and Weave Legacy, and both add agents such as Loom. Keep one of them in your
          plugin list.
        </p>
      </template>
    </SettingRow>

    <SettingRow
      v-if="detection && flavors.length > 0"
      id="weave-config"
      title="Config"
      :summary="sourceLabel"
    >
      <template #control>
        <Button
          variant="outline"
          size="sm"
          data-testid="weave-edit-config"
          @click="emit('editConfig')"
        >
          Edit…
        </Button>
      </template>
    </SettingRow>

    <div
      v-else-if="detection"
      class="px-4 py-3"
    >
      <div
        class="grid justify-items-center gap-1.5 rounded-card border border-dashed border-border px-4 py-6 text-center"
        data-testid="weave-none"
      >
        <p class="text-sm font-semibold text-text">
          Weave isn't running in any harness
        </p>
        <p class="max-w-md text-sm text-muted">
          Nothing here changes how Fleet works. Sessions use the harness's own agents. Once Weave is in a harness's
          plugin list, you can set its config here.
          <template v-if="all.some((harness) => harness.addTo)">
            Add Weave above puts it there for you.
          </template>
        </p>
        <a
          class="text-sm text-accent hover:underline"
          href="https://tryweave.io/docs/quickstart/"
          target="_blank"
          rel="noopener"
        >What Weave adds</a>
      </div>
    </div>
  </SettingGroup>
</template>
