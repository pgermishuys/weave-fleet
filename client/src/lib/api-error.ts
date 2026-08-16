/**
 * Extracts a human-readable message from an openapi-fetch error response.
 * The error is typically a ProblemDetails object with `title` and/or `detail`.
 */
export function extractApiError(error: unknown, fallback: string): string {
  if (!error) return fallback;
  if (typeof error === "string") return error;
  if (typeof error === "object" && error !== null) {
    const obj = error as Record<string, unknown>;
    if (typeof obj.detail === "string") return obj.detail;
    if (typeof obj.title === "string") return obj.title;
    if (typeof obj.message === "string") return obj.message;
  }
  return fallback;
}
