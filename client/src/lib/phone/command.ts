/**
 * A command split into the pieces it may wrap between: whole words, so `dotnet test *` never leaves the `*` alone on
 * a line and `--filter` never breaks in two. Runs of spaces stay with the word before them.
 */
export function commandWords(command: string): string[] {
  return command.match(/\S+\s*|\s+/g) ?? [];
}
