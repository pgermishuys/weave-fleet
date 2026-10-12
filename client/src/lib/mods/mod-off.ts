import { declaredModPanes } from "@/lib/mods/renders";
import { useModsStore } from "@/stores/mods";

/**
 * Whether a mod is turned off, for the pane it draws. The server lists a turned-off mod's declared panes with `off`; a
 * pane the mod opened without declaring is judged by the kept mods (Settings → Mods), once those are loaded.
 */
export function isModOff(sessionId: string, mod: string): boolean {
  const declared = declaredModPanes(sessionId).filter((pane) => pane.mod === mod);
  if (declared.length > 0) return declared.some((pane) => pane.off);
  return useModsStore().kept?.mods.find((kept) => kept.name === mod)?.off != null;
}

/** The same per-mod switch as Settings → Mods (a draft's own switch for the session's draft). The server then says `mods.changed`. */
export async function turnModOn(sessionId: string, mod: string): Promise<void> {
  const store = useModsStore();
  const draft = declaredModPanes(sessionId).some((pane) => pane.mod === mod && pane.draft);
  if (draft) await store.setDraftOn(sessionId, mod, true);
  else await store.setOn(mod, true);
}
