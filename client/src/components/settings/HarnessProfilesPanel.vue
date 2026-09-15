<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { Layers, Pencil, Plus, Star } from "lucide-vue-next";
import type { HarnessProfile } from "@/api/client";
import { Button } from "@/components/ui/button";
import HarnessProfileEditor from "@/components/settings/HarnessProfileEditor.vue";
import { profileSummary } from "@/lib/harness-profile";
import { useHarnessProfilesStore } from "@/stores/harness-profiles";

/** A harness's profiles in Settings: list, default, and an editor that opens in place. */
const props = defineProps<{
  harnessType: string;
  harnessName: string;
}>();

/** Which editor is open: a profile's id, or "new" (optionally a copy of another). */
interface Editing {
  id: string | "new";
  seed?: { name: string; content: string };
}

const store = useHarnessProfilesStore();
const profiles = computed(() => store.profilesFor(props.harnessType));
const hasDefault = computed(() => profiles.value.some((profile) => profile.isDefault));
const editing = shallowRef<Editing | null>(null);
const notice = shallowRef<string | null>(null);
const listError = shallowRef<string | null>(null);

onMounted(() => {
  void store.load(props.harnessType);
});

function edit(next: Editing): void {
  editing.value = next;
  notice.value = null;
  listError.value = null;
}

function closeEditor(message: string | null): void {
  editing.value = null;
  if (message) notice.value = message;
}

async function makeDefault(profile: HarnessProfile | null): Promise<void> {
  listError.value = null;
  try {
    await store.setDefault(props.harnessType, profile?.id ?? null);
    notice.value = profile ? `New sessions start with ${profile.name}.` : "New sessions start with no profile.";
  } catch (error) {
    listError.value = error instanceof Error ? error.message : "Couldn't change the default.";
  }
}

function usage(profile: HarnessProfile): string {
  const open = profile.openSessions;
  return open === 0 ? "No open sessions" : `Used by ${open} open ${open === 1 ? "session" : "sessions"}`;
}
</script>

<template>
  <section
    class="grid gap-3"
    aria-labelledby="harness-profiles-heading"
    data-testid="harness-profiles"
  >
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div>
        <p
          id="harness-profiles-heading"
          class="text-sm font-medium text-text"
        >
          Profiles
        </p>
        <p class="mt-1 max-w-2xl text-xs text-muted">
          Extra {{ harnessName }} config that sits on top of your own <code class="font-mono text-text">opencode.json</code>.
          You pick one when you start a session, and the session keeps it.
        </p>
      </div>
      <Button
        variant="outline"
        size="sm"
        data-testid="harness-profile-new"
        :disabled="editing?.id === 'new'"
        @click="edit({ id: 'new' })"
      >
        <Plus aria-hidden="true" />
        New profile
      </Button>
    </div>

    <p
      v-if="notice"
      class="text-xs text-running"
      role="status"
      data-testid="harness-profile-notice"
    >
      {{ notice }}
    </p>
    <p
      v-if="listError"
      class="text-xs text-error"
      role="alert"
    >
      {{ listError }}
    </p>

    <div
      v-if="profiles.length === 0 && !editing"
      class="rounded-card border border-dashed border-border p-4 text-xs text-muted"
      data-testid="harness-profiles-empty"
    >
      <p class="text-sm font-medium text-text">
        No profiles yet
      </p>
      <p class="mt-1 max-w-2xl">
        Every {{ harnessName }} session uses your opencode.json as it is, and the new-session box has no profile chip.
        Make a profile when some sessions need other providers, models or MCP servers.
      </p>
    </div>

    <ul
      v-else
      class="overflow-hidden rounded-card border border-border bg-main-bg"
    >
      <li
        v-if="profiles.length > 0"
        class="flex items-start gap-3 px-4 py-3"
        data-testid="harness-profile-row-none"
      >
        <Layers
          :size="14"
          class="mt-0.5 shrink-0 text-muted"
          aria-hidden="true"
        />
        <div class="min-w-0 flex-1">
          <p class="flex flex-wrap items-center gap-2 text-sm font-medium text-text">
            No profile
            <span
              v-if="!hasDefault"
              class="inline-flex items-center gap-1 rounded-full bg-accent/10 px-2 py-0.5 text-[10px] font-medium text-accent"
            >
              <Star
                :size="10"
                aria-hidden="true"
              />
              Default
            </span>
          </p>
          <p class="mt-0.5 text-xs text-muted">
            Your opencode.json as it is
          </p>
        </div>
        <Button
          v-if="hasDefault"
          variant="ghost"
          size="sm"
          @click="makeDefault(null)"
        >
          <Star aria-hidden="true" />
          Set default
        </Button>
      </li>

      <li
        v-for="profile in profiles"
        :key="profile.id"
        class="border-t border-border"
        :data-testid="`harness-profile-row-${profile.id}`"
      >
        <div class="flex flex-wrap items-start gap-3 px-4 py-3 sm:flex-nowrap">
          <Layers
            :size="14"
            class="mt-0.5 shrink-0 text-muted"
            aria-hidden="true"
          />
          <div class="min-w-0 flex-1">
            <p class="flex flex-wrap items-center gap-2 text-sm font-medium text-text">
              {{ profile.name }}
              <span
                v-if="profile.isDefault"
                class="inline-flex items-center gap-1 rounded-full bg-accent/10 px-2 py-0.5 text-[10px] font-medium text-accent"
              >
                <Star
                  :size="10"
                  aria-hidden="true"
                />
                Default
              </span>
            </p>
            <p class="mt-0.5 break-words text-xs text-muted">
              {{ profileSummary(profile.content) }}
            </p>
            <p class="mt-0.5 text-[11px] text-muted">
              {{ usage(profile) }}
            </p>
          </div>
          <div
            v-if="editing?.id !== profile.id"
            class="flex shrink-0 items-center gap-1"
          >
            <Button
              v-if="!profile.isDefault"
              variant="ghost"
              size="sm"
              @click="makeDefault(profile)"
            >
              <Star aria-hidden="true" />
              Set default
            </Button>
            <Button
              variant="outline"
              size="sm"
              :data-testid="`harness-profile-edit-${profile.id}`"
              @click="edit({ id: profile.id })"
            >
              <Pencil aria-hidden="true" />
              Edit
            </Button>
          </div>
        </div>
        <div
          v-if="editing?.id === profile.id"
          class="border-t border-border bg-card-bg px-4 pb-4 pt-3"
        >
          <HarnessProfileEditor
            :harness-type="harnessType"
            :harness-name="harnessName"
            :profile="profile"
            @close="closeEditor"
            @duplicate="(seed) => edit({ id: 'new', seed })"
          />
        </div>
      </li>

      <li
        v-if="editing?.id === 'new'"
        class="border-t border-border bg-card-bg px-4 pb-4 pt-3 first:border-t-0"
        data-testid="harness-profile-row-new"
      >
        <p class="mb-3 text-sm font-medium text-text">
          New profile
        </p>
        <HarnessProfileEditor
          :key="editing.seed?.name ?? 'blank'"
          :harness-type="harnessType"
          :harness-name="harnessName"
          :profile="null"
          :seed="editing.seed ?? null"
          @close="closeEditor"
          @duplicate="(seed) => edit({ id: 'new', seed })"
        />
      </li>
    </ul>
  </section>
</template>
