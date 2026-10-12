import { apiFetchOn } from "@/lib/api-client";
import type { MachineTarget } from "@/lib/machine-target";
import type { ModAction, ModSurface } from "@/lib/mods/types";

/**
 * Sends a used control to the mod that drew it (`POST /api/sessions/{id}/mods/action`). Every site's view takes the
 * function this makes as its `onAction`, so a Button, Input or Select works wherever a mod draws one. The mod answers
 * by invalidating (a `mod.ui` the site is already listening to). When the server can't deliver the action (the handle
 * is expired because the tree was drawn again or the mod reloaded, the host isn't running, or the request failed),
 * `redraw` runs so the site shows the current tree instead of a dead control.
 */
export function modActionSender(
  machine: MachineTarget,
  sessionId: string,
  redraw: () => void,
): (action: ModAction, surface: ModSurface) => void {
  return (action, surface) => {
    void (async () => {
      let handled = false;
      try {
        const response = await apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(sessionId)}/mods/action`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ handle: action.handle, kind: action.kind, value: action.value, surface }),
        });
        handled = response.ok && ((await response.json()) as { handled?: boolean }).handled === true;
      } catch {
        // Handled below: draw again.
      }
      if (!handled) redraw();
    })();
  };
}
