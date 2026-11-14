<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import ModRow from "@/components/settings/mods/ModRow.vue";
import ModsShowcase from "@/components/settings/mods/ModsShowcase.vue";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useMachineTarget } from "@/lib/machine-target";
import { useModsStore } from "@/stores/mods";
import { EmptyState } from "@/components/ui/empty-state";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";

/** Settings → Skills & tools → Mods: the mods the user kept, with on/off and Undo, and Fleet's sample mods to add. */
const store = useModsStore(useMachineTarget());
const { setActiveSection } = useSettingsNav();

const isLoading = ref(true);
const error = ref<string | null>(null);
const isSaving = ref(false);

const mods = computed(() => store.kept?.mods ?? []);

async function load(): Promise<void> {
  isLoading.value = true;
  error.value = null;
  try {
    await store.loadSwitch();
    if (store.isSwitchedOn) await store.loadKept();
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't load your mods.";
  } finally {
    isLoading.value = false;
  }
}

async function turnBackOn(): Promise<void> {
  if (isSaving.value) return;
  isSaving.value = true;
  error.value = null;
  try {
    await store.setSafeMode(false);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't turn mods back on.";
  } finally {
    isSaving.value = false;
  }
}

onMounted(load);
</script>

<template>
  <section
    class="rounded-card border border-border bg-card-bg p-4 shadow-sm sm:p-6"
    data-testid="mods-section"
  >
    <h2 class="text-lg font-semibold text-text">
      Mods
    </h2>
    <p
      v-if="store.isSwitchedOn"
      class="mt-1 text-sm text-muted"
    >
      Mods are on. The switch and the mod runtime (Bun) are in
      <button
        type="button"
        class="text-accent-text hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-accent"
        data-testid="mods-features-link"
        @click="setActiveSection('plugin:mods')"
      >Plugins › Mods</button>.
    </p>

    <p
      v-if="!store.isSwitchedOn && !isLoading"
      class="mt-5 rounded-card border border-dashed border-border p-6 text-center text-sm text-muted"
      data-testid="mods-switched-off"
    >
      Mods are off. Turn Mods on in
      <button
        type="button"
        class="text-accent-text hover:underline"
        data-testid="mods-features-link"
        @click="setActiveSection('plugin:mods')"
      >Plugins › Mods</button>.
    </p>

    <template v-else>
      <div
        v-if="store.safeMode"
        class="mt-4 flex flex-wrap items-center justify-between gap-3 rounded-card border border-idle/40 bg-idle/10 px-4 py-3 text-sm text-text"
        data-testid="mods-safe-mode"
      >
        <p class="m-0">
          Mods are stopped for now (Start without mods). They start again when Fleet restarts or you turn them back on.
        </p>
        <button
          type="button"
          class="rounded-btn border border-border px-3 py-1.5 text-xs font-medium hover:bg-main-bg disabled:opacity-60"
          :disabled="isSaving"
          data-testid="mods-safe-mode-off"
          @click="turnBackOn"
        >
          Turn mods back on
        </button>
      </div>

      <div
        v-if="isLoading"
        class="mt-5 flex items-center gap-2 text-sm text-muted"
        data-testid="mods-loading"
      >
        <Spinner />
        Loading your mods…
      </div>

      <ErrorNote
        v-else-if="error"
        :message="error"
        class="mt-5"
        data-testid="mods-error"
        @retry="load"
      />

      <EmptyState
        v-else-if="mods.length === 0"
        title="No kept mods yet"
        description="When an agent writes a mod in a session, you can keep it from there, or add one from the Showcase below."
        bordered
        class="mt-5"
        data-testid="mods-empty"
      />

      <ul
        v-else
        class="m-0 mt-5 list-none overflow-hidden rounded-card border border-border p-0"
      >
        <ModRow
          v-for="mod in mods"
          :key="mod.name"
          :mod="mod"
        />
      </ul>

      <ModsShowcase
        v-if="!isLoading && !error"
        :kept="mods"
      />
    </template>
  </section>
</template>
