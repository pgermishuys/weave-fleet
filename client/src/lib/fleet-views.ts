/** The views of Fleet (mission control), as the fleet-menu lists them. Each has its own route, so it can be linked to. */
export const NEEDS_YOU_VIEW = "needs-you";

export type FleetView = { kind: "needs-you" } | { kind: "machine"; machineId: string };

/** The route of a view; Needs you is the bare /fleet. */
export function fleetViewPath(view: FleetView): string {
  return view.kind === "needs-you" ? "/fleet" : `/fleet/machines/${encodeURIComponent(view.machineId)}`;
}

/** The view an address shows; anything that isn't a machine view (including a stale link) shows Needs you. */
export function fleetViewFromPath(pathname: string): FleetView {
  const [, , kind, machineId] = pathname.replace(/\/+$/, "").split("/");
  if (kind !== "machines" || !machineId) return { kind: "needs-you" };
  try {
    return { kind: "machine", machineId: decodeURIComponent(machineId) };
  } catch {
    return { kind: "needs-you" };
  }
}

export function isSameFleetView(a: FleetView, b: FleetView): boolean {
  return a.kind === b.kind && (a.kind === "needs-you" || (b.kind === "machine" && a.machineId === b.machineId));
}
