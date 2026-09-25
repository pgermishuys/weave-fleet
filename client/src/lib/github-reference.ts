/**
 * `#` in the new-session message picks a pull request or issue: `#`, `#318` or `#flicker` right before the caret,
 * at the start or after a space (so "C#" and "issue#3" don't open it).
 */
export interface HashTrigger {
  /** Where the `#` is. */
  start: number
  /** Where the typed query ends (the caret). */
  end: number
  /** What's typed after the `#`. */
  query: string
}

export function findHashTrigger(text: string, caret: number | null): HashTrigger | null {
  if (caret === null || caret < 1 || caret > text.length) return null
  const before = text.slice(0, caret)
  const match = /(^|\s)#([^\s#]*)$/.exec(before)
  if (!match) return null
  const start = caret - match[2].length - 1
  return { start, end: caret, query: match[2] }
}

/** The message without the `#query` that picked an item, and where the caret goes. */
export function removeTrigger(text: string, trigger: HashTrigger): { text: string; caret: number } {
  const before = text.slice(0, trigger.start)
  let after = text.slice(trigger.end)
  // Don't leave two spaces where the reference was.
  if (before.endsWith(" ") && after.startsWith(" ")) after = after.slice(1)
  const joined = before + after
  return { text: before.length === 0 ? joined.trimStart() : joined, caret: before.length === 0 ? 0 : before.length }
}

const GITHUB_REMOTE = /github\.com[:/]([A-Za-z0-9-]+)\/([A-Za-z0-9._-]+?)(?:\.git)?\/?$/i

/** `owner/repo` from a GitHub remote: `git@github.com:owner/repo.git`, `https://github.com/owner/repo`. */
export function parseGitHubRemote(url: string | null | undefined): { owner: string; repo: string } | null {
  const match = url ? GITHUB_REMOTE.exec(url.trim()) : null
  return match ? { owner: match[1], repo: match[2] } : null
}
