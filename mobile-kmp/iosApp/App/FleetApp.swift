import FleetShared
import SwiftUI

@main
struct FleetApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @ObservedObject private var model = AppModel.shared

    var body: some Scene {
        WindowGroup {
            RootView(model: model)
                .preferredColorScheme(.dark)
                .onOpenURL { url in
                    // fleetkmp://session/<id>, from the Live Activity
                    if url.host == "session", let id = url.pathComponents.last { model.open(id) }
                }
        }
    }
}

struct RootView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        ZStack {
            Fleet.bg.ignoresSafeArea()
            if let client = model.client {
                NavigationStack(path: $model.path) {
                    SessionsView(model: model, client: client)
                        .navigationDestination(for: String.self) { id in
                            SessionView(client: client, sessionId: id)
                        }
                }
            } else {
                PairView(model: model)
            }
        }
    }
}
