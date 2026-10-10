import { computed, type ComputedRef } from "vue";
import type { ContributionPoint } from "@/lib/contributions";
import { reportModTreeInvalid } from "@/lib/mods/failures";
import { isDraftView, useModView, type ModTreeView } from "@/lib/mods/points";
import type { ModRenderSite, ModWireElement } from "@/lib/mods/types";
import { validateModTree, type ModTreeCheck } from "@/lib/mods/validate";

/** What a site asks: draw this, or draw Fleet's own. */
export type ResolvedModView =
  | Readonly<{ draws: false }>
  | { draws: true; tree: ModWireElement | null; draft: boolean; view: ModTreeView };

/** The one "no mod draws here" answer, so a reader that gets it twice sees no change. */
export const NOTHING_DRAWN: ResolvedModView = Object.freeze({ draws: false as const });

const checks = new WeakMap<object, Map<ModRenderSite, ModTreeCheck>>();

function check(view: ModTreeView, site: ModRenderSite): ModTreeCheck {
  const tree = view.tree;
  // Only a tree object can be a key. `null` is valid and cheap; anything else invalid is checked each time.
  if (typeof tree !== "object" || tree === null) return validateModTree(tree, site);
  let bySite = checks.get(tree);
  const known = bySite?.get(site);
  if (known) return known;
  const result = validateModTree(tree, site);
  if (!bySite) checks.set(tree, (bySite = new Map()));
  bySite.set(site, result);
  if (!result.ok) reportModTreeInvalid({ site, reason: result.reason, mods: view.mods });
  return result;
}

/** Checks a site's view once per tree. An invalid tree is reported and draws as if no mod had contributed. */
export function resolveModView(view: ModTreeView | undefined, site: ModRenderSite): ResolvedModView {
  if (!view || view.mods.length === 0) return NOTHING_DRAWN;
  const result = check(view, site);
  if (!result.ok) return NOTHING_DRAWN;
  return { draws: true, tree: result.tree, draft: isDraftView(view), view };
}

/** A site's resolved view for one id, tracked per id (see `useModView`). Call it where a component sets up. */
export function useResolvedModView<T extends ModTreeView>(
  point: ContributionPoint<T>,
  id: () => string | undefined,
  site: ModRenderSite,
): ComputedRef<ResolvedModView> {
  const view = useModView(point, id);
  return computed(() => resolveModView(view.value, site));
}

/**
 * For a list that reads many ids in one render (a run of phone tool rows): the resolved view per id, each tracked on
 * its own. Computeds are kept per id, so reading one in a render subscribes to that id alone.
 */
export function resolvedViewsById<T extends ModTreeView>(
  point: ContributionPoint<T>,
  site: ModRenderSite,
): (id: string | undefined) => ResolvedModView {
  const known = new Map<string, ComputedRef<ResolvedModView>>();
  return (id) => {
    if (id === undefined) return NOTHING_DRAWN;
    let entry = known.get(id);
    if (!entry) known.set(id, (entry = useResolvedModView(point, () => id, site)));
    return entry.value;
  };
}
