//! Fleet's design tokens, copied from `client/src/assets/main.css` (`:root` is dark,
//! `:root[data-theme="light"]` is light). Rune's windows blend in sRGB as CSS does, so the
//! `rgba()` and `color-mix()` values carry over as they are.
use rune::tok::TextStyle;
use rune::{Color, Shadow};

fn c(s: &str) -> Color {
    rune::color::hex(s).expect("a colour literal")
}

#[derive(Clone, Copy)]
pub struct Theme {
    pub dark: bool,
    /// `--main-bg`: the window behind the rail, the session list and the status bar.
    pub main_bg: Color,
    /// `--panel-bg`: the workspace sheet.
    pub panel_bg: Color,
    /// `--card-bg`: the composer and the New session button.
    pub card_bg: Color,
    pub border: Color,
    pub text: Color,
    pub muted: Color,
    pub accent: Color,
    pub running: Color,
    pub idle: Color,
    pub error: Color,
}

impl Theme {
    pub fn of(dark: bool) -> Theme {
        if dark {
            Theme {
                dark,
                main_bg: c("#0d0d10"),
                panel_bg: c("#141418"),
                card_bg: c("#1b1b20"),
                border: Color::WHITE.alpha(0.075),
                text: c("#e8e8ec"),
                muted: c("#8e8e9a"),
                accent: c("#6366f1"),
                running: c("#22c55e"),
                idle: c("#f59e0b"),
                error: c("#ef4444"),
            }
        } else {
            Theme {
                dark,
                main_bg: c("#F3F2EF"),
                panel_bg: c("#FFFFFF"),
                card_bg: c("#FFFFFF"),
                border: c("#1A1918").alpha(0.09),
                text: c("#1A1918"),
                muted: c("#6E6A65"),
                accent: c("#5B6EC7"),
                running: c("#16a34a"),
                idle: c("#d97706"),
                error: c("#dc2626"),
            }
        }
    }

    /// `color-mix(in srgb, var(--text) N%, transparent)`: Fleet's hover and selection washes.
    pub fn wash(&self, percent: f32) -> Color {
        self.text.alpha(percent / 100.0)
    }

    /// `--sheet-shadow`: none in dark, two soft layers in light.
    pub fn sheet_shadows(&self) -> Vec<Shadow> {
        if self.dark {
            return Vec::new();
        }
        let ink = c("#1A1918");
        vec![
            Shadow { x: 0.0, y: 1.0, blur: 2.0, spread: 0.0, color: ink.alpha(0.04), inset: false },
            Shadow { x: 0.0, y: 12.0, blur: 32.0, spread: -18.0, color: ink.alpha(0.18), inset: false },
        ]
    }
}

/// Inter at `size`/`weight` with a CSS line-height multiplier.
pub const fn sans(size: f32, weight: f32, line: f32) -> TextStyle {
    TextStyle::new(size, weight).lh(size * line)
}

/// JetBrains Mono at `size` with a CSS line-height multiplier.
pub const fn mono(size: f32, weight: f32, line: f32) -> TextStyle {
    TextStyle::new(size, weight).mono().lh(size * line)
}
