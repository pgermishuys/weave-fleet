import FleetShared
import Foundation
import UserNotifications

/// The app's state: the saved pairing, the machine's client and its live session list. Kotlin calls back on the main
/// thread (the shared code runs on Dispatchers.Main), so the callbacks set published state directly.
final class AppModel: ObservableObject {
    static let shared = AppModel()

    let pairing = IosFleet.shared.pairing()
    let live = LiveActivities()

    @Published private(set) var client: FleetClient?
    @Published private(set) var sessions: SessionsUiState?
    @Published var path: [String] = []

    private var watches: [WatchHandle] = []

    private init() {
        if let saved = pairing.saved() { connect(saved) }
    }

    func connect(_ credentials: Credentials) {
        disconnect()
        let client = IosFleet.shared.client(credentials: credentials)
        self.client = client
        sessions = client.currentSessions()
        watches.append(client.watchSessions { [weak self] state in
            self?.sessions = state
            self?.live.sync(working: state.working, needsYou: state.needsYou)
        })
        watches.append(client.watchNotifications { [weak self] notification in
            self?.post(notification)
        })
    }

    func forget() {
        disconnect()
        pairing.forget()
    }

    private func disconnect() {
        watches.forEach { $0.cancel() }
        watches.removeAll()
        client?.close()
        client = nil
        sessions = nil
        path = []
    }

    func open(_ sessionId: String) {
        path = [sessionId]
    }

    /// A hub `session_notification` as a local notification, with Allow once / Deny or an inline answer. (There's no
    /// APNs yet: Fleet only does Web Push, so this works while the app is running or briefly in the background.)
    private func post(_ n: SessionNotification) {
        let content = UNMutableNotificationContent()
        content.title = n.title
        content.body = n.body
        content.sound = .default
        content.threadIdentifier = n.sessionId
        content.userInfo = ["sessionId": n.sessionId, "requestId": n.requestId ?? ""]
        let kind = n.kind ?? n.reason

        func add() {
            UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: "ask:\(n.sessionId)", content: content, trigger: nil))
        }

        switch kind {
        case "permission" where n.requestId != nil:
            content.categoryIdentifier = AppDelegate.permissionCategory
            add()
        case "question", "needs_you":
            client?.peekQuestion(sessionId: n.sessionId) { question in
                if let question {
                    content.body = question.question
                    content.categoryIdentifier = AppDelegate.questionCategory
                    content.userInfo = ["sessionId": n.sessionId, "requestId": question.requestId]
                }
                add()
            }
        default:
            add()
        }
    }
}
