import FleetShared
import SwiftUI

/// Holds the shared SessionController and republishes its state for SwiftUI.
final class SessionHolder: ObservableObject {
    let controller: SessionController
    @Published private(set) var state: SessionUiState
    private var watch: WatchHandle?

    init(client: FleetClient, sessionId: String) {
        controller = client.openSession(sessionId: sessionId)
        state = controller.current()
        watch = controller.watch { [weak self] in self?.state = $0 }
    }

    deinit {
        watch?.cancel()
        controller.close()
    }
}

/// Kotlin's sealed PhoneItem arrives as an Objective-C protocol; this gives ForEach a plain Identifiable.
private struct ItemBox: Identifiable {
    let item: PhoneItem
    var id: String { item.key }
}

struct SessionView: View {
    @StateObject private var holder: SessionHolder
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase

    init(client: FleetClient, sessionId: String) {
        _holder = StateObject(wrappedValue: SessionHolder(client: client, sessionId: sessionId))
    }

    var body: some View {
        let state = holder.state
        TimelineView(.periodic(from: .now, by: 1)) { timeline in
            VStack(spacing: 0) {
                header(state, now: timeline.date)
                Rectangle().fill(Fleet.border).frame(height: 1)
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 14) {
                            ForEach(state.items.map(ItemBox.init)) { box in itemView(box.item).id(box.id) }
                            if state.isWorking && !state.needsYou {
                                HStack(spacing: 10) {
                                    TickingDots(color: Fleet.text, size: 11)
                                    Text("Working · \(elapsed(from: state.workingSinceMs?.int64Value, now: timeline.date))").font(Fleet.body).foregroundStyle(Fleet.muted)
                                }
                                .id("working")
                            }
                            Color.clear.frame(height: 1).id("end")
                        }
                        .padding(16)
                    }
                    .defaultScrollAnchor(.bottom)
                    .onChange(of: state.items.count) { _, _ in withAnimation { proxy.scrollTo("end", anchor: .bottom) } }
                }
                if let error = state.error { Text(error).font(Fleet.small).foregroundStyle(Fleet.error).padding(.horizontal, 16) }
                Dock(holder: holder, state: state).padding(10)
            }
            .background(Fleet.panel, in: RoundedRectangle(cornerRadius: Fleet.radiusPanel))
            .padding(.horizontal, 8).padding(.vertical, 6)
        }
        .background(Fleet.bg)
        .toolbar(.hidden, for: .navigationBar)
        // In the background the phone isn't looking: Fleet may notify about this session then.
        .onChange(of: scenePhase) { _, phase in holder.controller.setFocused(focused: phase == .active) }
    }

    private func header(_ state: SessionUiState, now: Date) -> some View {
        HStack(spacing: 4) {
            Button { dismiss() } label: {
                Image(systemName: "chevron.left").font(.system(size: 18, weight: .semibold)).foregroundStyle(Fleet.text).frame(width: 44, height: 44)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(state.title).font(Fleet.title).foregroundStyle(Fleet.text).lineLimit(1)
                HStack(spacing: 6) {
                    if state.needsYou { Circle().fill(Fleet.waiting).frame(width: 7, height: 7) }
                    else if state.isWorking { TickingDots(color: Fleet.muted, size: 10) }
                    let status = state.needsYou ? "Needs you" : (state.isWorking ? "Working" : "Idle")
                    let parts = [status, state.isWorking ? elapsed(from: state.workingSinceMs?.int64Value, now: now) : "", state.folder]
                        + (state.connection == HubState.connected ? [] : ["reconnecting…"])
                    Text(parts.filter { !$0.isEmpty }.joined(separator: " · ")).font(Fleet.inter(14)).foregroundStyle(Fleet.muted).lineLimit(1)
                }
            }
            Spacer()
        }
        .padding(.horizontal, 8).padding(.vertical, 8)
    }

    @ViewBuilder
    private func itemView(_ item: PhoneItem) -> some View {
        if let tools = IosFleet.shared.toolsOf(item: item) {
            ToolsBox(tools: tools)
        } else if let block = IosFleet.shared.blockOf(item: item) {
            switch IosFleet.shared.kindOf(block: block) {
            case "user":
                let user = block as! PhoneBlockUser
                HStack {
                    Spacer(minLength: 48)
                    Text(user.text).font(Fleet.body).foregroundStyle(Fleet.text)
                        .padding(.horizontal, 14).padding(.vertical, 10)
                        .background(Fleet.card, in: RoundedRectangle(cornerRadius: 14))
                        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Fleet.border))
                }
            case "text":
                MarkdownText(text: (block as! PhoneBlockText).text)
            case "question":
                let q = block as! PhoneBlockQuestion
                HStack(alignment: .top, spacing: 10) {
                    Image(systemName: "questionmark.circle").foregroundStyle(q.pending ? Fleet.waiting : Fleet.muted)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(q.question).font(Fleet.body).foregroundStyle(q.pending ? Fleet.text : Fleet.muted)
                        Text(q.answer.map { "You answered: \($0)" } ?? "Waiting for your answer").font(Fleet.small).foregroundStyle(q.pending ? Fleet.waiting : Fleet.muted)
                    }
                }
            case "error":
                let e = block as! PhoneBlockError
                VStack(alignment: .leading, spacing: 4) {
                    Text(e.limit ? "Hit a limit" : "The turn failed").font(Fleet.inter(15, .semibold)).foregroundStyle(Fleet.error)
                    Text(e.text).font(Fleet.body).foregroundStyle(Fleet.text)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(14)
                .background(Fleet.error.opacity(0.07), in: RoundedRectangle(cornerRadius: Fleet.radiusCard))
                .overlay(RoundedRectangle(cornerRadius: Fleet.radiusCard).stroke(Fleet.error.opacity(0.45)))
            default:
                EmptyView()
            }
        }
    }
}

private struct ToolsBox: View {
    let tools: PhoneItemTools
    @State private var expanded = false

    var body: some View {
        VStack(spacing: 0) {
            ForEach(expanded ? tools.rows : tools.shown, id: \.id) { row in
                HStack(spacing: 12) {
                    Image(systemName: icon(row)).font(.system(size: 14)).foregroundStyle(Fleet.muted).frame(width: 18)
                    Text(row.label).font(Fleet.inter(15, .semibold)).foregroundStyle(Fleet.text)
                    Text(row.detail).font(row.subagent ? Fleet.body : Fleet.mono()).foregroundStyle(Fleet.muted).lineLimit(1).truncationMode(.tail)
                    Spacer(minLength: 6)
                    if !row.subagent {
                        switch row.result {
                        case .done: Image(systemName: "checkmark").font(.system(size: 13, weight: .semibold)).foregroundStyle(Fleet.running)
                        case .failed: Image(systemName: "xmark").font(.system(size: 13, weight: .semibold)).foregroundStyle(Fleet.error)
                        default: Circle().fill(Fleet.running).frame(width: 8, height: 8)
                        }
                    }
                }
                .frame(height: Fleet.row)
                .padding(.horizontal, 14)
            }
            if tools.hidden > 0 && !expanded {
                Button { expanded = true } label: {
                    Text("\(tools.hidden) more steps").font(Fleet.body).foregroundStyle(Fleet.muted)
                        .frame(maxWidth: .infinity, minHeight: Fleet.row, alignment: .leading).padding(.horizontal, 14)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.vertical, 4)
        .background(Fleet.card, in: RoundedRectangle(cornerRadius: Fleet.radiusCard))
        .overlay(RoundedRectangle(cornerRadius: Fleet.radiusCard).stroke(Fleet.border))
    }

    private func icon(_ row: ToolRow) -> String {
        if row.subagent { return row.result == .running ? "ellipsis" : "checkmark.circle" }
        switch row.category {
        case .run: return "terminal"
        case .search: return "magnifyingglass"
        case .edit: return "pencil"
        default: return "doc.text"
        }
    }
}

/// Markdown-ish: SwiftUI's inline Markdown per paragraph, with list markers kept and fenced code in mono.
struct MarkdownText: View {
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(Array(paragraphs.enumerated()), id: \.offset) { _, paragraph in
                if paragraph.hasPrefix("```") {
                    Text(paragraph.split(separator: "\n").dropFirst().filter { !$0.hasPrefix("```") }.joined(separator: "\n"))
                        .font(Fleet.mono()).foregroundStyle(Fleet.text)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(12)
                        .background(Fleet.bg, in: RoundedRectangle(cornerRadius: Fleet.radiusButton))
                } else {
                    let attributed = (try? AttributedString(markdown: paragraph, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(paragraph)
                    Text(attributed).font(paragraph.hasPrefix("#") ? Fleet.inter(17, .semibold) : Fleet.body).foregroundStyle(Fleet.text)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var paragraphs: [String] {
        text.components(separatedBy: "\n\n").map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            .map { $0.hasPrefix("#") ? String($0.drop(while: { $0 == "#" }).drop(while: { $0 == " " })) : $0 }
    }
}

// MARK: - The bottom: whatever needs you, else the composer

private struct Dock: View {
    let holder: SessionHolder
    let state: SessionUiState
    @State private var draft = ""

    var body: some View {
        if let ask = state.permission {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 12) {
                    Image(systemName: "exclamationmark.shield").foregroundStyle(Fleet.waiting)
                    Text(ask.heading).font(Fleet.inter(15, .semibold)).foregroundStyle(Fleet.text)
                }
                CommandBox(text: [ask.directory, ask.kind == "shell" ? "$ \(ask.title ?? ask.tool)" : (ask.title ?? ask.tool)].compactMap { $0 }.joined(separator: "\n"))
                HStack(spacing: 10) {
                    Button("Allow once") { holder.controller.replyToDocked(allow: true) }.buttonStyle(FleetButtonStyle(primary: true))
                    Button("Deny") { holder.controller.replyToDocked(allow: false) }.buttonStyle(FleetButtonStyle())
                }
                Text("The agent waits until you answer.").font(Fleet.small).foregroundStyle(Fleet.muted)
            }
            .padding(16)
            .background(Fleet.card, in: RoundedRectangle(cornerRadius: Fleet.radiusCard))
            .overlay(RoundedRectangle(cornerRadius: Fleet.radiusCard).stroke(Fleet.waiting.opacity(0.55)))
        } else if let question = state.question {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 12) {
                    Image(systemName: "questionmark.circle").foregroundStyle(Fleet.waiting)
                    Text(question.header).font(Fleet.inter(15, .semibold)).foregroundStyle(Fleet.text)
                }
                Text(question.question).font(Fleet.body).foregroundStyle(Fleet.text)
                ForEach(Array(question.options.enumerated()), id: \.offset) { index, option in
                    Button { holder.controller.answerDocked(answer: option) } label: {
                        HStack(spacing: 12) {
                            Text("\(index + 1)").font(Fleet.small).foregroundStyle(Fleet.text)
                                .frame(width: 24, height: 24)
                                .background(index == 0 ? Fleet.accent : Fleet.card, in: RoundedRectangle(cornerRadius: 6))
                            Text(option).font(Fleet.label).foregroundStyle(Fleet.text)
                            Spacer()
                        }
                        .padding(.horizontal, 12).frame(minHeight: Fleet.row)
                        .background(Fleet.bg, in: RoundedRectangle(cornerRadius: Fleet.radiusCard))
                        .overlay(RoundedRectangle(cornerRadius: Fleet.radiusCard).stroke(Fleet.border))
                    }
                    .buttonStyle(.plain)
                }
                if question.custom { input("Or type an answer…", id: "answer") { holder.controller.answerDocked(answer: $0) } }
            }
            .padding(16)
            .background(Fleet.card, in: RoundedRectangle(cornerRadius: Fleet.radiusCard))
            .overlay(RoundedRectangle(cornerRadius: Fleet.radiusCard).stroke(Fleet.waiting.opacity(0.55)))
        } else {
            input("Message \(state.folder.isEmpty ? "the agent" : state.folder)…", id: "composer") { holder.controller.send(text: $0) }
        }
    }

    private func input(_ placeholder: String, id: String, send: @escaping (String) -> Void) -> some View {
        HStack(alignment: .bottom, spacing: 8) {
            TextField("", text: $draft, prompt: Text(placeholder).foregroundStyle(Fleet.faint), axis: .vertical)
                .font(Fleet.body).foregroundStyle(Fleet.text).lineLimit(1...6)
                .padding(.vertical, 9)
                .accessibilityIdentifier(id)
            let ready = !draft.trimmingCharacters(in: .whitespaces).isEmpty && !state.sending
            Button {
                send(draft)
                draft = ""
            } label: {
                Image(systemName: "arrow.up").font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(ready ? Color.white : Fleet.muted)
                    .frame(width: 40, height: 40)
                    .background(ready ? Fleet.accent : Fleet.borderStrong, in: Circle())
            }
            .disabled(!ready)
            .accessibilityIdentifier(id == "composer" ? "send" : "\(id)-send")
        }
        .padding(.leading, 16).padding(.trailing, 8).padding(.vertical, 8)
        .background(Fleet.card, in: RoundedRectangle(cornerRadius: Fleet.radiusPanel))
        .overlay(RoundedRectangle(cornerRadius: Fleet.radiusPanel).stroke(Fleet.borderStrong))
    }
}
