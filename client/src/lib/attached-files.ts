/** The user preference behind the switch in Settings → Features. */
export const ATTACH_ANY_FILE_PREFERENCE_KEY = "AttachAnyFile";

/** What a big paste does: "ask" shows the bar, "attach" makes a file, "text" pastes as usual. */
export const LARGE_PASTES_PREFERENCE_KEY = "LargePastes";
export type LargePastes = "ask" | "attach" | "text";

/** What the server saved for an uploaded file; `path` is from the session's folder. */
export interface AttachedFile {
  path: string;
  name: string;
  size: number;
  /** Lines in a text file, null for anything else. */
  lines: number | null;
  mime: string;
}

/** A file named in a sent message's note. */
export interface AttachedFileRef {
  path: string;
  name: string;
  details: string;
}

/** `B` under 1 KB, whole `KB` under 1 MB, then `MB` with one decimal. */
export function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** The label a chip shows in its box: LOG, PDF, FILE when the name has no extension. */
export function fileExtensionLabel(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot > 0 && dot < name.length - 1 ? name.slice(dot + 1).toUpperCase().slice(0, 4) : "FILE";
}

function describe(file: AttachedFile): string {
  const size = formatFileSize(file.size);
  if (file.lines !== null) return `${file.lines.toLocaleString("en-US")} ${file.lines === 1 ? "line" : "lines"}, ${size}`;
  const dot = file.name.lastIndexOf(".");
  const type = dot > 0 && dot < file.name.length - 1 ? file.name.slice(dot + 1).toUpperCase() : "File";
  return `${type}, ${size}`;
}

/** The lines the agent gets after the typed text. */
export function formatAttachedNote(files: AttachedFile[]): string {
  const lines = files.map((file) => `Attached: ${file.path} (${describe(file)})`);
  lines.push(files.length === 1 ? "Read it as needed." : "Read them as needed.");
  return lines.join("\n");
}

export function withAttachedNote(text: string, files: AttachedFile[]): string {
  if (files.length === 0) return text;
  const note = formatAttachedNote(files);
  const typed = text.trimEnd();
  return typed ? `${typed}\n\n${note}` : note;
}

const NOTE = /(?:^|\n)((?:Attached: \S+ \([^\n]*\)\n)+)Read (?:it|them) as needed\.\s*$/;
const NOTE_LINE = /^Attached: (\S+) \((.*)\)$/;

/** Splits a sent message into what was typed and the files its trailing note names. */
export function parseAttachedNote(body: string): { text: string; files: AttachedFileRef[] } {
  const note = NOTE.exec(body);
  if (!note) return { text: body, files: [] };
  const files = note[1].trimEnd().split("\n").flatMap((line) => {
    const match = NOTE_LINE.exec(line);
    if (!match) return [];
    return [{ path: match[1], name: match[1].split("/").pop() ?? match[1], details: match[2] }];
  });
  return { text: body.slice(0, note.index).trimEnd(), files };
}

const BIG_PASTE_CHARACTERS = 2000;
const BIG_PASTE_LINES = 25;
/** A text file over this isn't read for its line count. */
const LINE_COUNT_MAX_BYTES = 5 * 1024 * 1024;
/** A text file under this goes into the prompt instead of being uploaded. */
export const INLINE_MAX_BYTES = 4096;

const TEXT_EXTENSIONS = new Set([
  "txt", "log", "md", "json", "xml", "yaml", "yml", "csv", "tsv", "toml", "ini", "sql", "html", "css", "js", "ts", "py", "cs", "sh",
]);

export function isBigPaste(text: string): boolean {
  return text.length >= BIG_PASTE_CHARACTERS || text.split("\n").length >= BIG_PASTE_LINES;
}

/** "2,031 lines pasted", or the characters when it's one long line. */
export function describePaste(text: string): string {
  const lines = countLines(text);
  return lines >= 2
    ? `${lines.toLocaleString("en-US")} lines pasted`
    : `${text.length.toLocaleString("en-US")} characters pasted`;
}

/** `pasted-text.txt`, then `pasted-text-2.txt`, the first name the draft doesn't have. */
export function pastedFileName(taken: string[]): string {
  for (let n = 1; ; n++) {
    const name = n === 1 ? "pasted-text.txt" : `pasted-text-${n}.txt`;
    if (!taken.includes(name)) return name;
  }
}

/** The way the server counts: a last line with no newline still counts, an empty text has none. */
export function countLines(text: string): number {
  if (text.length === 0) return 0;
  return text.split("\n").length - (text.endsWith("\n") ? 1 : 0);
}

export function isTextFile(file: Pick<File, "name" | "type">): boolean {
  if (file.type.startsWith("text/") || file.type.endsWith("json") || file.type.endsWith("xml")) return true;
  const dot = file.name.lastIndexOf(".");
  return dot > 0 && TEXT_EXTENSIONS.has(file.name.slice(dot + 1).toLowerCase());
}

/** Lines in a text file small enough to read, else undefined. */
export async function readLineCount(file: File): Promise<number | undefined> {
  if (!isTextFile(file) || file.size > LINE_COUNT_MAX_BYTES) return undefined;
  const text = await file.text();
  return text.includes("\u0000") || text.includes("\uFFFD") ? undefined : countLines(text);
}

/** "2,031 lines · 186 KB", or just the size when the lines aren't known. */
export function describeDraftFile(size: number, lines?: number): string {
  if (lines === undefined) return formatFileSize(size);
  return `${lines.toLocaleString("en-US")} ${lines === 1 ? "line" : "lines"} · ${formatFileSize(size)}`;
}

/** A small file as a fenced block headed by its name, the fence longer than any backtick run inside. */
export function inlineFileBlock(name: string, content: string): string {
  const longest = Math.max(0, ...(content.match(/`+/g) ?? []).map((run) => run.length));
  const fence = "`".repeat(Math.max(3, longest + 1));
  return `${name}:\n${fence}\n${content.replace(/\n$/, "")}\n${fence}`;
}
