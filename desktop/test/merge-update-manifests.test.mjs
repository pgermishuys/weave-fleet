import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { mergeManifestFiles, mergeManifests } from "../scripts/merge-update-manifests.mjs";

const x64 = {
  version: "0.25.0",
  files: [{ url: "Fleet-0.25.0-win-x64-setup.exe", sha512: "aaa", size: 100 }],
  path: "Fleet-0.25.0-win-x64-setup.exe",
  sha512: "aaa",
  releaseDate: "2026-09-15T08:00:00.000Z",
};
const arm64 = {
  version: "0.25.0",
  files: [{ url: "Fleet-0.25.0-win-arm64-setup.exe", sha512: "bbb", size: 90 }],
  path: "Fleet-0.25.0-win-arm64-setup.exe",
  sha512: "bbb",
  releaseDate: "2026-09-15T08:05:00.000Z",
};

describe("merging the Windows update feeds", () => {
  it("lists both installers, keeping x64 as the default", () => {
    const merged = mergeManifests([x64, arm64]);
    expect(merged.files.map((f) => f.url)).toEqual(["Fleet-0.25.0-win-x64-setup.exe", "Fleet-0.25.0-win-arm64-setup.exe"]);
    expect(merged.path).toBe("Fleet-0.25.0-win-x64-setup.exe");
    expect(merged.releaseDate).toBe("2026-09-15T08:05:00.000Z");
  });

  it("refuses feeds for different versions", () => {
    expect(() => mergeManifests([x64, { ...arm64, version: "0.24.0" }])).toThrow(/different versions/);
  });

  it("refuses two different files with the same name", () => {
    expect(() => mergeManifests([x64, { ...x64, files: [{ ...x64.files[0], sha512: "zzz" }] }])).toThrow(/both called/);
  });

  it("reads and writes the YAML electron-builder produces", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "fleet-manifests-"));
    const write = (name, body) => fs.writeFileSync(path.join(dir, name), body);
    write("x64.yml", "version: 0.25.0\nfiles:\n  - url: Fleet-0.25.0-win-x64-setup.exe\n    sha512: aaa\n    size: 100\npath: Fleet-0.25.0-win-x64-setup.exe\nsha512: aaa\nreleaseDate: '2026-09-15T08:00:00.000Z'\n");
    write("arm64.yml", "version: 0.25.0\nfiles:\n  - url: Fleet-0.25.0-win-arm64-setup.exe\n    sha512: bbb\n    size: 90\npath: Fleet-0.25.0-win-arm64-setup.exe\nsha512: bbb\nreleaseDate: '2026-09-15T08:05:00.000Z'\n");
    mergeManifestFiles(path.join(dir, "latest.yml"), [path.join(dir, "x64.yml"), path.join(dir, "arm64.yml")]);
    const text = fs.readFileSync(path.join(dir, "latest.yml"), "utf8");
    expect(text).toContain("url: Fleet-0.25.0-win-arm64-setup.exe");
    expect(text).toContain("version: 0.25.0");
    fs.rmSync(dir, { recursive: true });
  });
});
