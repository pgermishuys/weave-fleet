import ActivityKit
import SwiftUI
import WidgetKit

private enum W {
    static let bg = Color(red: 0x0d / 255, green: 0x0d / 255, blue: 0x10 / 255)
    static let text = Color(red: 0xe8 / 255, green: 0xe8 / 255, blue: 0xec / 255)
    static let muted = Color(red: 0x8e / 255, green: 0x8e / 255, blue: 0x9a / 255)
    static let accent = Color(red: 0x63 / 255, green: 0x66 / 255, blue: 0xf1 / 255)
    static let running = Color(red: 0x22 / 255, green: 0xc5 / 255, blue: 0x5e / 255)
    static let waiting = Color(red: 0xf5 / 255, green: 0x9e / 255, blue: 0x0b / 255)

    static func color(_ state: FleetActivityAttributes.ContentState) -> Color { state.needsYou ? waiting : running }
}

/// Lock screen banner and Dynamic Island for a working session: its title, "Working" or "Needs you", and how long the
/// turn has run (drawn by the system from `startedAt`, so it ticks without the app).
struct FleetLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: FleetActivityAttributes.self) { context in
            HStack(spacing: 12) {
                Circle().fill(W.color(context.state)).frame(width: 10, height: 10)
                VStack(alignment: .leading, spacing: 2) {
                    Text(context.attributes.title).font(.headline).foregroundStyle(W.text).lineLimit(1)
                    Text("\(context.state.status) · \(context.attributes.folder)").font(.subheadline).foregroundStyle(W.muted).lineLimit(1)
                }
                Spacer()
                Text(timerInterval: context.state.startedAt...Date.distantFuture, countsDown: false)
                    .monospacedDigit().font(.subheadline).foregroundStyle(W.muted)
                    .frame(maxWidth: 64, alignment: .trailing)
            }
            .padding(16)
            .activityBackgroundTint(W.bg)
            .activitySystemActionForegroundColor(W.text)
            .widgetURL(URL(string: "fleetkmp://session/\(context.attributes.sessionId)"))
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Label {
                        Text(context.state.status).foregroundStyle(W.color(context.state))
                    } icon: {
                        Circle().fill(W.color(context.state)).frame(width: 8, height: 8)
                    }
                    .font(.caption)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text(timerInterval: context.state.startedAt...Date.distantFuture, countsDown: false)
                        .monospacedDigit().font(.caption).foregroundStyle(W.muted)
                        .frame(maxWidth: 56, alignment: .trailing)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    Text(context.attributes.title).font(.subheadline).foregroundStyle(W.text).lineLimit(1)
                }
            } compactLeading: {
                Circle().fill(W.color(context.state)).frame(width: 8, height: 8)
            } compactTrailing: {
                Text(timerInterval: context.state.startedAt...Date.distantFuture, countsDown: false)
                    .monospacedDigit().font(.caption2).frame(maxWidth: 40)
            } minimal: {
                Circle().fill(W.color(context.state)).frame(width: 8, height: 8)
            }
            .widgetURL(URL(string: "fleetkmp://session/\(context.attributes.sessionId)"))
            .keylineTint(W.accent)
        }
    }
}
