import { readonly, shallowRef } from "vue";
import { api } from "@/api/client";

/** The reason in an error body: Fleet's `{ error }`, or a problem's `detail` or `title`. */
function errorMessage(body: unknown): string | undefined {
  if (typeof body === "string" && body.trim().length > 0) return body.trim();
  if (!body || typeof body !== "object") return undefined;

  const record = body as Record<string, unknown>;
  for (const key of ["error", "detail", "title"]) {
    const value = record[key];
    if (typeof value === "string" && value.trim().length > 0) return value;
  }
  return undefined;
}

/**
 * Runs a shell command the user typed (`!git status`) in the session's folder. The command and its output reach the
 * conversation as events, so this only reports a command the server or the harness refused.
 */
export function useRunShellCommand(sessionId: string) {
  const error = shallowRef<string | undefined>(undefined);

  async function runShellCommand(command: string): Promise<boolean> {
    const trimmed = command.trim();
    if (!trimmed) return false;

    error.value = undefined;
    try {
      const { error: body, response } = await api.POST("/api/sessions/{id}/shell", {
        params: { path: { id: sessionId } },
        body: { command: trimmed },
      });
      if (!response.ok) {
        error.value = errorMessage(body) ?? `The command couldn't be run (HTTP ${response.status}).`;
        return false;
      }
      return true;
    } catch (caughtError) {
      error.value = caughtError instanceof Error ? caughtError.message : "The command couldn't be run.";
      return false;
    }
  }

  function clearError(): void {
    error.value = undefined;
  }

  return {
    error: readonly(error),
    runShellCommand,
    clearError,
  };
}
