import { computed, type ComputedRef } from "vue";
import { defineContributionPoint, type ContributionPoint } from "@/lib/contributions";
import type { ModAction, ModAuthor, ModSurface, ModWireElement } from "@/lib/mods/types";

/**
 * Where mods' trees reach Fleet's screens: one contribution point per kind of site. A site with no contribution draws
 * exactly what it drew before mods. M6 contributes what arrives as `mod.ui`/`mod.pane`; until then, tests and mock
 * mode contribute fake ones.
 */

/** What every site's contribution carries. */
export interface ModTreeView {
  /** `null` with mods draws nothing (at `ToolResult`, an empty body). No mods means "no mod draws here", so remove it instead. */
  tree: ModWireElement | null;
  /** The mods that drew it, outermost first. */
  mods: readonly ModAuthor[];
  /** A control in the tree was used, on this surface. Nothing calls the server yet (M6). */
  onAction?: (action: ModAction, surface: ModSurface) => void;
}

/** A tool row's line (`ToolUse`) or opened body (`ToolResult`), for one call. */
export interface ToolRowView extends ModTreeView {
  site: "ToolUse" | "ToolResult";
  /** The session the call is in. A row finds its view by this, the call id and the site, not by the tool. */
  sessionId: string;
  callId: string;
  /** The canonical name from Fleet's tool registry (`getTool(kind).name`). Data for the mod; a row doesn't match on it. */
  tool: string;
}

export function toolRowViewId(site: ToolRowView["site"], sessionId: string, callId: string): string {
  return `${site}:${sessionId}:${callId}`;
}

export const toolRowViews = defineContributionPoint<ToolRowView>({
  name: "mod tool row views",
  idOf: (view) => toolRowViewId(view.site, view.sessionId, view.callId),
});

/** The band above a session's composer. */
export interface ComposerBandView extends ModTreeView {
  sessionId: string;
}

export const composerBands = defineContributionPoint<ComposerBandView>({
  name: "mod composer bands",
  idOf: (view) => view.sessionId,
});

/** A chip at the end of the status bar, shown only while its session is on screen. */
export interface StatusChipView extends ModTreeView {
  sessionId: string;
}

export const statusChips = defineContributionPoint<StatusChipView>({
  name: "mod status chips",
  idOf: (view) => view.sessionId,
});

/** A pane a mod opened in a session: a `mod` canvas on desktop, a sheet from the session menu on the phone. */
export interface ModPaneView extends ModTreeView {
  sessionId: string;
  paneId: string;
  title: string;
  /** The mod that opened it. */
  mod: string;
}

/** A pane id belongs to its mod: two mods may use the same one. */
export function modPaneViewId(sessionId: string, mod: string, paneId: string): string {
  return `${sessionId}:${mod}:${paneId}`;
}

/** Who contributes a pane's view: one owner per pane, so closing or redrawing it removes just that one. */
export function modPaneOwner(sessionId: string, mod: string, paneId: string): string {
  return `mod-pane:${modPaneViewId(sessionId, mod, paneId)}`;
}

export const modPanes = defineContributionPoint<ModPaneView>({
  name: "mod panes",
  idOf: (view) => modPaneViewId(view.sessionId, view.mod, view.paneId),
  labelOf: (view) => view.title,
});

/** A tree from a draft is marked Draft wherever it shows (band, chip, pane tab). */
export function isDraftView(view: Pick<ModTreeView, "mods">): boolean {
  return view.mods.some((mod) => mod.draft);
}

/**
 * One id's view, tracked on its own. A point's `get` reads the whole map, which is replaced on every change, so a
 * plain read in a row redraws every row. This computed re-runs on any change but only notifies its reader when this
 * id's view is a different object, so contributing a view for one call redraws that call's row alone.
 */
export function useModView<T extends object>(
  point: ContributionPoint<T>,
  id: () => string | undefined,
): ComputedRef<T | undefined> {
  return computed(() => {
    const key = id();
    return key === undefined ? undefined : point.get(key);
  });
}
