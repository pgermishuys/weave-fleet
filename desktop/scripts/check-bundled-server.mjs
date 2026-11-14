// Checks the packaged desktop app carries the Fleet server exactly as it was built: every file of the `dotnet publish`
// folder (less the .pdb/.dbg files electron-builder leaves out) is in the app's resources/fleet, byte for byte, and
// nothing else is. The release packages the app from the same publish folder its CLI archive came from and was
// smoke-tested with, so this proves the desktop app ships that very server.
//
// On macOS electron-builder signs the app (ad hoc until there's a Developer ID), which replaces the code signature of
// every Mach-O file inside (the server and its .dylibs). For those, every byte before the code signature must match,
// except the four fields signing rewrites: the signature's offset and size (LC_CODE_SIGNATURE) and __LINKEDIT's sizes,
// which grow with the signature. Anything the signature follows is code and data, and that must be identical.
//
// Usage: node scripts/check-bundled-server.mjs <server-dir> <electron-builder output dir, e.g. release>
import { createHash } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const LC_SEGMENT = 0x1;
const LC_SEGMENT_64 = 0x19;
const LC_CODE_SIGNATURE = 0x1d;
const MH_MAGIC = 0xfeedface;
const MH_MAGIC_64 = 0xfeedfacf;
const FAT_MAGIC = 0xcafebabe;

/** Whether a buffer starts like a (thin little-endian or fat) Mach-O file. */
export function isMachO(buf) {
  if (buf.length < 8) return false;
  const le = buf.readUInt32LE(0);
  return le === MH_MAGIC || le === MH_MAGIC_64 || buf.readUInt32BE(0) === FAT_MAGIC;
}

/**
 * A thin Mach-O image cut at its code signature, with the fields signing rewrites zeroed. Throws on what it can't read.
 * @returns {{ bytes: Buffer, signed: boolean }}
 */
function maskThin(image) {
  const magic = image.readUInt32LE(0);
  if (magic !== MH_MAGIC && magic !== MH_MAGIC_64) throw new Error("not a little-endian Mach-O image");
  const is64 = magic === MH_MAGIC_64;
  const ncmds = image.readUInt32LE(16);
  let offset = is64 ? 32 : 28;
  let end = image.length;
  let signed = false;
  const masked = Buffer.from(image);
  for (let i = 0; i < ncmds; i++) {
    const cmd = image.readUInt32LE(offset);
    const size = image.readUInt32LE(offset + 4);
    if (size < 8 || offset + size > image.length) throw new Error("load command runs past the file");
    if (cmd === LC_CODE_SIGNATURE) {
      signed = true;
      end = image.readUInt32LE(offset + 8);
      masked.fill(0, offset + 8, offset + 16);
    } else if (cmd === LC_SEGMENT_64 || cmd === LC_SEGMENT) {
      const name = image.subarray(offset + 8, offset + 24).toString("latin1").replace(/\0+$/, "");
      if (name === "__LINKEDIT") {
        if (cmd === LC_SEGMENT_64) {
          masked.fill(0, offset + 32, offset + 40); // vmsize
          masked.fill(0, offset + 48, offset + 56); // filesize
        } else {
          masked.fill(0, offset + 28, offset + 32);
          masked.fill(0, offset + 36, offset + 40);
        }
      }
    }
    offset += size;
  }
  return { bytes: masked.subarray(0, end), signed };
}

/** The thin images of a Mach-O file: itself, or each architecture of a fat (universal) file. */
function slices(buf) {
  if (buf.readUInt32BE(0) !== FAT_MAGIC) return [buf];
  const count = buf.readUInt32BE(4);
  const out = [];
  for (let i = 0; i < count; i++) {
    const entry = 8 + i * 20;
    const at = buf.readUInt32BE(entry + 8);
    out.push(buf.subarray(at, at + buf.readUInt32BE(entry + 12)));
  }
  return out;
}

/**
 * Compares two Mach-O files ignoring only their code signatures (and the size fields that change with them).
 * @returns {string | null} null when they're the same, else what differs
 */
export function compareMachO(built, shipped) {
  const a = slices(built);
  const b = slices(shipped);
  if (a.length !== b.length) return `${a.length} architecture(s) built, ${b.length} shipped`;
  for (let i = 0; i < a.length; i++) {
    let x, y;
    try {
      x = maskThin(a[i]);
      y = maskThin(b[i]);
    } catch (error) {
      return `slice ${i}: ${error.message}`;
    }
    const n = Math.min(x.bytes.length, y.bytes.length);
    const first = x.bytes.subarray(0, n).findIndex((byte, k) => byte !== y.bytes[k]);
    if (first !== -1) return `slice ${i}: differs at byte ${first} (0x${first.toString(16)})`;
    // codesign may align the signature differently; anything between must be padding.
    const longer = x.bytes.length > y.bytes.length ? x.bytes : y.bytes;
    if (longer.subarray(n).some((byte) => byte !== 0)) {
      return `slice ${i}: ${x.bytes.length} bytes before the signature built, ${y.bytes.length} shipped, and not just padding`;
    }
  }
  return null;
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

const sha256 = (buf) => createHash("sha256").update(buf).digest("hex");

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

/**
 * Checks every copy of the server under releaseDir against serverDir.
 * @returns {{ ok: boolean, lines: string[] }}
 */
export function checkBundledServer(serverDir, releaseDir) {
  const lines = [];
  let ok = true;
  const built = files(serverDir).filter((f) => !skipped(f));
  const copies = bundledServers(releaseDir);
  if (copies.length === 0) {
    return { ok: false, lines: [`::error::No resources/fleet folder under ${releaseDir}: was the app packaged?`] };
  }
  for (const copy of copies) {
    const shipped = files(copy);
    const missing = built.filter((f) => !shipped.includes(f));
    const extra = shipped.filter((f) => !built.includes(f));
    const differ = [];
    let resigned = 0;
    for (const file of built.filter((f) => shipped.includes(f))) {
      const a = path.join(serverDir, file);
      const b = path.join(copy, file);
      const [x, y] = [readFileSync(a), readFileSync(b)];
      if (x.equals(y)) continue;
      if (isMachO(x) && isMachO(y)) {
        const why = compareMachO(x, y);
        if (why === null) {
          resigned++;
          continue;
        }
        differ.push(`${file} (${why})`);
        continue;
      }
      differ.push(file);
    }
    const label = path.relative(releaseDir, copy);
    const same = built.length - missing.length - differ.length;
    lines.push(`${label}: ${same} of the ${built.length} built files shipped as built` +
      (resigned ? ` (${resigned} re-signed Mach-O files, identical apart from their code signatures)` : ""));
    for (const [what, list] of [["missing", missing], ["not from the build", extra], ["different", differ]]) {
      if (list.length === 0) continue;
      ok = false;
      lines.push(`::error::${label}: ${list.length} file(s) ${what}: ${list.slice(0, 20).join(", ")}`);
    }
  }
  const server = built.find((f) => /^WeaveFleet\.Api(\.exe)?$/.test(f));
  if (server) lines.push(`Server binary ${server}: sha256 ${sha256(readFileSync(path.join(serverDir, server)))}`);
  return { ok, lines };
}

// pathToFileURL, not `file://${argv[1]}`: on Windows that would never match, and the check would silently not run.
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [serverDir, releaseDir] = process.argv.slice(2);
  if (!serverDir || !releaseDir) {
    console.error("usage: node scripts/check-bundled-server.mjs <server-dir> <release-dir>");
    process.exit(2);
  }
  const { ok, lines } = checkBundledServer(serverDir, releaseDir);
  for (const line of lines) (line.startsWith("::error::") ? console.error : console.log)(line);
  process.exit(ok ? 0 : 1);
}
