//! Pieces more than one panel draws: the status glyphs and the small toolbar button.
use crate::State;
use crate::theme::Theme;
use rune::{Color, Cursor, Cycle, El, Icon, el, icon};

/// Fleet's "working" glyph (`StatusGlyph.vue`, `status-quad`): four 3px dots in a 2×2 grid, one
/// dim at a time, moving clockwise every 200ms. Holds still under reduced motion.
pub fn quad(color: Color) -> El {
    // (left, top, the fraction of the 800ms loop at which this dot dims)
    const DOTS: [(f32, f32, f32); 4] = [(0.0, 0.0, 0.0), (5.0, 0.0, 0.25), (5.0, 5.0, 0.5), (0.0, 5.0, 0.75)];
    static STEPS: [(f32, f32); 4] = [(0.0, 0.16), (0.2499, 0.16), (0.25, 1.0), (1.0, 1.0)];
    let mut glyph = el().size(8.0, 8.0);
    for (left, top, phase) in DOTS {
        let still = if phase == 0.0 { 0.16 } else { 1.0 };
        glyph = glyph.child(
            el().absolute(left, top).size(3.0, 3.0).radius(1.5).fill(color).cycle(Cycle { period: 0.8, phase, stops: &STEPS, still, fps: 30.0 }),
        );
    }
    glyph
}

/// The running dot: an 8px disc pulsing 1 → 0.3 → 1 over 1.2s.
pub fn pulse(color: Color) -> El {
    static STOPS: [(f32, f32); 3] = [(0.0, 1.0), (0.5, 0.3), (1.0, 1.0)];
    el().size(8.0, 8.0).radius(4.0).fill(color).cycle(Cycle { period: 1.2, phase: 0.0, stops: &STOPS, still: 1.0, fps: 30.0 })
}

/// The "waiting on you" glyph: a filled diamond in an 8px slot.
pub fn diamond(color: Color) -> El {
    el().size(8.0, 8.0).center().child(el().size(5.6, 5.6).fill(color).rotate(std::f32::consts::FRAC_PI_4))
}

/// Fleet's `toolbar-icon` button: 24.5px square (Tailwind `size-7` at a 14px root), muted icon,
/// a 6% wash and the text colour on hover.
pub fn tool_button(t: &Theme, key: &str, glyph: &'static Icon, size: f32, tip: &str) -> El {
    el().key(key).size(24.5, 24.5).center().radius(8.0).hover(t.wash(6.0)).press(t.wash(10.0)).hover_group()
        .cursor(Cursor::Pointer).help(tip).label(tip)
        .on_press(|_: &mut State| {})
        .child(icon(glyph, size).color(t.muted).group_color(t.text))
}
