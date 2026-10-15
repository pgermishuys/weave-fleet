/**
 * Whether the file editor wraps long lines. On for every file unless you turned it off for that
 * file; only those choices are kept, the most recent few hundred.
 */

/** Long lines wrap unless a file says otherwise. Flip this to make sideways scrolling the default. */
export const WRAP_BY_DEFAULT = true;

const STORAGE_KEY = "fleet.editor.wrap";
const MAX_FILES = 200;

function read(): Record<string, boolean> {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}");
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as Record<string, boolean>) : {};
  } catch {
    return {};
  }
}

export function wrapFor(path: string): boolean {
  const choice = read()[path];
  return typeof choice === "boolean" ? choice : WRAP_BY_DEFAULT;
}

export function setWrapFor(path: string, wrap: boolean): void {
  const choices = read();
  delete choices[path];
  // Insertion order is age: the newest choice goes last, the oldest drop off the front.
  if (wrap !== WRAP_BY_DEFAULT) choices[path] = wrap;
  const paths = Object.keys(choices);
  for (const old of paths.slice(0, Math.max(0, paths.length - MAX_FILES))) delete choices[old];
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(choices));
  } catch {
    // Storage full or off: the choice lasts until the tab closes.
  }
}
