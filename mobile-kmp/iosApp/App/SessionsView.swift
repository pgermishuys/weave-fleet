import FleetShared
import SwiftUI

struct SessionsView: View {
    @ObservedObject var model: AppModel
    let client: FleetClient

    var body: some View {
        let state = model.sessions
        TimelineView(.periodic(from: .now, by: 1)) { timeline in
            VStack(spacing: 0) {
                HStack {
                    Text("W").font(Fleet.inter(22, .bold)).foregroundStyle(Fleet.accent)
                    Spacer()
                    Button { model.forget() } label: {
                        HStack(spacing: 8) {
                            Circle().fill(state?.connection == HubState.connected ? Fleet.running : Fleet.waiting).frame(width: 7, height: 7)
                            Text(state?.machineName ?? "").font(Fleet.label).foregroundStyle(Fleet.text)
                        }
                        .padding(.horizontal, 12).padding(.vertical, 8)
                        .overlay(RoundedRectangle(cornerRadius: Fleet.radiusButton).stroke(Fleet.borderStrong))
                    }
                }
                .padding(.horizontal, 16).padding(.vertical, 12)

                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        if let state { content(state, now: timeline.date) }
                    }
                    .padding(.bottom, 24)
                }
                .background(Fleet.panel, in: RoundedRectangle(cornerRadius: Fleet.radiusPanel))
                .padding(.horizontal, 8)
                .refreshable { client.refreshSessions() }
            }
        }
        .toolbar(.hidden, for: .navigationBar)
        .background(Fleet.bg)
    }

    @ViewBuilder
    private func content(_ state: SessionsUiState, now: Date) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(state.needsYou.isEmpty ? "Sessions" : "Needs you").font(Fleet.display).foregroundStyle(Fleet.text)
            HStack(spacing: 8) {
                Circle().fill(state.connection == HubState.connected ? Fleet.running : Fleet.waiting).frame(width: 7, height: 7)
                Text("\(state.machineName) · \(state.connection == HubState.connected ? "online" : "connecting…")").font(Fleet.body).foregroundStyle(Fleet.muted)
            }
        }
        .padding(.horizontal, 18).padding(.top, 20).padding(.bottom, 12)

        ForEach(state.needsYou, id: \.id) { item in
            NeedsYouCard(client: client, item: item, detail: state.details[item.id], now: now) { model.open(item.id) }
        }
        if !state.working.isEmpty {
            section("Working", state.working.count)
            ForEach(state.working, id: \.id) { row($0, now: now) }
        }
        if !state.other.isEmpty {
            section("Recent", state.other.count)
            ForEach(state.other, id: \.id) { row($0, now: now) }
        }
    }

    private func section(_ title: String, _ count: Int) -> some View {
        HStack(spacing: 8) {
            Text(title).font(Fleet.inter(15, .medium)).foregroundStyle(Fleet.muted)
            Text("\(count)").font(Fleet.body).foregroundStyle(Fleet.faint)
        }
        .padding(.horizontal, 18).padding(.top, 18).padding(.bottom, 4)
    }

    private func row(_ item: SessionListItem, now: Date) -> some View {
        Button { model.open(item.id) } label: {
            HStack(spacing: 0) {
                StatusMark(status: item.activityStatus).frame(width: 26, alignment: .leading)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.title).font(Fleet.inter(16, .medium)).foregroundStyle(Fleet.text).lineLimit(1)
                    let sub = [item.folder, item.branch ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
                    if !sub.isEmpty { Text(sub).font(Fleet.inter(14)).foregroundStyle(Fleet.muted).lineLimit(1) }
                }
                Spacer(minLength: 10)
                Text(elapsed(from: (item.session.time?.updated ?? item.session.time?.created)?.int64Value, now: now))
                    .font(Fleet.inter(14)).foregroundStyle(Fleet.muted)
            }
            .padding(.horizontal, 18).padding(.vertical, 10)
            .frame(minHeight: 60)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

struct StatusMark: View {
    let status: String?

    var body: some View {
        switch status {
        case "busy", "working", "retry", "delegating": TickingDots(color: Fleet.text, size: 12)
        case "waiting_input": Circle().fill(Fleet.waiting).frame(width: 8, height: 8)
        default: Color.clear.frame(width: 12, height: 12)
        }
    }
}

private struct NeedsYouCard: View {
    let client: FleetClient
    let item: SessionListItem
    let detail: NeedsYou?
    let now: Date
    let open: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                Image(systemName: detail?.permission != nil ? "exclamationmark.shield" : "questionmark.circle").foregroundStyle(Fleet.waiting)
                Text(detail?.permission?.heading ?? (detail?.question != nil ? "Question" : "Needs you")).font(Fleet.inter(15, .semibold)).foregroundStyle(Fleet.text)
                Spacer()
            }
            Button(action: open) {
                HStack {
                    Text(item.title).font(Fleet.inter(16, .medium)).foregroundStyle(Fleet.text).lineLimit(1)
                    Spacer()
                    Image(systemName: "chevron.right").foregroundStyle(Fleet.muted)
                }
            }
            .buttonStyle(.plain)
            if let ask = detail?.permission {
                if let title = ask.title { CommandBox(text: ask.kind == "shell" ? "$ \(title)" : title) }
                HStack(spacing: 10) {
                    Button("Allow once") { client.replyToPermission(sessionId: item.id, requestId: ask.id, allow: true) { _ in client.refreshSessions() } }
                        .buttonStyle(FleetButtonStyle(primary: true))
                    Button("Deny") { client.replyToPermission(sessionId: item.id, requestId: ask.id, allow: false) { _ in client.refreshSessions() } }
                        .buttonStyle(FleetButtonStyle())
                }
            } else if let question = detail?.question {
                Text(question.question).font(Fleet.body).foregroundStyle(Fleet.text)
                HStack(spacing: 10) {
                    ForEach(Array(question.options.prefix(2)), id: \.self) { option in
                        Button(option) { client.answerQuestion(sessionId: item.id, requestId: question.requestId, answer: option) { _ in client.refreshSessions() } }
                            .buttonStyle(FleetButtonStyle())
                    }
                    Button("More…", action: open).buttonStyle(FleetButtonStyle())
                }
            } else {
                Button("Open", action: open).buttonStyle(FleetButtonStyle())
            }
        }
        .padding(16)
        .background(Fleet.waiting.opacity(0.06), in: RoundedRectangle(cornerRadius: Fleet.radiusCard))
        .overlay(RoundedRectangle(cornerRadius: Fleet.radiusCard).stroke(Fleet.waiting.opacity(0.55)))
        .padding(.horizontal, 12).padding(.vertical, 6)
    }
}

struct CommandBox: View {
    let text: String

    var body: some View {
        Text(text)
            .font(Fleet.mono())
            .foregroundStyle(Fleet.text)
            .lineLimit(3)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 14).padding(.vertical, 12)
            .background(Fleet.bg, in: RoundedRectangle(cornerRadius: Fleet.radiusButton))
            .overlay(RoundedRectangle(cornerRadius: Fleet.radiusButton).stroke(Fleet.border))
    }
}
