// A release build on Windows opens its window without a console window behind it.
#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

fn main() {
    fleet_shell::start();
}
