import { computed, readonly, ref, shallowRef, toValue, watch, type ComputedRef, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import { useFindFiles } from "@/composables/use-find-files";
import { api } from "@/api/client";
import type { AutocompleteAgent, AutocompleteCommand } from "@/api/client";
import { loadSessionAgentList } from "@/composables/use-agents";
import { sessionCatalogChanges } from "@/lib/harness-catalog-changes";

export interface AutocompleteItem {
  id: string;
  label: string;
  description?: string;
  group: "command" | "agent" | "file";
  value: string;
  meta?: string;
}

export interface UseAutocompleteParams {
  value: Ref<string>;
  setValue: (value: string) => void;
  sessionId: MaybeRefOrGetter<string | null | undefined>;
  inputRef: Ref<HTMLTextAreaElement | null>;
  cursorPosition: Ref<number>;
}

export interface UseAutocompleteResult {
  isOpen: ComputedRef<boolean>;
  items: ComputedRef<AutocompleteItem[]>;
  isLoading: ComputedRef<boolean>;
  error: ComputedRef<string | undefined>;
  selectedValue: ComputedRef<string | null>;
  selectedIndex: Readonly<Ref<number>>;
  onKeyDown: (event: KeyboardEvent) => void;
  onSelect: (value: string) => void;
  onOpenFolder: (value: string) => void;
  onClose: () => void;
}

interface Trigger {
  type: "slash" | "mention";
  startIndex: number;
}

interface UseStaticInstanceDataResult<T> {
  data: Readonly<Ref<readonly T[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

function useSessionCommands(sessionId: MaybeRefOrGetter<string | null | undefined>): UseStaticInstanceDataResult<AutocompleteCommand> {
  const data = ref<AutocompleteCommand[]>([]);
  const currentSessionId = computed(() => toValue(sessionId)?.trim() ?? "");
  const isLoading = shallowRef(Boolean(currentSessionId.value));
  const error = shallowRef<string | undefined>(undefined);
  const changes = sessionCatalogChanges(currentSessionId);

  watch(
    [currentSessionId, changes],
    async ([nextSessionId], previous, onCleanup) => {
      if (!nextSessionId) {
        data.value = [];
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      const controller = new AbortController();
      onCleanup(() => {
        controller.abort();
      });
      // Asked again because the harness's list changed: the old one stays up meanwhile.
      if (previous?.[0] !== nextSessionId) {
        isLoading.value = true;
      }
      error.value = undefined;

      try {
        const { data: responseData, error, response } = await api.GET("/api/sessions/{id}/commands", {
          params: { path: { id: nextSessionId } },
          signal: controller.signal,
        });

        if (error || !response.ok) {
          const payload = error as { error?: string } | undefined;
          throw new Error(payload?.error ?? `HTTP ${response.status}`);
        }

        const body = responseData as unknown as { commands?: AutocompleteCommand[] };
        data.value = body.commands ?? [];
      } catch (fetchError) {
        if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
          return;
        }

        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load commands";
      } finally {
        isLoading.value = false;
      }
    },
    { immediate: true },
  );

  return { data: readonly(data), isLoading: readonly(isLoading), error: readonly(error) };
}

function useSessionAgents(sessionId: MaybeRefOrGetter<string | null | undefined>): UseStaticInstanceDataResult<AutocompleteAgent> {
  const data = ref<AutocompleteAgent[]>([]);
  const currentSessionId = computed(() => toValue(sessionId)?.trim() ?? "");
  const isLoading = shallowRef(Boolean(currentSessionId.value));
  const error = shallowRef<string | undefined>(undefined);
  const changes = sessionCatalogChanges(currentSessionId);

  watch(
    [currentSessionId, changes],
    async ([nextSessionId], previous, onCleanup) => {
      if (!nextSessionId) {
        data.value = [];
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      // The request is shared with the session's agent picker, so leaving only drops its answer.
      let left = false;
      onCleanup(() => {
        left = true;
      });
      // Asked again because the harness's list changed: the old one stays up meanwhile.
      if (previous?.[0] !== nextSessionId) {
        isLoading.value = true;
      }
      error.value = undefined;

      try {
        const agents = await loadSessionAgentList(nextSessionId);
        if (!left) {
          data.value = agents;
        }
      } catch (fetchError) {
        if (!left) {
          error.value = fetchError instanceof Error ? fetchError.message : "Failed to load agents";
        }
      } finally {
        if (!left) {
          isLoading.value = false;
        }
      }
    },
    { immediate: true },
  );

  return { data: readonly(data), isLoading: readonly(isLoading), error: readonly(error) };
}

export function useAutocomplete({
  value,
  setValue,
  sessionId,
  inputRef,
  cursorPosition,
}: UseAutocompleteParams): UseAutocompleteResult {
  const selectedIndex = ref(0);
  const suppressedValue = ref<string | null>(null);

  const computedTrigger = computed<Trigger | null>(() => {
    if (!value.value) {
      return null;
    }

    if (value.value.startsWith("/") && cursorPosition.value >= 1) {
      const afterSlash = value.value.slice(1, cursorPosition.value);
      if (!afterSlash.includes(" ")) {
        return { type: "slash", startIndex: 0 };
      }

      return null;
    }

    const textBeforeCursor = value.value.slice(0, cursorPosition.value);
    const atIndex = textBeforeCursor.lastIndexOf("@");
    if (atIndex === -1) {
      return null;
    }

    const characterBefore = atIndex > 0 ? textBeforeCursor[atIndex - 1] : null;
    if (characterBefore !== null && !/\s/.test(characterBefore)) {
      return null;
    }

    const textBetween = textBeforeCursor.slice(atIndex + 1);
    if (textBetween.includes(" ")) {
      return null;
    }

    return { type: "mention", startIndex: atIndex };
  });

  const filterText = computed(() => {
    if (!computedTrigger.value) {
      return "";
    }

    return value.value.slice(computedTrigger.value.startIndex + 1, cursorPosition.value);
  });

  const { data: commands, isLoading: commandsLoading, error: commandsError } = useSessionCommands(sessionId);
  const { data: agents, isLoading: agentsLoading, error: agentsError } = useSessionAgents(sessionId);
  const mentionQuery = computed(() => (computedTrigger.value?.type === "mention" ? filterText.value : null));
  const { files, isLoading: filesLoading, error: filesError } = useFindFiles(sessionId, mentionQuery);

  const isSuppressed = computed(() => suppressedValue.value !== null && suppressedValue.value === value.value);
  const isOpen = computed(() => computedTrigger.value !== null && !isSuppressed.value);

  const items = computed<AutocompleteItem[]>(() => {
    if (!computedTrigger.value || isSuppressed.value) {
      return [];
    }

    if (computedTrigger.value.type === "slash") {
      const filter = filterText.value.toLowerCase();
      return commands.value
        .filter((command) => command.name.toLowerCase().startsWith(filter))
        .map((command) => ({
          id: `command:${command.name}`,
          label: `/${command.name}`,
          description: command.description,
          group: "command",
          value: `/${command.name} `,
        }));
    }

    const filter = filterText.value.toLowerCase();
    const agentItems: AutocompleteItem[] = agents.value
      .filter((agent) => filter === ""
        || agent.name.toLowerCase().includes(filter)
        || agent.description?.toLowerCase().includes(filter))
      .map((agent) => ({
        id: `agent:${agent.name}`,
        label: `@${agent.name}`,
        description: agent.description ?? agent.mode,
        group: "agent",
        value: `@${agent.name} `,
        meta: agent.color,
      }));

    const fileItems: AutocompleteItem[] = files.value.map((filePath) => {
      const isDirectory = filePath.endsWith("/");
      const trimmedPath = filePath.replace(/\/$/, "");
      const nameStart = trimmedPath.lastIndexOf("/") + 1;
      const parent = trimmedPath.slice(0, nameStart);

      return {
        id: `file:${filePath}`,
        label: filePath.slice(nameStart),
        description: parent.length > 48 ? `…${parent.slice(-47)}` : parent || undefined,
        group: "file",
        value: `@${filePath} `,
        meta: isDirectory ? "dir" : undefined,
      };
    });

    // Files and folders first: they're what @ is mostly for.
    return [...fileItems, ...agentItems];
  });

  const clampedIndex = computed(() => items.value.length === 0
    ? 0
    : Math.min(selectedIndex.value, items.value.length - 1));
  const selectedValue = computed(() => items.value[clampedIndex.value]?.value ?? null);

  /** Opens a folder in the popup: the text becomes `@folder/` and the folder's contents are listed. */
  function onOpenFolder(itemValue: string): void {
    onSelect(itemValue.trimEnd());
  }

  function onSelect(itemValue: string): void {
    const trigger = computedTrigger.value;
    if (!trigger) {
      return;
    }

    let newValue: string;
    let newCursor: number;

    if (trigger.type === "slash") {
      newValue = itemValue;
      newCursor = itemValue.length;
    } else {
      const before = value.value.slice(0, trigger.startIndex);
      const after = value.value.slice(cursorPosition.value);
      newValue = before + itemValue + after;
      newCursor = (before + itemValue).length;
    }

    setValue(newValue);
    // Move the tracked caret now, not on keyup, so an opened folder lists its contents straight away.
    cursorPosition.value = newCursor;
    selectedIndex.value = 0;
    suppressedValue.value = null;

    setTimeout(() => {
      const input = inputRef.value;
      if (!input) {
        return;
      }

      input.selectionStart = newCursor;
      input.selectionEnd = newCursor;
    }, 0);
  }

  function onClose(): void {
    suppressedValue.value = value.value;
    selectedIndex.value = 0;
  }

  function onKeyDown(event: KeyboardEvent): void {
    if (!isOpen.value) {
      return;
    }

    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        selectedIndex.value = items.value.length === 0 ? 0 : (selectedIndex.value + 1) % items.value.length;
        break;
      case "ArrowUp":
        event.preventDefault();
        selectedIndex.value = items.value.length === 0
          ? 0
          : (selectedIndex.value - 1 + items.value.length) % items.value.length;
        break;
      case "Enter":
      case "Tab": {
        const item = items.value[clampedIndex.value];
        if (item) {
          event.preventDefault();
          if (event.key === "Tab" && item.meta === "dir") {
            onOpenFolder(item.value);
          } else {
            onSelect(item.value);
          }
        }
        break;
      }
      case "Escape":
        event.preventDefault();
        event.stopPropagation();
        onClose();
        break;
      default:
        selectedIndex.value = 0;
        if (suppressedValue.value !== null && value.value !== suppressedValue.value) {
          suppressedValue.value = null;
        }
        break;
    }
  }

  const isLoading = computed(() => (
    computedTrigger.value?.type === "slash"
      ? commandsLoading.value
      : computedTrigger.value?.type === "mention"
        ? agentsLoading.value || filesLoading.value
        : false
  ));

  const error = computed(() => (
    computedTrigger.value?.type === "slash"
      ? commandsError.value
      : computedTrigger.value?.type === "mention"
        ? agentsError.value ?? filesError.value
        : undefined
  ));

  return {
    isOpen,
    items,
    isLoading,
    error,
    selectedValue,
    selectedIndex: readonly(selectedIndex),
    onKeyDown,
    onSelect,
    onOpenFolder,
    onClose,
  };
}
