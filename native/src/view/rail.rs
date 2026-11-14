//! The rail (`IconRail.vue`): 48px wide, 40px icon buttons, the logo on top and GitHub,
//! Settings and Help at the bottom.
use crate::icons::*;
use crate::theme::Theme;
use crate::{Rail, State};
use rune::{Color, Cursor, El, Icon, col, el, icon, spacer};

pub fn rail(s: &State, t: &Theme) -> El {
    // The logo file is 511×234, drawn `object-fit: contain` in a 32px box.
    let mut logo = el().size(32.0, 32.0).mb(4.0).center();
    if let Some(image) = &s.logo {
        logo = logo.child(el().size(32.0, 32.0 * 234.0 / 511.0).pixels(image.pixels()));
    }
    col().w(48.0).h_full().py(8.0).items_center()
        .child(logo)
        .child(item(s, t, Some(Rail::Sessions), &MESSAGE_SQUARE, "Sessions", false))
        // Workflows is a Labs feature, off by default: Fleet dims it and adds an amber dot.
        .child(item(s, t, Some(Rail::Workflows), &WORKFLOW, "Workflows (switched off)", true))
        .child(item(s, t, Some(Rail::Automations), &ZAP, "Automations", false))
        .child(divider(t))
        .child(item(s, t, Some(Rail::Analytics), &CHART_COLUMN, "Analytics", false))
        .child(spacer())
        .child(divider(t))
        .child(item(s, t, Some(Rail::GitHub), &GITHUB, "GitHub", false))
        .child(item(s, t, Some(Rail::Settings), &SETTINGS, "Settings", false))
        .child(item(s, t, None, &CIRCLE_QUESTION_MARK, "Help", false))
}

fn divider(t: &Theme) -> El {
    el().size(24.0, 1.0).my(4.0).fill(t.border)
}

/// One rail button. `which` is the view it opens; Help opens a menu in Fleet and nothing here.
fn item(s: &State, t: &Theme, which: Option<Rail>, glyph: &'static Icon, label: &str, off: bool) -> El {
    let active = which == Some(s.rail);
    let mut face = el().size(18.0, 18.0).child(
        icon(glyph, 18.0).color(if active { t.text } else { t.muted }).group_color(t.text).opacity(if off { 0.55 } else { 1.0 }),
    );
    if off {
        face = face.child(el().absolute(17.0, -3.0).size(6.0, 6.0).radius(3.0).fill(t.idle));
    }
    el().key(format!("rail-{}", label.split(' ').next().unwrap_or(label).to_lowercase()))
        .size(40.0, 40.0).mb(2.0).center().radius(8.0)
        .fill(if active { t.wash(9.0) } else { Color::TRANSPARENT })
        .hover(if active { Color::TRANSPARENT } else { t.wash(5.0) })
        .hover_group().cursor(Cursor::Pointer).help(label).label(label)
        .on_press(move |s: &mut State| {
            if let Some(which) = which {
                s.rail = which;
            }
        })
        .child(face)
}
