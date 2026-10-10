import type { InjectionKey, VNodeChild } from "vue";
import type { ModAction, ModRenderSite } from "@/lib/mods/types";

/** What `ModTree` hands every element it draws. */
export interface ModTreeContext {
  site: ModRenderSite;
  /** The session the tree is drawn for, which a `Page` is served under. */
  sessionId?: string;
  /** A control was used. */
  send: (action: ModAction) => void;
  /** Fleet's own drawing of the site, for a `Fleet` node. */
  drawFleet: () => VNodeChild;
}

export const MOD_TREE: InjectionKey<ModTreeContext> = Symbol("mod-tree");
