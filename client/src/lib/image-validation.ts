/**
 * Shared image attachment validation — used by both client and server.
 */

export const ALLOWED_IMAGE_MIMES = new Set([
  "image/png",
  "image/jpeg",
  "image/gif",
  "image/webp",
]);

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
