import { tmpdir } from "os";
import { join } from "path";
import { mkdirSync, writeFileSync, rmSync, existsSync } from "fs";
import { randomUUID } from "crypto";
import { detectProject } from "../detect-project";

describe("detectProject", () => {
  let testDir: string;

  beforeEach(() => {
    testDir = join(tmpdir(), `detect-project-test-${randomUUID()}`);
    mkdirSync(testDir, { recursive: true });
  });

  afterEach(() => {
    if (existsSync(testDir)) {
      rmSync(testDir, { recursive: true, force: true });
    }
  });

  it("DetectsCSharpProject", () => {
    writeFileSync(join(testDir, "MyApp.csproj"), "<Project></Project>");
    mkdirSync(join(testDir, ".git"));

    const profile = detectProject(testDir);

    expect(profile.languages).toContain("csharp");
    expect(profile.frameworks).toContain("dotnet");
    expect(profile.suggestedSkills).toContain("enforcing-csharp-standards");
    expect(profile.suggestedSkills).toContain("enforcing-dotnet-testing");
    expect(profile.suggestedSkills).toContain("reviewing-csharp-code");
    expect(profile.suggestedSkills).toContain("verifying-release-builds");
    expect(profile.suggestedSkills).toContain("managing-pull-requests");
    expect(profile.isGitRepo).toBe(true);
  });

  it("DetectsSolutionFile", () => {
    writeFileSync(join(testDir, "MyApp.sln"), "");

    const profile = detectProject(testDir);

    expect(profile.languages).toContain("csharp");
    expect(profile.frameworks).toContain("dotnet");
  });

  it("DetectsTypeScriptNodeProject", () => {
    writeFileSync(join(testDir, "package.json"), "{}");
    writeFileSync(join(testDir, "tsconfig.json"), "{}");

    const profile = detectProject(testDir);

    expect(profile.languages).toContain("typescript");
    expect(profile.languages).toContain("javascript");
    expect(profile.frameworks).toContain("nodejs");
  });

  it("DetectsNextJsProject", () => {
    writeFileSync(join(testDir, "package.json"), "{}");
    writeFileSync(join(testDir, "next.config.ts"), "");

    const profile = detectProject(testDir);

    expect(profile.languages).toContain("typescript");
    expect(profile.frameworks).toContain("nextjs");
  });

  it("DetectsGoProject", () => {
    writeFileSync(join(testDir, "go.mod"), "module example.com/myapp");

    const profile = detectProject(testDir);

    expect(profile.languages).toContain("go");
  });

  it("DetectsRustProject", () => {
    writeFileSync(join(testDir, "Cargo.toml"), "[package]");

    const profile = detectProject(testDir);

    expect(profile.languages).toContain("rust");
  });

  it("DetectsPythonProject", () => {
    writeFileSync(join(testDir, "pyproject.toml"), "[project]");

    const profile = detectProject(testDir);

    expect(profile.languages).toContain("python");
  });

  it("DetectsGitRepo", () => {
    mkdirSync(join(testDir, ".git"));

    const profile = detectProject(testDir);

    expect(profile.isGitRepo).toBe(true);
    expect(profile.suggestedSkills).toContain("managing-pull-requests");
  });

  it("HandlesEmptyDirectory", () => {
    const profile = detectProject(testDir);

    expect(profile.languages).toEqual([]);
    expect(profile.frameworks).toEqual([]);
    expect(profile.suggestedSkills).toEqual([]);
    expect(profile.isGitRepo).toBe(false);
  });

  it("DetectsMultiLanguageProject", () => {
    writeFileSync(join(testDir, "MyApp.csproj"), "<Project></Project>");
    writeFileSync(join(testDir, "package.json"), "{}");
    writeFileSync(join(testDir, "tsconfig.json"), "{}");
    mkdirSync(join(testDir, ".git"));

    const profile = detectProject(testDir);

    expect(profile.languages).toContain("csharp");
    expect(profile.languages).toContain("typescript");
    expect(profile.languages).toContain("javascript");
    expect(profile.isGitRepo).toBe(true);
  });

  it("ThrowsForNonExistentDirectory", () => {
    expect(() => detectProject("/non/existent/path")).toThrow(
      "Directory does not exist"
    );
  });

  it("ThrowsForFilePath", () => {
    const filePath = join(testDir, "somefile.txt");
    writeFileSync(filePath, "");

    expect(() => detectProject(filePath)).toThrow("Not a directory");
  });
});
