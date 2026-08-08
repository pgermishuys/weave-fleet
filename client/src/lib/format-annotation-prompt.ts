/**
 * Formats an annotation into a structured prompt for the conversation.
 *
 * @param filePath - The file path where the annotation was made (optional)
 * @param anchorText - The text that was annotated
 * @param userComment - The user's comment on the annotation
 * @returns Formatted prompt string
 */
export function formatAnnotationPrompt(
  filePath: string,
  anchorText: string,
  userComment: string
): string {
  const header = filePath ? `[Annotation on ${filePath}]` : '[Annotation]'
  return `${header}\n> ${anchorText}\n\n${userComment}`
}
