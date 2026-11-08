import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";

/**
 * Rows picked in the session list for a bulk action. ⌘/Ctrl-click toggles one row and Shift-click
 * takes the range from the last picked row, the way file lists work.
 *
 * The rows picked are all on one machine, so a bulk action never spans machines: each call names the machine's tree
 * the row is in (`null`, the default, for the live machine's; another machine's key for its group), and picking a row
 * on another machine starts again from it.
 */
export const useSessionSelectionStore = defineStore("session-selection", () => {
  const selectedIds = shallowRef<ReadonlySet<string>>(new Set());
  const anchorId = shallowRef<string | null>(null);
  /** The machine the picked rows are on: null for the live machine's tree. */
  const machineKey = shallowRef<string | null>(null);
  /** The ids each machine's tree shows, top to bottom; a Shift-click range is taken from it. */
  const visibleOrders = shallowRef<ReadonlyMap<string | null, readonly string[]>>(new Map());

  const count = computed(() => selectedIds.value.size);
  const isSelecting = computed(() => selectedIds.value.size > 0);

  function isSelected(sessionId: string): boolean {
    return selectedIds.value.has(sessionId);
  }

  /** Whether rows are being picked in `machine`'s tree. */
  function isSelectingOn(machine: string | null = null): boolean {
    return isSelecting.value && machineKey.value === machine;
  }

  /** Rows picked on another machine are let go when one on `machine` is picked. */
  function selectionOn(machine: string | null): Set<string> {
    if (machineKey.value === machine) return new Set(selectedIds.value);
    machineKey.value = machine;
    anchorId.value = null;
    return new Set();
  }

  function toggle(sessionId: string, machine: string | null = null): void {
    const next = selectionOn(machine);
    if (next.has(sessionId)) {
      next.delete(sessionId);
    } else {
      next.add(sessionId);
    }

    selectedIds.value = next;
    anchorId.value = sessionId;
  }

  /** Adds every row between the anchor (or `fallbackAnchorId`, usually the open session) and this one. */
  function extendTo(sessionId: string, fallbackAnchorId: string | null, machine: string | null = null): void {
    if (machineKey.value !== machine) {
      toggle(sessionId, machine);
      return;
    }

    const order = visibleOrders.value.get(machine) ?? [];
    const from = order.indexOf(anchorId.value ?? fallbackAnchorId ?? sessionId);
    const to = order.indexOf(sessionId);
    if (from < 0 || to < 0) {
      toggle(sessionId, machine);
      return;
    }

    const next = new Set(selectedIds.value);
    for (const id of order.slice(Math.min(from, to), Math.max(from, to) + 1)) {
      next.add(id);
    }

    selectedIds.value = next;
    anchorId.value = sessionId;
  }

  function setVisibleOrder(order: readonly string[], machine: string | null = null): void {
    visibleOrders.value = new Map(visibleOrders.value).set(machine, order);
    // A row that leaves the list (archived, filtered out) leaves the selection too.
    if (selectedIds.value.size > 0 && machineKey.value === machine) {
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
    machineKey.value = null;
  }

  return {
    selectedIds,
    machineKey,
    count,
    isSelecting,
    isSelected,
    isSelectingOn,
    toggle,
    extendTo,
    setVisibleOrder,
    clear,
  };
});
