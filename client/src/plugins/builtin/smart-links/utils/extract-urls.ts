/**
 * Extracts HTTP(S) URLs and GitHub shorthand references from a text string.
 * Handles plain URLs, markdown links [text](url), trailing punctuation,
 * and GitHub shorthand patterns (owner/repo#123, #123 with context).
 * Returns deduplicated array of unique URLs.
 */
export function extractUrls(text: string): string[] {
  const seen = new Set<string>()
  const urls: string[] = []

  function add(url: string): void {
    if (url && !seen.has(url)) {
      seen.add(url)
      urls.push(url)
    }
  }

  // Match markdown links first: [text](url)
  const markdownLinkRegex = /\[(?:[^\]]*)\]\((https?:\/\/[^)]+)\)/g
  let match: RegExpExecArray | null

  while ((match = markdownLinkRegex.exec(text)) !== null) {
    add(normalizeUrl(match[1]))
  }

  // Match bare URLs (not inside markdown link parentheses)
  const textWithoutMarkdown = text.replace(/\[(?:[^\]]*)\]\((https?:\/\/[^)]+)\)/g, ' ')
  const bareUrlRegex = /https?:\/\/[^\s<>"')\]]+/g

  while ((match = bareUrlRegex.exec(textWithoutMarkdown)) !== null) {
    add(normalizeUrl(match[0]))
  }

  // Match GitHub shorthand references: owner/repo#123
  const ownerRepoRefRegex = /(?<![/\w])([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)#(\d+)/g

  while ((match = ownerRepoRefRegex.exec(text)) !== null) {
    const [, ownerRepo, number] = match
    const url = `https://github.com/${ownerRepo}/issues/${number}`
    add(url)
  }

  return urls
}

/**
 * Strips common trailing punctuation from a URL that is likely not part of it.
 */
function normalizeUrl(raw: string): string {
  // Remove trailing punctuation: . , ; : ! ? ) ] >
  return raw.replace(/[.,;:!?)\]>]+$/, '')
}
