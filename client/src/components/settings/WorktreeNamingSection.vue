<script setup lang="ts">
import { computed, onMounted, reactive, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { AlertCircle, Check, LoaderCircle } from "lucide-vue-next";
import { useWorktreeNamingStore } from "@/stores/worktree-naming";
import { resolveWorktreeName, type WorktreeNamingTemplates } from "@/lib/worktree-naming";

const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 font-mono text-[12.5px] text-text outline-none transition-colors placeholder:text-muted focus:border-accent disabled:cursor-not-allowed disabled:opacity-60";

const store = useWorktreeNamingStore();
const { user, defaults, isSaving, error } = storeToRefs(store);

/** What the fields hold, which is the user's own layer — not the effective templates. */
const draft = reactive({
  branch: "",
  root: "",
  folder: "",
  ticket: "",
  initials: "",
});

const saved = shallowRef(false);

/** A sentence to name something from, so the preview has real input. */
const sample = shallowRef("PLAT-1841 add rate limiting to the session create endpoint");

const tokensFor: Record<string, readonly { name: string; hint: string }[]> = {
  branch: [
    { name: "slug", hint: "from your message" },
    { name: "user", hint: "your username" },
    { name: "initials", hint: "set below" },
    { name: "ticket", hint: "captured" },
    { name: "date", hint: "2026-09-21" },
    { name: "repo", hint: "repository name" },
    { name: "shortid", hint: "random" },
  ],
  root: [
    { name: "repoParent", hint: "beside the repository" },
    { name: "repo", hint: "repository name" },
    { name: "home", hint: "your home folder" },
    { name: "user", hint: "your username" },
  ],
  folder: [
    { name: "branch", hint: "the resolved branch" },
    { name: "slug", hint: "from your message" },
    { name: "ticket", hint: "captured" },
    { name: "repo", hint: "repository name" },
    { name: "shortid", hint: "random" },
  ],
};

onMounted(async () => {
  await store.load();
  syncDraft();
});

watch(user, syncDraft);

function syncDraft(): void {
  draft.branch = user.value.branch ?? "";
  draft.root = user.value.root ?? "";
  draft.folder = user.value.folder ?? "";
  draft.ticket = user.value.capture?.ticket ?? "";
  draft.initials = user.value.initials ?? "";
}

function insertToken(field: "branch" | "root" | "folder", token: string): void {
  draft[field] = `${draft[field]}{${token}}`;
}

/**
 * The preview is of your own templates. A repository that ships its own convention overrides them
 * for that repository, which the composer's plan line shows as you start a session there.
 */
const previewTemplates = computed<WorktreeNamingTemplates>(() => ({
  branch: draft.branch || defaults.value.branch,
  root: draft.root || defaults.value.root,
  folder: draft.folder || defaults.value.folder,
  capture: draft.ticket ? { ticket: draft.ticket } : null,
  initials: draft.initials,
}));

const preview = computed(() => resolveWorktreeName(
  previewTemplates.value,
  {
    repositoryPath: "/home/you/source/weave-fleet",
    user: "you",
    initials: previewTemplates.value.initials ?? "",
    date: new Date().toISOString().slice(0, 10),
    shortId: "a1b2c3d4",
    home: "/home/you",
  },
  sample.value,
));

async function save(): Promise<void> {
  saved.value = false;
  const ok = await store.save({
    branch: draft.branch || null,
    root: draft.root || null,
    folder: draft.folder || null,
    capture: draft.ticket ? { ticket: draft.ticket } : null,
    initials: draft.initials || null,
  });

  if (ok) {
    saved.value = true;
    window.setTimeout(() => { saved.value = false; }, 2000);
  }
}

async function resetToDefaults(): Promise<void> {
  draft.branch = "";
  draft.root = "";
  draft.folder = "";
  draft.ticket = "";
  await save();
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Worktrees
      </h2>
      <p class="text-sm text-muted">
        How new worktrees are named. Tokens are filled in when a session starts. A repository that
        commits its own <code>weave.jsonc</code> names branches its way instead, for everyone who
        clones it.
      </p>
    </div>


    <div class="mt-5 grid gap-4">
      <label
        v-for="field in (['branch', 'root', 'folder'] as const)"
        :key="field"
        class="grid gap-1.5 text-sm text-text"
      >
        <span class="text-xs font-medium uppercase tracking-wide text-muted">
          {{ field === "branch" ? "Branch name" : field === "root" ? "Worktree root" : "Folder name" }}
        </span>
        <input
          v-model="draft[field]"
          type="text"
          spellcheck="false"
          :class="inputClass"
          :placeholder="defaults[field] ?? ''"
        >
        <span class="flex flex-wrap gap-1">
          <button
            v-for="token in tokensFor[field]"
            :key="token.name"
            type="button"
            class="rounded-full border border-border px-2 font-mono text-[11px] text-muted transition-colors hover:border-accent/45 hover:text-text"
            @click="insertToken(field, token.name)"
          >
            {{ "{" + token.name + "}" }}
            <span class="opacity-60">{{ token.hint }}</span>
          </button>
        </span>
      </label>

      <label class="grid gap-1.5 text-sm text-text">
        <span class="text-xs font-medium uppercase tracking-wide text-muted">Capture: ticket</span>
        <input
          v-model="draft.ticket"
          type="text"
          spellcheck="false"
          :class="inputClass"
          placeholder="[A-Z]{2,}-\d+"
        >
        <span class="text-xs text-muted">
          Read from your message so <code>{{ "{ticket}" }}</code> has a value. What it captures
          leaves the slug, so the key isn't said twice.
        </span>
      </label>

      <label class="grid gap-1.5 text-sm text-text">
        <span class="text-xs font-medium uppercase tracking-wide text-muted">Initials</span>
        <input
          v-model="draft.initials"
          type="text"
          spellcheck="false"
          :class="`${inputClass} max-w-[160px]`"
          placeholder="pg"
        >
        <span class="text-xs text-muted">What <code>{{ "{initials}" }}</code> resolves to.</span>
      </label>
    </div>

    <div class="mt-6 rounded-card border border-border bg-main-bg p-4">
      <h3 class="text-xs font-semibold uppercase tracking-wide text-muted">
        What you'd get
      </h3>
      <input
        v-model="sample"
        type="text"
        spellcheck="false"
        class="mt-2 w-full rounded-btn border border-border bg-card-bg px-3 py-2 text-sm text-text outline-none transition-colors focus:border-accent"
        placeholder="Describe a task…"
      >
      <dl class="mt-3 grid gap-1.5 text-sm">
        <div class="flex flex-wrap items-baseline gap-x-3">
          <dt class="w-20 text-xs uppercase tracking-wide text-muted">
            Branch
          </dt>
          <dd class="font-mono text-[13px] text-text">
            {{ preview.branch ?? "weave-session-a1b2c3d4 (named by the server)" }}
          </dd>
        </div>
        <div class="flex flex-wrap items-baseline gap-x-3">
          <dt class="w-20 text-xs uppercase tracking-wide text-muted">
            Worktree
          </dt>
          <dd class="font-mono text-[13px] break-all text-text">
            {{ preview.root }}/{{ preview.folder || "weave-session-a1b2c3d4" }}
          </dd>
        </div>
      </dl>
      <p
        v-if="!preview.branch"
        class="mt-2 text-xs text-muted"
      >
        This message gives no slug, so the server names the worktree instead of collapsing the
        template to its prefix.
      </p>
    </div>

    <div
      v-if="error"
      class="mt-4 flex items-start gap-2 rounded-card border border-error/30 bg-error/10 p-3 text-sm text-error"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ error }}</span>
    </div>

    <div class="mt-5 flex items-center gap-3">
      <button
        type="button"
        class="inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-1.5 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60"
        :disabled="isSaving"
        @click="save"
      >
        <LoaderCircle
          v-if="isSaving"
          :size="16"
          class="animate-spin"
          aria-hidden="true"
        />
        Save
      </button>
      <button
        type="button"
        class="inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60"
        :disabled="isSaving"
        @click="resetToDefaults"
      >
        Use Fleet's defaults
      </button>
      <span
        v-if="saved"
        class="flex items-center gap-1 text-sm text-running"
      >
        <Check
          :size="15"
          aria-hidden="true"
        />
        Saved
      </span>
    </div>
  </section>
</template>
