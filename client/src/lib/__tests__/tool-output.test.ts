import { describe, expect, it } from "vitest";
import { linesToHtml, lineText, parseToolOutput, parseToolOutputCached } from "@/lib/tool-output";

const ESC = "\x1b";

describe("parseToolOutput", () => {
  it("keeps plain text as it is, one line per line", () => {
    const parsed = parseToolOutput("a\nb\n\n");

    expect(parsed.lines.map(lineText)).toEqual(["a", "b"]);
    expect(parsed.lines[0]).toEqual([{ text: "a" }]);
    expect(parsed.plain).toBe("a\nb");
  });

  it("has no lines for empty or blank output", () => {
    expect(parseToolOutput("").lines).toEqual([]);
    expect(parseToolOutput("\n\n").lines).toEqual([]);
  });

  it("turns colour codes into classes in Fleet's palette, and resets them", () => {
    const parsed = parseToolOutput(`${ESC}[32m✓${ESC}[0m ok ${ESC}[1;31mFAIL${ESC}[22m x${ESC}[39m done`);

    expect(parsed.lines[0]).toEqual([
      { text: "✓", className: "ansi-fg-green" },
      { text: " ok " },
      { text: "FAIL", className: "ansi-fg-red ansi-bold" },
      { text: " x", className: "ansi-fg-red" },
      { text: " done" },
    ]);
    expect(parsed.plain).toBe("✓ ok FAIL x done");
  });

  it("carries a colour across lines until it's reset", () => {
    const parsed = parseToolOutput(`${ESC}[33mone\ntwo${ESC}[m\nthree`);

    expect(parsed.lines).toEqual([
      [{ text: "one", className: "ansi-fg-yellow" }],
      [{ text: "two", className: "ansi-fg-yellow" }],
      [{ text: "three" }],
    ]);
  });

  it("reads bright, background, dim, italic and underline codes", () => {
    const [line] = parseToolOutput(`${ESC}[92;41;2;3;4mx${ESC}[23;24;49my`).lines;

    expect(line).toEqual([
      { text: "x", className: "ansi-fg-bright-green ansi-bg-red ansi-dim ansi-italic ansi-underline" },
      { text: "y", className: "ansi-fg-bright-green ansi-dim" },
    ]);
  });

  it("draws 256-colour and true-colour codes, palette indexes as classes and the rest as a colour", () => {
    const [line] = parseToolOutput(`${ESC}[38;5;9ma${ESC}[38;5;208mb${ESC}[38;2;10;20;300mc${ESC}[48;5;244md`).lines;

    expect(line).toEqual([
      { text: "a", className: "ansi-fg-bright-red" },
      { text: "b", style: "color:rgb(255,135,0)" },
      { text: "c", style: "color:rgb(10,20,255)" },
      { text: "d", style: "color:rgb(10,20,255);background-color:rgb(128,128,128)" },
    ]);
  });

  it("drops cursor moves, erases, private modes, titles and charset picks", () => {
    const raw = `${ESC}]0;my title${ESC}\\${ESC}[?25l${ESC}(Bstart${ESC}[2K${ESC}[1A${ESC}[10Gend${ESC}]8;;https://x.test${ESC}\\link${ESC}]8;;${ESC}\\`;

    expect(parseToolOutput(raw).plain).toBe("startendlink");
  });

  it("keeps only the last frame of a line rewritten with \\r", () => {
    const raw = "Installing\n[#   ] 25%\r[##  ] 50%\r[####] 100%\nDone\n";

    expect(parseToolOutput(raw).lines.map(lineText)).toEqual(["Installing", "[####] 100%", "Done"]);
  });

  it("keeps a frame a trailing \\r would otherwise wipe, and the colour set in a dropped frame", () => {
    const parsed = parseToolOutput(`${ESC}[32m10%\r50%\r${ESC}[2K\r`);

    expect(parsed.lines).toEqual([[{ text: "50%", className: "ansi-fg-green" }]]);
  });

  it("treats \\r\\n as a plain line end", () => {
    expect(parseToolOutput("one\r\ntwo\r\n").lines.map(lineText)).toEqual(["one", "two"]);
  });

  it("drops a broken escape instead of showing it, and keeps the text around it", () => {
    expect(parseToolOutput(`before${ESC}`).plain).toBe("before");
    expect(parseToolOutput(`a${ESC}[31`).plain).not.toContain(ESC);
    expect(parseToolOutput(`a${ESC}]0;title with no end\nnext`).plain).toBe("a\nnext");
    expect(parseToolOutput(`${ESC}[38;5mx${ESC}[38;2;1mz`).plain).toBe("xz");
    expect(parseToolOutput(`x${ESC}[1;>my`).lines).toEqual([[{ text: "xy" }]]);
    expect(parseToolOutput(`x${ESC}[999my`).lines).toEqual([[{ text: "xy" }]]);
  });

  it("drops bells, backspaces and the other control characters, but keeps tabs", () => {
    expect(parseToolOutput("a\x07b\x08c\td\x00").plain).toBe("abc\td");
  });

  it("never turns the output into markup", () => {
    const [line] = parseToolOutput(`${ESC}[31m<img src=x onerror=alert(1)>`).lines;

    expect(line).toEqual([{ text: "<img src=x onerror=alert(1)>", className: "ansi-fg-red" }]);
  });

  it("parses 5,000 coloured lines quickly", () => {
    const raw = Array.from(
      { length: 5000 },
      (_, i) => `${ESC}[32m✓${ESC}[0m ok  suite/case_${i} ${ESC}[2m(${i % 97}ms)${ESC}[0m${i % 50 === 0 ? `\r${ESC}[2K${ESC}[33mretry ${i}${ESC}[0m` : ""}`,
    ).join("\n");

    const started = performance.now();
    const parsed = parseToolOutput(raw);
    const elapsed = performance.now() - started;

    expect(parsed.lines).toHaveLength(5000);
    expect(lineText(parsed.lines[4999]!)).toBe("✓ ok  suite/case_4999 (52ms)");
    expect(lineText(parsed.lines[50]!)).toBe("retry 50");
    expect(parsed.plain).not.toContain(ESC);
    // Linear work takes well under 100 ms here; the bound is loose for busy CI machines and catches a quadratic slip.
    expect(elapsed).toBeLessThan(1000);
  });
});

describe("linesToHtml", () => {
  it("escapes every bit of text, leaves uncoloured text bare and wraps coloured text in Fleet's span", () => {
    const { lines } = parseToolOutput(`<b>&"'\n${ESC}[31m<img src=x onerror=alert(1)>${ESC}[0m ok\n${ESC}[38;2;1;2;3mx`);

    expect(linesToHtml(lines)).toBe(
      "&lt;b&gt;&amp;&quot;&#39;\n" +
        '<span class="ansi-fg-red">&lt;img src=x onerror=alert(1)&gt;</span> ok\n' +
        '<span style="color:rgb(1,2,3)">x</span>',
    );
  });
});

describe("parseToolOutputCached", () => {
  it("parses the same output once", () => {
    const raw = `${ESC}[31mcached ${Math.random()}`;

    expect(parseToolOutputCached(raw)).toBe(parseToolOutputCached(raw));
    expect(parseToolOutputCached(`${raw}!`)).not.toBe(parseToolOutputCached(raw));
  });
});
