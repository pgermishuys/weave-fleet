import { onBeforeUnmount, toValue, watch, type MaybeRefOrGetter } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { useMachineTarget } from "@/lib/machine-target";
import { closeModPane } from "@/lib/mods/panes";
import { drawModPane, openAndDrawModPane } from "@/lib/mods/pane-renders";
import { useCanvasesStore } from "@/stores/canvases";

/**
 * Keeps the open session's mod panes in step with its mods. `mod.pane` opens a tab (and the right panel, if it is
 * closed) or closes it. An open pane draws again when its mod redraws (`mod.ui`), and when mods are loaded or changed
 * (`mods.changed`, or `mod.ui` with no mod): a draw that landed while the host was reloading came back empty, and
 * nothing else would ask again. It lives with the session's page, not the right panel, which is not always mounted.
 */
export function useSessionModPanes(sessionId: MaybeRefOrGetter<string | null | undefined>): void {
  const machine = useMachineTarget();
  const canvases = useCanvasesStore();
  const { showRightPanel } = useSidebarMobile();
  let stop: Array<() => void> = [];

  /** Draw the session's open panes again; only the named mod's when one is. */
  function redraw(id: string, mod?: string | null): void {
    for (const canvas of canvases.sessionCanvases(id).canvases) {
      const pane = canvas.modPane;
      if (pane && (!mod || pane.mod === mod)) void drawModPane(machine, id, pane.mod, pane.paneId, pane.title);
    }
  }

  function listen(id: string): void {
    const topic = `session:${id}`;
    stop = [
      onDomainEvent(machine, topic, "mod.pane", ({ payload }) => {
        if (payload.open) {
          openAndDrawModPane(machine, id, payload.mod, payload.id, payload.title ?? payload.id);
          showRightPanel();
        } else {
          closeModPane(id, payload.mod, payload.id);
        }
      }),
      onDomainEvent(machine, topic, "mod.ui", ({ payload }) => redraw(id, payload.mod)),
      onDomainEvent(machine, "sessions", "mod.ui", ({ payload }) => {
        if (!payload.sessionId || payload.sessionId === id) redraw(id, payload.mod);
      }),
      onDomainEvent(machine, "sessions", "mods.changed", () => redraw(id)),
    ];
  }

  watch(() => toValue(sessionId), (id) => {
    for (const off of stop) off();
    stop = [];
    if (id) listen(id);
  }, { immediate: true });

  onBeforeUnmount(() => {
    for (const off of stop) off();
    stop = [];
  });
}
