// Merges the Windows x64 and arm64 update feeds into the one latest.yml electron-updater reads on Windows. Each
// build writes its own latest.yml; the updater picks the file whose name has the machine's architecture in it.
//
//   node scripts/merge-update-manifests.mjs <output.yml> <input.yml>...
import fs from "node:fs";
import { createRequire } from "node:module";
const yaml = createRequire(import.meta.url)("js-yaml");

export function mergeManifests(manifests) {
  const [first, ...rest] = manifests;
  if (!first) throw new Error("Nothing to merge.");
  const files = new Map();
  for (const manifest of manifests) {
    if (manifest.version !== first.version) {
      throw new Error(`Can't merge update feeds for different versions (${first.version} and ${manifest.version}).`);
    }
    for (const file of manifest.files ?? []) {
      const existing = files.get(file.url);
      if (existing && (existing.sha512 !== file.sha512 || existing.size !== file.size)) {
        throw new Error(`Two different files are both called ${file.url}.`);
      }
      files.set(file.url, file);
    }
  }
  const releaseDate = [first, ...rest].map((m) => m.releaseDate).filter(Boolean).sort().at(-1);
  return { ...first, files: [...files.values()], ...(releaseDate ? { releaseDate } : {}) };
}

export function mergeManifestFiles(output, inputs) {
  const merged = mergeManifests(inputs.map((input) => yaml.load(fs.readFileSync(input, "utf8"))));
  fs.writeFileSync(output, yaml.dump(merged, { lineWidth: -1 }));
  return merged;
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href) {
  const [output, ...inputs] = process.argv.slice(2);
  if (!output || inputs.length === 0) {
    console.error("usage: node scripts/merge-update-manifests.mjs <output.yml> <input.yml>...");
    process.exit(2);
  }
  const merged = mergeManifestFiles(output, inputs);
  console.log(`${output}: ${merged.version}, ${merged.files.map((f) => f.url).join(", ")}`);
}
