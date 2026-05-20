<script setup lang="ts">
import { computed, onMounted } from "vue";
import { Cpu } from "lucide-vue-next";
import { usePreferencesStore } from "@/stores/preferences";
import NuCodeSettingsPanel from "@/components/settings/NuCodeSettingsPanel.vue";

// ── State ──────────────────────────────────────────────────────────────────

const prefsStore = usePreferencesStore();

// ── Computed ───────────────────────────────────────────────────────────────

const enabled = computed(() => prefsStore.get("nucode.enabled", "false") === "true");
const provider = computed(() => prefsStore.get("nucode.provider", "copilot"));
const modelId = computed(() => prefsStore.get("nucode.modelId", ""));
const baseUrl = computed(() => prefsStore.get("nucode.baseUrl", ""));

const readinessStatus = computed<"ready" | "missing-credentials" | "not-configured">(() => {
  if (!modelId.value) return "not-configured";
  if (provider.value === "custom" && !baseUrl.value) return "not-configured";
  return "ready";
});

// ── Lifecycle ──────────────────────────────────────────────────────────────

onMounted(async () => {
  await prefsStore.refresh();
});

// ── Actions ────────────────────────────────────────────────────────────────

async function toggleEnabled(): Promise<void> {
  await prefsStore.set("nucode.enabled", enabled.value ? "false" : "true");
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <!-- Header -->
    <div class="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
      <div class="flex items-center gap-3">
        <div class="rounded-btn border border-border bg-main-bg p-2 text-text">
          <Cpu
            :size="16"
            aria-hidden="true"
          />
        </div>
        <div class="space-y-1">
          <div class="flex items-center gap-2">
            <h2 class="text-lg font-semibold text-text">
              NuCode
            </h2>
            <!-- Readiness badge -->
            <span
              v-if="enabled && readinessStatus === 'ready'"
              class="rounded-full border border-green-500/30 bg-green-500/10 px-2 py-0.5 text-[10px] font-medium text-green-300"
            >
              Ready
            </span>
            <span
              v-else-if="enabled && readinessStatus === 'missing-credentials'"
              class="rounded-full border border-yellow-500/30 bg-yellow-500/10 px-2 py-0.5 text-[10px] font-medium text-yellow-300"
            >
              Missing credentials
            </span>
            <span
              v-else-if="enabled && readinessStatus === 'not-configured'"
              class="rounded-full border border-border px-2 py-0.5 text-[10px] font-medium text-muted"
            >
              Not configured
            </span>
          </div>
          <p class="text-sm text-muted">
            In-process AI coding agent. Enable to make NuCode available as a harness.
          </p>
        </div>
      </div>

      <!-- Enable toggle -->
      <button
        type="button"
        role="switch"
        :aria-checked="enabled"
        class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg"
        :class="enabled ? 'bg-accent' : 'bg-border'"
        @click="toggleEnabled"
      >
        <span
          class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
          :class="enabled ? 'translate-x-5' : 'translate-x-0'"
        />
      </button>
    </div>

    <!-- Configuration (visible only when enabled) -->
    <div
      v-if="enabled"
      class="mt-6"
    >
      <NuCodeSettingsPanel />
    </div>
  </section>
</template>
