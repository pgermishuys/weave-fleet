import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { checkBundledServer, compareMachO, isMachO } from "../scripts/check-bundled-server.mjs";

// A minimal 64-bit Mach-O: header, __TEXT and __LINKEDIT segments, LC_CODE_SIGNATURE, the code, then the signature.
function machO({ code = Buffer.from("the server's code"), padding = 0, signature = Buffer.from("SIG-A") } = {}) {
  const header = 32;
  const segment = 72;
  const sigCmd = 16;
  const cmds = segment * 2 + sigCmd;
  const codeAt = header + cmds;
  const dataoff = codeAt + code.length + padding;
  const buf = Buffer.alloc(dataoff + signature.length);
  buf.writeUInt32LE(0xfeedfacf, 0);
  buf.writeUInt32LE(0x0100000c, 4); // arm64
  buf.writeUInt32LE(6, 12); // dylib
  buf.writeUInt32LE(3, 16);
  buf.writeUInt32LE(cmds, 20);
  let at = header;
  for (const [name, size] of [["__TEXT", codeAt + code.length], ["__LINKEDIT", padding + signature.length]]) {
    buf.writeUInt32LE(0x19, at);
    buf.writeUInt32LE(segment, at + 4);
    buf.write(name, at + 8, "latin1");
    buf.writeBigUInt64LE(BigInt(size), at + 32); // vmsize
    buf.writeBigUInt64LE(BigInt(size), at + 48); // filesize
    at += segment;
  }
  buf.writeUInt32LE(0x1d, at);
  buf.writeUInt32LE(sigCmd, at + 4);
  buf.writeUInt32LE(dataoff, at + 8);
  buf.writeUInt32LE(signature.length, at + 12);
  code.copy(buf, codeAt);
  signature.copy(buf, dataoff);
  return buf;
}

function fat(...images) {
  const head = Buffer.alloc(8 + images.length * 20);
  head.writeUInt32BE(0xcafebabe, 0);
  head.writeUInt32BE(images.length, 4);
  let offset = 4096;
  const parts = [];
  images.forEach((image, i) => {
    head.writeUInt32BE(offset, 8 + i * 20 + 8);
    head.writeUInt32BE(image.length, 8 + i * 20 + 12);
    parts.push({ offset, image });
    offset += Math.ceil(image.length / 4096) * 4096;
  });
  const out = Buffer.alloc(offset);
  head.copy(out, 0);
  for (const { offset: o, image } of parts) image.copy(out, o);
  return out;
}

describe("compareMachO", () => {
  it("sees a re-signed file as the same", () => {
    const resigned = machO({ signature: Buffer.from("a much longer ad-hoc signature from codesign") });
    expect(isMachO(machO())).toBe(true);
    expect(compareMachO(machO(), resigned)).toBeNull();
  });

  it("allows zero padding before a signature codesign placed further on", () => {
    expect(compareMachO(machO(), machO({ padding: 12, signature: Buffer.from("SIG-B") }))).toBeNull();
  });

  it("catches one changed byte of code", () => {
    const changed = machO({ code: Buffer.from("the server's codf") });
    expect(compareMachO(machO(), changed)).toMatch(/differs at byte/);
  });

  it("catches bytes hidden where only padding may be", () => {
    const padded = machO({ padding: 12 });
    padded[32 + 160 + "the server's code".length + 3] = 7;
    expect(compareMachO(machO(), padded)).toMatch(/not just padding/);
  });

  it("compares each architecture of a fat file", () => {
    const resigned = machO({ signature: Buffer.from("SIG-LONGER") });
    expect(compareMachO(fat(machO(), machO()), fat(resigned, resigned))).toBeNull();
    const changed = machO({ code: Buffer.from("the server's cod3") });
    expect(compareMachO(fat(machO(), machO()), fat(resigned, changed))).toMatch(/^slice 1: differs/);
  });
});

describe("checkBundledServer", () => {
  const dirs = [];
  afterEach(() => {
    for (const dir of dirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
  });

  function layout(files) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "bundled-server-"));
    dirs.push(root);
    for (const [name, content] of Object.entries(files)) {
      fs.mkdirSync(path.dirname(path.join(root, name)), { recursive: true });
      fs.writeFileSync(path.join(root, name), content);
    }
    return root;
  }

  const built = {
    "WeaveFleet.Api": machO(),
    "libe_sqlite3.dylib": machO({ code: Buffer.from("sqlite") }),
    "wwwroot/index.html": "<html>",
    "WeaveFleet.Api.dbg": "debug symbols",
  };
  const app = "mac-arm64/Fleet.app/Contents/Resources/fleet";

  it("passes an app that ships the server as built, re-signed Mach-O files included", () => {
    const server = layout(built);
    const release = layout({
      [`${app}/WeaveFleet.Api`]: machO({ signature: Buffer.from("electron-builder ad-hoc signature") }),
      [`${app}/libe_sqlite3.dylib`]: machO({ code: Buffer.from("sqlite"), signature: Buffer.from("SIG-2") }),
      [`${app}/wwwroot/index.html`]: "<html>",
    });
    const { ok, lines } = checkBundledServer(server, release);
    expect(ok).toBe(true);
    expect(lines[0]).toContain("3 of the 3 built files shipped as built (2 re-signed Mach-O files");
  });

  it("fails on a changed, missing or extra file", () => {
    const server = layout(built);
    const release = layout({
      "linux-unpacked/resources/fleet/WeaveFleet.Api": machO({ code: Buffer.from("other server code") }),
      "linux-unpacked/resources/fleet/wwwroot/index.html": "<html>",
      "linux-unpacked/resources/fleet/stray.txt": "?",
    });
    const { ok, lines } = checkBundledServer(server, release);
    expect(ok).toBe(false);
    const text = lines.join("\n");
    expect(text).toMatch(/1 file\(s\) missing: libe_sqlite3\.dylib/);
    expect(text).toMatch(/1 file\(s\) not from the build: stray\.txt/);
    expect(text).toMatch(/1 file\(s\) different: WeaveFleet\.Api \(slice 0: /);
  });

  it("fails when nothing was packaged", () => {
    expect(checkBundledServer(layout(built), layout({ "other/file": "x" })).ok).toBe(false);
  });
});
