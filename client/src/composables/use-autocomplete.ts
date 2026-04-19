import { computed, readonly, ref, shallowRef, watch, type ComputedRef, type Ref, type ShallowRef } from "vue";
import { useFindFiles } from "@/composables/use-find-files";
import { apiFetch } from "@/lib/api-client";
import type { AutocompleteAgent, AutocompleteCommand } from "@/lib/api-types";

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
  instanceId: string;
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

function useInstanceCommands(instanceId: string): UseStaticInstanceDataResult<AutocompleteCommand> {
  const data = ref<AutocompleteCommand[]>([]);
  const isLoading = shallowRef(Boolean(instanceId));
  const error = shallowRef<string | undefined>(undefined);

  watch(
    () => instanceId,
    async (nextInstanceId) => {
      if (!nextInstanceId) {
        data.value = [];
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      const controller = new AbortController();
      isLoading.value = true;
      error.value = undefined;

      try {
        const response = await apiFetch(`/api/instances/${encodeURIComponent(nextInstanceId)}/commands`, {
          signal: controller.signal,
        });

        if (!response.ok) {
          const payload = (await response.json().catch(() => ({}))) as { error?: string };
          throw new Error(payload.error ?? `HTTP ${response.status}`);
        }

        const body = (await response.json()) as { commands?: AutocompleteCommand[] };
        data.value = body.commands ?? [];
      } catch (fetchError) {
        if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
          return;
        }

        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load commands";
      } finally {
        isLoading.value = false;
      }

      return () => {
        controller.abort();
      };
    },
    { immediate: true },
  );

  return { data: readonly(data), isLoading: readonly(isLoading), error: readonly(error) };
}

function useInstanceAgents(instanceId: string): UseStaticInstanceDataResult<AutocompleteAgent> {
  const data = ref<AutocompleteAgent[]>([]);
  const isLoading = shallowRef(Boolean(instanceId));
  const error = shallowRef<string | undefined>(undefined);

  watch(
    () => instanceId,
    async (nextInstanceId) => {
      if (!nextInstanceId) {
        data.value = [];
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      const controller = new AbortController();
      isLoading.value = true;
      error.value = undefined;

      try {
        const response = await apiFetch(`/api/instances/${encodeURIComponent(nextInstanceId)}/agents`, {
          signal: controller.signal,
        });

        if (!response.ok) {
          const payload = (await response.json().catch(() => ({}))) as { error?: string };
          throw new Error(payload.error ?? `HTTP ${response.status}`);
        }

        const body = (await response.json()) as { agents?: AutocompleteAgent[] } | AutocompleteAgent[];
        data.value = Array.isArray(body) ? body : body.agents ?? [];
      } catch (fetchError) {
        if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
          return;
        }

        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load agents";
      } finally {
        isLoading.value = false;
      }

      return () => {
        controller.abort();
      };
    },
    { immediate: true },
  );

  return { data: readonly(data), isLoading: readonly(isLoading), error: readonly(error) };
}

export function useAutocomplete({
  value,
  setValue,
  instanceId,
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

  const { data: commands, isLoading: commandsLoading, error: commandsError } = useInstanceCommands(instanceId);
  const { data: agents, isLoading: agentsLoading, error: agentsError } = useInstanceAgents(instanceId);
  const { files, isLoading: filesLoading, error: filesError } = useFindFiles(instanceId, filterText);

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
      const segments = filePath.replace(/\/$/, "").split("/");
      const shortName = `${segments[segments.length - 1]}${isDirectory ? "/" : ""}`;
      const displayPath = filePath.length > 40 ? `…${filePath.slice(-39)}` : filePath;

      return {
        id: `file:${filePath}`,
        label: displayPath,
        description: shortName !== displayPath ? shortName : undefined,
        group: "file",
        value: `@${filePath} `,
        meta: isDirectory ? "dir" : undefined,
      };
    });

    return [...agentItems, ...fileItems];
  });

  const clampedIndex = computed(() => items.value.length === 0
    ? 0
    : Math.min(selectedIndex.value, items.value.length - 1));
  const selectedValue = computed(() => items.value[clampedIndex.value]?.value ?? null);

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
          onSelect(item.value);
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
    onClose,
  };
}
