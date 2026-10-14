import { describe, expect, it } from "vitest";
import {
  fileExtensionLabel,
  formatAttachedNote,
  formatFileSize,
  parseAttachedNote,
  withAttachedNote,
  type AttachedFile,
} from "@/lib/attached-files";

const log: AttachedFile = { path: ".weave/attachments/3f9a2c1d/build-output.log", name: "build-output.log", size: 188416, lines: 2031, mime: "text/plain" };
const pdf: AttachedFile = { path: ".weave/attachments/3f9a2c1d/incident-report.pdf", name: "incident-report.pdf", size: 421888, lines: null, mime: "application/pdf" };

describe("attached files", () => {
  it("formats sizes", () => {
    expect(formatFileSize(900)).toBe("900 B");
    expect(formatFileSize(188416)).toBe("184 KB");
    expect(formatFileSize(1.5 * 1024 * 1024)).toBe("1.5 MB");
  });

  it("labels a chip by extension", () => {
    expect(fileExtensionLabel("a.log")).toBe("LOG");
    expect(fileExtensionLabel("Makefile")).toBe("FILE");
  });

  it("writes the note for one and for many files", () => {
    expect(formatAttachedNote([log])).toBe(
      "Attached: .weave/attachments/3f9a2c1d/build-output.log (2,031 lines, 184 KB)\nRead it as needed.",
    );
    expect(formatAttachedNote([log, pdf])).toBe(
      "Attached: .weave/attachments/3f9a2c1d/build-output.log (2,031 lines, 184 KB)\n"
      + "Attached: .weave/attachments/3f9a2c1d/incident-report.pdf (PDF, 412 KB)\nRead them as needed.",
    );
  });

  it("says line for one line and File for no extension", () => {
    expect(formatAttachedNote([{ ...log, lines: 1, size: 10 }])).toContain("(1 line, 10 B)");
    expect(formatAttachedNote([{ ...pdf, name: "dump", size: 3072 }])).toContain("(File, 3 KB)");
  });

  it("puts the note after the typed text", () => {
    expect(withAttachedNote("Why does it fail?  \n", [log])).toBe(`Why does it fail?\n\n${formatAttachedNote([log])}`);
    expect(withAttachedNote("", [log])).toBe(formatAttachedNote([log]));
    expect(withAttachedNote("hi", [])).toBe("hi");
  });

  it("parses what it wrote", () => {
    const parsed = parseAttachedNote(withAttachedNote("Why does it fail?", [log, pdf]));
    expect(parsed.text).toBe("Why does it fail?");
    expect(parsed.files).toEqual([
      { path: log.path, name: "build-output.log", details: "2,031 lines, 184 KB" },
      { path: pdf.path, name: "incident-report.pdf", details: "PDF, 412 KB" },
    ]);
  });

  it("parses a message that is only the note, and leaves other text alone", () => {
    expect(parseAttachedNote(formatAttachedNote([log])).text).toBe("");
    expect(parseAttachedNote("Attached: nothing here")).toEqual({ text: "Attached: nothing here", files: [] });
  });
});
