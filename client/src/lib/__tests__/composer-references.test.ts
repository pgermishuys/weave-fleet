import { beforeEach, describe, expect, it } from "vitest";
import {
  caretOutsideReferences,
  forgetFileReferencesNotIn,
  pickedFileTokens,
  pickedReferenceRanges,
  referenceKeyAction,
  rememberFileReference,
  resetFileReferences,
  splitDraftReferences,
} from "@/lib/composer-references";

describe("splitDraftReferences", () => {
  it("marks files and folders picked with @", () => {
    expect(splitDraftReferences("look at @src/app.ts and @src/components/ please")).toEqual([
      { text: "look at " },
      { text: "@src/app.ts", reference: "file" },
      { text: " and " },
      { text: "@src/components/", reference: "folder" },
      { text: " please" },
    ]);
  });

  it("leaves emails, a lone @ and @@ alone", () => {
    expect(splitDraftReferences("mail me@example.com @ now @@x")).toEqual([
      { text: "mail me@example.com @ now @@x" },
    ]);
  });

  it("keeps the reference the caret is still typing plain", () => {
    const text = "fix @src/ap";

    expect(splitDraftReferences(text, text.length)).toEqual([{ text }]);
    expect(splitDraftReferences(`${text} `, text.length + 1)).toEqual([
      { text: "fix " },
      { text: "@src/ap", reference: "file" },
      { text: " " },
    ]);
  });

  it("treats a reference at the very start and one without a caret as done", () => {
    expect(splitDraftReferences("@README.md")).toEqual([{ text: "@README.md", reference: "file" }]);
  });

  it("drops sentence punctuation after a reference", () => {
    expect(splitDraftReferences("see @docs/guide.md, then (@src/)")).toEqual([
      { text: "see " },
      { text: "@docs/guide.md", reference: "file" },
      { text: ", then (@src/)" },
    ]);
  });

  it("finds references on later lines", () => {
    expect(splitDraftReferences("first\n@src/app.ts")).toEqual([
      { text: "first\n" },
      { text: "@src/app.ts", reference: "file" },
    ]);
  });
});

describe("splitDraftReferences with sessions", () => {
  it("marks a token picked as a session from the @ list as a session", () => {
    expect(splitDraftReferences("from @t3code-notes and @src/a.ts ", null, new Set(["@t3code-notes"]))).toEqual([
      { text: "from " },
      { text: "@t3code-notes", reference: "session", picked: true },
      { text: " and " },
      { text: "@src/a.ts", reference: "file" },
      { text: " " },
    ]);
  });
});

describe("references picked from the @ list", () => {
  const text = "look at @src/app.ts and @t3code-notes now";
  const sessions = new Set(["@t3code-notes"]);
  const files = new Set(["@src/app.ts"]);

  it("stay references with the caret at their end, where a typed one is still being typed", () => {
    const caret = "look at @src/app.ts".length;

    expect(splitDraftReferences(text, caret, sessions, files)[1]).toEqual({ text: "@src/app.ts", reference: "file", picked: true });
    expect(splitDraftReferences(text, caret, sessions)[0]).toEqual({ text: "look at @src/app.ts and " });
  });

  it("are found where they are in the text", () => {
    expect(pickedReferenceRanges(text, sessions, files)).toEqual([{ start: 8, end: 19 }, { start: 24, end: 37 }]);
    expect(pickedReferenceRanges("look at @src/app.ts", new Set(), new Set())).toEqual([]);
  });

  it("go in one piece on Backspace after them or Delete before them, leaving the delete to the browser", () => {
    const ranges = pickedReferenceRanges(text, sessions, files);
    const whole = { selectionStart: 8, selectionEnd: 19, preventDefault: false };

    expect(referenceKeyAction("Backspace", 19, ranges)).toEqual(whole);
    expect(referenceKeyAction("Delete", 8, ranges)).toEqual(whole);
    expect(referenceKeyAction("Backspace", 8, ranges)).toBeNull();
    expect(referenceKeyAction("Delete", 19, ranges)).toBeNull();
    expect(referenceKeyAction("Backspace", 20, ranges)).toBeNull();
  });

  it("are stepped over by the arrows", () => {
    const ranges = pickedReferenceRanges(text, sessions, files);

    expect(referenceKeyAction("ArrowLeft", 19, ranges)).toEqual({ selectionStart: 8, selectionEnd: 8, preventDefault: true });
    expect(referenceKeyAction("ArrowRight", 8, ranges)).toEqual({ selectionStart: 19, selectionEnd: 19, preventDefault: true });
    expect(referenceKeyAction("ArrowLeft", 8, ranges)).toBeNull();
    expect(referenceKeyAction("a", 19, ranges)).toBeNull();
  });

  it("never keep the caret inside: it moves to the nearer edge", () => {
    const ranges = pickedReferenceRanges(text, sessions, files);

    expect(caretOutsideReferences(10, ranges)).toBe(8);
    expect(caretOutsideReferences(17, ranges)).toBe(19);
    expect(caretOutsideReferences(21, ranges)).toBe(21);
  });
});

describe("picked files and folders", () => {
  beforeEach(() => {
    resetFileReferences();
    window.localStorage.clear();
  });

  it("are remembered with the draft, and forgotten once they're out of it", () => {
    rememberFileReference("session-1", "@src/app.ts");
    rememberFileReference("session-1", "@docs/");

    expect(pickedFileTokens("session-1")).toEqual(new Set(["@src/app.ts", "@docs/"]));
    expect(pickedFileTokens("session-2")).toEqual(new Set());

    resetFileReferences();
    expect(pickedFileTokens("session-1")).toEqual(new Set(["@src/app.ts", "@docs/"]));

    forgetFileReferencesNotIn("session-1", "look at @src/app.ts and @docs/guide.md");
    expect(pickedFileTokens("session-1")).toEqual(new Set(["@src/app.ts"]));

    forgetFileReferencesNotIn("session-1", "");
    expect(window.localStorage.getItem("weave-fleet.draft.file-refs.session-1")).toBeNull();
  });
});
