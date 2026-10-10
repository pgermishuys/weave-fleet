import { readFile, realpath, stat } from "node:fs/promises";
import { basename, dirname, isAbsolute, relative, resolve } from "node:path";
import type { CheckProblem } from "fleet-mods/protocol";
import type { Manifest } from "./index";
import { MOD_NAME } from "../ids";

const KNOWN = new Set(["name", "version", "description", "hooks"]);
const EXTENSIONS = [".js", ".mjs", ".ts", ".mts"];

export interface ManifestResult {
  manifest?: Manifest;
  /** The hooks module, absolute. Present with `manifest`. */
  modulePath?: string;
  /** What was read of the manifest even if it is unusable. */
  partial: { name: string; version: string; description: string };
  errors: CheckProblem[];
  warnings: CheckProblem[];
}

const inside = (base: string, path: string) => {
  const r = relative(base, path);
  return r === "" || (!r.startsWith("..") && !isAbsolute(r));
};

/** Reads and validates mod.json for the mod in `root`. */
export async function readManifest(root: string, manifestPath: string): Promise<ManifestResult> {
  const errors: CheckProblem[] = [];
  const warnings: CheckProblem[] = [];
  const bad = (message: string) => errors.push({ code: "manifest", message });
  const partial = { name: "", version: "", description: "" };
  const done = (): ManifestResult => ({ partial, errors, warnings });

  const file = resolve(root, manifestPath);
  let text: string;
  try {
    text = await readFile(file, "utf8");
  } catch {
    bad(`mod.json not found at ${file}`);
    return done();
  }
  let data: unknown;
  try {
    data = JSON.parse(text);
  } catch (error) {
    bad(`mod.json isn't valid JSON: ${(error as Error).message}`);
    return done();
  }
  if (typeof data !== "object" || data === null || Array.isArray(data)) {
    bad("mod.json must be a JSON object");
    return done();
  }
  const m = data as Record<string, unknown>;
  if (typeof m.name === "string") partial.name = m.name;
  if (typeof m.version === "string") partial.version = m.version;
  if (typeof m.description === "string") partial.description = m.description;

  for (const key of Object.keys(m)) {
    if (!KNOWN.has(key)) warnings.push({ code: "manifest-field", message: `unknown field "${key}" in mod.json` });
  }

  const name = m.name;
  if (typeof name !== "string") bad('"name" is required and must be a string');
  else if (!MOD_NAME.test(name)) bad(`"name" must be lowercase letters, digits and "-", 1 to 64, starting with a letter: ${JSON.stringify(name)}`);
  else if (name.startsWith("fleet-")) bad('"name" can\'t start with "fleet-": that prefix is reserved');
  else {
    const folder = basename(resolve(root));
    const kept = /^v\d+$/.test(folder) && basename(dirname(resolve(root))) === name;
    if (folder !== name && !kept) bad(`"name" is ${JSON.stringify(name)} but the folder is ${JSON.stringify(folder)}: they must match`);
  }

  if (typeof m.version !== "string" || m.version.length === 0 || m.version.length > 64) bad('"version" is required: a string of 1 to 64 characters');

  const description = m.description;
  if (typeof description !== "string" || description.trim().length === 0 || description.length > 200) {
    bad('"description" is required: one line of 1 to 200 characters');
  } else if (/[\r\n\u2028\u2029]/.test(description)) {
    bad('"description" must be one line');
  }

  let modulePath: string | undefined;
  const hooks = m.hooks;
  if (typeof hooks !== "string" || hooks.length === 0) {
    bad('"hooks" is required: the path of the hooks module');
  } else if (isAbsolute(hooks) || /^[a-zA-Z]:[\\/]/.test(hooks)) {
    bad('"hooks" must be a path relative to mod.json');
  } else if (!EXTENSIONS.includes(hooks.slice(hooks.lastIndexOf(".")).toLowerCase()) || hooks.lastIndexOf(".") < 0) {
    bad('"hooks" must be a .js, .mjs, .ts or .mts file');
  } else {
    const full = resolve(dirname(file), hooks);
    if (!inside(resolve(root), full)) {
      bad('"hooks" must stay inside the mod\'s folder');
    } else {
      try {
        const real = await realpath(full);
        if (!inside(await realpath(root), real)) bad('"hooks" must stay inside the mod\'s folder (it is a link to outside)');
        else if (!(await stat(real)).isFile()) bad(`"hooks" ${JSON.stringify(hooks)} isn't a file`);
        else modulePath = full;
      } catch {
        bad(`"hooks" file ${JSON.stringify(hooks)} doesn't exist`);
      }
    }
  }

  if (errors.length > 0 || modulePath === undefined) return done();
  return {
    manifest: { name: m.name as string, version: m.version as string, description: description as string, hooks: hooks as string },
    modulePath,
    partial,
    errors,
    warnings,
  };
}
