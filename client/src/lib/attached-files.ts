/** The user preference behind the switch in Settings → Features. */
export const ATTACH_ANY_FILE_PREFERENCE_KEY = "AttachAnyFile";

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
