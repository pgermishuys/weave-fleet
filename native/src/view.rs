//! The shell: `[rail][context]` over the status bar. Sizes are Fleet's CSS pixels at its default
//! 14px root (see `client/src/components/layout/AppShell.vue`).
mod parts;
mod rail;
mod sessions;
mod sheet;
mod status;

use crate::theme::{Theme, sans};
use crate::{Rail, State};
use rune::{Cursor, Cx, Drag, El, Icon, Phase, col, el, icon, row, text};

/// The session list's width, and how far its gutter can drag it (`ContextPanel.vue`).
pub const SIDEBAR_W: f32 = 306.0;
const SIDEBAR_MIN: f32 = 200.0;
const SIDEBAR_MAX: f32 = 500.0;

pub fn root(s: &State, cx: &mut Cx<State>) -> El {
    let t = Theme::of(s.dark.unwrap_or_else(|| cx.system_dark()));
    s.drawn_dark.set(t.dark);
    let mut main = row().w_full().grow(1.0).min_h(0.0).padding(8.0).child(rail::rail(s, &t));
    main = match s.rail {
        Rail::Sessions if s.sidebar_open => main.child(sessions::panel(s, &t).ml(8.0)).child(gutter(&t)).child(sheet::sheet(s, &t)),
        Rail::Sessions => main.child(sheet::sheet(s, &t).ml(8.0)),
        other => main.child(notice(&t, other).ml(8.0)),
    };
    col().w_full().h_full().fill(t.main_bg).child(main).child(status::bar(s, &t))
}

/// The resize handle between the session list and the sheet: 8px wide in the 16px between them.
fn gutter(t: &Theme) -> El {
    el().key("sidebar-gutter").w(8.0).h_full().mx(4.0).radius(4.0).hover(t.accent.alpha(0.15)).press(t.accent.alpha(0.15))
        .cursor(Cursor::ResizeColumn)
        .on_drag(|s: &mut State, d: &mut Drag, _| {
            if d.phase == Phase::Down {
                d.data = s.sidebar_w;
            }
            s.sidebar_w = if d.cancelled() { d.data } else { (d.data + d.dx).clamp(SIDEBAR_MIN, SIDEBAR_MAX) };
        })
}

/// What a rail entry other than Sessions shows: it owns the whole context area, as Fleet's rule
/// says, and says plainly that this prototype stops at the Sessions shell.
fn notice(t: &Theme, which: Rail) -> El {
    let (name, glyph): (&str, &'static Icon) = match which {
        Rail::Workflows => ("Workflows", &crate::icons::WORKFLOW),
        Rail::Automations => ("Automations", &crate::icons::ZAP),
        Rail::Analytics => ("Analytics", &crate::icons::CHART_COLUMN),
        Rail::GitHub => ("GitHub", &crate::icons::GITHUB),
        Rail::Settings | Rail::Sessions => ("Settings", &crate::icons::SETTINGS),
    };
    col().grow(1.0).h_full().center().gap(10.0).fill(t.panel_bg).border(1.0, t.border).radius(12.0)
        .child(icon(glyph, 22.0).color(t.muted))
        .child(text(name).font(sans(14.0, 600.0, 1.3)).color(t.text))
        .child(text("Only the Sessions shell is rebuilt in this prototype.").font(sans(13.0, 400.0, 1.4)).color(t.muted))
}
