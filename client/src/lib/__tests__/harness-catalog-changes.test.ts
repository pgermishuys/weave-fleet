import { describe, expect, it } from "vitest";
import { isCatalogChangeFor, parseCatalogChange, type HarnessCatalogChange } from "@/lib/harness-catalog-changes";

const change: HarnessCatalogChange = {
  harnessType: "opencode2",
  directory: "/work/rocket",
  quickChat: false,
  profileIds: ["none"],
  sessionIds: ["s1"],
};

describe("parseCatalogChange", () => {
  it("reads the server's payload", () => {
    expect(parseCatalogChange({
      harnessType: "opencode2",
      directory: "/work/rocket",
      profileIds: ["p1", 3],
      sessionIds: ["s1"],
    })).toEqual({ harnessType: "opencode2", directory: "/work/rocket", quickChat: false, profileIds: ["p1"], sessionIds: ["s1"] });
  });

  it("drops a payload without a harness or folder", () => {
    expect(parseCatalogChange({ directory: "/work/rocket" })).toBeNull();
    expect(parseCatalogChange({ harnessType: "opencode2" })).toBeNull();
    expect(parseCatalogChange(null)).toBeNull();
  });
});

describe("isCatalogChangeFor", () => {
  it("matches the harness, folder and profile the catalog was asked for", () => {
    expect(isCatalogChangeFor(change, "opencode2", "/work/rocket", "none")).toBe(true);
    expect(isCatalogChangeFor(change, "opencode2", "/work/rocket/", "none")).toBe(true);
    expect(isCatalogChangeFor(change, "opencode", "/work/rocket", "none")).toBe(false);
    expect(isCatalogChangeFor(change, "opencode2", "/work/comet", "none")).toBe(false);
  });

  it("matches a profile only when the change names it", () => {
    const onProfile = { ...change, profileIds: ["p1"] };

    expect(isCatalogChangeFor(onProfile, "opencode2", "/work/rocket", "p1")).toBe(true);
    expect(isCatalogChangeFor(onProfile, "opencode2", "/work/rocket", "p2")).toBe(false);
    expect(isCatalogChangeFor(onProfile, "opencode2", "/work/rocket", "none")).toBe(false);
  });

  it("matches any profile when the catalog was asked for the default one", () => {
    expect(isCatalogChangeFor({ ...change, profileIds: ["p1"] }, "opencode2", "/work/rocket", undefined)).toBe(true);
  });

  it("matches a quick chat's catalog by the server's word for its folder", () => {
    expect(isCatalogChangeFor({ ...change, quickChat: true }, "opencode2", null, undefined)).toBe(true);
    expect(isCatalogChangeFor(change, "opencode2", null, undefined)).toBe(false);
  });
});
