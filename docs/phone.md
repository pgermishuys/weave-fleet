# Fleet on your phone

Fleet runs as an app on your phone: notifications when an agent needs you, a list of everything waiting on you
across every machine, and each session to read, answer and steer. It's the same Fleet, served by your own machine
and installed to the Home Screen. Nothing goes through a Weave server.

## The phone app and the web app

There are two ways to use Fleet on a phone. Both look at the machines you've connected; neither runs anything itself.

- **The phone app** (iOS and Android, built from `mobile/`). Install it, then connect it to each machine: on the machine,
  open Fleet and choose **Add your phone** (Settings → Machines), or run `fleet node pair` on a machine with no screen.
  In the app, scan the code, or type the machine's address and the code under the QR code. The app pairs with each
  machine separately and keeps a key for each, so connect it once per machine. It tries a machine's addresses in order
  and checks the machine's id before it sends a key. Besides `https://`, it uses plain `http://` to a Tailscale address
  (Tailscale encrypts it) or a private address on your home network, never to a name that could resolve anywhere.
  The rest of this page, from [Pairing](#pairing) on, describes the web app.
- **The web app**: Fleet opened in the phone's browser and added to the Home Screen. It needs `https://` to install and
  to send notifications, and gets keys for your other machines through the machine it paired with (see
  [One phone, every machine](#one-phone-every-machine-the-home-machine)).

A machine with no screen (`fleet node up`) has no web app, so it has no Add your phone button: it prints its pairing
link with `fleet node pair`, and the phone app takes that code.

## What you need

- Your machine and your phone on the same [Tailscale](https://tailscale.com) tailnet.
- Fleet behind `tailscale serve`, so the phone gets HTTPS:

  ```sh
  fleet --require-token
  tailscale serve --bg --https=443 http://127.0.0.1:6262
  ```

  HTTPS certificates have to be on for the tailnet (admin console → DNS → HTTPS Certificates). `--require-token`
  is required behind the proxy; see [machines.md](machines.md#behind-tailscale-serve-https).
- iPhone: iOS 16.4 or later, and Fleet added to the Home Screen (iOS only sends web notifications to Home Screen
  apps). Android: Chrome.

**Plain http still works, but only as a web page.** Opened as `http://100.x.y.z:6262` the phone can pair and use
Fleet in the browser, but browsers only install apps and allow notifications on https (or `localhost`). The setup
screen says so.

## Pairing

1. On the machine: Settings → Machines → This machine → **Add your phone**. The code carries every address Fleet can be
   reached on (Tailscale, the home network), and the card lists them under "Your phone will reach hangar". Nothing
   asks which one: the phone tries each. A tunnel or a domain of your own goes under **Use my own address**. On a
   machine with no screen, `fleet node up` or `fleet node pair` prints the QR code in the terminal.
2. On the phone: scan the QR code with the camera. The page asks **Connect this phone to hangar?** with a name for the
   phone. Connect.
3. The phone now has its own key to that machine. Settings → Machines → **Devices with access** lists it with when it
   was added and last used.

The QR code works once and expires after 10 minutes. Its secret sits in the URL after `#`, which browsers never send
to a server, so it doesn't reach any log. Every phone gets its own key: removing one signs out only that phone.
A key that isn't used for 30 days stops working; pair again.

No camera? Open `<address>/pair` on the phone and type the code shown under the QR code (`XXXX-XXXX`).

The card says where the phone will reach the machine: at home on your Wi-Fi, away from home with Tailscale on the
phone, or at an address of your own. The web app on a phone installs and gets notifications only from an https://
address (`tailscale serve`); the phone app uses plain http to a Tailscale or home-network address once the machine's id
answers there.

**Adding Fleet to an iPhone's Home Screen keeps you signed in.** iOS 17.2 and later copy Safari's cookies into the
Home Screen app (not its other storage), so Fleet opens there signed in and gets the phone a fresh key for itself
and for your other machines; the key left behind in Safari stops working. On older iOS the Home Screen app starts
empty and asks to pair once more: make a new code on the machine.

## Notifications

After pairing, the phone shows **Notifications**:

1. iPhone: tap Share → **Add to Home Screen**, then open Fleet from its icon. Android: **Install Fleet** (or Chrome's
   menu ⋮ → Install app); notifications work in Chrome without installing.
2. Choose what to be told about: **Needs you** (a command or edit waits for approval, or a workflow waits on you),
   **Questions**, **Finished**, **Failed**, and **Quiet while I'm at the desk** (no pushes while Fleet is open and
   visible on a desktop).
3. **Turn on notifications**, then **Send a test notification**.

Tapping a notification opens the session at the ask. On Android a permission ask has **Allow once** and **Deny** on
the notification itself; iPhone notifications have no buttons. Notifications for the same session replace each other.

Pushes go through the phone's own push service (Google for Chrome, Apple for Safari), encrypted end to end
(`aes128gcm`), carrying the machine, session, a title and one line. Never a token or a file.

## One phone, every machine: the home machine

The machine a phone paired with is its **home**. Home sends the phone's notifications for every machine in home's
list (Settings → Machines on that machine): it keeps a connection to each one and pushes their notifications as theirs.
Upgrade every machine: an older Fleet only reports a notification when its own desktop notifications setting is on,
and without the details the phone uses.

To read and answer sessions on another machine, the phone needs its own key there too. Home gets it one on each listed
machine (a device grant) the first time the phone opens its inbox, without the phone ever seeing that machine's own
token. The other machine has to be reachable from the phone over https (`https://falcon.<tailnet>.ts.net`); one listed
by an http address shows as "Reachable only from machines".

If home is off or unreachable, notifications stop for every machine, and the inbox marks the machines it can't reach.

## On the phone

- **Needs you**: everything waiting on you on every machine, newest first. Allow once or answer a question from the
  list, or **More…** for every choice. Then what's working and what finished today.
- **Sessions**: everything from the last month, and **Open the full Fleet** for anything the phone view doesn't do.
- **New session** (**+** at the top, or the button under Sessions): say what the agent should do, then pick the
  machine (home or any machine the phone has a key for), the folder (recent ones first, then the machine's
  repositories, or no folder), where in a repository (a new worktree or the folder itself), the harness, agent and
  model. They start where you left them last time on that machine. **Start** opens the session.
- **Machines**: which machines the phone reaches, and why not when it can't. A machine with several addresses says how
  the phone reaches it ("Connected over Tailscale", "Connected on your network · Tailscale isn't answering") and lists
  them in the order the phone tries them, with arrows to reorder; see
  [One machine, several addresses](machines.md#one-machine-several-addresses).
- **A session**: the header says the machine and what it's doing. Each run of tool calls is one row ("Read 4 files ·
  edited 1"); tap it for the steps and a step for its diff or output. When the agent asks, the ask replaces the
  composer. The send button is **Send** when idle, **Queue** while the agent works and **Stop** when there's nothing
  typed; **Send now** steers the agent mid-turn where the harness can. **+** adds photos, a file, a command (`!`) or a
  side question (`/btw`). The **⋯** menu has Changes, Files, Terminal, Fork, Rename, Archive.
- **Not on the phone**: terminals, the editor and app previews. The phone says so, runs a one-off `!` command instead,
  or sends you a link to open the session on the computer. A machine without the web app (one run with `fleet node`) has no such link:
  open the session in Fleet on the computer the phone is paired with.
- **When a machine stops answering**: the session stays as you last saw it, the header says when the machine was last
  heard, and what you type is held on the phone and sent when it's back. Answers to permission asks are never held.

## Removing a phone

Settings → Machines → This machine → Devices with access → **Remove**. The phone's key stops working at once, an open
Fleet on the phone is disconnected, its notifications stop, and home removes the keys it got the phone on other
machines (a machine that's off gets the removal when it's next reachable).

Removing a machine from home's list removes the phones' keys on it too, if it answers. If it doesn't, remove them in
that machine's own Settings → Machines → Devices with access.

## Reach it from outside your network

Tailscale is the easiest way, not the only one. The code carries the addresses Fleet found, and the address saved as
the machine's phone address goes first (Settings → Machines → This machine → **Add your phone** → **Use my own address**). Any `https://` address that reaches this machine will do:

- Tailscale with `tailscale serve`, as above.
- A tunnel you run yourself, for example a reverse SSH tunnel to a server of yours, or a tunnel service, that ends
  in HTTPS.
- A domain of your own pointing at this machine behind a reverse proxy that has a certificate.

Fleet runs no relay: the phone talks to your address and nothing goes through Weave. Keep `--require-token` on
whatever address is reachable from the internet. The phone needs `https://` to install the app and get notifications.
Over Wi-Fi at home the phone can still pair on an `http://` address.

## Troubleshooting

- **No notifications at all.** On the phone: Fleet → bell → is it on, and does the test arrive? iPhone: Fleet must be
  opened from the Home Screen icon, not Safari; Focus modes can silence it. Check the home machine is running and
  reachable. If "Quiet while I'm at the desk" is on, close or hide Fleet on the computer.
- **"Notifications are off for this phone."** The phone's settings took permission away. Allow notifications for Fleet
  in the phone's settings, then turn them on again in Fleet.
- **Nothing from another machine.** Is it in home's list (Settings → Machines on home), shown as live? An old Fleet
  there needs upgrading. In the inbox's Machines tab, a reason shows when the phone can't reach it.
- **The QR code says it expired.** Each code works once for 10 minutes. Make a new one.
- **Install isn't offered.** The page isn't https. Use the `tailscale serve` address.
