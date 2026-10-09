import type { IssueFilterState } from "../composables/github-types";

const quote = (value: string) => (/\s/.test(value) ? `"${value}"` : value)

/** The issue filter bar's choices as GitHub search qualifiers, e.g. `is:open label:bug sort:updated-desc`. */
export function issueFilterQuery(filter: IssueFilterState): string {
  const parts: string[] = []
  if (filter.state !== "all") parts.push(`is:${filter.state}`)
  for (const label of filter.labels) parts.push(`label:${quote(label)}`)
  if (filter.author) parts.push(`author:${filter.author}`)
  if (filter.assignee === "none") parts.push("no:assignee")
  else if (filter.assignee && filter.assignee !== "*") parts.push(`assignee:${filter.assignee}`)
  if (filter.milestone) parts.push(`milestone:${quote(filter.milestone)}`)
  if (filter.type) parts.push(`type:${quote(filter.type)}`)
  parts.push(`sort:${filter.sort}-${filter.direction}`)
  if (filter.search.trim()) parts.push(filter.search.trim())
  return parts.join(" ")
}
