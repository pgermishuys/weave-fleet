import { homedir } from "os";
import { resolve } from "path";
import {
  getProfileName,
  isDefaultProfile,
  getProfileDir,
  getProfileDbPath,
  getProfileWorkspaceRoot,
  getProfileIntegrationsPath,
  getProfilePortRange,
  getProfileServerPort,
  validateProfileName,
} from "@/lib/server/profile";

// ─── Helpers ──────────────────────────────────────────────────────────────────

function withEnv(
  vars: Record<string, string | undefined>,
  fn: () => void
): void {
  const original: Record<string, string | undefined> = {};
  for (const [k, v] of Object.entries(vars)) {
    original[k] = process.env[k];
    if (v === undefined) {
      delete process.env[k];
    } else {
      process.env[k] = v;
    }
  }
  try {
    fn();
  } finally {
    for (const [k, v] of Object.entries(original)) {
      if (v === undefined) {
        delete process.env[k];
      } else {
        process.env[k] = v;
      }
    }
  }
}

// ─── getProfileName ───────────────────────────────────────────────────────────

describe("getProfileName", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
  });

  it("ReturnsDefaultWhenEnvVarUnset", () => {
    delete process.env.WEAVE_PROFILE;
    expect(getProfileName()).toBe("default");
  });

  it("ReturnsProfileNameWhenSet", () => {
    process.env.WEAVE_PROFILE = "dev";
    expect(getProfileName()).toBe("dev");
  });

  it("ReturnsProfileNameForStagingProfile", () => {
    process.env.WEAVE_PROFILE = "staging";
    expect(getProfileName()).toBe("staging");
  });
});

// ─── isDefaultProfile ─────────────────────────────────────────────────────────

describe("isDefaultProfile", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
  });

  it("ReturnsTrueWhenEnvVarUnset", () => {
    delete process.env.WEAVE_PROFILE;
    expect(isDefaultProfile()).toBe(true);
  });

  it("ReturnsTrueWhenExplicitlySetToDefault", () => {
    process.env.WEAVE_PROFILE = "default";
    expect(isDefaultProfile()).toBe(true);
  });

  it("ReturnsFalseForNamedProfile", () => {
    process.env.WEAVE_PROFILE = "dev";
    expect(isDefaultProfile()).toBe(false);
  });
});

// ─── getProfileDir ────────────────────────────────────────────────────────────

describe("getProfileDir", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
  });

  it("ReturnsWeaveHomeForDefaultProfile", () => {
    delete process.env.WEAVE_PROFILE;
    expect(getProfileDir()).toBe(resolve(homedir(), ".weave"));
  });

  it("ReturnsProfileSubdirForNamedProfile", () => {
    process.env.WEAVE_PROFILE = "dev";
    expect(getProfileDir()).toBe(resolve(homedir(), ".weave", "profiles", "dev"));
  });

  it("ReturnsCorrectSubdirForDifferentProfiles", () => {
    process.env.WEAVE_PROFILE = "staging";
    expect(getProfileDir()).toBe(resolve(homedir(), ".weave", "profiles", "staging"));
  });
});

// ─── getProfileDbPath ─────────────────────────────────────────────────────────

describe("getProfileDbPath", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
    delete process.env.WEAVE_DB_PATH;
  });

  it("ReturnsDefaultPathForDefaultProfile", () => {
    delete process.env.WEAVE_PROFILE;
    delete process.env.WEAVE_DB_PATH;
    expect(getProfileDbPath()).toBe(resolve(homedir(), ".weave", "fleet.db"));
  });

  it("ReturnsProfilePathForNamedProfile", () => {
    process.env.WEAVE_PROFILE = "dev";
    delete process.env.WEAVE_DB_PATH;
    expect(getProfileDbPath()).toBe(
      resolve(homedir(), ".weave", "profiles", "dev", "fleet.db")
    );
  });

  it("RespectsWEAVE_DB_PATHOverride", () => {
    process.env.WEAVE_PROFILE = "dev";
    process.env.WEAVE_DB_PATH = "/custom/path/my.db";
    expect(getProfileDbPath()).toBe(resolve("/custom/path/my.db"));
  });

  it("RespectsWEAVE_DB_PATHOverrideForDefaultProfile", () => {
    delete process.env.WEAVE_PROFILE;
    process.env.WEAVE_DB_PATH = "/override/fleet.db";
    expect(getProfileDbPath()).toBe(resolve("/override/fleet.db"));
  });
});

// ─── getProfileWorkspaceRoot ──────────────────────────────────────────────────

describe("getProfileWorkspaceRoot", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
    delete process.env.WEAVE_WORKSPACE_ROOT;
  });

  it("ReturnsDefaultWorkspaceRootForDefaultProfile", () => {
    delete process.env.WEAVE_PROFILE;
    delete process.env.WEAVE_WORKSPACE_ROOT;
    expect(getProfileWorkspaceRoot()).toBe(
      resolve(homedir(), ".weave", "workspaces")
    );
  });

  it("ReturnsProfileWorkspaceRootForNamedProfile", () => {
    process.env.WEAVE_PROFILE = "dev";
    delete process.env.WEAVE_WORKSPACE_ROOT;
    expect(getProfileWorkspaceRoot()).toBe(
      resolve(homedir(), ".weave", "profiles", "dev", "workspaces")
    );
  });

  it("RespectsWEAVE_WORKSPACE_ROOTOverride", () => {
    process.env.WEAVE_PROFILE = "dev";
    process.env.WEAVE_WORKSPACE_ROOT = "/custom/workspaces";
    expect(getProfileWorkspaceRoot()).toBe(resolve("/custom/workspaces"));
  });
});

// ─── getProfileIntegrationsPath ───────────────────────────────────────────────

describe("getProfileIntegrationsPath", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
  });

  it("ReturnsDefaultIntegrationsPathForDefaultProfile", () => {
    delete process.env.WEAVE_PROFILE;
    expect(getProfileIntegrationsPath()).toBe(
      resolve(homedir(), ".weave", "integrations.json")
    );
  });

  it("ReturnsProfileIntegrationsPathForNamedProfile", () => {
    process.env.WEAVE_PROFILE = "dev";
    expect(getProfileIntegrationsPath()).toBe(
      resolve(homedir(), ".weave", "profiles", "dev", "integrations.json")
    );
  });
});

// ─── getProfilePortRange ──────────────────────────────────────────────────────

describe("getProfilePortRange", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
    delete process.env.WEAVE_PORT_RANGE_START;
  });

  it("ReturnsDefaultRangeForDefaultProfile", () => {
    delete process.env.WEAVE_PROFILE;
    const range = getProfilePortRange();
    expect(range.start).toBe(4097);
    expect(range.end).toBe(4200);
  });

  it("ReturnsNonDefaultRangeForNamedProfile", () => {
    process.env.WEAVE_PROFILE = "dev";
    const range = getProfilePortRange();
    expect(range.start).not.toBe(4097);
    expect(range.end - range.start).toBe(103); // 104 ports (same block size as default)
  });

  it("ReturnsDeterministicRangeForSameName", () => {
    process.env.WEAVE_PROFILE = "dev";
    const r1 = getProfilePortRange();
    const r2 = getProfilePortRange();
    expect(r1.start).toBe(r2.start);
    expect(r1.end).toBe(r2.end);
  });

  it("ReturnsDifferentRangesForDifferentNames", () => {
    process.env.WEAVE_PROFILE = "dev";
    const devRange = getProfilePortRange();

    process.env.WEAVE_PROFILE = "staging";
    const stagingRange = getProfilePortRange();

    expect(devRange.start).not.toBe(stagingRange.start);
  });

  it("DoesNotOverlapDefaultRange", () => {
    const defaultStart = 4097;
    const defaultEnd = 4200;

    for (const name of ["dev", "staging", "test", "prod", "ci", "local"]) {
      process.env.WEAVE_PROFILE = name;
      const range = getProfilePortRange();
      const overlaps = range.start <= defaultEnd && range.end >= defaultStart;
      expect(overlaps).toBe(false);
    }
  });

  it("RespectsWEAVE_PORT_RANGE_STARTOverride", () => {
    process.env.WEAVE_PROFILE = "dev";
    process.env.WEAVE_PORT_RANGE_START = "10000";
    const range = getProfilePortRange();
    expect(range.start).toBe(10000);
    expect(range.end).toBe(10103);
  });
});

// ─── getProfileServerPort ─────────────────────────────────────────────────────

describe("getProfileServerPort", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
    delete process.env.PORT;
  });

  it("Returns3000ForDefaultProfile", () => {
    delete process.env.WEAVE_PROFILE;
    delete process.env.PORT;
    expect(getProfileServerPort()).toBe(3000);
  });

  it("ReturnsDifferentPortForNamedProfile", () => {
    process.env.WEAVE_PROFILE = "dev";
    delete process.env.PORT;
    expect(getProfileServerPort()).not.toBe(3000);
  });

  it("RespectsPortOverride", () => {
    process.env.WEAVE_PROFILE = "dev";
    process.env.PORT = "8080";
    expect(getProfileServerPort()).toBe(8080);
  });

  it("RespectsPortOverrideForDefaultProfile", () => {
    delete process.env.WEAVE_PROFILE;
    process.env.PORT = "4000";
    expect(getProfileServerPort()).toBe(4000);
  });

  it("ReturnsDeterministicPortForSameProfile", () => {
    process.env.WEAVE_PROFILE = "staging";
    delete process.env.PORT;
    const p1 = getProfileServerPort();
    const p2 = getProfileServerPort();
    expect(p1).toBe(p2);
  });
});

// ─── validateProfileName ──────────────────────────────────────────────────────

describe("validateProfileName", () => {
  it("AcceptsSimpleLowercaseName", () => {
    expect(() => validateProfileName("dev")).not.toThrow();
  });

  it("AcceptsNameWithHyphens", () => {
    expect(() => validateProfileName("my-profile")).not.toThrow();
  });

  it("AcceptsNameWithDigits", () => {
    expect(() => validateProfileName("test1")).not.toThrow();
  });

  it("AcceptsDefaultAsValidInput", () => {
    expect(() => validateProfileName("default")).not.toThrow();
  });

  it("AcceptsMaxLengthName", () => {
    expect(() => validateProfileName("a".repeat(32))).not.toThrow();
  });

  it("RejectsEmptyString", () => {
    expect(() => validateProfileName("")).toThrow();
  });

  it("RejectsNameWithSpaces", () => {
    expect(() => validateProfileName("my profile")).toThrow();
  });

  it("RejectsUpperCaseName", () => {
    expect(() => validateProfileName("MY_PROFILE")).toThrow();
  });

  it("RejectsPathTraversal", () => {
    expect(() => validateProfileName("../hack")).toThrow();
  });

  it("RejectsNameTooLong", () => {
    expect(() => validateProfileName("a".repeat(33))).toThrow();
  });

  it("RejectsNameWithUnderscores", () => {
    expect(() => validateProfileName("my_profile")).toThrow();
  });

  it("RejectsLeadingHyphen", () => {
    expect(() => validateProfileName("-foo")).toThrow();
  });

  it("RejectsTrailingHyphen", () => {
    expect(() => validateProfileName("foo-")).toThrow();
  });

  it("RejectsBareHyphen", () => {
    expect(() => validateProfileName("-")).toThrow();
  });

  it("RejectsDoubleHyphenOnly", () => {
    expect(() => validateProfileName("--")).toThrow();
  });

  it("AcceptsSingleChar", () => {
    expect(() => validateProfileName("a")).not.toThrow();
  });

  it("AcceptsSingleDigit", () => {
    expect(() => validateProfileName("1")).not.toThrow();
  });
});
