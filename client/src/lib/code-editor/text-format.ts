import { EditorState, type Extension } from "@codemirror/state";
import { EditorView } from "@codemirror/view";

/**
 * How a file's text is laid out on disk, so the editor can give back exactly the
 * bytes it was given. CodeMirror splits lines on any break and joins them with
 * `\n` unless told otherwise, and a BOM would show up as a stray character.
 */
export interface TextFormat {
  /** The break used between lines: the file's most common one. */
  lineSeparator: "\n" | "\r\n" | "\r";
  /** Whether the file starts with a UTF-8 byte order mark. */
  bom: boolean;
}

const BOM = "\uFEFF";

/** Pick the file's most common line break. Ties and files without breaks use `\n`. */
function dominantSeparator(text: string): TextFormat["lineSeparator"] {
  let crlf = 0;
  let lf = 0;
  let cr = 0;
  for (let i = 0; i < text.length; i++) {
    const ch = text.charCodeAt(i);
    if (ch === 13) {
      if (text.charCodeAt(i + 1) === 10) {
        crlf++;
        i++;
      } else {
        cr++;
      }
    } else if (ch === 10) {
      lf++;
    }
  }
  if (crlf > lf && crlf >= cr) return "\r\n";
  if (cr > lf && cr > crlf) return "\r";
  return "\n";
}

/** Split text as read from disk into the editor's text and its format. */
export function readTextFormat(raw: string): { text: string; format: TextFormat } {
  const bom = raw.startsWith(BOM);
  const text = bom ? raw.slice(1) : raw;
  return { text, format: { lineSeparator: dominantSeparator(text), bom } };
}

/** Text to write to disk for an editor state opened with {@link textFormatExtension}. */
export function toDiskText(state: EditorState, format: TextFormat): string {
  // `doc.toString()` always joins with `\n`; sliceDoc uses the state's line separator.
  const text = state.sliceDoc();
  return format.bom ? BOM + text : text;
}

/**
 * The editor splits lines only on the file's own break, so a file with mixed
 * endings keeps the odd ones as characters and saves back byte for byte. Pasted
 * or dropped text is converted to the file's break, since it would otherwise land
 * inside a line.
 */
export function textFormatExtension(format: TextFormat): Extension {
  const sep = format.lineSeparator;
  return [
    EditorState.lineSeparator.of(sep),
    EditorView.clipboardInputFilter.of((text) => text.replace(/\r\n?|\n/g, sep)),
  ];
}

export function sameTextFormat(a: TextFormat, b: TextFormat): boolean {
  return a.lineSeparator === b.lineSeparator && a.bom === b.bom;
}
