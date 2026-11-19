/**
 * Phone first: someone installs the phone app before Fleet runs anywhere. The phone can't run Fleet, so it says where
 * Fleet runs and hands over the two things to install it with: the download page (sent to the computer with the
 * share sheet) and the one install line for a machine with no screen.
 *
 * Pure: no Vue, no React Native. Used by the phone app (`mobile/`) and its tests.
 */

/** Where the desktop installers live: the newest release, with an installer for each platform. */
export const FLEET_DOWNLOAD_URL = "https://github.com/pgermishuys/weave-fleet/releases/latest";

/** The install line for macOS and Linux, from the README. It installs Fleet; `fleet node up` then starts it. */
export const FLEET_INSTALL_LINE = "curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh";

/** The share sheet's message: the link comes with a line saying what it is, since it lands in a chat or an email. */
export function downloadShare(): { message: string; url: string } {
  return { message: `Get Fleet for your computer: ${FLEET_DOWNLOAD_URL}`, url: FLEET_DOWNLOAD_URL };
}

/** What "Share the line" tells the person next, under the button. */
export const INSTALL_LINE_NOTE = "Send it to the machine and run it there to install Fleet, then run fleet node up.";

/** The share sheet's content for the install line: just the line, so Copy gives something that runs as it is. */
export function installShare(): { message: string } {
  return { message: FLEET_INSTALL_LINE };
}
