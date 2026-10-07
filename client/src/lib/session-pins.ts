/**
 * Pinned sessions: they sit in a Pinned group above the projects, in the order the user dragged them to (`pinOrder`,
 * ascending). Everything else stays newest first in its project. Everything that came from a pinned session (forks,
 * sessions its agent started) goes with it, nested under it as in a project. Archiving a session unpins it.
 */
import { lineageOf, type LineageFields } from "@/lib/session-lineage";

type PinnableItem = LineageFields & { session: { id: string }; pinOrder?: number | null };

export function isPinned(item: { pinOrder?: number | null }): boolean {
  return item.pinOrder !== null && item.pinOrder !== undefined;
}

/** The pinned sessions among `items`, in their pinned order. */
export function pinnedInOrder<T extends { pinOrder?: number | null }>(items: readonly T[]): T[] {
  return items.filter(isPinned).sort((left, right) => left.pinOrder! - right.pinOrder!);
}

/**
 * Splits the list into the Pinned group and the rest. The Pinned group has each pinned session, in its pinned order,
 * then everything that came from one (in the list's order, to nest under it). A pinned session that came from a session
 * that isn't pinned stands on its own there.
 */
export function splitPinned<T extends PinnableItem>(items: readonly T[]): { pinned: T[]; rest: T[] } {
  const byId = new Map(items.map((item) => [item.session.id, item]));

  function inPinned(item: T): boolean {
    const seen = new Set<string>();
    for (let current: T | undefined = item; current && !seen.has(current.session.id);) {
      if (isPinned(current)) return true;
      seen.add(current.session.id);
      const parentId: string | undefined = lineageOf(current)?.parentId;
      current = parentId ? byId.get(parentId) : undefined;
    }
    return false;
  }

  const pinned: T[] = [];
  const rest: T[] = [];
  for (const item of items) (inPinned(item) ? pinned : rest).push(item);
  return { pinned: [...pinnedInOrder(pinned), ...pinned.filter((item) => !isPinned(item))], rest };
}

/**
 * The order a session gets when it's pinned just before `beforeId`, or at the end when that's null or isn't pinned:
 * what the server answers too, so the list can move at once.
 */
export function pinOrderFor(items: readonly PinnableItem[], sessionId: string, beforeId: string | null): number {
  const others = pinnedInOrder(items.filter((item) => item.session.id !== sessionId));
  const at = beforeId === null ? -1 : others.findIndex((item) => item.session.id === beforeId);
  if (at < 0) return (others.at(-1)?.pinOrder ?? 0) + 1;
  const after = at === 0 ? others[0]!.pinOrder! - 1 : others[at - 1]!.pinOrder!;
  return (after + others[at]!.pinOrder!) / 2;
}

/**
 * Where a pinned session goes when it moves one place up (-1) or down (+1): the pinned session it then sits before,
 * or null for the end. Undefined when it can't move that way.
 */
export function pinnedNeighbourFor(
  items: readonly PinnableItem[],
  sessionId: string,
  delta: -1 | 1,
): string | null | undefined {
  const ids = pinnedInOrder(items).map((item) => item.session.id);
  const index = ids.indexOf(sessionId);
  const target = index + delta;
  if (index < 0 || target < 0 || target >= ids.length) return undefined;
  // Moving up sits before the one above; moving down sits before the one after the next (or at the end).
  return delta === -1 ? ids[target]! : ids[target + 1] ?? null;
}
