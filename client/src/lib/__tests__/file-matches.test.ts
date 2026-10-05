import { describe, expect, it } from "vitest";
import { narrowFileMatches } from "@/lib/file-matches";

describe("narrowFileMatches", () => {
  const entries = ["src/", "src/components/", "src/components/Composer.vue", "src/app.ts", "docs/composer.md", "README.md"];

  it("ranks like the server: name is the query, starts with it, contains it, then the path, then fuzzy", () => {
    expect(narrowFileMatches(entries, "composer")).toEqual(["docs/composer.md", "src/components/Composer.vue"]);
    expect(narrowFileMatches(entries, "comp")).toEqual(["src/components/", "docs/composer.md", "src/components/Composer.vue"]);
    expect(narrowFileMatches(entries, "cmpsr")).toEqual(["docs/composer.md", "src/components/Composer.vue"]);
    expect(narrowFileMatches(entries, "xyz")).toEqual([]);
  });

  it("matches the whole path once the query has a slash", () => {
    expect(narrowFileMatches(entries, "src/comp")).toEqual(["src/components/", "src/components/Composer.vue"]);
  });

  it("lists a folder's children, folders first, for an empty query or one ending in /", () => {
    expect(narrowFileMatches(entries, "")).toEqual(["docs/", "src/", "README.md"].filter((entry) => entries.includes(entry)));
    expect(narrowFileMatches(entries, "src/")).toEqual(["src/components/", "src/app.ts"]);
  });
});
