# Fleet's desktop shell in Rune

A prototype that rebuilds the layout of Weave Fleet's desktop app in
[Rune](https://github.com/anomalyco/rune), a GPU-rendered UI framework written in Rust. It exists
to see what Fleet's shell looks and feels like without a browser engine under it.

It draws the shell only, from invented data. It doesn't talk to a Fleet server.

## Run it

You need [rustup](https://rustup.rs). The first build downloads Rune and takes a few minutes.

```sh
cd native
cargo run --release
```

- **macOS and Windows** need nothing else (on Windows, the Visual Studio C++ build tools that
  rustup asks for).
- **Linux** needs two development packages to build, and one library to open a window:
  `sudo apt-get install libxkbcommon-dev libwayland-dev libxkbcommon-x11-0`
  (Fedora: `dnf install libxkbcommon-devel wayland-devel libxkbcommon-x11`).

The window opens at the desktop app's size (1360 x 880, no smaller than 720 x 480) and follows the
system's light or dark theme.

## What works

| Part | What it does |
|---|---|
| Rail | Sessions is the shell. Workflows, Automations, Analytics, GitHub and Settings each take the whole context area with a notice. Help does nothing. |
| Session list | Click a session to open it. Click a project to fold it. Type in the filter. Drag the gutter to resize the list (200 to 500). |
| Header | The open session's title, project, harness, model, branch and path. The buttons show their tooltips and do nothing, except Abort. |
| Conversation | Scrolls, and stays at the bottom as messages arrive. Text can be selected. |
| Composer | Type and press Enter (or click Send). The message joins the conversation and a canned reply follows 2.5 seconds later. Shift+Enter adds a line; the field grows to seven lines. |
| Content panel | Click the collapsed rail on the right to open the session's plan; the chevron closes it. In a sheet too narrow for a third column the panel opens over the conversation, as in Fleet. |
| Status bar | The keyboard hints, the harness and the open session's token count. |

Keys (Cmd on macOS, Ctrl elsewhere):

| Key | Does |
|---|---|
| Cmd/Ctrl+B | Show or hide the session list |
| Cmd/Ctrl+[ and ] | Previous and next session |
| Esc | Interrupt the working session |
| Cmd/Ctrl+Shift+L | Switch between light and dark (the prototype's own key, not one of Fleet's) |

## What isn't built

Everything behind the shell: the server connection, real sessions, New session, the command
palette, the terminal, Settings and the other rail views, menus and dialogs, markdown and code
blocks in messages, tool call details, the content panel's tabs, and the phone layout.

## Pictures without a window

The prototype can draw itself to a file, which is how it was checked on a machine with no display.
On Linux without a GPU, install `mesa-vulkan-drivers` and set
`VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json`.

```sh
cargo build
target/debug/fleet-shell --list-scenes
target/debug/fleet-shell --snap dark.png --size 1280x760 --scale 2 --theme dark
target/debug/fleet-shell --snap plan.png --scene content-open --size 1280x760 --scale 2
```

Scenes: `light`, `dark`, `sidebar-closed`, `content-open`, `needs-input`, `automations`.

`scripts/tour.json` clicks and types through the shell (opens sessions, sends a message, opens the
plan, drags the gutter, hides the list, switches theme). Take a picture at one of its marks
(`sent`, `replied`, `plan`, `dragged`, `end`), or record the whole run (needs `ffmpeg`):

```sh
target/debug/fleet-shell --script scripts/tour.json --snap replied.png --at mark:replied
target/debug/fleet-shell --script scripts/tour.json --video tour.mp4
```

## In a browser

Rune also builds for the web (WebGPU on a canvas; experimental). The page needs a browser with
WebGPU, and HTTPS anywhere but `localhost`.

```sh
git clone https://github.com/anomalyco/rune ../../rune   # for its build script; any folder will do
cargo install wasm-bindgen-cli --version 0.2.130 --locked
bash ../../rune/scripts/web-build.sh fleet-shell --out target/web
python3 -m http.server -d target/web 8000   # then open http://localhost:8000
```

## How it's put together

| File | Holds |
|---|---|
| `src/lib.rs` | The state, the keys, Send and Interrupt, and the app's `AppSpec` |
| `src/theme.rs` | Fleet's colours and type sizes, copied from `client/src/assets/main.css` |
| `src/mock.rs` | The invented projects, sessions and messages |
| `src/view.rs` | `[rail][context]` over the status bar, and the resize gutter |
| `src/view/rail.rs` | The rail |
| `src/view/sessions.rs` | The session list |
| `src/view/sheet.rs` | The workspace sheet: header, conversation, composer, content panel |
| `src/view/status.rs` | The status bar |
| `src/view/parts.rs` | The working glyph, the running dot and the toolbar button |
| `src/icons.rs` | Lucide icons, generated |

Sizes are Fleet's CSS pixels at its 14px root font size, taken from the Vue components named at
the top of each file.

`Cargo.toml` pins Rune to one commit. Rune is pre-release and its API changes, so move the pin on
purpose (the `rev` appears five times).

### Icons

`src/icons.rs` is generated from the same lucide package Fleet's client uses. To add an icon, add
its name to this command and run it again (after `bun install` in `client/`):

```sh
python3 tools/gen-icons.py ../client/node_modules/lucide-vue-next \
  message-square workflow zap chart-column github settings circle-question-mark plus folder-plus \
  search chevron-down chevron-right archive square-terminal octagon-x git-fork ellipsis \
  chart-no-axes-column git-branch terminal file-text pencil paperclip circle-x arrow-up list-todo \
  circle circle-check
```

## Licences

- Rune: MIT.
- Inter (`assets/fonts/InterVariable.ttf`): SIL Open Font License, `assets/fonts/Inter-LICENSE.txt`.
- JetBrains Mono (`assets/fonts/JetBrainsMono-Variable.ttf`): SIL Open Font License,
  `assets/fonts/JetBrainsMono-OFL.txt`.
- Lucide icons (`src/icons.rs`): ISC, `tools/lucide-LICENSE.txt`.
