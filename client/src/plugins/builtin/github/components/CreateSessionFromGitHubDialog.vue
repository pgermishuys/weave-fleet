<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Rocket } from "lucide-vue-next";
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { useRepositories } from "@/composables/use-repositories";
import { useCreateSession } from "@/composables/use-session-actions";
import {
  buildGitHubSessionSourceSelection,
  createGitHubSessionSourcePreset,
  findRepositoryForGitHubPreset,
} from "@/lib/github-session-source";

const props = defineProps<{
  type: "github-issue" | "github-pull-request";
  owner: string;
  repo: string;
  number: number;
  title: string;
  body: string | null;
  htmlUrl: string;
  repoFullName: string;
  headBranch?: string | null;
}>();

const router = useRouter();
const { repositories } = useRepositories();
const { createSession, isLoading } = useCreateSession();

const open = ref(false);
const isolationStrategy = ref<"worktree" | "existing">("worktree");
const branch = ref("");
const selectedRepoPath = ref<string>("");
const creationError = ref<string | null>(null);

const dialogTitle = computed(() =>
  props.type === "github-issue" ? "Work on this issue" : "Review this PR",
);

const itemLabel = computed(() =>
  props.type === "github-issue"
    ? `Issue #${props.number}: ${props.title}`
    : `PR #${props.number}: ${props.title}`,
);

const sessionTitle = computed(() =>
  props.type === "github-issue"
    ? `Issue #${props.number}: ${props.title}`
    : `PR #${props.number}: ${props.title}`,
);

function generateBranchFromTitle(title: string): string {
  return title
    .toLowerCase()
    .replace(/[^a-z0-9\s-]/g, "")
    .replace(/\s+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "")
    .slice(0, 50);
}

watch(open, (isOpen) => {
  if (isOpen) {
    creationError.value = null;

    // Auto-select repository
    const preset = createGitHubSessionSourcePreset({
      sourceType: props.type,
      owner: props.owner,
      repo: props.repo,
      number: props.number,
      title: props.title,
      body: props.body,
      htmlUrl: props.htmlUrl,
      repoFullName: props.repoFullName,
      suggestedBranch: props.headBranch,
    });

    const matched = findRepositoryForGitHubPreset(preset, repositories.value);
    selectedRepoPath.value = matched?.path ?? (repositories.value[0]?.path ?? "");

    // Set branch
    if (props.type === "github-pull-request" && props.headBranch) {
      branch.value = props.headBranch;
    } else {
      branch.value = generateBranchFromTitle(props.title);
    }

    isolationStrategy.value = "worktree";
  }
});

async function handleCreate(): Promise<void> {
  creationError.value = null;

  if (!selectedRepoPath.value) {
    creationError.value = "Please select a repository.";
    return;
  }

  try {
    const preset = createGitHubSessionSourcePreset({
      sourceType: props.type,
      owner: props.owner,
      repo: props.repo,
      number: props.number,
      title: props.title,
      body: props.body,
      htmlUrl: props.htmlUrl,
      repoFullName: props.repoFullName,
      suggestedBranch: props.headBranch,
    });

    const source = buildGitHubSessionSourceSelection(
      preset,
      selectedRepoPath.value,
      isolationStrategy.value,
      isolationStrategy.value === "worktree" ? branch.value : undefined,
    );

    const response = await createSession(undefined, {
      title: sessionTitle.value,
      source,
      isolationStrategy: isolationStrategy.value,
      branch: isolationStrategy.value === "worktree" ? branch.value : undefined,
    });

    open.value = false;

    void router.navigate({
      to: "/sessions/$id" as string,
      params: { id: response.session.id },
      search: { instanceId: response.instanceId },
    });
  } catch (err) {
    creationError.value = err instanceof Error ? err.message : "Failed to create session.";
  }
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogTrigger as-child>
      <slot>
        <button
          class="create-session-trigger"
          :title="dialogTitle"
          @click.stop
        >
          <Rocket :size="13" />
        </button>
      </slot>
    </DialogTrigger>

    <DialogContent
      class="create-session-dialog"
      @click.stop
    >
      <DialogHeader>
        <DialogTitle>{{ dialogTitle }}</DialogTitle>
      </DialogHeader>

      <div class="dialog-body">
        <p class="item-label">
          {{ itemLabel }}
        </p>

        <label class="field-label">
          Repository
          <select
            v-model="selectedRepoPath"
            class="field-select"
          >
            <option
              v-for="repoOption in repositories"
              :key="repoOption.path"
              :value="repoOption.path"
            >
              {{ repoOption.name }} — {{ repoOption.path }}
            </option>
          </select>
        </label>

        <fieldset class="field-group">
          <legend class="field-label">
            Strategy
          </legend>
          <label class="radio-label">
            <input
              v-model="isolationStrategy"
              type="radio"
              value="worktree"
            >
            Worktree
          </label>
          <label class="radio-label">
            <input
              v-model="isolationStrategy"
              type="radio"
              value="existing"
            >
            Existing
          </label>
        </fieldset>

        <label
          v-if="isolationStrategy === 'worktree'"
          class="field-label"
        >
          Branch
          <input
            v-model="branch"
            type="text"
            class="field-input"
            placeholder="branch-name"
          >
        </label>

        <p
          v-if="creationError"
          class="error-message"
        >
          {{ creationError }}
        </p>
      </div>

      <DialogFooter>
        <button
          class="btn btn--secondary"
          :disabled="isLoading"
          @click="open = false"
        >
          Cancel
        </button>
        <button
          class="btn btn--primary"
          :disabled="isLoading || !selectedRepoPath"
          @click="handleCreate"
        >
          {{ isLoading ? "Creating..." : "Create Session" }}
        </button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.create-session-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.15s, color 0.15s;
}

.create-session-trigger:hover {
  color: var(--text);
  background: rgba(255, 255, 255, 0.08);
}

.dialog-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 8px 0;
}

.item-label {
  margin: 0;
  font-size: 12px;
  font-weight: 500;
  color: var(--muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.field-label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text);
}

.field-select,
.field-input {
  padding: 6px 8px;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: var(--sidebar);
  color: var(--text);
  font-size: 12px;
  outline: none;
}

.field-select:focus,
.field-input:focus {
  border-color: var(--accent);
}

.field-group {
  border: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.field-group legend {
  margin-bottom: 2px;
}

.radio-label {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text);
  cursor: pointer;
}

.error-message {
  margin: 0;
  font-size: 11px;
  color: #ef4444;
}

.btn {
  padding: 6px 14px;
  border: 1px solid var(--border);
  border-radius: 4px;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.15s;
}

.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.btn--secondary {
  background: transparent;
  color: var(--text);
}

.btn--secondary:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.06);
}

.btn--primary {
  background: var(--accent);
  color: #fff;
  border-color: var(--accent);
}

.btn--primary:hover:not(:disabled) {
  filter: brightness(1.1);
}
</style>
