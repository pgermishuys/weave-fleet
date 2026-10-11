<script setup lang="ts">
import { shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

/** Adds one of Fleet's sample mods (chips on tool rows) so there is something to try. */
const SAMPLES = [
  { name: "test-chips", added: "Added. Run tests in any session to see it." },
  { name: "build-summary", added: "Added. Run a build in any session to see it." },
  { name: "git-glance", added: "Added. Run git status in any session to see it." },
  { name: "http-check", added: "Added. Run curl -i in any session to see it." },
  { name: "lint-summary", added: "Added. Run ESLint or ruff in any session to see it." },
  { name: "edit-size", added: "Added. Edit a file in any session to see it." },
  { name: "search-hits", added: "Added. Run a grep in any session to see it." },
  { name: "branch-context", added: "Added. Ask about the branch, a PR or a push in any session." },
  { name: "tests-after-edits", added: "Added. When a turn changes files, \"Run the tests\" is queued next." },
] as const;

const isAdding = shallowRef(false);
const message = shallowRef<{ text: string; failed: boolean } | null>(null);

async function addSample(sample: (typeof SAMPLES)[number]): Promise<void> {
  isAdding.value = true;
  message.value = null;
  try {
    const response = await apiFetch(`/api/mods/samples/${sample.name}`, { method: "POST" });
    if (response.ok) {
      message.value = { text: sample.added, failed: false };
      return;
    }
    const body = (await response.json().catch(() => ({}))) as { error?: string; message?: string };
    message.value = { text: body.error ?? body.message ?? `The sample couldn't be added (HTTP ${response.status}).`, failed: true };
  } catch (error) {
    message.value = { text: error instanceof Error ? error.message : "The sample couldn't be added.", failed: true };
  } finally {
    isAdding.value = false;
  }
}
</script>

<template>
  <div class="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
    <button
      v-for="sample in SAMPLES"
      :key="sample.name"
      type="button"
      class="text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-60"
      :disabled="isAdding"
      data-testid="mods-sample-button"
      :data-sample="sample.name"
      @click="addSample(sample)"
    >
      {{ sample.name === 'test-chips' ? 'Try the test-chips sample' : `Try ${sample.name}` }}
    </button>
    <span
      v-if="message"
      :class="message.failed ? 'text-red-400' : 'text-muted'"
      role="status"
      data-testid="mods-sample-message"
    >
      {{ message.text }}
    </span>
  </div>
</template>
