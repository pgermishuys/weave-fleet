<script setup lang="ts">
import { computed, nextTick, shallowRef, useId, useTemplateRef, watch } from "vue";
import { ArrowLeft, Check, ChevronDown, Folder, FolderGit2, FolderOpen, MessageSquare, Search } from "lucide-vue-next";
import type { ScannedRepository } from "@/api/client";
import { Button } from "@/components/ui/button";
import DirectoryPickerPopover from "@/components/ui/DirectoryPickerPopover.vue";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useDirectoryBrowser } from "@/composables/use-directory-browser";
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
const directoryBrowser = useDirectoryBrowser();

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
      detail: "Any folder, git or not",
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

function showBrowse(): void {
  directoryDraft.value = props.folder?.kind === "directory" ? props.folder.path : "";
  view.value = "browse";
  void nextTick(() => directoryInput.value?.focus());
}

function showList(): void {
  view.value = "list";
  void nextTick(() => searchInput.value?.focus());
}

function useDirectory(path = directoryDraft.value): void {
  const trimmed = path.trim();
  if (trimmed) {
    choose({ kind: "directory", path: trimmed });
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
            {{ query.trim() ? `No repository matches “${query.trim()}”.` : "No repositories found in your workspace roots." }}
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
              @keydown.enter.prevent="useDirectory()"
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
        <div class="ns-folder-actions">
          <Button
            type="button"
            size="sm"
            :disabled="!directoryDraft.trim()"
            @click="useDirectory()"
          >
            Use this folder
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
  padding: 0 8px 6px;
}
</style>
