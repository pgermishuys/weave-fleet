import { nextTick, onMounted, onUnmounted, ref, watch, type Ref } from "vue";

const SCROLL_BOTTOM_THRESHOLD = 80;
const SCROLL_TOP_THRESHOLD = 100;
/** How long every message stays laid out for a jump to one, where the browser doesn't report the scroll's end. */
const SHOW_MESSAGE_LAYOUT_MS = 2000;

export interface StreamScrollOptions {
  /** The scroll box the messages are in. */
  streamRef: Ref<HTMLElement | null>;
  sessionId: () => string;
  /** How many messages are shown; a change after a render keeps the view pinned (or in place, for older ones). */
  messageCount: () => number;
  /** Whether the server has older messages, whether they are being fetched, and the fetch itself. */
  hasMore: () => boolean;
  isLoadingOlder: () => boolean;
  loadOlder: () => void;
  /** The older messages not mounted yet (see useIncrementalMount), and the way to mount them all. */
  unmountedOlder: () => number;
  mountAll: () => void;
}

/**
 * The conversation's scroll box: pinned to the bottom while you are there, a Jump to latest when you aren't,
 * older messages loaded as you reach the top without losing your place, and a jump to one message.
 */
export function useStreamScroll(options: StreamScrollOptions) {
  const { streamRef } = options;
  const showJumpToLatest = ref(false);
  // The Turns canvas asks for a round: scroll to where it began and mark it for a moment.
  const highlightedMessageId = ref<string | null>(null);

  let mutationObserver: MutationObserver | null = null;
  let resizeObserver: ResizeObserver | null = null;
  let keepPinnedToBottom = true;
  let scrollFrame: number | null = null;
  let isRestoringScroll = false;
  let preUpdateScrollHeight = 0;
  let preUpdateScrollTop = 0;
  let wasLoadingOlder = false;
  let highlightTimer: ReturnType<typeof setTimeout> | undefined;

  function isNearBottom(element: HTMLElement): boolean {
    return element.scrollHeight - element.scrollTop - element.clientHeight <= SCROLL_BOTTOM_THRESHOLD;
  }

  function updatePinnedState(): void {
    const element = streamRef.value;
    if (!element) {
      return;
    }

    keepPinnedToBottom = isNearBottom(element);
    showJumpToLatest.value = !keepPinnedToBottom;

    // Trigger loading older messages when scrolled near the top
    if (!isRestoringScroll && element.scrollTop <= SCROLL_TOP_THRESHOLD) {
      handleLoadOlder();
    }
  }

  function handleLoadOlder(): void {
    if (options.unmountedOlder() > 0) {
      options.mountAll();
      return;
    }

    if (options.hasMore() && !options.isLoadingOlder()) {
      options.loadOlder();
    }
  }

  function scrollToBottom(): void {
    const element = streamRef.value;
    if (!element) {
      return;
    }

    element.scrollTop = element.scrollHeight;
    keepPinnedToBottom = true;
    showJumpToLatest.value = false;
  }

  function handleJumpToLatest(): void {
    scrollToBottom();
  }

  function scrollToTop(): void {
    const element = streamRef.value;

    if (!element) {
      return;
    }

    element.scrollTo({ top: 0, behavior: "smooth" });
  }

  function showMessage(messageId: string): void {
    const target = streamRef.value?.querySelector<HTMLElement>(`[data-message-id="${CSS.escape(messageId)}"]`);
    if (!target) {
      if (options.unmountedOlder() > 0) {
        options.mountAll();
        void nextTick(() => showMessage(messageId));
      }
      return;
    }

    keepPinnedToBottom = false;
    // A message that was never on screen has a guessed height, and a smooth scroll past guessed heights aims at the
    // wrong place. Every message is laid out for the scroll; each keeps its real height from then on.
    const stream = streamRef.value!;
    stream.classList.add("activity-stream--laid-out");
    const endLayout = () => stream.classList.remove("activity-stream--laid-out");
    stream.addEventListener("scrollend", endLayout, { once: true });
    setTimeout(endLayout, SHOW_MESSAGE_LAYOUT_MS);
    target.scrollIntoView({ block: "center", behavior: "smooth" });
    highlightedMessageId.value = messageId;
    clearTimeout(highlightTimer);
    highlightTimer = setTimeout(() => {
      highlightedMessageId.value = null;
    }, 1600);
  }

  function scheduleScrollToBottom(): void {
    if (!keepPinnedToBottom || scrollFrame !== null) {
      return;
    }

    scrollFrame = window.requestAnimationFrame(() => {
      scrollFrame = null;
      scrollToBottom();
    });
  }

  onMounted(() => {
    nextTick(() => {
      scrollToBottom();
    });

    // A message can change height without a DOM change: an off-screen message is laid out at a guessed height until
    // it scrolls into view, and images and diagrams finish loading later. Runs after layout and before paint, so the
    // pinned view never shows the gap.
    resizeObserver = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(() => {
      if (keepPinnedToBottom) {
        scrollToBottom();
      }
    });

    mutationObserver = new MutationObserver((records) => {
      for (const record of records) {
        if (record.target !== streamRef.value) continue;
        record.addedNodes.forEach((node) => {
          if (node instanceof Element) resizeObserver?.observe(node);
        });
        record.removedNodes.forEach((node) => {
          if (node instanceof Element) resizeObserver?.unobserve(node);
        });
      }
      scheduleScrollToBottom();
    });

    if (streamRef.value) {
      for (const child of Array.from(streamRef.value.children)) {
        resizeObserver?.observe(child);
      }
      mutationObserver.observe(streamRef.value, {
        childList: true,
        subtree: true,
        characterData: true,
      });
    }
  });

  onUnmounted(() => {
    mutationObserver?.disconnect();
    mutationObserver = null;
    resizeObserver?.disconnect();
    resizeObserver = null;
    clearTimeout(highlightTimer);

    if (scrollFrame !== null) {
      window.cancelAnimationFrame(scrollFrame);
      scrollFrame = null;
    }
  });

  watch(
    options.messageCount,
    async () => {
      await nextTick();

      // If older messages were just prepended, restore scroll position
      if (wasLoadingOlder && streamRef.value) {
        const element = streamRef.value;
        const newScrollHeight = element.scrollHeight;
        const heightDelta = newScrollHeight - preUpdateScrollHeight;

        if (heightDelta > 0) {
          isRestoringScroll = true;
          element.scrollTop = preUpdateScrollTop + heightDelta;
          // Allow scroll handler to settle before re-enabling load-older detection
          requestAnimationFrame(() => {
            isRestoringScroll = false;
          });
        }

        wasLoadingOlder = false;
        return;
      }

      scheduleScrollToBottom();
    },
  );

  // Capture scroll position before older messages start loading
  watch(
    options.isLoadingOlder,
    (loading) => {
      if (loading && streamRef.value) {
        preUpdateScrollHeight = streamRef.value.scrollHeight;
        preUpdateScrollTop = streamRef.value.scrollTop;
        wasLoadingOlder = true;
      }
    },
  );

  watch(
    options.sessionId,
    async () => {
      keepPinnedToBottom = true;
      showJumpToLatest.value = false;
      await nextTick();
      scrollToBottom();
    },
  );

  return {
    showJumpToLatest,
    highlightedMessageId,
    updatePinnedState,
    handleLoadOlder,
    handleJumpToLatest,
    scrollToBottom,
    scrollToTop,
    showMessage,
  };
}
