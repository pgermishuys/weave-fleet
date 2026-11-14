// Checks the packaged desktop app carries the Fleet server exactly as it was built: every file of the `dotnet publish`
// folder (less the .pdb/.dbg files electron-builder leaves out) is in the app's resources/fleet, byte for byte, and
// nothing else is. The release packages the app from the same publish folder its CLI archive came from and was
// smoke-tested with, so this proves the desktop app ships that very server.
//
// On macOS electron-builder signs the app (ad hoc until there's a Developer ID), which rewrites the signature of every
// Mach-O file inside. Those files are compared with their signatures removed, so everything else in them must still
// match byte for byte.
//
// Usage: node scripts/check-bundled-server.mjs <server-dir> <electron-builder output dir, e.g. release>
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const [serverDir, releaseDir] = process.argv.slice(2);
if (!serverDir || !releaseDir) {
  console.error("usage: node scripts/check-bundled-server.mjs <server-dir> <release-dir>");
  process.exit(2);
}

const skipped = (file) => file.endsWith(".pdb") || file.endsWith(".dbg");

function files(root) {
  const out = [];
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else out.push(path.relative(root, full).split(path.sep).join("/"));
    }
  };
  walk(root);
  return out.sort();
}

const sha256 = (file) => createHash("sha256").update(readFileSync(file)).digest("hex");

// The app's copies of the server: <unpacked>/resources/fleet, or Fleet.app/Contents/Resources/fleet on macOS.
function bundledServers(root) {
  const found = [];
  const walk = (dir, depth) => {
    if (depth > 5) return;
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const full = path.join(dir, entry.name);
      if (entry.name === "fleet" && /^resources$/i.test(path.basename(dir))) found.push(full);
      else walk(full, depth + 1);
    }
  };
  walk(root, 0);
  return found;
}

const isMachO = (file) => {
  const head = readFileSync(file).subarray(0, 4).readUInt32BE(0);
  return [0xfeedface, 0xfeedfacf, 0xcefaedfe, 0xcffaedfe, 0xcafebabe].includes(head);
};

function unsignedHash(file, scratch) {
  const copy = path.join(scratch, `${Math.random().toString(36).slice(2)}-${path.basename(file)}`);
  copyFileSync(file, copy);
  try {
    execFileSync("codesign", ["--remove-signature", copy], { stdio: "pipe" });
  } catch {
    // Not signed at all: nothing to remove.
  }
  return sha256(copy);
}

const built = files(serverDir).filter((f) => !skipped(f));
const copies = bundledServers(releaseDir);
if (copies.length === 0) {
  console.error(`No resources/fleet folder under ${releaseDir}: was the app packaged?`);
  process.exit(1);
}

let failed = false;
const scratch = mkdtempSync(path.join(tmpdir(), "bundled-server-"));
try {
  for (const copy of copies) {
    const shipped = files(copy);
    const missing = built.filter((f) => !shipped.includes(f));
    const extra = shipped.filter((f) => !built.includes(f));
    const differ = [];
    let resigned = 0;
    for (const file of built.filter((f) => shipped.includes(f))) {
      const a = path.join(serverDir, file);
      const b = path.join(copy, file);
      if (statSync(a).size === statSync(b).size && sha256(a) === sha256(b)) continue;
      if (process.platform === "darwin" && isMachO(a) && unsignedHash(a, scratch) === unsignedHash(b, scratch)) {
        resigned++;
        continue;
      }
      differ.push(file);
    }
    const label = path.relative(releaseDir, copy);
    const same = built.length - missing.length - differ.length;
    console.log(`${label}: ${same} of the ${built.length} built files shipped as built` +
      (resigned ? ` (${resigned} of them re-signed, the same once the signature is removed)` : ""));
    for (const [what, list] of [["missing", missing], ["not from the build", extra], ["different", differ]]) {
      if (list.length === 0) continue;
      failed = true;
      console.error(`::error::${label}: ${list.length} file(s) ${what}: ${list.slice(0, 20).join(", ")}`);
    }
  }
  const server = built.find((f) => /^WeaveFleet\.Api(\.exe)?$/.test(f));
  if (server) console.log(`Server binary ${server}: sha256 ${sha256(path.join(serverDir, server))}`);
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
process.exit(failed ? 1 : 0);
