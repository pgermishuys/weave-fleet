//! Invented projects, sessions and conversations. Nothing here comes from a real Fleet.

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Status {
    /// A turn is running: the four-dot glyph, and no time label.
    Working,
    /// Waiting on the user: the amber diamond and a "Needs input" pill.
    NeedsInput,
    /// Nothing live: an empty glyph slot and how long ago it was last used.
    Idle,
}

pub enum Msg {
    User(String),
    Assistant(Vec<&'static str>),
    Tool { name: &'static str, detail: &'static str, state: ToolState },
}

#[derive(Clone, Copy, PartialEq)]
pub enum ToolState {
    Running,
    /// Lines added and removed by an edit.
    Diff(u32, u32),
    /// How long a shell command took.
    Took(&'static str),
    Plain,
}

pub struct Session {
    pub title: &'static str,
    pub status: Status,
    /// Compact age ("2m", "3h", "4d") and how far Fleet dims the row for it (1.0, 0.55 or 0.40).
    pub age: &'static str,
    pub dim: f32,
    pub branch: &'static str,
    pub messages: Vec<Msg>,
    pub tokens: u32,
}

pub struct Project {
    pub name: &'static str,
    pub path: &'static str,
    pub open: bool,
    pub sessions: Vec<Session>,
}

fn tool(name: &'static str, detail: &'static str, state: ToolState) -> Msg {
    Msg::Tool { name, detail, state }
}

fn session(title: &'static str, status: Status, age: &'static str, dim: f32, branch: &'static str, messages: Vec<Msg>) -> Session {
    let tokens = 1_840 + 3_217 * messages.len() as u32;
    Session { title, status, age, dim, branch, messages, tokens }
}

/// A short stand-in conversation for the sessions that aren't written out in full.
fn brief(ask: &'static str, answer: &'static str) -> Vec<Msg> {
    vec![Msg::User(ask.into()), Msg::Assistant(vec![answer])]
}

pub fn projects() -> Vec<Project> {
    vec![
        Project {
            name: "Orchard",
            path: "~/code/orchard",
            open: true,
            sessions: vec![
                session(
                    "Move the image cache to disk",
                    Status::Working,
                    "now",
                    1.0,
                    "cache-on-disk",
                    vec![
                        Msg::User("The image cache lives in memory and we lose it on every restart. Can you move it to disk, and keep the 200 MB limit?".into()),
                        Msg::Assistant(vec![
                            "I'll read how the cache is used first, then swap the store underneath it so callers don't change.",
                        ]),
                        tool("Read", "src/cache/image_cache.ts", ToolState::Plain),
                        tool("Read", "src/cache/eviction.ts", ToolState::Plain),
                        Msg::Assistant(vec![
                            "The cache is one map behind get and put, and eviction already works from a size total. So the change is small: a disk store with the same two calls, an index file for sizes and last use, and eviction that deletes files instead of dropping entries.",
                            "I'll write the store, move the tests over to a temporary folder, and run them.",
                        ]),
                        tool("Edit", "src/cache/disk_store.ts", ToolState::Diff(86, 0)),
                        tool("Edit", "src/cache/image_cache.ts", ToolState::Diff(14, 31)),
                        tool("Bash", "bun test src/cache", ToolState::Took("4.2s")),
                        Msg::User("Also make sure a half-written file can't be served after a crash.".into()),
                        tool("Bash", "bun test src/cache --rerun-each 20", ToolState::Running),
                    ],
                ),
                session(
                    "Which plan does the checkout page show?",
                    Status::NeedsInput,
                    "",
                    1.0,
                    "main",
                    vec![
                        Msg::User("The checkout page shows the yearly plan first. Should it?".into()),
                        Msg::Assistant(vec![
                            "It shows whichever plan comes first in plans.json, and yearly was added at the top last month. Nothing in the code picks one on purpose.",
                            "I can make monthly the default, or keep yearly first and mark it as the default explicitly. Which do you want?",
                        ]),
                    ],
                ),
                session(
                    "Fix the flaky checkout test",
                    Status::Idle,
                    "12m",
                    1.0,
                    "fix-checkout-flake",
                    brief(
                        "checkout.spec fails about one run in ten. Find out why.",
                        "The test reads the total before the discount request returns. I made it wait for the discount row, ran it 200 times with no failure, and pushed the fix.",
                    ),
                ),
                session(
                    "Upgrade the date picker",
                    Status::Idle,
                    "3h",
                    1.0,
                    "date-picker-4",
                    brief(
                        "Move us to version 4 of the date picker.",
                        "Done. Two props were renamed and the week now starts from the locale, so I removed our own week-start setting. All 48 tests pass.",
                    ),
                ),
                session(
                    "Tidy the release notes",
                    Status::Idle,
                    "2d",
                    0.55,
                    "main",
                    brief(
                        "Group the release notes by area and drop the internal items.",
                        "Grouped into Shop, Accounts and Fixes. I left out nine items that only touched tests or build scripts.",
                    ),
                ),
            ],
        },
        Project {
            name: "Lighthouse",
            path: "~/code/lighthouse",
            open: true,
            sessions: vec![
                session(
                    "Why is the build slow on Windows?",
                    Status::Idle,
                    "1h",
                    1.0,
                    "main",
                    brief(
                        "The Windows build takes 14 minutes and Linux takes 5. Why?",
                        "Nine of the fourteen minutes are the antivirus scanning node_modules. Excluding the build folder in the runner's settings brings it to about six minutes.",
                    ),
                ),
                session(
                    "Sketch the onboarding copy",
                    Status::Idle,
                    "5h",
                    1.0,
                    "onboarding",
                    brief(
                        "Write three short screens of onboarding copy.",
                        "Three screens: what Lighthouse watches, how to add your first site, and where alerts go. Each is one heading and one sentence.",
                    ),
                ),
                session(
                    "Add a dark map style",
                    Status::Idle,
                    "4d",
                    0.4,
                    "dark-map",
                    brief(
                        "The map is blinding at night. Add a dark style.",
                        "Added a dark style that follows the app's theme. Roads and labels keep the same contrast as the light one.",
                    ),
                ),
            ],
        },
        Project {
            name: "Field notes",
            path: "~/notes/field",
            open: false,
            sessions: vec![session(
                "Summarise last week's notes",
                Status::Idle,
                "6d",
                0.4,
                "main",
                brief("Summarise last week's notes in five lines.", "Five lines, one per day you wrote."),
            )],
        },
    ]
}

/// What the agent in this prototype answers to anything you send.
pub const CANNED_REPLY: &str = "This is a layout prototype, so no agent is listening. The message you sent is drawn by Rune the same way Fleet's client draws it.";
