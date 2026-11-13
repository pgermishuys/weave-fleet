/**
 * Shared image attachment validation — used by both client and server.
 */

export const ALLOWED_IMAGE_MIMES = new Set([
  "image/png",
  "image/jpeg",
  "image/gif",
  "image/webp",
]);

/**
 * The image type the bytes themselves say, from the file's first bytes (base64 or raw). A file's browser-given type
 * comes from its name, so a PNG called shot.jpg arrives as image/jpeg and the provider rejects it mid-turn.
 * Null when the bytes aren't PNG, JPEG, GIF or WebP.
 */
export function sniffImageMime(head: Uint8Array | string): string | null {
  const bytes =
    typeof head === "string"
      ? Uint8Array.from(atob(head.slice(0, 24)), (c) => c.charCodeAt(0))
      : head;
  const at = (offset: number, text: string): boolean =>
    [...text].every((c, i) => bytes[offset + i] === c.charCodeAt(0));
  if (bytes[0] === 0x89 && at(1, "PNG\r\n\x1a\n")) return "image/png";
  if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "image/jpeg";
  if (at(0, "GIF87a") || at(0, "GIF89a")) return "image/gif";
  if (at(0, "RIFF") && at(8, "WEBP")) return "image/webp";
  return null;
}

/** 5MB — matches Claude's per-image limit */
export const MAX_IMAGE_BYTES = 5 * 1024 * 1024;

/** Maximum number of images per prompt */
export const MAX_ATTACHMENTS_PER_PROMPT = 10;

export interface ImageValidationError {
  index: number;
  message: string;
}

export function validateImageAttachment(
  attachment: { mime: string; data: string },
  index: number
): ImageValidationError | null {
  if (!ALLOWED_IMAGE_MIMES.has(attachment.mime)) {
    return { index, message: `Unsupported image type: ${attachment.mime}` };
  }
  // Base64 string length → approximate byte size (each char = 6 bits)
  const approxBytes = Math.ceil((attachment.data.length * 3) / 4);
  if (approxBytes > MAX_IMAGE_BYTES) {
    return {
      index,
      message: `Image exceeds 5MB limit (${(approxBytes / 1024 / 1024).toFixed(1)}MB)`,
    };
  }
  return null;
}

export function validateAttachments(
  attachments: Array<{ mime: string; data: string }>
): ImageValidationError[] {
  const errors: ImageValidationError[] = [];
  if (attachments.length > MAX_ATTACHMENTS_PER_PROMPT) {
    errors.push({
      index: -1,
      message: `Too many images (max ${MAX_ATTACHMENTS_PER_PROMPT})`,
    });
  }
  for (let i = 0; i < attachments.length; i++) {
    const error = validateImageAttachment(attachments[i], i);
    if (error) errors.push(error);
  }
  return errors;
}
