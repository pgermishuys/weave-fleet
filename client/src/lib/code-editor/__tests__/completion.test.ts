import { CompletionContext } from "@codemirror/autocomplete";
import { EditorState } from "@codemirror/state";
import { describe, expect, it, vi } from "vitest";
import { importFolder, importPathSource } from "../completion";
import { languageFor } from "../languages";

describe("importFolder", () => {
  it("resolves ./ and ../ against the file's folder", () => {
    expect(importFolder("src/components/Foo.vue", "./")).toBe("src/components/");
    expect(importFolder("src/components/Foo.vue", "../lib/")).toBe("src/lib/");
    expect(importFolder("app.ts", "./")).toBe("");
  });

  it("maps @/ to the src folder the file is in", () => {
    expect(importFolder("client/src/components/Foo.vue", "@/lib/")).toBe("client/src/lib/");
    expect(importFolder("src/main.ts", "@/")).toBe("src/");
  });

  it("doesn't leave the repo or complete packages", () => {
    expect(importFolder("app.ts", "../")).toBeNull();
    expect(importFolder("src/app.ts", "vue/")).toBeNull();
  });
});

describe("importPathSource", () => {
  async function complete(doc: string, listing: string[]) {
    const listFolder = vi.fn(async () => listing);
    const state = EditorState.create({ doc });
    const source = importPathSource("src/components/Foo.ts", listFolder);
    const result = await source(new CompletionContext(state, doc.length, false));
    return { result, listFolder };
  }

  it("lists the typed folder and replaces only the last segment", async () => {
    const doc = 'import { x } from "../lib/fo';
    const { result, listFolder } = await complete(doc, ["src/lib/format.ts", "src/lib/types.d.ts", "src/lib/nested/"]);
    expect(listFolder).toHaveBeenCalledWith("src/lib/");
    expect(result!.from).toBe(doc.length - 2);
    expect(result!.options.map((option) => option.label)).toEqual(["format", "types", "nested/"]);
  });

  it("also works in import() and require()", async () => {
    expect((await complete('const m = await import("./', ["src/components/Bar.ts"])).result!.options[0].label).toBe("Bar");
    expect((await complete("const m = require('./", ["src/components/Bar.js"])).result!.options[0].label).toBe("Bar");
  });

  it("leaves out the file itself", async () => {
    const { result } = await complete('import a from "./', ["src/components/Foo.ts", "src/components/Bar.ts"]);
    expect(result!.options.map((option) => option.label)).toEqual(["Bar"]);
  });

  it("stays out of the way outside an import, and before the first slash", async () => {
    expect((await complete('const s = "./', [])).result).toBeNull();
    expect((await complete('import a from ".', [])).result).toBeNull();
    expect((await complete('import a from "vu', [])).result).toBeNull();
  });
});

describe("languageFor", () => {
  it.each([
    ["src/app.ts", "TypeScript"],
    ["src/App.vue", "Vue"],
    ["src/Program.cs", "C#"],
    ["WeaveFleet.Api.csproj", "XML"],
    ["Directory.Build.props", "XML"],
    ["main.go", "Go"],
    ["script.py", "Python"],
    ["lib.rs", "Rust"],
    ["README.md", "Markdown"],
    ["config.yml", "YAML"],
    ["Dockerfile", "Dockerfile"],
    [".env.local", "Properties files"],
  ])("%s is %s", (path, name) => {
    expect(languageFor(path)?.name).toBe(name);
  });

  it("is null for plain text", () => {
    expect(languageFor("notes.txt")).toBeNull();
    expect(languageFor("LICENSE")).toBeNull();
  });
});
