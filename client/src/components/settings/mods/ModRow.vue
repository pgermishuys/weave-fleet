<script setup lang="ts">
import { computed, ref } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { LoaderCircle, Undo2 } from "lucide-vue-next";
import { formatAbsoluteTimestamp, formatRelativeTime } from "@/lib/format-utils";
import type { KeptMod } from "@/lib/mods/kept";
import { useModsStore } from "@/stores/mods";

/** One kept mod: its switch, Undo and why it is off. */
const props = defineProps<{ mod: KeptMod }>();

const store = useModsStore();
const router = useRouter();
const busy = ref<string | null>(null);
const error = ref<string | null>(null);

const activeVersion = computed(() => props.mod.versions.find((v) => v.number === props.mod.active) ?? null);
const isOn = computed(() => props.mod.off === null);
const offByStrikes = computed(() => props.mod.off?.by === "strikes");

/** The server's rule: on vN (N>1) the previous version returns and the mod turns on; on v1 the mod turns off. */
const undoLabel = computed(() => ((props.mod.active ?? 1) > 1 ? `Undo (back to v${(props.mod.active ?? 1) - 1})` : "Undo (turn off)"));
const undoDisabledReason = computed(() => {
  if (props.mod.active === null) return "Nothing to undo: no version is kept yet.";
  if (props.mod.active === 1 && !isOn.value) return "Nothing to undo: v1 is the first version and the mod is already off.";
  return null;
});

async function run(key: string, action: () => Promise<unknown>): Promise<void> {
  if (busy.value) return;
  busy.value = key;
  error.value = null;
  try {
    await action();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Something went wrong.";
  } finally {
    busy.value = null;
  }
}

function openSession(sessionId: string): void {
  void router.navigate({ to: "/sessions/$id", params: { id: sessionId }, search: { instanceId: undefined, parentSessionId: undefined } });
}

const setOn = (on: boolean) => run("on", () => store.setOn(props.mod.name, on));
const undo = () => run("undo", () => store.undo(props.mod.name));
</script>

<template>
  <li
    class="px-3 py-3.5 sm:px-4 [&+&]:border-t [&+&]:border-border"
    :data-testid="`mod-row-${mod.name}`"
  >
    <div class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <p class="m-0 flex flex-wrap items-baseline gap-x-2 text-sm">
          <span class="break-all font-mono font-semibold text-text">{{ mod.name }}</span>
          <span class="font-mono text-xs text-text">v{{ mod.active }}</span>
          <span
            v-if="mod.activeVersion"
            class="font-mono text-xs text-muted"
          >{{ mod.activeVersion }}</span>
        </p>
        <p
          v-if="mod.description"
          class="mt-1 text-sm text-muted"
        >
          {{ mod.description }}
        </p>
        <p
          v-if="activeVersion"
          class="mt-1 text-xs text-muted"
        >
          kept <span :title="formatAbsoluteTimestamp(Date.parse(activeVersion.createdAt))">{{ formatRelativeTime(activeVersion.createdAt) }}</span>
          <template v-if="activeVersion.sessionTitle">
            · from “<a
              v-if="activeVersion.sessionId"
              :href="`/sessions/${encodeURIComponent(activeVersion.sessionId)}`"
              class="text-accent hover:underline"
              @click.prevent="openSession(activeVersion.sessionId)"
            >{{ activeVersion.sessionTitle }}</a><template v-else>{{ activeVersion.sessionTitle }}</template>”
          </template>
        </p>
      </div>

      <div class="flex shrink-0 items-center gap-2">
        <span
          v-if="!isOn && !offByStrikes"
          class="text-xs text-muted"
        >Off</span>
        <button
          type="button"
          role="switch"
          :aria-checked="isOn"
          :disabled="busy !== null"
          :aria-label="`Run ${mod.name}`"
          :data-testid="`mod-switch-${mod.name}`"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isOn ? 'bg-accent' : 'bg-border'"
          @click="setOn(!isOn)"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isOn ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>

    <p
      v-if="offByStrikes"
      class="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-error"
      :data-testid="`mod-strikes-${mod.name}`"
    >
      <span>Turned off after 3 failures: {{ mod.off?.error }}</span>
      <button
        type="button"
        class="inline-flex items-center gap-1 p-0 text-accent hover:enabled:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent disabled:cursor-default disabled:opacity-50"
        :disabled="busy !== null"
        :data-testid="`mod-turn-on-${mod.name}`"
        @click="setOn(true)"
      >
        Turn on again
      </button>
    </p>

    <p
      v-if="error"
      class="mt-2 text-xs text-error"
      role="alert"
      :data-testid="`mod-error-${mod.name}`"
    >
      {{ error }}
    </p>

    <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2 text-xs">
      <button
        type="button"
        class="inline-flex items-center gap-1 p-0 text-accent hover:enabled:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent disabled:cursor-default disabled:opacity-50"
        :disabled="busy !== null || undoDisabledReason !== null"
        :title="undoDisabledReason ?? undefined"
        :data-testid="`mod-undo-${mod.name}`"
        @click="undo"
      >
        <LoaderCircle
          v-if="busy === 'undo'"
          class="h-3 w-3 animate-spin"
          aria-hidden="true"
        />
        <Undo2
          v-else
          class="h-3 w-3"
          aria-hidden="true"
        />{{ undoLabel }}
      </button>
    </div>
  </li>
</template>
