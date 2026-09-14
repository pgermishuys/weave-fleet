<script setup lang="ts">
import { computed, nextTick, shallowRef, useId, useTemplateRef, watch } from "vue";
import { ArrowLeft, Check, ChevronDown, Folder, FolderGit2, FolderOpen, FolderPlus, MessageSquare, Search } from "lucide-vue-next";
import type { FolderInspection, ScannedRepository } from "@/api/client";
import { Button } from "@/components/ui/button";
import DirectoryPickerPopover from "@/components/ui/DirectoryPickerPopover.vue";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useDirectoryBrowser } from "@/composables/use-directory-browser";
import { addFolderToFleet, folderForInspection, inspectFolder } from "@/lib/folder-access";
import { tildePath } from "@/lib/new-session-plan";
import type { NewSessionFolder } from "@/lib/new-session-request";

const props = defineProps<{
  folder: NewSessionFolder | null;
  repositories: readonly ScannedRepository[];
  recentFolders: readonly NewSessionFolder[];
  /** Any folder on disk (not in cloud mode, not with a GitHub issue attached). */
  allowBrowse: boolean;
  /** No folder at all (not with a GitHub issue attached). */
  allowNone: boolean;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  "update:folder": [folder: NewSessionFolder];
  closeAutoFocus: [event: Event];
  /** A folder was added to the workspace roots, so the repository list is out of date. */
  folderAdded: [];
}>();

const open = defineModel<boolean>("open", { default: false });

interface FolderOption {
  id: string;
  group: "Recent" | "Repositories" | "Other";
  title: string;
  detail: string;
  mono: boolean;
  icon: "repository" | "directory" | "browse" | "none";
  isSelected: boolean;
  choose: () => void;
}

const listId = useId();
const view = shallowRef<"list" | "browse">("list");
const query = shallowRef("");
const highlightedIndex = shallowRef(0);
const directoryDraft = shallowRef("");
const isDirectoryPickerOpen = shallowRef(false);
const searchInput = useTemplateRef<HTMLInputElement>("search");
const directoryInput = useTemplateRef<HTMLInputElement>("directory");
// Any folder on this computer: one outside the workspace roots can be added from here.
const directoryBrowser = useDirectoryBrowser(false, { unconstrained: true });
const browseStatus = shallowRef<"idle" | "checking" | "adding">("idle");
const browseError = shallowRef<string | null>(null);
/** A folder that was picked but is outside the workspace roots, waiting to be added. */
const folderToAdd = shallowRef<FolderInspection | null>(null);

function baseName(path: string): string {
  return path.split(/[/\\]/).filter(Boolean).pop() ?? path;
}

function isCurrent(candidate: NewSessionFolder): boolean {
  const current = props.folder;
  if (!current || current.kind !== candidate.kind) {
    return false;
  }
  return current.kind === "none" || (candidate.kind !== "none" && current.path === candidate.path);
}

function choose(folder: NewSessionFolder): void {
  emit("update:folder", folder);
  open.value = false;
}

function repositoryOption(repository: ScannedRepository, group: FolderOption["group"]): FolderOption {
  const folder: NewSessionFolder = { kind: "repository", path: repository.path };
  return {
    id: `${group}:${repository.path}`,
    group,
    title: repository.name,
    detail: tildePath(repository.path),
    mono: true,
    icon: "repository",
    isSelected: isCurrent(folder),
    choose: () => choose(folder),
  };
}

const options = computed<FolderOption[]>(() => {
  const needle = query.value.trim().toLowerCase();
  const matches = (text: string) => !needle || text.toLowerCase().includes(needle);
  const byPath = new Map(props.repositories.map((repository) => [repository.path, repository]));

  const recent: FolderOption[] = [];
  const recentPaths = new Set<string>();
  for (const folder of props.recentFolders) {
    if (folder.kind === "repository") {
      const repository = byPath.get(folder.path);
      if (repository && matches(`${repository.name} ${repository.path}`)) {
        recent.push(repositoryOption(repository, "Recent"));
      }
      recentPaths.add(folder.path);
    } else if (folder.kind === "directory" && props.allowBrowse && matches(folder.path)) {
      recent.push({
        id: `Recent:dir:${folder.path}`,
        group: "Recent",
        title: baseName(folder.path),
        detail: tildePath(folder.path),
        mono: true,
        icon: "directory",
        isSelected: isCurrent(folder),
        choose: () => choose(folder),
      });
    }
  }

  const others = [...props.repositories]
    .filter((repository) => !recentPaths.has(repository.path) && matches(`${repository.name} ${repository.path}`))
    .sort((left, right) => left.name.localeCompare(right.name))
    .map((repository) => repositoryOption(repository, "Repositories"));

  const extras: FolderOption[] = [];
  if (props.allowBrowse) {
    extras.push({
      id: "browse",
      group: "Other",
      title: "Browse for a folder…",
      detail: "Any repository or folder on this computer",
      mono: false,
      icon: "browse",
      isSelected: props.folder?.kind === "directory",
      choose: () => showBrowse(),
    });
  }
  if (props.allowNone) {
    extras.push({
      id: "none",
      group: "Other",
      title: "No folder, just chat",
      detail: "Ask anything without a repository",
      mono: false,
      icon: "none",
      isSelected: props.folder?.kind === "none",
      choose: () => choose({ kind: "none" }),
    });
  }

  return [...recent, ...others, ...extras];
});

const hasRepositoryMatches = computed(() => options.value.some((option) => option.icon === "repository"));

function optionDomId(index: number): string {
  return `${listId}-option-${index}`;
}

function startsGroup(index: number): boolean {
  return index === 0 || options.value[index - 1]?.group !== options.value[index]?.group;
}

function handleSearchKeydown(event: KeyboardEvent): void {
  const count = options.value.length;
  if (count === 0) {
    return;
  }

  if (event.key === "ArrowDown") {
    event.preventDefault();
    highlightedIndex.value = (highlightedIndex.value + 1) % count;
    scrollHighlightedIntoView();
  } else if (event.key === "ArrowUp") {
    event.preventDefault();
    highlightedIndex.value = (highlightedIndex.value - 1 + count) % count;
    scrollHighlightedIntoView();
  } else if (event.key === "Enter") {
    event.preventDefault();
    options.value[highlightedIndex.value]?.choose();
  }
}

function scrollHighlightedIntoView(): void {
  void nextTick(() => {
    document.getElementById(optionDomId(highlightedIndex.value))?.scrollIntoView?.({ block: "nearest" });
  });
}

function resetBrowseCheck(): void {
  browseError.value = null;
  folderToAdd.value = null;
}

function showBrowse(): void {
  directoryDraft.value = props.folder?.kind === "directory" ? props.folder.path : "";
  resetBrowseCheck();
  view.value = "browse";
  void nextTick(() => directoryInput.value?.focus());
}

function showList(): void {
  view.value = "list";
  void nextTick(() => searchInput.value?.focus());
}

/**
 * Uses the typed or picked folder: a git checkout becomes a repository (so it can get a
 * worktree), anything else a plain folder. One outside the workspace roots asks to be added first.
 */
async function useDirectory(path = directoryDraft.value): Promise<void> {
  const trimmed = path.trim();
  if (!trimmed || browseStatus.value !== "idle") {
    return;
  }

  directoryDraft.value = trimmed;
  resetBrowseCheck();
  browseStatus.value = "checking";
  try {
    const inspection = await inspectFolder(trimmed);
    if (!inspection.exists) {
      browseError.value = "There's no folder at that path.";
    } else if (!inspection.isWithinRoots) {
      directoryDraft.value = inspection.path;
      folderToAdd.value = inspection;
    } else {
      choose(folderForInspection(inspection));
    }
  } catch (error) {
    browseError.value = error instanceof Error ? error.message : "Couldn't check that folder.";
  } finally {
    browseStatus.value = "idle";
  }
}

async function addAndUseFolder(): Promise<void> {
  const inspection = folderToAdd.value;
  if (!inspection || browseStatus.value !== "idle") {
    return;
  }

  browseStatus.value = "adding";
  try {
    await addFolderToFleet(inspection.path);
    emit("folderAdded");
    choose(folderForInspection(inspection));
  } catch (error) {
    folderToAdd.value = null;
    browseError.value = error instanceof Error ? error.message : "Couldn't add that folder.";
  } finally {
    browseStatus.value = "idle";
  }
}

function handleDirectoryPickerOpenChange(value: boolean): void {
  if (value) {
    directoryBrowser.browse(directoryDraft.value.trim() || null);
  }
  isDirectoryPickerOpen.value = value;
}

watch(query, () => {
  highlightedIndex.value = 0;
});

watch(open, (isOpen) => {
  if (isOpen) {
    query.value = "";
    view.value = "list";
    // After the query watcher, which would put the highlight back on the first row.
    void nextTick(() => {
      const selected = options.value.findIndex((option) => option.isSelected);
      highlightedIndex.value = selected >= 0 ? selected : 0;
      scrollHighlightedIntoView();
    });
  } else {
    isDirectoryPickerOpen.value = false;
  }
});

const chipLabel = computed(() => {
  const folder = props.folder;
  if (!folder) {
    return "Choose a folder";
  }
  if (folder.kind === "none") {
    return "No folder";
  }
  if (folder.kind === "directory") {
    return tildePath(folder.path);
  }
  return props.repositories.find((repository) => repository.path === folder.path)?.name ?? baseName(folder.path);
});
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        :class="{ 'ns-chip--attention': !folder }"
        data-testid="new-session-folder-chip"
        :disabled="disabled"
        :title="folder && folder.kind !== 'none' ? folder.path : undefined"
      >
        <MessageSquare
          v-if="folder?.kind === 'none'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <Folder
          v-else-if="folder?.kind === 'directory'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <FolderGit2
          v-else
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">{{ chipLabel }}</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </PopoverTrigger>

    <PopoverContent
      class="ns-pop"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <template v-if="view === 'list'">
        <div class="ns-folder-search">
          <Search
            class="ns-folder-search__icon"
            aria-hidden="true"
          />
          <input
            ref="search"
            v-model="query"
            type="text"
            class="ns-folder-search__input"
            placeholder="Search repositories…"
            autocomplete="off"
            spellcheck="false"
            role="combobox"
            aria-label="Search repositories"
            aria-expanded="true"
            :aria-controls="listId"
            :aria-activedescendant="options.length > 0 ? optionDomId(highlightedIndex) : undefined"
            @keydown="handleSearchKeydown"
          >
        </div>

        <div
          :id="listId"
          class="ns-folder-list"
          role="listbox"
          aria-label="Folders"
        >
          <template
            v-for="(option, index) in options"
            :key="option.id"
          >
            <div
              v-if="startsGroup(index) && option.group === 'Other' && index > 0"
              class="ns-pop__separator"
              role="presentation"
            />
            <div
              v-else-if="startsGroup(index) && option.group !== 'Other'"
              class="ns-pop__label"
              role="presentation"
            >
              {{ option.group }}
            </div>
            <button
              :id="optionDomId(index)"
              type="button"
              class="ns-option"
              role="option"
              tabindex="-1"
              :aria-selected="option.isSelected"
              :data-highlighted="index === highlightedIndex ? '' : undefined"
              @click="option.choose()"
              @mousemove="highlightedIndex = index"
            >
              <FolderGit2
                v-if="option.icon === 'repository'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <Folder
                v-else-if="option.icon === 'directory'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <FolderOpen
                v-else-if="option.icon === 'browse'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <MessageSquare
                v-else
                class="ns-option__icon"
                aria-hidden="true"
              />
              <span class="ns-option__text">
                <span class="ns-option__title">{{ option.title }}</span>
                <span
                  class="ns-option__detail"
                  :class="{ 'ns-option__detail--mono': option.mono }"
                >{{ option.detail }}</span>
              </span>
              <Check
                v-if="option.isSelected"
                class="ns-option__check"
                aria-hidden="true"
              />
            </button>
          </template>

          <p
            v-if="!hasRepositoryMatches"
            class="ns-pop__note"
          >
            <template v-if="query.trim()">
              No repository matches “{{ query.trim() }}”.
            </template>
            <template v-else-if="allowBrowse">
              No repositories yet. Browse for a folder to add one.
            </template>
            <template v-else>
              No repositories found in your workspace roots.
            </template>
          </p>
        </div>
      </template>

      <template v-else>
        <button
          type="button"
          class="ns-option ns-folder-back"
          @click="showList"
        >
          <ArrowLeft
            class="ns-option__icon"
            aria-hidden="true"
          />
          <span class="ns-option__text">
            <span class="ns-option__title">All folders</span>
          </span>
        </button>
        <div class="ns-pop__separator" />
        <div class="ns-field">
          <label
            for="new-session-directory"
            class="ns-field__label"
          >Folder</label>
          <div class="ns-folder-path">
            <input
              id="new-session-directory"
              ref="directory"
              v-model="directoryDraft"
              type="text"
              class="ns-field__input ns-field__input--mono"
              placeholder="/path/to/folder"
              autocomplete="off"
              spellcheck="false"
              @input="resetBrowseCheck"
              @keydown.enter.prevent="folderToAdd ? addAndUseFolder() : useDirectory()"
            >
            <DirectoryPickerPopover
              :browser="directoryBrowser"
              :open="isDirectoryPickerOpen"
              mode="navigate"
              align="end"
              content-class="w-[25rem] max-w-[calc(100vw-2rem)]"
              @update:open="handleDirectoryPickerOpenChange"
              @select="useDirectory"
            >
              <template #trigger>
                <Button
                  type="button"
                  variant="outline"
                  size="icon"
                  class="shrink-0"
                  aria-label="Browse folders"
                  title="Browse folders"
                >
                  <FolderOpen class="h-4 w-4" />
                </Button>
              </template>
            </DirectoryPickerPopover>
          </div>
        </div>
        <p
          v-if="browseError"
          class="ns-folder-error"
          role="alert"
        >
          {{ browseError }}
        </p>
        <div
          v-else-if="folderToAdd"
          class="ns-folder-add"
          role="status"
          data-testid="new-session-add-folder"
        >
          <FolderPlus
            class="ns-folder-add__icon"
            aria-hidden="true"
          />
          <div class="ns-folder-add__text">
            <p class="ns-folder-add__title">
              {{ baseName(folderToAdd.path) }} isn't in Fleet yet
            </p>
            <p class="ns-folder-add__detail">
              {{ folderToAdd.isGitRepo
                ? "Add this repository to work in its checkout or in new worktrees beside it."
                : "Add this folder so sessions can work in it." }}
              You can remove it in Settings.
            </p>
          </div>
        </div>
        <div class="ns-folder-actions">
          <template v-if="folderToAdd">
            <Button
              type="button"
              size="sm"
              variant="ghost"
              :disabled="browseStatus !== 'idle'"
              @click="resetBrowseCheck"
            >
              Cancel
            </Button>
            <Button
              type="button"
              size="sm"
              :disabled="browseStatus !== 'idle'"
              @click="addAndUseFolder()"
            >
              {{ browseStatus === "adding" ? "Adding…" : folderToAdd.isGitRepo ? "Add repository" : "Add folder" }}
            </Button>
          </template>
          <Button
            v-else
            type="button"
            size="sm"
            :disabled="!directoryDraft.trim() || browseStatus !== 'idle'"
            @click="useDirectory()"
          >
            {{ browseStatus === "checking" ? "Checking…" : "Use this folder" }}
          </Button>
        </div>
      </template>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.ns-folder-search {
  display: flex;
  align-items: center;
  gap: 7px;
  margin: -1px -1px 4px;
  padding: 5px 8px 7px;
  border-bottom: 1px solid var(--border);
  color: var(--muted);
}

.ns-folder-search__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.ns-folder-search__input {
  min-width: 0;
  flex: 1;
  border: 0;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  outline: none;
}

.ns-folder-search__input::placeholder {
  color: var(--muted);
}

.ns-folder-list {
  max-height: min(340px, 50vh);
  overflow-y: auto;
  overscroll-behavior: contain;
}

.ns-folder-back {
  grid-template-columns: 16px minmax(0, 1fr);
}

.ns-folder-path {
  display: flex;
  gap: 6px;
}

.ns-folder-actions {
  display: flex;
  justify-content: flex-end;
  gap: 6px;
  padding: 0 8px 6px;
}

.ns-folder-error {
  margin: 0 8px 8px;
  color: var(--error);
  font-size: 12.5px;
}

.ns-folder-add {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr);
  gap: 8px;
  margin: 0 8px 8px;
  padding: 8px;
  border: 1px solid color-mix(in srgb, var(--accent) 35%, var(--border));
  border-radius: calc(var(--radius-btn) - 2px);
  background: color-mix(in srgb, var(--accent) 7%, transparent);
}

.ns-folder-add__icon {
  width: 14px;
  height: 14px;
  margin-top: 2px;
  color: var(--accent);
}

.ns-folder-add__text {
  display: grid;
  min-width: 0;
  gap: 2px;
}

.ns-folder-add__title {
  overflow: hidden;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ns-folder-add__detail {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}
</style>
