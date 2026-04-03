import { DEFAULT_ISSUE_FILTER, type IssueFilterState } from "../types";

// ─── Parser ───────────────────────────────────────────────────────────────────

/**
 * Tokenises a filter expression string, respecting quoted values.
 * Returns an array of tokens (each is a raw string like `label:bug` or `is:open`
 * or a plain search word).
 */
function tokenise(expr: string): string[] {
  const tokens: string[] = [];
  let i = 0;
  while (i < expr.length) {
    // Skip whitespace
    if (expr[i] === " " || expr[i] === "\t") {
      i++;
      continue;
    }

    // Detect qualifier prefix: word followed by ':'
    const colonIdx = expr.indexOf(":", i);
    if (colonIdx !== -1) {
      // Check that everything between i and colonIdx is a word char (no space)
      const possibleKey = expr.slice(i, colonIdx);
      if (possibleKey.length > 0 && /^[a-zA-Z][a-zA-Z0-9_-]*$/.test(possibleKey)) {
        // It's a qualifier — consume key + ':' + value (possibly quoted)
        const afterColon = colonIdx + 1;
        if (afterColon < expr.length && expr[afterColon] === '"') {
          // Quoted value
          const closeQuote = expr.indexOf('"', afterColon + 1);
          if (closeQuote !== -1) {
            tokens.push(expr.slice(i, closeQuote + 1));
            i = closeQuote + 1;
          } else {
            // Unclosed quote — treat rest as value
            tokens.push(expr.slice(i));
            break;
          }
        } else {
          // Unquoted value — read until next whitespace
          let end = afterColon;
          while (end < expr.length && expr[end] !== " " && expr[end] !== "\t") {
            end++;
          }
          tokens.push(expr.slice(i, end));
          i = end;
        }
        continue;
      }
    }

    // Plain word (no qualifier prefix)
    let end = i;
    while (end < expr.length && expr[end] !== " " && expr[end] !== "\t") {
      end++;
    }
    tokens.push(expr.slice(i, end));
    i = end;
  }
  return tokens.filter((t) => t.length > 0);
}

/**
 * Strips surrounding quotes from a value string, if present.
 */
function unquote(value: string): string {
  if (value.startsWith('"') && value.endsWith('"') && value.length >= 2) {
    return value.slice(1, -1);
  }
  return value;
}

/**
 * Parses a GitHub-compatible filter expression string into structured
 * `IssueFilterState`. Unknown qualifiers are preserved in the `search` field.
 *
 * Supported qualifiers:
 *   is:open | is:closed | is:all
 *   label:<name> | label:"multi word"
 *   milestone:<title>
 *   assignee:<username> | assignee:none | assignee:*
 *   author:<username>
 *   type:<name> | type:none | type:*
 *   sort:created-desc | sort:created-asc | sort:updated-desc |
 *         sort:updated-asc | sort:comments-desc | sort:comments-asc
 *
 * Remaining unqualified tokens are joined as the `search` field.
 */
export function parseFilterExpression(expr: string): IssueFilterState {
  if (!expr.trim()) {
    return { ...DEFAULT_ISSUE_FILTER };
  }

  const tokens = tokenise(expr);
  const state: IssueFilterState = { ...DEFAULT_ISSUE_FILTER };
  const searchTokens: string[] = [];

  for (const token of tokens) {
    const colonIdx = token.indexOf(":");
    if (colonIdx === -1) {
      searchTokens.push(token);
      continue;
    }

    const key = token.slice(0, colonIdx).toLowerCase();
    const rawValue = token.slice(colonIdx + 1);
    const value = unquote(rawValue);

    switch (key) {
      case "is":
        if (value === "open" || value === "closed" || value === "all") {
          state.state = value;
        } else {
          searchTokens.push(token);
        }
        break;

      case "label":
        if (value && !state.labels.includes(value)) {
          state.labels = [...state.labels, value];
        }
        break;

      case "milestone":
        state.milestone = value || null;
        break;

      case "assignee":
        state.assignee = value || null;
        break;

      case "author":
        state.author = value || null;
        break;

      case "type":
        state.type = value || null;
        break;

      case "sort": {
        // Format: sort:<field>-<direction>
        const dashIdx = value.lastIndexOf("-");
        if (dashIdx !== -1) {
          const field = value.slice(0, dashIdx);
          const dir = value.slice(dashIdx + 1);
          if (
            (field === "created" || field === "updated" || field === "comments") &&
            (dir === "asc" || dir === "desc")
          ) {
            state.sort = field;
            state.direction = dir;
          } else {
            searchTokens.push(token);
          }
        } else {
          searchTokens.push(token);
        }
        break;
      }

      default:
        // Unknown qualifier — preserve as search text
        searchTokens.push(token);
        break;
    }
  }

  state.search = searchTokens.join(" ");
  return state;
}

// ─── Serializer ───────────────────────────────────────────────────────────────

/**
 * Wraps a value in double quotes if it contains a space or special characters.
 */
function quoteIfNeeded(value: string): string {
  if (/[\s":]/.test(value)) {
    return `"${value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
  }
  return value;
}

/**
 * Serialises an `IssueFilterState` back into a GitHub-compatible filter
 * expression string. Omits qualifiers that match their default values to keep
 * the expression minimal. The `search` field is appended at the end.
 */
export function serializeFilterExpression(state: IssueFilterState): string {
  const parts: string[] = [];

  // state — only emit when non-default ("open" is default, omit it)
  if (state.state !== DEFAULT_ISSUE_FILTER.state) {
    parts.push(`is:${state.state}`);
  }

  // labels
  for (const label of state.labels) {
    parts.push(`label:${quoteIfNeeded(label)}`);
  }

  // milestone
  if (state.milestone !== null) {
    parts.push(`milestone:${quoteIfNeeded(state.milestone)}`);
  }

  // assignee
  if (state.assignee !== null) {
    parts.push(`assignee:${quoteIfNeeded(state.assignee)}`);
  }

  // author
  if (state.author !== null) {
    parts.push(`author:${quoteIfNeeded(state.author)}`);
  }

  // type
  if (state.type !== null) {
    parts.push(`type:${quoteIfNeeded(state.type)}`);
  }

  // sort — only emit when non-default (updated/desc is default)
  const defaultSort = DEFAULT_ISSUE_FILTER.sort;
  const defaultDirection = DEFAULT_ISSUE_FILTER.direction;
  if (state.sort !== defaultSort || state.direction !== defaultDirection) {
    parts.push(`sort:${state.sort}-${state.direction}`);
  }

  // search text last
  if (state.search.trim()) {
    parts.push(state.search.trim());
  }

  return parts.join(" ");
}
