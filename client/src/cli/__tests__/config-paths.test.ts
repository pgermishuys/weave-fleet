import { join } from "path";
import { homedir } from "os";
import { getDataDir, getAuthJsonPath } from "../config-paths";

describe("getDataDir", () => {
  const originalEnv = { ...process.env };

  afterEach(() => {
    process.env = { ...originalEnv };
  });

  it("ReturnsXdgDataHomeWhenSet", () => {
    process.env.XDG_DATA_HOME = "/custom/data";
    const result = getDataDir();
    expect(result).toBe(join("/custom/data", "opencode"));
  });

  it("ReturnsDefaultOnNonWindows", () => {
    delete process.env.XDG_DATA_HOME;
    // On macOS/Linux (the test platform), should return ~/.local/share/opencode
    if (process.platform !== "win32") {
      const result = getDataDir();
      expect(result).toBe(join(homedir(), ".local", "share", "opencode"));
    }
  });
});

describe("getAuthJsonPath", () => {
  it("ReturnsPathEndingInAuthJson", () => {
    const result = getAuthJsonPath();
    expect(result).toMatch(/auth\.json$/);
  });

  it("IsInsideDataDir", () => {
    const dataDir = getDataDir();
    const authPath = getAuthJsonPath();
    expect(authPath).toBe(join(dataDir, "auth.json"));
  });
});
