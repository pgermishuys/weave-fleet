import { apiFetchOn } from "@/lib/api-client";
import type { AttachedFile } from "@/lib/attached-files";
import type { MachineTarget } from "@/lib/machine-target";

/**
 * Save a file in the session's folder, for the agent to read.
 *
 * @throws Error with the server's reason when it refuses the file (too big, archived session)
 */
export async function uploadSessionAttachment(
  machine: MachineTarget,
  sessionId: string,
  file: File,
): Promise<AttachedFile> {
  const response = await apiFetchOn(
    machine.connection,
    `/api/sessions/${encodeURIComponent(sessionId)}/attachments?name=${encodeURIComponent(file.name)}`,
    { method: "POST", headers: { "Content-Type": file.type || "application/octet-stream" }, body: file },
  );

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { error?: unknown } | null;
    throw new Error(typeof body?.error === "string" && body.error ? body.error : `Failed to upload ${file.name}: HTTP ${response.status}`);
  }

  return (await response.json()) as AttachedFile;
}

/** Delete every file attached to the session's prompts. */
export async function clearSessionAttachments(machine: MachineTarget, sessionId: string): Promise<void> {
  const response = await apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(sessionId)}/attachments`, { method: "DELETE" });
  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { error?: unknown } | null;
    throw new Error(typeof body?.error === "string" && body.error ? body.error : `Failed to clear attachments: HTTP ${response.status}`);
  }
}
