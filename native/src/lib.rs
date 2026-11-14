//! Weave Fleet's desktop shell, rebuilt in Rune: `[rail][session-list][conversation][content]`
//! and the status bar, drawn from Fleet's own tokens with invented data. A look-and-feel
//! prototype: nothing here talks to a Fleet server.
mod icons;
mod mock;
mod theme;
mod view;

use mock::{Msg, Project, Session, Status, ToolState};
use rune::{AppSpec, Image, ImageCache, KeyEvent, Key, TextEdit, Tx};

/// The rail entries, in Fleet's order. Only Sessions is rebuilt; the others show a notice.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Rail {
    Sessions,
    Workflows,
    Automations,
    Analytics,
    GitHub,
    Settings,
}

pub struct State {
    /// `None` follows the system's appearance, as Fleet's default "System" theme does.
    pub dark: Option<bool>,
    pub rail: Rail,
    pub sidebar_open: bool,
    pub sidebar_w: f32,
    pub content_open: bool,
    /// The workspace sheet's measured width; 0 until its first layout.
    pub sheet_w: f32,
    /// The theme the last frame was drawn in, so the theme key can flip what is on screen even
    /// while `dark` still follows the system.
    pub drawn_dark: std::cell::Cell<bool>,
    pub projects: Vec<Project>,
    /// The open session: (project, session).
    pub open: (usize, usize),
    pub filter: TextEdit,
    pub draft: TextEdit,
    /// Seconds since start, from `poll`.
    pub clock: f64,
    /// When the open session's turn started, on `clock`.
    pub working_since: f64,
    /// A canned reply on its way: (project, session, due).
    reply: Option<(usize, usize, f64)>,
    cache: Option<ImageCache>,
    pub logo: Option<Image>,
}

impl Default for State {
    fn default() -> Self {
        State {
            dark: None,
            rail: Rail::Sessions,
            sidebar_open: true,
            sidebar_w: view::SIDEBAR_W,
            content_open: false,
            sheet_w: 0.0,
            drawn_dark: std::cell::Cell::new(true),
            projects: mock::projects(),
            open: (0, 0),
            filter: TextEdit::new(""),
            draft: TextEdit::new(""),
            clock: 0.0,
            working_since: -18.0,
            reply: None,
            cache: None,
            logo: None,
        }
    }
}

impl State {
    pub fn session(&self) -> &Session {
        &self.projects[self.open.0].sessions[self.open.1]
    }

    /// Every session in list order, for the previous/next keys.
    fn order(&self) -> Vec<(usize, usize)> {
        self.projects.iter().enumerate().flat_map(|(p, project)| (0..project.sessions.len()).map(move |s| (p, s))).collect()
    }

    fn step(&mut self, by: isize) {
        let order = self.order();
        let at = order.iter().position(|&id| id == self.open).unwrap_or(0) as isize;
        self.open = order[(at + by).rem_euclid(order.len() as isize) as usize];
        self.projects[self.open.0].open = true;
    }
}

/// The composer's Send: the message joins the conversation, and a canned reply follows.
pub fn send(s: &mut State, tx: &mut Tx) {
    let text = s.draft.text.trim().to_string();
    if text.is_empty() {
        return;
    }
    let (p, i) = s.open;
    let session = &mut s.projects[p].sessions[i];
    session.messages.push(Msg::User(text));
    session.status = Status::Working;
    session.age = "now";
    session.dim = 1.0;
    s.draft.clear();
    s.working_since = s.clock;
    s.reply = Some((p, i, s.clock + 2.5));
    tx.scroll_to_bottom("stream");
    tx.wake_after(std::time::Duration::from_millis(2600));
}

/// Esc, and the composer's Interrupt button.
pub fn interrupt(s: &mut State) {
    let (p, i) = s.open;
    let session = &mut s.projects[p].sessions[i];
    if session.status == Status::Working {
        session.status = Status::Idle;
        end_tools(session);
        s.reply = None;
    }
}

/// A turn that ends takes its running tool calls with it.
fn end_tools(session: &mut Session) {
    for msg in &mut session.messages {
        if let Msg::Tool { state, .. } = msg {
            if *state == ToolState::Running {
                *state = ToolState::Plain;
            }
        }
    }
}

fn poll(s: &mut State, now: f64, tx: &mut Tx) -> bool {
    // Whole seconds only, so the "Working · 18s" label redraws once a second.
    let ticked = now.floor() != s.clock.floor();
    s.clock = now;
    match s.reply {
        Some((p, i, due)) if now >= due => {
            let session = &mut s.projects[p].sessions[i];
            end_tools(session);
            session.messages.push(Msg::Assistant(vec![mock::CANNED_REPLY]));
            session.status = Status::Idle;
            s.reply = None;
            tx.scroll_to_bottom("stream");
            true
        }
        _ => ticked && s.session().status == Status::Working,
    }
}

fn on_key(s: &mut State, ev: &KeyEvent, _: &mut Tx) -> bool {
    if ev.is_command('b') {
        s.sidebar_open = !s.sidebar_open;
    } else if ev.is_command('[') {
        s.step(-1);
    } else if ev.is_command(']') {
        s.step(1);
    } else if ev.is_command('l') && ev.mods.shift {
        // Not a Fleet shortcut: a quick way to see both themes in the prototype.
        s.dark = Some(!s.drawn_dark.get());
    } else if ev.key == Key::Escape {
        interrupt(s);
    } else {
        return false;
    }
    true
}

fn setup(s: &mut State, waker: rune::Waker) {
    let cache = s.cache.insert(ImageCache::new(waker));
    let logo = cache.load(&include_bytes!("../assets/weave_logo.png")[..]);
    // Decoded before the first frame, so a headless snapshot has the logo in it.
    #[cfg(not(target_arch = "wasm32"))]
    let _ = logo.wait();
    s.logo = Some(logo);
}

pub fn spec() -> AppSpec<State> {
    AppSpec {
        min_size: Some((720.0, 480.0)),
        scenes: &[
            ("light", |s| s.dark = Some(false)),
            ("dark", |s| s.dark = Some(true)),
            ("sidebar-closed", |s| s.sidebar_open = false),
            ("content-open", |s| s.content_open = true),
            ("needs-input", |s| s.open = (0, 1)),
            ("automations", |s| s.rail = Rail::Automations),
        ],
        fonts: &[include_bytes!("../assets/fonts/InterVariable.ttf"), include_bytes!("../assets/fonts/JetBrainsMono-Variable.ttf")],
        families: ("Inter", "JetBrains Mono"),
        setup: Some(setup),
        poll: Some(poll),
        on_key: Some(on_key),
        // The desktop app's own window: 1360 x 880, no smaller than 720 x 480 (`desktop/src/main.ts`).
        ..AppSpec::new("Weave Fleet", (1360.0, 880.0), State::default, view::root)
    }
}

/// The browser build's entry point; the desktop build starts from `main.rs`.
#[cfg_attr(target_arch = "wasm32", wasm_bindgen::prelude::wasm_bindgen(start))]
pub fn start() {
    rune::run(spec());
}
