import { describe, expect, it } from "vitest";
import { splitDraftReferences } from "@/lib/composer-references";

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
