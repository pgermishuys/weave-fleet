import ActivityKit
import FleetShared
import Foundation

/// One Live Activity per session that's working or needs you, started and updated from the hub's session list and
/// ended when the session goes idle. Updates are local (pushType nil): with no APNs, they stop when the app is
/// suspended, but the elapsed time keeps ticking because the system draws it from `startedAt`.
final class LiveActivities {
    private var activities: [String: Activity<FleetActivityAttributes>] = [:]
    private var states: [String: FleetActivityAttributes.ContentState] = [:]

    func sync(working: [SessionListItem], needsYou: [SessionListItem]) {
        guard ActivityAuthorizationInfo().areActivitiesEnabled else { return }
        let wanted = needsYou.map { ($0, true) } + working.map { ($0, false) }
        let ids = Set(wanted.map { $0.0.id })

        for (id, activity) in activities where !ids.contains(id) {
            Task { await activity.end(nil, dismissalPolicy: .immediate) }
            activities[id] = nil
            states[id] = nil
        }

        for (item, needs) in wanted {
            let startedAt = states[item.id]?.startedAt ?? Date()
            let state = FleetActivityAttributes.ContentState(status: needs ? "Needs you" : "Working", needsYou: needs, startedAt: startedAt)
            guard states[item.id] != state else { continue }
            states[item.id] = state
            let content = ActivityContent(state: state, staleDate: nil)
            if let activity = activities[item.id] {
                Task { await activity.update(content) }
            } else {
                let attributes = FleetActivityAttributes(sessionId: item.id, title: item.title, folder: item.folder)
                activities[item.id] = try? Activity.request(attributes: attributes, content: content, pushType: nil)
            }
        }
    }
}
