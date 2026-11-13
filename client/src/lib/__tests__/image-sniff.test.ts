import { describe, expect, it } from "vitest";
import { sniffImageMime } from "../image-validation";

const b64 = (bytes: number[]): string => btoa(String.fromCharCode(...bytes, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

describe("sniffImageMime", () => {
  it("names each accepted format from its first bytes", () => {
    expect(sniffImageMime(b64([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))).toBe("image/png");
    expect(sniffImageMime(b64([0xff, 0xd8, 0xff, 0xe0]))).toBe("image/jpeg");
    expect(sniffImageMime(b64([...Buffer.from("GIF89a")]))).toBe("image/gif");
    expect(sniffImageMime(b64([...Buffer.from("RIFF"), 0, 0, 0, 0, ...Buffer.from("WEBP")]))).toBe("image/webp");
  });

  it("is null for SVG, BMP and ICO", () => {
    expect(sniffImageMime(b64([...Buffer.from("<svg xmlns")]))).toBeNull();
    expect(sniffImageMime(b64([0x42, 0x4d, 0x36, 0, 0, 0]))).toBeNull();
    expect(sniffImageMime(b64([0, 0, 1, 0, 1, 0]))).toBeNull();
  });
});
