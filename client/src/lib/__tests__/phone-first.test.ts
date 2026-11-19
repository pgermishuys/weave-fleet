import { describe, expect, test } from "vitest";
import { downloadShare, installShare, FLEET_DOWNLOAD_URL, FLEET_INSTALL_LINE, INSTALL_LINE_NOTE } from "../phone-first";

describe("phone first", () => {
  test("the share sheet carries the download page", () => {
    const share = downloadShare();
    expect(share.url).toBe(FLEET_DOWNLOAD_URL);
    expect(share.message).toContain(FLEET_DOWNLOAD_URL);
  });

  test("the install line shares as itself", () => { expect(installShare().message).toBe(FLEET_INSTALL_LINE); });

  test("the install line is the README's and the note names the next command", () => {
    expect(FLEET_INSTALL_LINE).toMatch(/^curl -fsSL https:\/\/.+\/install\.sh \| sh$/);
    expect(INSTALL_LINE_NOTE).toContain("fleet node up");
  });
});
