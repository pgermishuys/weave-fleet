import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";

/**
 * Rows picked in the session list for a bulk action. ⌘/Ctrl-click toggles one row and Shift-click
 * takes the range from the last picked row, the way file lists work.
 */
export const useSessionSelectionStore = defineStore("session-selection", () => {
  const selectedIds = shallowRef<ReadonlySet<string>>(new Set());
  const anchorId = shallowRef<string | null>(null);
  /** The ids the list shows, top to bottom; a Shift-click range is taken from it. */
  const visibleOrder = shallowRef<readonly string[]>([]);

  const count = computed(() => selectedIds.value.size);
  const isSelecting = computed(() => selectedIds.value.size > 0);

  function isSelected(sessionId: string): boolean {
    return selectedIds.value.has(sessionId);
  }

  function toggle(sessionId: string): void {
    const next = new Set(selectedIds.value);
    if (next.has(sessionId)) {
      next.delete(sessionId);
    } else {
      next.add(sessionId);
    }

    selectedIds.value = next;
    anchorId.value = sessionId;
  }

  /** Adds every row between the anchor (or `fallbackAnchorId`, usually the open session) and this one. */
  function extendTo(sessionId: string, fallbackAnchorId: string | null): void {
    const order = visibleOrder.value;
    const from = order.indexOf(anchorId.value ?? fallbackAnchorId ?? sessionId);
    const to = order.indexOf(sessionId);
    if (from < 0 || to < 0) {
      toggle(sessionId);
      return;
    }

    const next = new Set(selectedIds.value);
    for (const id of order.slice(Math.min(from, to), Math.max(from, to) + 1)) {
      next.add(id);
    }

    selectedIds.value = next;
    anchorId.value = sessionId;
  }

  function setVisibleOrder(order: readonly string[]): void {
    visibleOrder.value = order;
    // A row that leaves the list (archived, filtered out) leaves the selection too.
    if (selectedIds.value.size > 0) {
      const visible = new Set(order);
      const kept = [...selectedIds.value].filter((id) => visible.has(id));
      if (kept.length !== selectedIds.value.size) {
        selectedIds.value = new Set(kept);
      }
    }
  }

  function clear(): void {
    selectedIds.value = new Set();
    anchorId.value = null;
  }

  return {
    selectedIds,
    count,
    isSelecting,
    isSelected,
    toggle,
    extendTo,
    setVisibleOrder,
    clear,
  };
});
