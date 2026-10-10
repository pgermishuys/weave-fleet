/**
 * The address of a page a mod ships, built in one place from the mod that owns it, the session, the path and the query.
 * M6 serves it at `/api/sessions/{sessionId}/mods/{modId}/pages/{path}` (with a `Content-Security-Policy: sandbox` header);
 * until then the frame shows whatever the server answers.
 *
 * The path comes from a mod, so it is treated as hostile: each segment is encoded, and anything that could climb out of
 * the mod's folder, name a scheme or leave the machine's API origin is refused (null).
 */

export interface ModPageUrlInput {
  /** How the client makes an address on the machine's API, as `apiUrl`/`apiUrlOn` do. */
  apiUrl: (path: string) => string;
  sessionId: string;
  /** The id of the mod that made the page (`name@v3`, `name@draft:{session}`), from the wire `Page`. */
  mod: string;
  /** The page's path within the mod, like `ui/report.html`. */
  path: string;
  query?: Readonly<Record<string, unknown>>;
}

/**
 * A mod's id on the wire: its name, then `@v` and a version for a kept mod or `@draft:` and the session for a draft.
 * The contract leaves the exact shape to the host; this is the one the client accepts.
 */
export const MOD_ID_PATTERN = /^[a-z][a-z0-9-]{0,63}@(?:v[1-9][0-9]*|draft:[A-Za-z0-9_-]{1,128})$/;
const CONTROL = /[\u0000-\u001f\u007f]/;

function pathSegments(path: string): string[] | null {
  if (!path.endsWith(".html") || CONTROL.test(path) || path.includes("\\") || path.includes(":")) return null;
  const segments = path.split("/");
  // A leading or trailing slash and `a//b` leave an empty segment.
  return segments.every((s) => s !== "" && s !== "." && s !== "..") ? segments : null;
}

/** The page's address, or null when the mod, session, path or query can't make a safe one. */
export function modPageUrl({ apiUrl, sessionId, mod, path, query }: ModPageUrlInput): string | null {
  if (!sessionId || !MOD_ID_PATTERN.test(mod)) return null;
  const segments = pathSegments(path);
  if (!segments) return null;

  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(query ?? {})) {
    if (typeof value === "string") params.append(name, value);
  }
  const search = params.toString();

  try {
    const address = apiUrl(
      `/api/sessions/${encodeURIComponent(sessionId)}/mods/${encodeURIComponent(mod)}/pages/${segments.map(encodeURIComponent).join("/")}${search ? `?${search}` : ""}`,
    );
    const origin = new URL(apiUrl("/"), location.href).origin;
    return new URL(address, location.href).origin === origin ? address : null;
  } catch {
    return null;
  }
}
