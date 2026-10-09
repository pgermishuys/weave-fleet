import { computed, shallowRef, type ComputedRef } from "vue";

/**
 * One shape for "things many owners add to one list" (plugin panels, settings sections, and later commands,
 * keybindings, rails and canvas kinds).
 *
 * A contribution point is:
 *  - reactive: `items` and `get` are tracked, so a computed or template that reads them updates when anything
 *    contributes or is removed (the map is replaced on every change, never mutated in place);
 *  - ordered: by `order` (missing counts as 0), then `group`, then label, then the order contributed in;
 *  - owned: every contribution names an owner; `contribute` returns a disposer and `removeByOwner` removes the
 *    lot, so a plugin can leave without anyone tracking what it added;
 *  - explicit about duplicates: ids are unique within a point, and a later contribution with the same id
 *    REPLACES the earlier one (it keeps the earlier one's place in the list; in development a warning names
 *    both owners). Nothing is ever rejected or merged.
 */

export interface ContributionPointOptions<T, Id extends string> {
  /** Names the point in warnings. */
  name: string;
  /** The id that makes an item unique within this point. */
  idOf: (item: T) => Id;
  /** The label to sort by when `order` and `group` tie. Defaults to the item's `label`, if it has one. */
  labelOf?: (item: T) => string | undefined;
}

export interface ContributionPoint<T, Id extends string = string> {
  readonly name: string;
  /** Every item, in display order. */
  readonly items: ComputedRef<readonly T[]>;
  /** The item with this id, or undefined. */
  get: (id: Id) => T | undefined;
  /** Who contributed the item with this id, or undefined. */
  ownerOf: (id: Id) => string | undefined;
  /** Adds items for an owner. The returned function removes the ones that are still this call's. */
  contribute: (owner: string, items: readonly T[]) => () => void;
  /** Removes everything this owner contributed. */
  removeByOwner: (owner: string) => void;
  /** Removes everything. */
  clear: () => void;
}

interface Entry<T> {
  owner: string;
  item: T;
  /** Identifies the `contribute` call, so a disposer never removes a replacement made by someone else. */
  token: symbol;
}

interface Sortable {
  order?: number;
  group?: string;
  label?: string;
}

function compareText(left: string | undefined, right: string | undefined): number {
  return (left ?? "").localeCompare(right ?? "");
}

export function defineContributionPoint<T extends object, Id extends string = string>(
  options: ContributionPointOptions<T, Id>,
): ContributionPoint<T, Id> {
  const { name, idOf } = options;
  const labelOf = options.labelOf ?? ((item: T) => (item as Sortable).label);
  const entries = shallowRef<ReadonlyMap<Id, Entry<T>>>(new Map());

  const items = computed<readonly T[]>(() =>
    Array.from(entries.value.values())
      .map((entry) => entry.item)
      // Array.prototype.sort is stable, so equal items stay in the order they were contributed.
      .sort((left, right) => {
        const a = left as Sortable;
        const b = right as Sortable;
        return (a.order ?? 0) - (b.order ?? 0)
          || compareText(a.group, b.group)
          || compareText(labelOf(left), labelOf(right));
      }),
  );

  function contribute(owner: string, added: readonly T[]): () => void {
    if (added.length === 0) return () => {};
    const token = Symbol(`${name}:${owner}`);
    const next = new Map(entries.value);

    for (const item of added) {
      const id = idOf(item);
      const existing = next.get(id);
      if (existing && import.meta.env.DEV) {
        console.warn(`[contributions] "${name}" already has "${id}" from "${existing.owner}"; "${owner}" replaces it.`);
      }
      next.set(id, { owner, item, token });
    }

    entries.value = next;

    return () => {
      removeWhere((entry) => entry.token === token);
    };
  }

  function removeWhere(matches: (entry: Entry<T>) => boolean): void {
    const next = new Map(entries.value);
    for (const [id, entry] of next) {
      if (matches(entry)) next.delete(id);
    }
    // Only publish a change when there is one, so readers don't recompute for nothing.
    if (next.size !== entries.value.size) entries.value = next;
  }

  return {
    name,
    items,
    get: (id) => entries.value.get(id)?.item,
    ownerOf: (id) => entries.value.get(id)?.owner,
    contribute,
    removeByOwner: (owner) => removeWhere((entry) => entry.owner === owner),
    clear: () => {
      if (entries.value.size > 0) entries.value = new Map();
    },
  };
}
