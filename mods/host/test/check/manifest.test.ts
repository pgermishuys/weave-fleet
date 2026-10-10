import { describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { checkMod } from "../../src/check";
import { check, codes, reg } from "./helpers";

const ok = reg(`on("session.start", ($, e, next) => next(e));`);

describe("manifest", () => {
  test("a valid manifest passes and is returned", async () => {
    const r = await check({ source: ok });
    expect(r.report.ok).toBe(true);
    expect(r.manifest).toEqual({ name: "demo-mod", version: "0.1.0", description: "A demo", hooks: "mod.js" });
    expect(r.modulePath?.endsWith("/demo-mod/mod.js")).toBe(true);
    expect(r.js).toBe(ok);
  });

  test("a missing mod.json is a manifest error with an empty report", async () => {
    const dir = await mkdtemp(join(tmpdir(), "mods-check-"));
    const r = await checkMod(dir);
    expect(r.report.ok).toBe(false);
    expect(r.report.errors.map((e) => e.code)).toEqual(["manifest"]);
    expect(r.report).toMatchObject({ name: "", version: "", description: "", lines: 0, sha256: "", hooks: [] });
    expect(r.js).toBeUndefined();
  });

  test("mod.json that isn't JSON is a manifest error", async () => {
    const r = await check({ manifest: "{ nope", source: ok });
    expect(codes(r)).toEqual(["manifest"]);
  });

  test("mod.json that isn't an object is a manifest error", async () => {
    expect(codes(await check({ manifest: "[1]", source: ok }))).toEqual(["manifest"]);
    expect(codes(await check({ manifest: "null", source: ok }))).toEqual(["manifest"]);
  });

  test("the manifest path may be given relative to the root or absolute", async () => {
    const r = await check({ source: ok });
    expect((await checkMod(r.root, "mod.json")).report.ok).toBe(true);
    expect((await checkMod(r.root, join(r.root, "mod.json"))).report.ok).toBe(true);
    expect((await checkMod(r.root, "other.json")).report.errors[0]?.code).toBe("manifest");
  });

  test.each([
    ["uppercase", "Demo"],
    ["leading digit", "1demo"],
    ["underscore", "de_mo"],
    ["too long", "a".repeat(65)],
    ["empty", ""],
  ])("a name that is %s is refused", async (_, name) => {
    const r = await check({ name: name || "x", manifest: { name }, source: ok });
    expect(codes(r)).toContain("manifest");
  });

  test("a name of 64 characters is fine", async () => {
    expect((await check({ name: "a".repeat(64), source: ok })).report.ok).toBe(true);
  });

  test("the fleet- prefix is reserved", async () => {
    const r = await check({ name: "fleet-chips", source: ok });
    expect(r.report.errors[0]).toMatchObject({ code: "manifest" });
    expect(r.report.errors[0]?.message).toContain("fleet-");
  });

  test("the name must match the folder", async () => {
    const r = await check({ name: "demo-mod", manifest: { name: "other-mod" }, source: ok });
    expect(codes(r)).toEqual(["manifest"]);
    expect(r.report.errors[0]?.message).toContain("folder");
  });

  test("a kept version folder v3 matches its parent's name", async () => {
    const parent = await mkdtemp(join(tmpdir(), "mods-check-"));
    await mkdir(join(parent, "demo-mod", "v3"), { recursive: true });
    const root = join(parent, "demo-mod", "v3");
    await writeFile(join(root, "mod.json"), JSON.stringify({ name: "demo-mod", version: "1", description: "d", hooks: "mod.js" }));
    await writeFile(join(root, "mod.js"), ok);
    expect((await checkMod(root)).report.ok).toBe(true);
    await writeFile(join(root, "mod.json"), JSON.stringify({ name: "other", version: "1", description: "d", hooks: "mod.js" }));
    expect((await checkMod(root)).report.errors[0]?.code).toBe("manifest");
  });

  test.each([
    ["missing", undefined],
    ["empty", ""],
    ["not a string", 3],
    ["over 64 characters", "v".repeat(65)],
  ])("a version that is %s is refused", async (_, version) => {
    const r = await check({ manifest: { version }, source: ok });
    expect(codes(r)).toEqual(["manifest"]);
  });

  test.each([
    ["missing", undefined],
    ["empty", ""],
    ["two lines", "a\nb"],
    ["over 200 characters", "d".repeat(201)],
    ["not a string", {}],
  ])("a description that is %s is refused", async (_, description) => {
    expect(codes(await check({ manifest: { description }, source: ok }))).toEqual(["manifest"]);
  });

  test("a description of 200 characters is fine", async () => {
    expect((await check({ manifest: { description: "d".repeat(200) }, source: ok })).report.ok).toBe(true);
  });

  test("unusable manifests still report what they could read", async () => {
    const r = await check({ manifest: { version: "" }, source: ok });
    expect(r.report).toMatchObject({ name: "demo-mod", version: "", description: "A demo", lines: 0, sha256: "" });
  });

  test.each([
    ["absolute", "/etc/passwd.js"],
    ["parent traversal", "../x.js"],
    ["traversal through a subfolder", "sub/../../x.js"],
    ["a bad extension", "mod.txt"],
    ["no extension", "mod"],
    ["a missing file", "nope.js"],
    ["a non-string", 7],
  ])("hooks with %s is refused", async (_, hooks) => {
    expect(codes(await check({ manifest: { hooks }, source: ok }))).toEqual(["manifest"]);
  });

  test("hooks that is a directory is refused", async () => {
    const r = await check({ manifest: { hooks: "dir.js" }, source: ok, files: { "dir.js/x": "" } });
    expect(codes(r)).toEqual(["manifest"]);
  });

  test("hooks may be ./-prefixed, nested, and use any of the four extensions", async () => {
    for (const file of ["./mod.js", "src/mod.mjs", "mod.ts", "mod.mts"]) {
      expect((await check({ file, source: ok })).report.ok).toBe(true);
    }
  });

  test("hooks that is a symlink pointing outside the folder is refused", async () => {
    const outside = await mkdtemp(join(tmpdir(), "mods-outside-"));
    await writeFile(join(outside, "evil.js"), ok);
    const r = await check({ manifest: { hooks: "link.js" }, links: { "link.js": join(outside, "evil.js") }, source: ok });
    expect(codes(r)).toEqual(["manifest"]);
    expect(r.report.errors[0]?.message).toContain("inside");
  });

  test("hooks that is a symlink to a file inside the folder is fine", async () => {
    const r = await check({ manifest: { hooks: "link.js" }, links: { "link.js": "mod.js" }, source: ok });
    expect(r.report.ok).toBe(true);
  });

  test("a symlinked parent folder pointing outside is refused", async () => {
    const outside = await mkdtemp(join(tmpdir(), "mods-outside-"));
    await writeFile(join(outside, "evil.js"), ok);
    const r = await check({ manifest: { hooks: "sub/evil.js" }, links: { sub: outside }, source: ok });
    expect(codes(r)).toEqual(["manifest"]);
  });

  test("unknown fields are a manifest-field warning, not an error", async () => {
    const r = await check({ manifest: { options: {}, extra: 1 }, source: ok });
    expect(r.report.ok).toBe(true);
    expect(r.report.warnings.map((w) => w.code)).toEqual(["manifest-field", "manifest-field"]);
    expect(r.report.warnings[0]?.message).toContain("options");
  });
});
