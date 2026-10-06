/** Sends a link to the computer: the share sheet where there is one, else the clipboard. */
export async function shareLink(title: string, url: string): Promise<"shared" | "copied" | "failed"> {
  try {
    if (navigator.share) {
      await navigator.share({ title, url });
      return "shared";
    }
    await navigator.clipboard.writeText(url);
    return "copied";
  } catch {
    return "failed";
  }
}
