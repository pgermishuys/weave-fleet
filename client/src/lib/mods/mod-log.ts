import { apiFetchOn } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import type { MachineConnection } from "@/lib/machines";
import type { ModLogLine } from "@/lib/mods/kept";
import { ModsRequestError } from "@/lib/mods/kept-api";

/** A mod's recent log lines, newest last; null on a 404 (the route isn't there, or the mod has no log). */
export async function fetchModLog(name: string, machine: MachineConnection | null = null): Promise<ModLogLine[] | null> {
  const response = await apiFetchOn(machine, `/api/mods/${encodeURIComponent(name)}/log`);
  if (response.status === 404) return null;
  if (!response.ok) {
    let message = `Couldn't read the log (${response.status}).`;
    try {
      message = extractApiError(await response.json(), message);
    } catch {
      // Not JSON: the fallback says enough.
    }
    throw new ModsRequestError(message, response.status);
  }
  return (await response.json()) as ModLogLine[];
}
