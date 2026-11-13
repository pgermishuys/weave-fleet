/**
 * Builds the bench's repositories (invented content) under <home>/proj and creates one session per repository on a
 * scratch Fleet. Each repository sits on a feature branch whose changes are part committed, part in the working tree,
 * so Changes compares it with where it left main: the same diff whichever Fleet build (or when) the session is made.
 *
 *   bun run setup.ts --home C:\fleet-scratch\changes\home --base http://127.0.0.1:6390 [--repos]   (--repos: (re)build them)
 *
 * Writes <home>/sessions-<port>.json: { small, large, big } session ids.
 * The big repository is a clone of a large public project at <home>/proj/bigrepo (made by hand; see README.md).
 */
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

function arg(name: string, fallback?: string): string {
  const index = process.argv.indexOf(`--${name}`);
  const value = index >= 0 ? process.argv[index + 1] : fallback;
  if (!value) throw new Error(`--${name} is required`);
  return value;
}
const home = resolve(arg("home"));
const base = arg("base");
const proj = join(home, "proj");

function git(cwd: string, ...args: string[]): string {
  return execFileSync("git", ["-c", "user.name=Bench", "-c", "user.email=bench@example.invalid", ...args], {
    cwd,
    encoding: "utf8",
    maxBuffer: 1 << 28,
  });
}

// Deterministic invented text.
let seed = 42;
function rand(): number {
  seed = (seed * 1103515245 + 12345) & 0x7fffffff;
  return seed / 0x7fffffff;
}
const WORDS = ["lantern", "harbor", "pebble", "orchard", "copper", "meadow", "quill", "ember", "willow", "saffron", "tide", "marble"];
function word(): string {
  return WORDS[Math.floor(rand() * WORDS.length)]!;
}
function codeLines(count: number, tag: string): string[] {
  const lines: string[] = [];
  for (let i = 0; i < count; i++) {
    if (i % 12 === 0) lines.push(`export function ${word()}${tag}${i}(input: string): string {`);
    else if (i % 12 === 11) lines.push("}");
    else lines.push(`  const ${word()}${i} = input.replace("${word()}", "${word()} ${word()}"); // ${tag}`);
  }
  return lines;
}
function write(path: string, text: string | Uint8Array): void {
  mkdirSync(join(path, ".."), { recursive: true });
  writeFileSync(path, text);
}

function freshRepo(name: string): string {
  const dir = join(proj, name);
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  git(dir, "init", "-q", "-b", "main");
  return dir;
}

function buildSmall(): void {
  const dir = freshRepo("tiny-notes");
  for (let i = 0; i < 15; i++) write(join(dir, `notes/note-${i}.md`), codeLines(40, `n${i}`).join("\n") + "\n");
  git(dir, "add", "-A");
  git(dir, "commit", "-q", "-m", "notes");
  git(dir, "checkout", "-q", "-b", "feature/tidy-notes");
  for (const i of [2, 7]) {
    const lines = codeLines(40, `n${i}`);
    lines.splice(10, 2, "  // tidied by the agent", "  const tidy = true;");
    write(join(dir, `notes/note-${i}.md`), lines.join("\n") + "\n");
  }
  write(join(dir, "notes/new-note.md"), codeLines(20, "new").join("\n") + "\n");
}

function buildLarge(): void {
  const dir = freshRepo("lantern-shop");
  const originals = new Map<string, string[]>();
  for (let m = 0; m < 30; m++) {
    for (let f = 0; f < 20; f++) {
      const path = `src/module-${m}/file-${f}.ts`;
      const lines = codeLines(120, `m${m}f${f}`);
      originals.set(path, lines);
      write(join(dir, path), lines.join("\n") + "\n");
    }
  }
  write(join(dir, "data/catalog.json"), JSON.stringify(Array.from({ length: 3000 }, (_, i) => ({ id: i, name: `${word()} ${word()}` })), null, 1));
  write(join(dir, "package-lock.json"), JSON.stringify({ name: "lantern-shop", packages: {} }, null, 2) + "\n");
  git(dir, "add", "-A");
  git(dir, "commit", "-q", "-m", "shop");
  git(dir, "checkout", "-q", "-b", "feature/storefront");

  // 300 modified files, a few lines each; half committed on the branch, half left in the working tree.
  const paths = [...originals.keys()];
  for (let i = 0; i < 300; i++) {
    const path = paths[i * 2]!;
    const lines = [...originals.get(path)!];
    lines.splice(20, 3, `  // storefront change ${i}`, `  const shelf${i} = "${word()}";`, `  const price${i} = ${i};`);
    lines.splice(80, 1, `  const banner${i} = "${word()} ${word()}";`);
    write(join(dir, path), lines.join("\n") + "\n");
    if (i === 149) {
      git(dir, "add", "-A");
      git(dir, "commit", "-q", "-m", "storefront part one");
    }
  }
  // 40 new files (untracked), 10 deleted.
  for (let i = 0; i < 40; i++) write(join(dir, `src/storefront/shelf-${i}.ts`), codeLines(60, `s${i}`).join("\n") + "\n");
  for (let i = 0; i < 10; i++) rmSync(join(dir, paths[i * 2 + 1]!));
  // A big rewrite (thousands of lines), a lockfile and a binary.
  write(join(dir, "data/catalog.json"), JSON.stringify(Array.from({ length: 3000 }, (_, i) => ({ id: i, name: `${word()} ${word()}`, stock: i % 7 })), null, 1));
  const lock: Record<string, unknown> = {};
  for (let i = 0; i < 1500; i++) lock[`node_modules/${word()}-${i}`] = { version: `1.${i}.0`, resolved: `https://registry.example.invalid/${i}.tgz`, integrity: `sha512-${i}` };
  write(join(dir, "package-lock.json"), JSON.stringify({ name: "lantern-shop", packages: lock }, null, 2) + "\n");
  const png = new Uint8Array(200_000);
  for (let i = 0; i < png.length; i++) png[i] = Math.floor(rand() * 256);
  png.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00]);
  write(join(dir, "assets/logo.png"), png);
}

function buildBig(): void {
  const dir = join(proj, "bigrepo");
  if (!existsSync(join(dir, ".git"))) throw new Error(`${dir} is missing: clone a large public repository there first (README.md)`);
  git(dir, "checkout", "-q", "-f", "main");
  git(dir, "clean", "-qfd");
  try {
    git(dir, "branch", "-q", "-D", "bench/changes");
  } catch {
    // first run
  }
  git(dir, "checkout", "-q", "-b", "bench/changes");
  const tracked = git(dir, "ls-files", "--", "*.md").split("\n").filter(Boolean).slice(0, 14);
  tracked.forEach((path, i) => {
    write(join(dir, path), `<!-- bench edit ${i} -->\n` + codeLines(30, `b${i}`).join("\n") + "\n");
    if (i === 6) {
      git(dir, "add", "-A");
      git(dir, "commit", "-q", "-m", "bench part one");
    }
  });
  write(join(dir, "bench-notes/added.md"), codeLines(40, "added").join("\n") + "\n");
}

async function api(path: string, body: unknown): Promise<any> {
  const response = await fetch(`${base}${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  if (!response.ok) throw new Error(`POST ${path}: ${response.status} ${await response.text()}`);
  return response.json();
}

if (process.argv.includes("--repos")) {
  buildSmall();
  buildLarge();
  buildBig();
}

await api("/api/workspace-roots", { path: proj });
const sessions: Record<string, string> = {};
for (const [key, folder, title] of [
  ["small", "tiny-notes", "Tidy the notes"],
  ["large", "lantern-shop", "Build the storefront"],
  ["big", "bigrepo", "Edit the docs"],
] as const) {
  const created = await api("/api/sessions", { directory: join(proj, folder), title });
  sessions[key] = created.session.id;
}
const port = new URL(base).port;
writeFileSync(join(home, `sessions-${port}.json`), JSON.stringify(sessions, null, 2));
console.log(JSON.stringify(sessions));
