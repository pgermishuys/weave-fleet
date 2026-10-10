import { mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterAll } from "bun:test";
import { checkMod, type CheckResult } from "../../src/check";

const made: string[] = [];
afterAll(async () => {
  for (const dir of made.splice(0)) await rm(dir, { recursive: true, force: true });
});

export interface ModSpec {
  name?: string;
  /** The hooks module's source. */
  source?: string;
  file?: string;
  manifest?: Record<string, unknown> | string;
  /** Extra files, relative to the mod folder. */
  files?: Record<string, string>;
  links?: Record<string, string>;
}

/** Writes a mod to a temp dir (folder named after it), checks it, and returns the result. */
export async function check(spec: ModSpec = {}, limits?: Parameters<typeof checkMod>[2]): Promise<CheckResult & { root: string }> {
  const name = spec.name ?? "demo-mod";
  const file = spec.file ?? "mod.js";
  const parent = await mkdtemp(join(tmpdir(), "mods-check-"));
  made.push(parent);
  const root = join(parent, name);
  await mkdir(root, { recursive: true });
  const manifest =
    typeof spec.manifest === "string"
      ? spec.manifest
      : JSON.stringify({ name, version: "0.1.0", description: "A demo", hooks: file, ...spec.manifest });
  await writeFile(join(root, "mod.json"), manifest);
  if (spec.source !== undefined) {
    await mkdir(dirname(join(root, file)), { recursive: true });
    await writeFile(join(root, file), spec.source);
  }
  for (const [path, body] of Object.entries(spec.files ?? {})) {
    await mkdir(dirname(join(root, path)), { recursive: true });
    await writeFile(join(root, path), body);
  }
  for (const [path, target] of Object.entries(spec.links ?? {})) await symlink(target, join(root, path));
  return { ...(await checkMod(root, undefined, limits)), root };
}

/** Wraps hook bodies in a register that takes `on`. */
export const reg = (body: string, extra = "") => `${extra}\nexport function register(on) {\n${body}\n}\n`;

export const codes = (r: CheckResult) => r.report.errors.map((e) => e.code);
