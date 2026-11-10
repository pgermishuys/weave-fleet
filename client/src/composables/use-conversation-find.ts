import { computed, nextTick, onBeforeUnmount, onMounted, ref, toValue, watch, type MaybeRefOrGetter } from "vue";
import { getDesktopBridge } from "@/lib/desktop";
import {
  COMPOSER_SELECTOR,
  clearMarks,
  findHits,
  focusKeepsFindKey,
  isFindShortcut,
  markMatches,
  type FindScope,
  type FindUnit,
} from "@/lib/conversation-find";

/**
 * Asks the conversation on screen to open its find bar: the desktop app's Edit → Find in Conversation and the phone's
 * ⋯ menu say it, the conversation listens. A counter, so asking twice opens it twice.
 */
const findRequests = ref(0);
export function requestConversationFind(): void {
  findRequests.value += 1;
}

export interface ConversationFindOptions {
  /** What the conversation says now, oldest first. */
  units: MaybeRefOrGetter<readonly FindUnit[]>;
  /** The stream's paging: older history pages load while the bar is open. */
  hasMore: MaybeRefOrGetter<boolean>;
  isLoadingOlder: MaybeRefOrGetter<boolean>;
  /** The last older page failed: the paging stops until the user retries. */
  loadOlderFailed?: MaybeRefOrGetter<boolean>;
  loadOlder: () => void;
  /** The conversation's element, which the Ctrl/⌘F key is taken inside of. */
  root: MaybeRefOrGetter<HTMLElement | null | undefined>;
  /** The element a unit is drawn in, if it's drawn. */
  elementOf: (unitId: string) => Element | null;
  /** Makes a unit visible and scrolls it into view (opens its folded turn first). */
  reveal: (hit: { unitId: string; turnId: string | null }) => void;
  /** Scrolls a mark into view once the unit is drawn. */
  scrollTo: (range: Range) => void;
  /** Puts the cursor in the bar's field (and selects what it holds). */
  focusField: () => void;
  /** Returns focus to the composer when the bar closes. */
  focusComposer?: () => void;
  /** This conversation is the one on screen and may take the key (not a side conversation, not hidden). */
  enabled?: MaybeRefOrGetter<boolean>;
}

/**
 * Find in this conversation: the bar's state, the matches over the message data, the background paging of older
 * history while the bar is open, and the marks on the page.
 */
export function useConversationFind(options: ConversationFindOptions) {
  const isOpen = ref(false);
  const query = ref("");
  const scope = ref<FindScope>("messages");
  const current = ref(0);
  /** Older history is being loaded for the search. Stop turns it off until the bar is closed and opened again. */
  const pagingStopped = ref(false);

  const hits = computed(() => (isOpen.value ? findHits(toValue(options.units), query.value, scope.value) : []));
  const currentHit = computed(() => hits.value[current.value] ?? null);
  const olderFailed = computed(() => isOpen.value && !pagingStopped.value && toValue(options.hasMore) && toValue(options.loadOlderFailed) === true);
  const searchingOlder = computed(() => isOpen.value && !pagingStopped.value && toValue(options.hasMore) && !olderFailed.value);
  const hasQuery = computed(() => query.value.trim().length > 0);

  function open(): void {
    isOpen.value = true;
    pagingStopped.value = false;
    void nextTick(options.focusField);
  }

  function close(): void {
    if (!isOpen.value) return;
    isOpen.value = false;
    clearMarks();
    options.focusComposer?.();
  }

  function stopSearchingOlder(): void {
    pagingStopped.value = true;
  }

  /** Asks again for the page that failed; the paging carries on from there. */
  function retryOlder(): void {
    options.loadOlder();
  }

  // Older pages load one after another while the bar is open: each arrival asks for the next.
  watch(
    () => [searchingOlder.value, toValue(options.isLoadingOlder)] as const,
    ([searching, loading]) => {
      if (searching && !loading) options.loadOlder();
    },
    { immediate: true },
  );

  function marks(scrollToCurrent: boolean): void {
    if (!isOpen.value || !hasQuery.value) {
      clearMarks();
      return;
    }
    const seen = new Set<string>();
    const elements: Element[] = [];
    for (const hit of hits.value) {
      if (seen.has(hit.unitId)) continue;
      seen.add(hit.unitId);
      const element = options.elementOf(hit.unitId);
      if (element) elements.push(element);
    }
    const hit = currentHit.value;
    const range = markMatches(elements, query.value, {
      element: hit ? options.elementOf(hit.unitId) : null,
      ordinal: hit?.ordinal ?? 0,
    });
    if (scrollToCurrent && range) options.scrollTo(range);
  }

  async function jump(): Promise<void> {
    const hit = currentHit.value;
    if (!hit) {
      marks(false);
      return;
    }
    options.reveal(hit);
    await nextTick();
    // The unit may have been mounted or unfolded just now: wait a frame for it to be laid out.
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    marks(true);
  }

  function step(direction: 1 | -1): void {
    const total = hits.value.length;
    if (total === 0) return;
    current.value = (current.value + direction + total) % total;
    void jump();
  }

  // A new query or scope starts at the first match (the one nearest the end would be the newest; the first is the
  // oldest, so Enter walks forward through the conversation).
  watch([query, scope], () => {
    current.value = 0;
  });
  watch(hits, (next) => {
    if (current.value >= next.length) current.value = 0;
    void nextTick(() => marks(false));
  });
  watch([query, scope, () => hits.value.length > 0], () => void jump());

  // The Ctrl/⌘F key, while the conversation has focus or nothing in particular does.
  function onKeydown(event: KeyboardEvent): void {
    if (toValue(options.enabled) === false) return;
    const apple = typeof navigator !== "undefined" && /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);
    if (!isFindShortcut(event, apple) || event.defaultPrevented) return;
    const root = toValue(options.root);
    if (!root || root.offsetParent === null) return;
    const target = event.target;
    const owner = target instanceof Node && target !== document.body && target !== document.documentElement;
    if (isOpen.value && target instanceof HTMLInputElement && root.contains(target)) {
      // The bar's own field: a second press selects the query, as a browser's find does.
      event.preventDefault();
      target.select();
      return;
    }
    if (focusKeepsFindKey(target)) return;
    // The composer sits beside the conversation, not in it, but is part of it for this key.
    const inComposer = target instanceof HTMLElement && target.matches(COMPOSER_SELECTOR);
    if (owner && !inComposer && !root.contains(target as Node)) return;
    event.preventDefault();
    open();
  }

  let lastRequest = findRequests.value;
  const stopRequests = watch(findRequests, (count) => {
    if (count === lastRequest) return;
    lastRequest = count;
    if (toValue(options.enabled) === false) return;
    const root = toValue(options.root);
    if (root && root.offsetParent === null) return;
    open();
  });

  let stopDesktopMenu: (() => void) | undefined;
  onMounted(() => {
    window.addEventListener("keydown", onKeydown);
    stopDesktopMenu = getDesktopBridge()?.onFindInConversation?.(requestConversationFind);
  });
  onBeforeUnmount(() => {
    window.removeEventListener("keydown", onKeydown);
    stopDesktopMenu?.();
    stopRequests();
    if (isOpen.value) clearMarks();
  });

  return {
    isOpen,
    query,
    scope,
    current,
    hits,
    searchingOlder,
    olderFailed,
    retryOlder,
    open,
    close,
    step,
    stopSearchingOlder,
  };
}
