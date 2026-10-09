/**
 * What a tool call's `input` holds, read once. Harnesses disagree on the names (OpenCode says `filePath`, OpenCode 2
 * `path`, Claude Code `file_path`), and every field is optional: an input can be half-streamed, empty or missing.
 * Only non-blank strings count as present.
 */

export function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

/** The first of `keys` that holds a non-blank string, as written (not trimmed). */
export function stringField(input: Record<string, unknown> | null | undefined, ...keys: readonly string[]): string | undefined {
  for (const key of keys) {
    const value = input?.[key];
    if (typeof value === "string" && value.trim() !== "") return value;
  }
  return undefined;
}

export interface ToolQuestion {
  question?: string;
  header?: string;
}

/** The fields the common tools carry, whichever harness sent them. */
export interface ToolInput {
  /** The file the call is about: `filePath`, `path`, `file_path`, `file` or `notebook_path`. */
  filePath?: string;
  /** What a shell tool runs. */
  command?: string;
  /** A human sentence about the call (a shell command's description, a subagent's task). */
  description?: string;
  /** A glob or grep pattern. */
  pattern?: string;
  url?: string;
  /** A web search query. */
  query?: string;
  /** A skill's name: OpenCode says `name`, OpenCode 2 `id`. */
  skill?: string;
  /** The agent a subagent call runs. */
  agent?: string;
  /** A Code Mode script. */
  code?: string;
  /** What a write call writes. */
  content?: string;
  oldString?: string;
  newString?: string;
  questions: ToolQuestion[];
  /** Whatever else the call sent, for tools that have their own fields. */
  raw: Record<string, unknown>;
}

const EMPTY: Record<string, unknown> = Object.freeze({}) as Record<string, unknown>;

export function parseToolInput(input: unknown): ToolInput {
  const record = asRecord(input);
  const questions = Array.isArray(record?.questions)
    ? record.questions.flatMap((entry): ToolQuestion[] => {
        const item = asRecord(entry);
        if (!item) return [];
        return [{ question: stringField(item, "question"), header: stringField(item, "header") }];
      })
    : [];
  return {
    filePath: stringField(record, "filePath", "path", "file_path", "file", "notebook_path"),
    command: stringField(record, "command"),
    description: stringField(record, "description"),
    pattern: stringField(record, "pattern"),
    url: stringField(record, "url"),
    query: stringField(record, "query"),
    skill: stringField(record, "name", "id"),
    agent: stringField(record, "agent"),
    code: typeof record?.code === "string" ? record.code : undefined,
    content: stringField(record, "content", "text", "newContent", "new_content"),
    oldString: stringField(record, "oldString", "old_string"),
    newString: stringField(record, "newString", "new_string"),
    questions,
    raw: record ?? EMPTY,
  };
}
