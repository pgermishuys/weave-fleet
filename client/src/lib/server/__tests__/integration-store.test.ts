import { join } from "path";
import { existsSync, rmSync } from "fs";
import { homedir } from "os";
import { resolve } from "path";
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import {
  getIntegrationConfig,
  setIntegrationConfig,
  removeIntegrationConfig,
  getAllIntegrationConfigs,
} from "../integration-store";
import { getProfileIntegrationsPath } from "../profile";
import { createSecureTempDir, writeTempFile } from "./test-temp-utils";

// Mock the logger to avoid noise in test output
vi.mock("../logger", () => ({
  log: {
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
  },
}));

describe("Integration Store", () => {
  let testDir: string;
  let filePath: string;

  beforeEach(() => {
    testDir = createSecureTempDir("integration-store-test-");
    filePath = join(testDir, "integrations.json");
  });

  afterEach(() => {
    if (existsSync(testDir)) {
      rmSync(testDir, { recursive: true, force: true });
    }
  });

  describe("getIntegrationConfig", () => {
    it("ReturnsNullForNonexistentFile", () => {
      const result = getIntegrationConfig("github", filePath);
      expect(result).toBeNull();
    });

    it("ReturnsNullForUnknownIntegration", () => {
      setIntegrationConfig("jira", { token: "xyz" }, filePath);
      const result = getIntegrationConfig("github", filePath);
      expect(result).toBeNull();
    });

    it("ReturnsConfigForKnownIntegration", () => {
      setIntegrationConfig("github", { token: "ghp_test" }, filePath);
      const result = getIntegrationConfig("github", filePath);
      expect(result).not.toBeNull();
      expect(result!.token).toBe("ghp_test");
    });
  });

  describe("setIntegrationConfig", () => {
    it("CreatesFileAndSavesConfig", () => {
      const success = setIntegrationConfig(
        "github",
        { token: "ghp_test" },
        filePath
      );
      expect(success).toBe(true);
      expect(existsSync(filePath)).toBe(true);

      const result = getIntegrationConfig("github", filePath);
      expect(result!.token).toBe("ghp_test");
    });

    it("AddsConnectedAtTimestampIfMissing", () => {
      setIntegrationConfig("github", { token: "ghp_test" }, filePath);
      const result = getIntegrationConfig("github", filePath);
      expect(result!.connectedAt).toBeDefined();
      expect(typeof result!.connectedAt).toBe("string");
    });

    it("PreservesExistingConnectedAt", () => {
      const connectedAt = "2025-01-01T00:00:00.000Z";
      setIntegrationConfig(
        "github",
        { token: "ghp_test", connectedAt },
        filePath
      );
      const result = getIntegrationConfig("github", filePath);
      expect(result!.connectedAt).toBe(connectedAt);
    });

    it("UpdatesExistingConfig", () => {
      setIntegrationConfig("github", { token: "ghp_old" }, filePath);
      setIntegrationConfig("github", { token: "ghp_new" }, filePath);
      const result = getIntegrationConfig("github", filePath);
      expect(result!.token).toBe("ghp_new");
    });

    it("DoesNotOverwriteOtherIntegrations", () => {
      setIntegrationConfig("github", { token: "ghp_test" }, filePath);
      setIntegrationConfig("jira", { token: "jira_test" }, filePath);
      expect(getIntegrationConfig("github", filePath)!.token).toBe("ghp_test");
      expect(getIntegrationConfig("jira", filePath)!.token).toBe("jira_test");
    });
  });

  describe("removeIntegrationConfig", () => {
    it("RemovesExistingConfig", () => {
      setIntegrationConfig("github", { token: "ghp_test" }, filePath);
      const success = removeIntegrationConfig("github", filePath);
      expect(success).toBe(true);
      expect(getIntegrationConfig("github", filePath)).toBeNull();
    });

    it("ReturnsTrueForNonexistentIntegration", () => {
      const success = removeIntegrationConfig("nonexistent", filePath);
      expect(success).toBe(true);
    });

    it("DoesNotRemoveOtherIntegrations", () => {
      setIntegrationConfig("github", { token: "ghp_test" }, filePath);
      setIntegrationConfig("jira", { token: "jira_test" }, filePath);
      removeIntegrationConfig("github", filePath);
      expect(getIntegrationConfig("jira", filePath)!.token).toBe("jira_test");
    });
  });

  describe("getAllIntegrationConfigs", () => {
    it("ReturnsEmptyObjectForNonexistentFile", () => {
      const result = getAllIntegrationConfigs(filePath);
      expect(result).toEqual({});
    });

    it("ReturnsAllConfigs", () => {
      setIntegrationConfig("github", { token: "ghp_test" }, filePath);
      setIntegrationConfig("jira", { token: "jira_test" }, filePath);
      const result = getAllIntegrationConfigs(filePath);
      expect(Object.keys(result)).toContain("github");
      expect(Object.keys(result)).toContain("jira");
    });
  });

  describe("Error handling", () => {
    it("ReturnsNullForMalformedJson", () => {
      writeTempFile(testDir, "integrations.json", "not valid json {{{");
      const result = getIntegrationConfig("github", filePath);
      expect(result).toBeNull();
    });

    it("ReturnsEmptyObjectForMalformedJson", () => {
      writeTempFile(testDir, "integrations.json", "null");
      const result = getAllIntegrationConfigs(filePath);
      expect(result).toEqual({});
    });
  });
});

describe("Integration Store — profile awareness", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
  });

  it("UsesProfileIntegrationsPathWhenWeaveProfileIsSet", () => {
    process.env.WEAVE_PROFILE = "test-int-profile";

    const expected = resolve(homedir(), ".weave", "profiles", "test-int-profile", "integrations.json");
    expect(getProfileIntegrationsPath()).toBe(expected);
  });

  it("DefaultProfileUsesRootWeaveIntegrationsPath", () => {
    delete process.env.WEAVE_PROFILE;

    const expected = resolve(homedir(), ".weave", "integrations.json");
    expect(getProfileIntegrationsPath()).toBe(expected);
  });
});
