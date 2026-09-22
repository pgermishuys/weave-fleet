<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Check, LoaderCircle, Play, X } from "lucide-vue-next";
import type { HarnessProfile, HarnessProfileCheck } from "@/api/client";
import { Button } from "@/components/ui/button";
import ProfileConfigEditor from "@/components/settings/ProfileConfigEditor.vue";
import { NEW_PROFILE_CONTENT, parseJsonc } from "@/lib/harness-profile";
import { useHarnessProfilesStore } from "@/stores/harness-profiles";

/** Edits one profile, or makes a new one when `profile` is null. Saving asks the harness to load it first. */
const props = defineProps<{
  harnessType: string;
  harnessName: string;
  profile: HarnessProfile | null;
  /** What a new profile starts with, when it's a copy of another. */
  seed?: { name: string; content: string } | null;
}>();

const emit = defineEmits<{
  /** Closed, with what to tell the person if something was saved or deleted. */
  close: [notice: string | null];
  duplicate: [seed: { name: string; content: string }];
}>();

const store = useHarnessProfilesStore();
const name = shallowRef(props.profile?.name ?? props.seed?.name ?? "");
const content = shallowRef(props.profile?.content ?? props.seed?.content ?? NEW_PROFILE_CONTENT);
const busy = shallowRef<"test" | "save" | "delete" | null>(null);
const result = shallowRef<HarnessProfileCheck | null>(null);
const confirmingDelete = shallowRef(false);

const syntaxProblem = computed(() =>
  content.value.trim() && !parseJsonc(content.value)
    ? "This isn't valid JSON yet. Comments and trailing commas are fine."
    : null,
);

async function test(): Promise<void> {
  busy.value = "test";
  try {
    result.value = await store.check(props.harnessType, content.value);
  } finally {
    busy.value = null;
  }
}

async function save(): Promise<void> {
  if (busy.value) return;
  busy.value = "save";
  result.value = null;
  const finalName = name.value.trim() || "Untitled profile";
  try {
    if (props.profile) {
      const contentChanged = props.profile.content !== content.value;
      const saved = await store.update(props.harnessType, props.profile.id, finalName, content.value);
      const open = saved.openSessions;
      emit("close", contentChanged && open > 0
        ? `Saved ${saved.name}. New sessions use it now; the ${open} open ${open === 1 ? "session picks" : "sessions pick"} it up when Fleet restarts.`
        : `Saved ${saved.name}.`);
    } else {
      const created = await store.create(props.harnessType, finalName, content.value);
      emit("close", `Created ${created.name}. Pick it from the profile chip when you start a session.`);
    }
  } catch (error) {
    result.value = { ok: false, error: error instanceof Error ? error.message : "Couldn't save the profile." };
  } finally {
    busy.value = null;
  }
}

async function remove(): Promise<void> {
  const profile = props.profile;
  if (!profile) return;
  if (!confirmingDelete.value) {
    confirmingDelete.value = true;
    return;
  }
  busy.value = "delete";
  try {
    await store.remove(props.harnessType, profile.id);
    emit("close", profile.isDefault ? `Deleted ${profile.name}. New sessions start with no profile.` : `Deleted ${profile.name}.`);
  } catch (error) {
    result.value = { ok: false, error: error instanceof Error ? error.message : "Couldn't delete the profile." };
    confirmingDelete.value = false;
  } finally {
    busy.value = null;
  }
}
</script>

<template>
  <div
    class="grid gap-3"
    data-testid="harness-profile-editor"
  >
    <label class="flex items-center gap-3 text-xs text-muted">
      <span class="w-12 shrink-0">Name</span>
      <input
        v-model="name"
        class="h-8 w-full max-w-xs rounded-btn border border-border bg-main-bg px-2.5 text-sm text-text outline-none focus:border-accent"
        data-testid="harness-profile-name"
        placeholder="Work, Local models, Reviewer…"
        maxlength="60"
      >
    </label>

    <ProfileConfigEditor
      v-model="content"
      :label="`${name || 'Profile'} config`"
      @save="save"
    />

    <p class="text-xs text-muted">
      Fleet hands this to {{ harnessName }} as <code class="font-mono text-text">OPENCODE_CONFIG</code>, on top of your
      own opencode.json. Fleet's own settings (every tool allowed, the Fleet plugin) still apply after it.
      <template v-if="harnessType === 'opencode2'">
        Keep API keys out of it: sign in to providers in {{ harnessName }} itself, or refer to a variable Fleet runs with
        as <code class="font-mono text-text">{env:NAME}</code>. Sessions on a profile run on an {{ harnessName }} server
        of their own, which stops after a few minutes unused.
      </template>
      <template v-else>
        Keep API keys in Credentials and refer to them with <code class="font-mono text-text">{env:NAME}</code>.
      </template>
    </p>
    <p
      v-if="syntaxProblem"
      class="text-xs text-idle"
    >
      {{ syntaxProblem }}
    </p>

    <div
      v-if="result"
      class="rounded-card border px-3 py-2 text-xs"
      :class="result.ok ? 'border-running/30 bg-running/10 text-running' : 'border-error/30 bg-error/10 text-error'"
      role="status"
      data-testid="harness-profile-result"
    >
      <p class="flex items-center gap-1.5 font-medium">
        <Check
          v-if="result.ok"
          :size="13"
          aria-hidden="true"
        />
        <X
          v-else
          :size="13"
          aria-hidden="true"
        />
        {{ result.ok ? `${harnessName} loads this profile.` : result.error }}
      </p>
      <pre
        v-if="!result.ok && result.details?.length"
        class="mt-1.5 overflow-x-auto whitespace-pre font-mono text-[11px] text-text"
      >{{ result.details.join("\n") }}</pre>
    </div>

    <div class="flex flex-wrap items-center justify-between gap-2">
      <div class="flex gap-1">
        <template v-if="profile">
          <Button
            variant="ghost"
            size="sm"
            @click="emit('duplicate', { name: `${name.trim() || profile.name} copy`, content })"
          >
            Duplicate
          </Button>
          <Button
            variant="ghost"
            size="sm"
            class="text-error"
            data-testid="harness-profile-delete"
            :disabled="busy !== null"
            @click="remove"
          >
            {{ confirmingDelete ? `Delete ${profile.name}?` : "Delete" }}
          </Button>
        </template>
      </div>
      <div class="flex gap-1">
        <Button
          variant="ghost"
          size="sm"
          @click="emit('close', null)"
        >
          Cancel
        </Button>
        <Button
          variant="outline"
          size="sm"
          data-testid="harness-profile-test"
          :disabled="busy !== null"
          @click="test"
        >
          <LoaderCircle
            v-if="busy === 'test'"
            class="animate-spin"
            aria-hidden="true"
          />
          <Play
            v-else
            aria-hidden="true"
          />
          Test
        </Button>
        <Button
          size="sm"
          data-testid="harness-profile-save"
          :disabled="busy !== null"
          @click="save"
        >
          <LoaderCircle
            v-if="busy === 'save'"
            class="animate-spin"
            aria-hidden="true"
          />
          {{ busy === "save" ? `Checking with ${harnessName}…` : "Save" }}
        </Button>
      </div>
    </div>
  </div>
</template>
