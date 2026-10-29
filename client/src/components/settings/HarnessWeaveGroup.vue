<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { LoaderCircle, Plus } from "lucide-vue-next";
import type { WeaveHarnessDetection } from "@/api/client";
import { Button } from "@/components/ui/button";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import WeaveConfigDialog from "@/components/settings/WeaveConfigDialog.vue";
import type { useWeaveConfig } from "@/composables/use-weave-config";
import {
  WEAVE_FLAVORS,
  describeWeaveInstall,
  loadedFlavors,
  usableFlavorsOf,
  weaveAddedByFleet,
  weavePill,
} from "@/lib/weave-flavors";

/**
 * One harness's Weave: whether its plugin list loads Weave (with Add Weave and Remove), and whose config Weave reads in
 * Fleet sessions. Shown only for harnesses Fleet can ask about Weave. The config itself is Fleet's, one for all
 * harnesses, and opens in a dialog.
 */
const props = defineProps<{
  harnessType: string;
  /** The page's one Weave state, so every tab shares what Fleet last asked the harnesses. */
  weave: ReturnType<typeof useWeaveConfig>;
}>();

const { view, error, loading, load, changePlugin } = props.weave;

const detection = computed<WeaveHarnessDetection | undefined>(() =>
  view.value?.harnesses.find((harness) => harness.harnessType === props.harnessType));
const all = computed(() => view.value?.harnesses ?? []);
const flavors = computed(() => loadedFlavors(all.value));
const usableFlavors = computed(() => usableFlavorsOf(all.value));
const outdated = computed(() => flavors.value.filter((flavor) => !usableFlavors.value.includes(flavor)));
const bothLoaded = computed(() => (detection.value?.installs ?? []).some((install) => install.flavor === "weave")
  && (detection.value?.installs ?? []).some((install) => install.flavor === "legacy"));

const pluginBusy = shallowRef(false);
const pluginNotice = shallowRef<{ text: string; tone: "ok" | "warn" | "error" } | null>(null);
const configOpen = shallowRef(false);

async function changeWeavePlugin(current: WeaveHarnessDetection, add: boolean): Promise<void> {
  pluginBusy.value = true;
  pluginNotice.value = null;
  try {
    const result = await changePlugin(current.harnessType, add);
    pluginNotice.value = { text: result.message, tone: result.loaded ? "ok" : "warn" };
  } catch (caught) {
    pluginNotice.value = {
      text: caught instanceof Error ? caught.message : `Couldn't change ${current.harnessName}'s plugins.`,
      tone: "error",
    };
  } finally {
    pluginBusy.value = false;
  }
}

const sourceLabel = computed(() => {
  if (flavors.value.length === 0) {
    return "Weave isn't running in any harness yet. Once it is in a plugin list, you can set its config here.";
  }
  return view.value?.source === "fleet"
    ? "Kept in Fleet. Weave reads it in Fleet sessions."
    : "Weave reads your own file in Fleet sessions.";
});
</script>

<template>
  <SettingGroup
    v-if="detection || (!view && error)"
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
            :disabled="loading || pluginBusy"
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
            :disabled="pluginBusy"
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
            :disabled="pluginBusy"
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
      v-if="detection"
      id="weave-config"
      title="Config"
      :summary="sourceLabel"
    >
      <template #control>
        <Button
          variant="outline"
          size="sm"
          data-testid="weave-edit-config"
          :disabled="flavors.length === 0"
          @click="configOpen = true"
        >
          Edit…
        </Button>
      </template>
    </SettingRow>

    <WeaveConfigDialog
      v-if="detection"
      v-model:open="configOpen"
      :weave="weave"
    />
  </SettingGroup>
</template>
