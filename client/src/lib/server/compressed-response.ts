/**
 * Route-level response compression for JSON API endpoints.
 *
 * Provides gzip and brotli compression based on the client's Accept-Encoding
 * header. This works in both dev and production modes, and supplements the
 * Next.js built-in `compress: true` (which only applies gzip in production).
 *
 * Usage:
 *   return compressedJson(request, data, { status: 200, headers: { ... } });
 */

import { NextRequest, NextResponse } from "next/server";
import { gzipSync, brotliCompressSync, constants } from "zlib";

interface CompressedJsonOptions {
  status?: number;
  headers?: Record<string, string>;
}

/**
 * Negotiate the best encoding from the Accept-Encoding header.
 * Preference order: br > gzip > identity.
 */
function negotiateEncoding(
  acceptEncoding: string | null
): "br" | "gzip" | null {
  if (!acceptEncoding) return null;
  const lower = acceptEncoding.toLowerCase();
  if (lower.includes("br")) return "br";
  if (lower.includes("gzip")) return "gzip";
  return null;
}

/**
 * Minimum payload size (bytes) to bother compressing.
 * Below this threshold the overhead isn't worth it.
 */
const MIN_COMPRESS_SIZE = 1024;

/**
 * Return a compressed JSON Response, honouring the client's Accept-Encoding.
 *
 * Falls back to an uncompressed response when:
 * - The payload is smaller than MIN_COMPRESS_SIZE
 * - The client doesn't accept gzip or brotli
 * - Compression fails for any reason (graceful degradation)
 */
export function compressedJson(
  request: NextRequest,
  data: unknown,
  options: CompressedJsonOptions = {}
): NextResponse {
  const { status = 200, headers: extraHeaders = {} } = options;
  const json = JSON.stringify(data);
  const jsonBytes = Buffer.from(json, "utf-8");

  // Don't bother compressing small payloads
  if (jsonBytes.length < MIN_COMPRESS_SIZE) {
    return new NextResponse(json, {
      status,
      headers: {
        "Content-Type": "application/json",
        ...extraHeaders,
      },
    });
  }

  const encoding = negotiateEncoding(
    request.headers.get("accept-encoding")
  );

  if (!encoding) {
    return new NextResponse(json, {
      status,
      headers: {
        "Content-Type": "application/json",
        ...extraHeaders,
      },
    });
  }

  try {
    let compressed: Buffer;
    if (encoding === "br") {
      compressed = brotliCompressSync(jsonBytes, {
        params: {
          // Quality 4 is a good trade-off: ~85% of max compression at ~10% of the CPU cost
          [constants.BROTLI_PARAM_QUALITY]: 4,
        },
      });
    } else {
      compressed = gzipSync(jsonBytes, { level: 6 });
    }

    return new NextResponse(new Uint8Array(compressed), {
      status,
      headers: {
        "Content-Type": "application/json",
        "Content-Encoding": encoding,
        "Vary": "Accept-Encoding",
        ...extraHeaders,
      },
    });
  } catch {
    // Compression failed — fall back to uncompressed
    return new NextResponse(json, {
      status,
      headers: {
        "Content-Type": "application/json",
        ...extraHeaders,
      },
    });
  }
}
