import { describe, expect, it } from "vitest";
import {
  GLOBAL_TARGET,
  isSameTarget,
  targetBody,
  targetFromKey,
  targetKey,
  targetLabel,
  targetQuery,
  toInstallTarget,
} from "@/lib/install-target";

describe("install-target", () => {
  it("reads the API's scope fields, treating anything but a project with a path as global", () => {
    expect(toInstallTarget("project", "/src/app")).toEqual({ scope: "project", projectPath: "/src/app" });
    expect(toInstallTarget("project", null)).toEqual(GLOBAL_TARGET);
    expect(toInstallTarget("global", "/src/app")).toEqual(GLOBAL_TARGET);
    expect(toInstallTarget(undefined, undefined)).toEqual(GLOBAL_TARGET);
  });

  it("compares targets by scope and repository", () => {
    const app = { scope: "project", projectPath: "/src/app" } as const;
    expect(isSameTarget(GLOBAL_TARGET, { scope: "global" })).toBe(true);
    expect(isSameTarget(app, { scope: "project", projectPath: "/src/app" })).toBe(true);
    expect(isSameTarget(app, { scope: "project", projectPath: "/src/other" })).toBe(false);
    expect(isSameTarget(app, GLOBAL_TARGET)).toBe(false);
  });

  it("round-trips through a select key, including Windows paths", () => {
    const windows = { scope: "project", projectPath: "C:\\src\\app" } as const;
    expect(targetFromKey(targetKey(windows))).toEqual(windows);
    expect(targetFromKey(targetKey(GLOBAL_TARGET))).toEqual(GLOBAL_TARGET);
  });

  it("builds request fields", () => {
    expect(targetBody(GLOBAL_TARGET)).toEqual({ scope: "global", projectPath: null });
    expect(targetQuery({ scope: "project", projectPath: "/src/app" })).toEqual({ scope: "project", projectPath: "/src/app" });
    expect(targetQuery(GLOBAL_TARGET)).toEqual({ scope: "global" });
  });

  it("labels a project by its folder name on any OS", () => {
    expect(targetLabel(GLOBAL_TARGET)).toBe("Global");
    expect(targetLabel({ scope: "project", projectPath: "/home/me/src/weave-fleet/" })).toBe("weave-fleet");
    expect(targetLabel({ scope: "project", projectPath: "C:\\src\\weave-fleet" })).toBe("weave-fleet");
  });
});
