import ActivityKit
import Foundation

/// The Live Activity for one session: lock screen + Dynamic Island. Shared by the app (which starts and updates it
/// from hub events) and the widget extension (which draws it). Nothing Kotlin crosses into the extension.
struct FleetActivityAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        /// "Working" or "Needs you".
        var status: String
        var needsYou: Bool
        /// When the turn started; the elapsed time is drawn from it by the system, so it ticks without updates.
        var startedAt: Date
    }

    var sessionId: String
    var title: String
    var folder: String
}
