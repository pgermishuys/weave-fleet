import { join } from "path";
import {
  rmSync,
  existsSync,
} from "fs";

import { getConnectedProviders } from "../auth-store";
import { createSecureTempDir, writeTempFile } from "./test-temp-utils";

// Mock the logger to avoid noise in test output
vi.mock("../logger", () => ({
  log: {
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
  },
}));

describe("getConnectedProviders", () => {
  let testDir: string;

  beforeEach(() => {
    testDir = createSecureTempDir("auth-store-test-");
  });

  afterEach(() => {
    if (existsSync(testDir)) {
      rmSync(testDir, { recursive: true, force: true });
    }
  });

  it("ReturnsEmptyArrayForNonexistentFile", () => {
    const result = getConnectedProviders(join(testDir, "nonexistent.json"));
    expect(result).toEqual([]);
  });

  it("ReturnsEmptyArrayForEmptyFile", () => {
    const filePath = join(testDir, "auth.json");
    writeTempFile(testDir, "auth.json", "");
    const result = getConnectedProviders(filePath);
    expect(result).toEqual([]);
  });

  it("ReturnsEmptyArrayForMalformedJson", () => {
    const filePath = join(testDir, "auth.json");
    writeTempFile(testDir, "auth.json", "not valid json {{{");
    const result = getConnectedProviders(filePath);
    expect(result).toEqual([]);
  });

  it("ReturnsEmptyArrayForNonObjectJson", () => {
    const filePath = join(testDir, "auth.json");

    // Array
    writeTempFile(testDir, "auth.json", '["a", "b"]');
    expect(getConnectedProviders(filePath)).toEqual([]);

    // String
    writeTempFile(testDir, "auth.json", '"just a string"');
    expect(getConnectedProviders(filePath)).toEqual([]);

    // Number
    writeTempFile(testDir, "auth.json", "42");
    expect(getConnectedProviders(filePath)).toEqual([]);

    // Null
    writeTempFile(testDir, "auth.json", "null");
    expect(getConnectedProviders(filePath)).toEqual([]);
  });

  it("ParsesValidAuthJsonWithApiKeyProvider", () => {
    const filePath = join(testDir, "auth.json");
    writeTempFile(
      testDir,
      "auth.json",
      JSON.stringify({
        anthropic: { type: "api", token: "sk-ant-xxx" },
      })
    );

    const result = getConnectedProviders(filePath);
    expect(result).toHaveLength(1);
    expect(result[0]).toEqual({ id: "anthropic", authType: "api" });
  });

  it("ParsesValidAuthJsonWithOauthProvider", () => {
    const filePath = join(testDir, "auth.json");
    writeTempFile(
      testDir,
      "auth.json",
      JSON.stringify({
        "github-copilot": { type: "oauth", token: "ghu_xxx" },
      })
    );

    const result = getConnectedProviders(filePath);
    expect(result).toHaveLength(1);
    expect(result[0]).toEqual({ id: "github-copilot", authType: "oauth" });
  });

  it("ParsesValidAuthJsonWithWellknownProvider", () => {
    const filePath = join(testDir, "auth.json");
    writeTempFile(
      testDir,
      "auth.json",
      JSON.stringify({
        "amazon-bedrock": { type: "wellknown" },
      })
    );

    const result = getConnectedProviders(filePath);
    expect(result).toHaveLength(1);
    expect(result[0]).toEqual({ id: "amazon-bedrock", authType: "wellknown" });
  });

  it("SkipsEntriesWithoutValidTypeField", () => {
    const filePath = join(testDir, "auth.json");
    writeTempFile(
      testDir,
      "auth.json",
      JSON.stringify({
        invalid1: { type: "unknown" },
        invalid2: { noType: true },
        invalid3: "just a string",
        invalid4: 42,
        invalid5: null,
        invalid6: [1, 2, 3],
      })
    );

    const result = getConnectedProviders(filePath);
    expect(result).toEqual([]);
  });

  it("HandlesMixedValidAndInvalidEntries", () => {
    const filePath = join(testDir, "auth.json");
    writeTempFile(
      testDir,
      "auth.json",
      JSON.stringify({
        anthropic: { type: "api", token: "sk-ant-xxx" },
        invalid: { type: "unknown" },
        openai: { type: "api", token: "sk-xxx" },
        broken: "not an object",
        google: { type: "oauth", token: "ya29.xxx" },
      })
    );

    const result = getConnectedProviders(filePath);
    expect(result).toHaveLength(3);
    expect(result.map((p) => p.id)).toEqual(["anthropic", "openai", "google"]);
  });
});
