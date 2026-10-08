import FleetShared
import UIKit
import UserNotifications

/// Notification categories and their actions. Allow once / Deny and a typed answer call Fleet's REST API through the
/// shared client, without opening the app's UI.
final class AppDelegate: NSObject, UIApplicationDelegate, UNUserNotificationCenterDelegate {
    static let permissionCategory = "FLEET_PERMISSION"
    static let questionCategory = "FLEET_QUESTION"
    static let allowAction = "ALLOW_ONCE"
    static let denyAction = "DENY"
    static let answerAction = "ANSWER"

    func application(_ application: UIApplication, didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        // Allowing runs something on your computer, so it asks for Face ID first; denying doesn't.
        let allow = UNNotificationAction(identifier: Self.allowAction, title: "Allow once", options: [.authenticationRequired])
        let deny = UNNotificationAction(identifier: Self.denyAction, title: "Deny", options: [.destructive])
        let answer = UNTextInputNotificationAction(
            identifier: Self.answerAction,
            title: "Answer",
            options: [.authenticationRequired],
            textInputButtonTitle: "Send",
            textInputPlaceholder: "Your answer"
        )
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Self.permissionCategory, actions: [allow, deny], intentIdentifiers: [], options: []),
            UNNotificationCategory(identifier: Self.questionCategory, actions: [answer], intentIdentifiers: [], options: []),
        ])
        center.requestAuthorization(options: [.alert, .sound, .badge]) { _, _ in }
        return true
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list, .sound])
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        let info = response.notification.request.content.userInfo
        guard let sessionId = info["sessionId"] as? String else { completionHandler(); return }
        let requestId = info["requestId"] as? String ?? ""
        // The app may have been launched just for this action: AppModel.shared reconnects from the Keychain.
        guard let client = AppModel.shared.client else { completionHandler(); return }

        switch response.actionIdentifier {
        case Self.allowAction:
            client.replyToPermission(sessionId: sessionId, requestId: requestId, allow: true) { _ in completionHandler() }
        case Self.denyAction:
            client.replyToPermission(sessionId: sessionId, requestId: requestId, allow: false) { _ in completionHandler() }
        case Self.answerAction:
            let text = (response as? UNTextInputNotificationResponse)?.userText ?? ""
            guard !text.isEmpty else { completionHandler(); return }
            client.answerQuestion(sessionId: sessionId, requestId: requestId, answer: text) { _ in completionHandler() }
        default:
            AppModel.shared.open(sessionId)
            completionHandler()
        }
    }
}
