import { computed, toValue, watch, type ComputedRef, type MaybeRefOrGetter } from "vue";
import {
  caretOutsideReferences,
  forgetFileReferencesNotIn,
  pickedFileTokens,
  pickedReferenceRanges,
  referenceKeyAction,
  splitDraftReferences,
  type DraftSegment,
  type ReferenceRange,
} from "@/lib/composer-references";
import { rememberedSessionTokens } from "@/lib/session-references";
import type { SkillDelivery } from "@/lib/skill-mentions";

export interface UseDraftReferencesParams {
  text: MaybeRefOrGetter<string>;
  caret: MaybeRefOrGetter<number | null>;
  /** Whose picks these are: the session the composer sends to, or the New Session draft's row key. */
  draftKey: MaybeRefOrGetter<string>;
  /** The skills the draft names, and how each reaches the agent. */
  skills?: MaybeRefOrGetter<ReadonlyMap<string, SkillDelivery>>;
  /** The draft is plain text, with no references in it (a shell command). */
  plain?: MaybeRefOrGetter<boolean>;
  /** Tokens drawn as plain text unless picked again: picks the New Session page's folder no longer has. */
  plainTokens?: MaybeRefOrGetter<ReadonlySet<string>>;
}

export interface UseDraftReferencesResult {
  /** The draft split for `ReferenceMirror` to tint. */
  segments: ComputedRef<DraftSegment[]>;
  /** Where the references picked from the `@` list are, which edit as one piece. */
  pickedRanges: ComputedRef<ReferenceRange[]>;
  /** Backspace, Delete and the left and right arrows next to a picked reference; true when the key was about one. */
  handleReferenceKey: (event: KeyboardEvent) => boolean;
  /** A caret left inside a picked reference (by a click, say) moves to its nearer edge. */
  keepCaretOutside: (textarea: HTMLTextAreaElement) => void;
}

/**
 * A composer draft's references: how `ReferenceMirror` tints them, and the keys that edit a reference picked from the
 * `@` list as one piece. Files and folders picked and then deleted from the draft are forgotten.
 */
export function useDraftReferences({ text, caret, draftKey, skills, plain, plainTokens }: UseDraftReferencesParams): UseDraftReferencesResult {
  const segments = computed(() => toValue(plain)
    ? [{ text: toValue(text) }]
    : splitDraftReferences(
      toValue(text),
      toValue(caret),
      rememberedSessionTokens(toValue(draftKey)),
      pickedFileTokens(toValue(draftKey)),
      toValue(skills),
      toValue(plainTokens),
    ));
  const pickedRanges = computed(() => toValue(plain)
    ? []
    : pickedReferenceRanges(toValue(text), rememberedSessionTokens(toValue(draftKey)), pickedFileTokens(toValue(draftKey))));
  watch(() => toValue(text), (draft) => {
    forgetFileReferencesNotIn(toValue(draftKey), draft);
  }, { immediate: true });

  /**
   * Backspace and Delete next to a reference picked from the @ list select all of it and let the browser delete the
   * selection, so it goes in one piece and Undo brings it back; the left and right arrows step over it.
   */
  function handleReferenceKey(event: KeyboardEvent): boolean {
    const isDelete = event.key === "Backspace" || event.key === "Delete";
    // Shift+arrows select, and Cmd+Backspace deletes the whole line: those stay the browser's.
    if (event.isComposing || event.metaKey || (!isDelete && (event.shiftKey || event.altKey || event.ctrlKey))) {
      return false;
    }

    const target = event.target as HTMLTextAreaElement;
    if (target.selectionStart !== target.selectionEnd) {
      return false;
    }

    const action = referenceKeyAction(event.key, target.selectionStart, pickedRanges.value);
    if (!action) {
      return false;
    }

    if (action.preventDefault) {
      event.preventDefault();
    }
    target.setSelectionRange(action.selectionStart, action.selectionEnd);
    return true;
  }

  function keepCaretOutside(textarea: HTMLTextAreaElement): void {
    // A click (or a word jump) never leaves the caret inside a picked reference.
    if (textarea.selectionStart !== textarea.selectionEnd) {
      return;
    }
    const moved = caretOutsideReferences(textarea.selectionStart, pickedRanges.value);
    if (moved !== textarea.selectionStart) {
      textarea.setSelectionRange(moved, moved);
    }
  }

  return { segments, pickedRanges, handleReferenceKey, keepCaretOutside };
}
