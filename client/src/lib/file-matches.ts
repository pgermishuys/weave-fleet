/**
 * The `@` list's files and folders, narrowed in the browser: while the server's answer for what was just typed is on
 * its way, the last answer is cut down to what still matches, so the list follows each key straight away. Folders end
 * in "/". The order is the server's (`WorkspaceFileSearch`): an empty query or one ending in "/" lists that folder,
 * folders first; anything else ranks by name, then path, then fuzzy matches, then shallower paths.
 */
export function narrowFileMatches(entries: readonly string[], query: string): string[] {
  const normalized = query.trim().replace(/\\/g, "/").replace(/^\/+/, "");

  if (normalized === "" || normalized.endsWith("/")) {
    const folder = normalized.toLowerCase();
    return entries
      .filter((entry) => {
        if (entry.length <= folder.length || !entry.toLowerCase().startsWith(folder)) return false;
        const slash = entry.indexOf("/", folder.length);
        return slash === -1 || slash === entry.length - 1;
      })
      .sort((a, b) => Number(b.endsWith("/")) - Number(a.endsWith("/")) || compareText(a, b));
  }

  const lowerQuery = normalized.toLowerCase();
  const matchesPathOnly = lowerQuery.includes("/");
  return entries
    .map((entry) => ({ entry, rank: rank(entry, lowerQuery, matchesPathOnly) }))
    .filter((candidate) => candidate.rank >= 0)
    .sort((a, b) => a.rank - b.rank
      || depth(a.entry) - depth(b.entry)
      || Number(b.entry.endsWith("/")) - Number(a.entry.endsWith("/"))
      || compareText(a.entry, b.entry))
    .map((candidate) => candidate.entry);
}

/** Lower is better; -1 means no match. */
function rank(entry: string, query: string, matchesPathOnly: boolean): number {
  const path = entry.replace(/\/$/, "").toLowerCase();
  if (matchesPathOnly) {
    if (path.startsWith(query)) return 0;
    if (path.includes(query)) return 3;
    return inOrder(path, query) ? 5 : -1;
  }

  const name = path.slice(path.lastIndexOf("/") + 1);
  if (name === query) return 0;
  if (name.startsWith(query)) return 1;
  if (name.includes(query)) return 2;
  if (path.includes(query)) return 3;
  if (inOrder(name, query)) return 4;
  return inOrder(path, query) ? 5 : -1;
}

/** Whether every character of `query` appears in `text`, in order. */
function inOrder(text: string, query: string): boolean {
  let next = 0;
  for (const character of text) {
    if (next < query.length && character === query[next]) next += 1;
  }
  return next === query.length;
}

function depth(entry: string): number {
  return entry.replace(/\/$/, "").split("/").length - 1;
}

function compareText(a: string, b: string): number {
  const lowerA = a.toLowerCase();
  const lowerB = b.toLowerCase();
  return lowerA < lowerB ? -1 : lowerA > lowerB ? 1 : 0;
}
