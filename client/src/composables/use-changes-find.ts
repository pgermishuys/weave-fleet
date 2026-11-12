import { computed, nextTick, onBeforeUnmount, ref, toValue, watch, type MaybeRefOrGetter } from "vue";
import { isFindShortcut } from "@/lib/conversation-find";
import {
  clearChangesMarks,
  findChangesHits,
  markChangesMatches,
  scrollRangeIntoView,
  selectedQuery,
  stepShortcut,
  type ChangesHit,
  type FindFile,
} from "@/lib/changes-find";

export interface ChangesFindOptions {
  /** The files' searchable lines, top to bottom. */
  files: MaybeRefOrGetter<readonly FindFile[]>;
  /** The Changes view: it takes focus back when the bar closes, and a selection in it fills the bar. */
  root: MaybeRefOrGetter<HTMLElement | null | undefined>;
  /** The scrolling list of files, which holds the drawn lines. */
  body: MaybeRefOrGetter<HTMLElement | null | undefined>;
  /** Draws the hit's line: opens its file, its fold, or the rest of a long diff. */
  reveal: (hit: ChangesHit) => void;
  /** Puts the cursor in the bar's field and selects what it holds. */
  focusField: () => void;
  /** Anything that redraws the lines (layout, folds, files opening): the marks are put back after it. */
  redraws?: MaybeRefOrGetter<unknown>;
}

function isApple(): boolean {
  return typeof navigator !== "undefined" && /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);
}

/** Find in Changes: the bar's state, the matches over the diff data, stepping, and the marks on the page. */
export function useChangesFind(options: ChangesFindOptions) {
  const isOpen = ref(false);
  const query = ref("");
  const current = ref(0);

  const hits = computed(() => (isOpen.value ? findChangesHits(toValue(options.files), query.value) : []));
  const currentHit = computed(() => hits.value[current.value] ?? null);

  function open(): void {
    const selected = selectedQuery(toValue(options.root) ?? null);
    if (selected) query.value = selected;
    isOpen.value = true;
    void nextTick(options.focusField);
  }

  function close(): void {
    if (!isOpen.value) return;
    isOpen.value = false;
    clearChangesMarks();
    toValue(options.root)?.focus({ preventScroll: true });
  }

  function marks(scrollToCurrent: boolean): void {
    const body = toValue(options.body);
    if (!isOpen.value || query.value.trim() === "" || !body) {
      clearChangesMarks();
      return;
    }
    const range = markChangesMatches(body, hits.value, query.value, currentHit.value);
    if (scrollToCurrent && range) scrollRangeIntoView(range, body);
  }

  async function jump(): Promise<void> {
    const hit = currentHit.value;
    if (!hit) {
      marks(false);
      return;
    }
    options.reveal(hit);
    await nextTick();
    // The line may have been drawn just now: wait a frame for it to be laid out.
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    marks(true);
  }

  function step(direction: 1 | -1): void {
    const total = hits.value.length;
    if (total === 0) return;
    current.value = (current.value + direction + total) % total;
    void jump();
  }

  watch(query, () => {
    current.value = 0;
  });
  watch(hits, (next) => {
    if (current.value >= next.length) current.value = 0;
    void nextTick(() => marks(false));
  });
  // A new query, or the first matches arriving as collapsed files load, goes to the first match.
  watch([query, () => hits.value.length > 0], () => void jump());
  watch(() => toValue(options.redraws), () => void nextTick(() => marks(false)), { flush: "post" });

  /**
   * Bound on the Changes view itself, so it runs before the app's shortcuts on the document: Esc there would stop the
   * agent, and an unhandled Ctrl/⌘F would open the browser's find (or the conversation's).
   */
  function onKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
    const target = event.target;
    const apple = isApple();
    if (isFindShortcut(event, apple)) {
      // Keeps the browser's own find out of Changes.
      event.preventDefault();
      if (isOpen.value && target instanceof HTMLInputElement) target.select();
      else open();
      return;
    }
    if (!isOpen.value) return;
    const direction = stepShortcut(event, apple);
    if (direction !== 0) {
      event.preventDefault();
      step(direction);
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      close();
    }
  }

  onBeforeUnmount(() => {
    if (isOpen.value) clearChangesMarks();
  });

  return { isOpen, query, current, hits, currentHit, open, close, step, onKeydown };
}
