import { computed, readonly, ref, shallowRef, toValue, watch, type ComputedRef, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import { useFindFiles } from "@/composables/use-find-files";
import type { AutocompleteAgent, AutocompleteCommand, SessionListItem } from "@/api/client";
import { loadSessionAgentList } from "@/composables/use-agents";
import { sessionCatalogChanges } from "@/lib/harness-catalog-changes";
import { pickedFileTokens, rememberFileReference } from "@/lib/composer-references";
import {
  matchReferableSessions,
  rememberSessionReference,
  rememberedSessionTokens,
  sessionReferenceToken,
  type ReferableSession,
} from "@/lib/session-references";
import { useMachineTarget } from "@/lib/machine-target";
import { rememberSessionSkills, sessionSkillNames, sessionSkills, skillNamesIn, skillTrigger } from "@/lib/skill-mentions";

export interface AutocompleteItem {
  id: string;
  label: string;
  description?: string;
  group: "command" | "agent" | "file" | "session" | "skill";
  value: string;
  meta?: string;
  /** For a session: what its row shows besides the title. */
  session?: ReferableSession;
  /** Why it can't be picked here; the row is greyed out. */
  disabledReason?: string;
  /** For a skill: a warning to show while it's selected (it goes as a note, not with the message). */
  warning?: string;
}

/** How many sessions the `@` list offers: a few for a bare `@`, which is mostly for files, more once you type. */
const SESSIONS_FOR_BARE_MENTION = 3;
const SESSIONS_FOR_QUERY = 5;
const SESSION_VALUE_PREFIX = "session:";

export interface UseAutocompleteParams {
  value: Ref<string>;
  setValue: (value: string) => void;
  sessionId: MaybeRefOrGetter<string | null | undefined>;
  /**
   * Whose picks these are, when it isn't `sessionId`: the New Session draft's row key. With no session, the `@` list
   * offers sessions and the files in `directory`; there are no `/` commands, `$` skills or agents to offer.
   */
  draftKey?: MaybeRefOrGetter<string | null | undefined>;
  /** Where files are found when there's no session yet: the New Session page's folder. */
  directory?: MaybeRefOrGetter<string | null | undefined>;
  inputRef: Ref<HTMLTextAreaElement | null>;
  cursorPosition: Ref<number>;
  /** Fleet's own commands (`/btw`), listed before the harness's; a harness command of the same name is left out. */
  fleetCommands?: MaybeRefOrGetter<readonly AutocompleteCommand[]>;
  /** Sessions the `@` list can offer (see `useReferableSessions`); the session written to is left out. */
  sessions?: MaybeRefOrGetter<readonly SessionListItem[]>;
  /** A harness's name for the sessions' rows. */
  harnessName?: (type: string) => string | undefined;
  /** The session's harness, which decides how a `$` skill is sent (see `skillDeliveries`). */
  harnessType?: MaybeRefOrGetter<string | null | undefined>;
}

export interface UseAutocompleteResult {
  isOpen: ComputedRef<boolean>;
  /** Whether the `@` list is open: what's typed after `@` names a file, an agent or a session. */
  isMentionOpen: ComputedRef<boolean>;
  /** Whether the `$` list is open: what's typed after `$` names a skill. */
  isSkillOpen: ComputedRef<boolean>;
  items: ComputedRef<AutocompleteItem[]>;
  /** What's typed after the `@` or `/`, for the popup to mark in the rows. */
  query: ComputedRef<string>;
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
  type: "slash" | "mention" | "skill";
  startIndex: number;
}

interface UseStaticInstanceDataResult<T> {
  data: Readonly<Ref<readonly T[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

function useSessionCommands(sessionId: MaybeRefOrGetter<string | null | undefined>): UseStaticInstanceDataResult<AutocompleteCommand> {
  const machine = useMachineTarget();
  const data = ref<AutocompleteCommand[]>([]);
  const currentSessionId = computed(() => toValue(sessionId)?.trim() ?? "");
  const isLoading = shallowRef(Boolean(currentSessionId.value));
  const error = shallowRef<string | undefined>(undefined);
  const changes = sessionCatalogChanges(machine, currentSessionId);

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
        const { data: responseData, error, response } = await machine.api.GET("/api/sessions/{id}/commands", {
          params: { path: { id: nextSessionId } },
          signal: controller.signal,
        });

        if (error || !response.ok) {
          const payload = error as { error?: string } | undefined;
          throw new Error(payload?.error ?? `HTTP ${response.status}`);
        }

        const body = responseData as unknown as { commands?: AutocompleteCommand[] };
        data.value = body.commands ?? [];
        rememberSessionSkills(nextSessionId, data.value);
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
  const machine = useMachineTarget();
  const data = ref<AutocompleteAgent[]>([]);
  const currentSessionId = computed(() => toValue(sessionId)?.trim() ?? "");
  const isLoading = shallowRef(Boolean(currentSessionId.value));
  const error = shallowRef<string | undefined>(undefined);
  const changes = sessionCatalogChanges(machine, currentSessionId);

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
        const agents = await loadSessionAgentList(machine, nextSessionId);
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
  draftKey = sessionId,
  directory,
  inputRef,
  cursorPosition,
  fleetCommands = [],
  sessions = [],
  harnessName,
  harnessType,
}: UseAutocompleteParams): UseAutocompleteResult {
  const selectedIndex = ref(0);
  const suppressedValue = ref<string | null>(null);
  // Enter or Tab pressed before anything matched what's typed: the draft then, and the key, to pick the first match.
  const pendingPick = shallowRef<{ draft: string; key: string } | null>(null);

  const computedTrigger = computed<Trigger | null>(() => {
    if (!value.value) {
      return null;
    }

    // A `$` the caret is typing, anywhere (after a `/command` too), the caret is typing, when the session has skills to offer.
    const skill = skillTrigger(value.value, cursorPosition.value);
    if (skill && (sessionSkills(toValue(sessionId)).length > 0 || commandsLoading.value)) {
      return { type: "skill", startIndex: skill.start };
    }

    if (value.value.startsWith("/") && cursorPosition.value >= 1 && toValue(sessionId)) {
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

    // The caret at the end of a reference picked from this list: it's done, not being typed.
    const draftSessionId = toValue(draftKey);
    const token = `@${textBetween}`;
    if (draftSessionId && (pickedFileTokens(draftSessionId).has(token) || rememberedSessionTokens(draftSessionId).has(token))) {
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
  const { files, isLoading: filesLoading, error: filesError } = useFindFiles(sessionId, mentionQuery, directory);

  const isSuppressed = computed(() => suppressedValue.value !== null && suppressedValue.value === value.value);
  const isOpen = computed(() => computedTrigger.value !== null && !isSuppressed.value);

  const items = computed<AutocompleteItem[]>(() => {
    if (!computedTrigger.value || isSuppressed.value) {
      return [];
    }

    if (computedTrigger.value.type === "slash") {
      const filter = filterText.value.toLowerCase();
      const own = toValue(fleetCommands);
      const ownNames = new Set(own.map((command) => command.name));
      return [...own, ...commands.value.filter((command) => !ownNames.has(command.name))]
        .filter((command) => command.name.toLowerCase().startsWith(filter))
        .map((command) => ({
          id: `command:${command.name}`,
          label: `/${command.name}`,
          description: command.description,
          group: "command",
          value: `/${command.name} `,
        }));
    }

    if (computedTrigger.value.type === "skill") {
      return skillItems(computedTrigger.value.startIndex);
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

    const sessionItems: AutocompleteItem[] = matchReferableSessions(toValue(sessions), filterText.value, {
      excludeId: toValue(sessionId),
      limit: filter === "" ? SESSIONS_FOR_BARE_MENTION : SESSIONS_FOR_QUERY,
      harnessName,
    }).map((session) => ({
      id: `session:${session.id}`,
      label: session.title,
      description: session.description,
      group: "session",
      value: `${SESSION_VALUE_PREFIX}${session.id}`,
      session,
    }));

    // Sessions, then files and folders: a session is the rarer pick, so the few that match go on top.
    return [...sessionItems, ...fileItems, ...agentItems];
  });

  /**
   * The session's skills matching what's typed after `$`, names starting with it first. On Claude Code, which expands
   * only the skill a message starts with, a skill picked after another goes as a note, and one the model can't load
   * can't go at all.
   */
  function skillItems(startIndex: number): AutocompleteItem[] {
    const filter = filterText.value.toLowerCase();
    const draftSessionId = toValue(sessionId);
    const others = value.value.slice(0, startIndex) + value.value.slice(cursorPosition.value);
    const first = toValue(harnessType) === "claude-code" ? skillNamesIn(others, sessionSkillNames(draftSessionId))[0] : undefined;
    const harness = harnessName?.("claude-code") ?? "Claude Code";

    return sessionSkills(draftSessionId)
      .filter((skill) => skill.name.toLowerCase().includes(filter))
      .sort((a, b) => Number(!a.name.toLowerCase().startsWith(filter)) - Number(!b.name.toLowerCase().startsWith(filter)))
      .map((skill) => ({
        id: `skill:${skill.name}`,
        label: skill.name,
        description: skill.description,
        group: "skill",
        value: `$${skill.name} `,
        ...(first && skill.name !== first
          ? skill.userOnly
            ? { disabledReason: `The model can't load ${skill.name}, so it only runs first in a message, and this one already runs ${first}.` }
            : { warning: `This message already runs ${first}. ${harness} runs one skill per message, so this one goes as a note.` }
          : {}),
      }));
  }

  const clampedIndex = computed(() => items.value.length === 0
    ? 0
    : Math.min(selectedIndex.value, items.value.length - 1));
  const selectedValue = computed(() => items.value[clampedIndex.value]?.value ?? null);

  /** Opens a folder in the popup: the text becomes `@folder/` and the folder's contents are listed. */
  function onOpenFolder(itemValue: string): void {
    onSelect(itemValue.trimEnd());
  }

  function onSelect(selected: string): void {
    const trigger = computedTrigger.value;
    if (!trigger) {
      return;
    }

    let itemValue = selected;
    if (selected.startsWith(SESSION_VALUE_PREFIX)) {
      // A session goes in as a token made from its title; the composer remembers which session the token names.
      const session = items.value.find((item) => item.value === selected)?.session;
      const draftSessionId = toValue(draftKey);
      if (!session || !draftSessionId) {
        return;
      }
      const token = sessionReferenceToken(draftSessionId, session);
      rememberSessionReference(draftSessionId, { token, sessionId: session.id, title: session.title });
      itemValue = `${token} `;
    } else {
      // A file or folder picked (not a folder opened to look inside): the composer edits it as one piece.
      const draftSessionId = toValue(draftKey);
      const picked = items.value.find((item) => item.value === selected);
      if (picked?.group === "file" && draftSessionId) {
        rememberFileReference(draftSessionId, selected.trimEnd());
      }
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
        if (item?.disabledReason) {
          event.preventDefault();
        } else if (item) {
          event.preventDefault();
          pick(item, event.key);
        } else if (isLoading.value) {
          event.preventDefault();
          pendingPick.value = { draft: value.value, key: event.key };
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

  function pick(item: AutocompleteItem, key: string): void {
    if (item.disabledReason) {
      return;
    }
    if (key === "Tab" && item.meta === "dir") {
      onOpenFolder(item.value);
    } else {
      onSelect(item.value);
    }
  }

  const isLoading = computed(() => (
    computedTrigger.value?.type === "slash" || computedTrigger.value?.type === "skill"
      ? commandsLoading.value
      : computedTrigger.value?.type === "mention"
        ? agentsLoading.value || filesLoading.value
        : false
  ));

  const error = computed(() => (
    computedTrigger.value?.type === "slash" || computedTrigger.value?.type === "skill"
      ? commandsError.value
      : computedTrigger.value?.type === "mention"
        ? agentsError.value ?? filesError.value
        : undefined
  ));

  // The answer came: pick its first match, unless the draft changed or the list closed meanwhile.
  watch([items, isLoading], () => {
    const pending = pendingPick.value;
    if (!pending) {
      return;
    }
    if (value.value !== pending.draft || !isOpen.value) {
      pendingPick.value = null;
      return;
    }
    if (isLoading.value) {
      return;
    }
    pendingPick.value = null;
    const item = items.value[0];
    if (item) {
      pick(item, pending.key);
    }
  });

  return {
    isOpen,
    isMentionOpen: computed(() => isOpen.value && computedTrigger.value?.type === "mention"),
    isSkillOpen: computed(() => isOpen.value && computedTrigger.value?.type === "skill"),
    items,
    query: filterText,
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
