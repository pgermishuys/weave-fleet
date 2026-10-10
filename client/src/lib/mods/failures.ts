import type { ModAuthor, ModRenderSite } from "@/lib/mods/types";

/** A tree Fleet refused to draw. M6 counts these against the mods that made it. */
export interface ModTreeFailure {
  site: ModRenderSite;
  reason: string;
  mods: readonly ModAuthor[];
}

type Listener = (failure: ModTreeFailure) => void;

const listeners = new Set<Listener>();

/** Calls `listener` for each invalid tree. The returned function stops it. */
export function onModTreeInvalid(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function reportModTreeInvalid(failure: ModTreeFailure): void {
  if (import.meta.env.DEV) {
    const names = failure.mods.map((mod) => mod.name).join(", ");
    console.warn(`[mods] ${failure.site} tree from ${names} is invalid: ${failure.reason}`);
  }
  for (const listener of [...listeners]) {
    try {
      listener(failure);
    } catch (error) {
      if (import.meta.env.DEV) console.warn("[mods] a failure listener threw", error);
    }
  }
}
