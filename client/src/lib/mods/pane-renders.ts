import { apiFetchOn } from "@/lib/api-client";
import type { MachineTarget } from "@/lib/machine-target";
import { modActionSender } from "@/lib/mods/actions";
import { openModPane, updateModPane } from "@/lib/mods/panes";
import type { ModAuthor, ModWireElement } from "@/lib/mods/types";

/**
 * Draws a mod's pane: asks the server for site `Pane` (`requestId` is the pane id, `mod` names the owner, props are
 * `{ title }`) and contributes the tree to `modPanes`, which the pane's tab draws. A control in the tree goes to the
 * mod through `modActionSender`; the mod answers by invalidating, which draws the pane again.
 */

interface RenderResponse {
  tree: ModWireElement | null;
  mods: ModAuthor[];
}

/** The newest ask per pane wins, so a slow answer can't replace a newer one. */
const newest = new Map<string, number>();

/** Asks the mod to draw the pane and updates the open tab. A pane whose tab is closed is left alone. */
export async function drawModPane(machine: MachineTarget, sessionId: string, mod: string, paneId: string, title: string): Promise<void> {
  const key = `${sessionId}:${mod}:${paneId}`;
  const mine = (newest.get(key) ?? 0) + 1;
  newest.set(key, mine);
  let response: RenderResponse | null = null;
  try {
    const answer = await apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(sessionId)}/mods/render`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ site: "Pane", requestId: paneId, mod, props: { title } }),
    });
    response = answer.ok ? ((await answer.json()) as RenderResponse) : null;
  } catch {
    response = null;
  }
  if (newest.get(key) !== mine || !response) return;
  updateModPane({
    sessionId, paneId, title, mod,
    tree: response.tree,
    mods: response.mods.length > 0 ? response.mods : [{ name: mod, draft: false }],
    onAction: modActionSender(machine, sessionId, () => void drawModPane(machine, sessionId, mod, paneId, title)),
  });
}

/** Opens (or brings forward) the pane's tab, then draws it. */
export function openAndDrawModPane(machine: MachineTarget, sessionId: string, mod: string, paneId: string, title: string): void {
  openModPane({ sessionId, paneId, title, mod, tree: null, mods: [{ name: mod, draft: false }] });
  void drawModPane(machine, sessionId, mod, paneId, title);
}
