/**
 * What the + menu's Browser field means: an address on Fleet's machine to show, or a command for Fleet to run.
 * `5173`, `localhost:5173/cart` and `http://127.0.0.1:8080` are addresses; anything else is a command.
 */
export type BrowserTarget = { kind: "address"; url: string } | { kind: "command"; command: string };

const LOCAL_HOST = /^(localhost|[\w-]+\.localhost|127(?:\.\d{1,3}){3}|0\.0\.0\.0|\[::1?\])(:\d{1,5})?(\/\S*)?$/i;

export function browserTarget(input: string): BrowserTarget | null {
  const value = input.trim();
  if (!value) return null;
  if (/^\d{1,5}$/.test(value)) return { kind: "address", url: `http://localhost:${value}/` };
  if (/^https?:\/\/\S+$/i.test(value)) return { kind: "address", url: normalize(value) };
  if (LOCAL_HOST.test(value)) return { kind: "address", url: normalize(`http://${value}`) };
  return { kind: "command", command: value };
}

function normalize(url: string): string {
  try {
    return new URL(url).toString();
  } catch {
    return url;
  }
}
