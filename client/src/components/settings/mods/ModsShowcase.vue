<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import { apiFetch } from "@/lib/api-client";
import type { KeptMod } from "@/lib/mods/kept";

/**
 * Fleet's own sample mods, under the user's kept ones: what each does and where to see it, and Add to keep it. Adding
 * one keeps it like any mod, so it then shows in the list above too.
 */
const props = defineProps<{ kept: readonly KeptMod[] }>();

interface Sample {
  name: string;
  /** As the sample's mod.json says it. */
  description: string;
  /** Where to see it once it's added. */
  tryIt: string;
}

interface SampleGroup {
  title: string;
  samples: Sample[];
}

const GROUPS: SampleGroup[] = [
  {
    title: "Tool rows",
    samples: [
      { name: "test-chips", description: "Draws test runs as passed, failed and skipped counts", tryIt: "Run tests in any session." },
      { name: "build-summary", description: "Draws build output as error and warning counts", tryIt: "Run a build in any session." },
      { name: "git-glance", description: "Draws git status and log as counts", tryIt: "Run git status in any session." },
      { name: "http-check", description: "Draws curl responses as a status pill and size", tryIt: "Run curl -i in any session." },
      { name: "lint-summary", description: "Draws ESLint and ruff runs as error and warning counts", tryIt: "Run ESLint or ruff in any session." },
      { name: "edit-size", description: "Draws file writes and patches as lines added and removed", tryIt: "Edit a file in any session." },
      { name: "search-hits", description: "Draws grep and glob results as match and file counts", tryIt: "Run a grep in any session." },
    ],
  },
  {
    title: "Prompts, turns and permission asks",
    samples: [
      { name: "branch-context", description: "Tells the model which git branch the session is on when you ask about branches, PRs, pushes or merges", tryIt: "Ask about the branch, a PR or a push in any session." },
      { name: "tests-after-edits", description: "When a turn changed files, queues \"Run the tests\" as the next message", tryIt: "Have an agent change a file." },
      { name: "push-guard", description: "Refuses the agent's git push while the session is on main, and tells it to push a branch instead", tryIt: "Set Settings → Permissions to ask, then have an agent push from main." },
    ],
  },
  {
    title: "Buttons and panes",
    samples: [
      { name: "press-counter", description: "Counts presses of a button in an opened shell row", tryIt: "Open a shell command's row in any session and press Count a press." },
      { name: "release-board", description: "A Release board pane in the + menu, and a button in a shell row that opens it and closes it again", tryIt: "Pick Release board under From mods in a session's + menu." },
    ],
  },
];

const adding = shallowRef<string | null>(null);
const failed = shallowRef<{ name: string; text: string } | null>(null);

const keptByName = computed(() => new Map(props.kept.map((mod) => [mod.name, mod])));

/** "Added" once it's kept and on; a kept one that's off can be added (turned on) again. */
function isAdded(name: string): boolean {
  const mod = keptByName.value.get(name);
  return mod !== undefined && mod.off === null;
}

async function add(sample: Sample): Promise<void> {
  if (adding.value) return;
  adding.value = sample.name;
  failed.value = null;
  try {
    const response = await apiFetch(`/api/mods/samples/${sample.name}`, { method: "POST" });
    if (response.ok) return;
    const body = (await response.json().catch(() => ({}))) as { error?: string; message?: string };
    failed.value = { name: sample.name, text: body.error ?? body.message ?? `The sample couldn't be added (HTTP ${response.status}).` };
  } catch (error) {
    failed.value = { name: sample.name, text: error instanceof Error ? error.message : "The sample couldn't be added." };
  } finally {
    adding.value = null;
  }
}
</script>

<template>
  <div
    class="mt-8"
    data-testid="mods-showcase"
  >
    <h3 class="text-sm font-semibold text-text">
      Showcase
    </h3>
    <p class="mt-1 text-sm text-muted">
      Fleet's own sample mods, to see what a mod can do. Add one and it's kept like any mod: on in every session, and
      you can turn it off or remove it above.
    </p>

    <div
      v-for="group in GROUPS"
      :key="group.title"
      class="mt-4"
    >
      <p class="mb-1.5 text-xs font-medium uppercase tracking-wide text-muted">
        {{ group.title }}
      </p>
      <ul class="m-0 list-none overflow-hidden rounded-card border border-border p-0">
        <li
          v-for="sample in group.samples"
          :key="sample.name"
          class="flex items-start justify-between gap-3 px-3 py-3 sm:px-4 [&+&]:border-t [&+&]:border-border"
          data-testid="mods-sample"
          :data-sample="sample.name"
        >
          <div class="min-w-0">
            <p class="m-0 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">
              <span class="break-all font-mono font-semibold text-text">{{ sample.name }}</span>
              <span class="rounded-full border border-accent/40 px-1.5 py-px text-[10px] font-medium uppercase tracking-wide text-accent">
                Showcase
              </span>
            </p>
            <p class="mt-1 text-sm text-muted">
              {{ sample.description }}
            </p>
            <p class="mt-1 text-xs text-muted">
              <span class="font-medium text-text">Try it:</span> {{ sample.tryIt }}
            </p>
            <p
              v-if="failed?.name === sample.name"
              class="mt-1 text-xs text-error"
              role="alert"
              data-testid="mods-sample-error"
            >
              {{ failed.text }}
            </p>
          </div>

          <span
            v-if="isAdded(sample.name)"
            class="shrink-0 py-1.5 text-xs text-muted"
            data-testid="mods-sample-added"
          >Added</span>
          <button
            v-else
            type="button"
            class="inline-flex shrink-0 items-center gap-1.5 rounded-btn border border-border px-3 py-1.5 text-xs font-medium text-text hover:enabled:bg-main-bg focus-visible:outline focus-visible:outline-2 focus-visible:outline-accent disabled:cursor-not-allowed disabled:opacity-60"
            :disabled="adding !== null"
            :aria-label="`Add ${sample.name}`"
            data-testid="mods-sample-add"
            @click="add(sample)"
          >
            <LoaderCircle
              v-if="adding === sample.name"
              class="h-3.5 w-3.5 animate-spin"
              aria-hidden="true"
            />
            Add
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>
