const FENCE = /^ {0,3}(`{3,}|~{3,})(.*)$/;
const ORDERED_ITEM = /^ {0,3}(\d{1,9})[.)][ \t]+\S/;
const HEADING = /^ {0,3}#{1,6}(?:[ \t]|$)/;
const OTHER_BLOCK = /^(?: {0,3}(?:[-*+][ \t]|\d{1,9}[.)](?:[ \t]|$)|>|\|)| {4}|\t)/;

/**
 * CommonMark lets only a list that starts at 1 interrupt a paragraph, so an agent's "**Follow-ups**\n3. ..." would
 * draw as one run-on paragraph. Adds the blank line before such a list. Code fences, other lists and numbers inside
 * a sentence are left alone.
 */
export function separateOrderedLists(markdown: string): string {
  const out: string[] = [];
  let fence: { char: string; length: number } | null = null;
  let inParagraph = false;
  let blockStart = true;

  for (const line of markdown.split("\n")) {
    const fenceMatch = FENCE.exec(line);
    if (fence) {
      const marker = fenceMatch?.[1];
      if (marker && marker[0] === fence.char && marker.length >= fence.length && fenceMatch?.[2].trim() === "") {
        fence = null;
        blockStart = true;
      }
      out.push(line);
      continue;
    }
    if (fenceMatch && !(fenceMatch[1][0] === "`" && fenceMatch[2].includes("`"))) {
      fence = { char: fenceMatch[1][0], length: fenceMatch[1].length };
      inParagraph = false;
      out.push(line);
      continue;
    }

    if (line.trim() === "" || HEADING.test(line)) {
      inParagraph = false;
      blockStart = true;
      out.push(line);
      continue;
    }

    const item = ORDERED_ITEM.exec(line);
    if (inParagraph && item && Number(item[1]) !== 1) {
      out.push("");
      inParagraph = false;
    } else if (OTHER_BLOCK.test(line)) {
      if (blockStart || !/^[ \t]/.test(line)) inParagraph = false;
    } else if (blockStart) {
      inParagraph = true;
    }
    blockStart = false;
    out.push(line);
  }

  return out.join("\n");
}
