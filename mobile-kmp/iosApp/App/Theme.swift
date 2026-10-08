import SwiftUI

/// Weave Fleet's dark tokens, as on Android and the web.
enum Fleet {
    static let bg = Color(hex: 0x0d0d10)
    static let panel = Color(hex: 0x141418)
    static let card = Color(hex: 0x1b1b20)
    static let border = Color.white.opacity(0.075)
    static let borderStrong = Color.white.opacity(0.14)
    static let text = Color(hex: 0xe8e8ec)
    static let muted = Color(hex: 0x8e8e9a)
    static let faint = Color(hex: 0x5e5e6a)
    static let accent = Color(hex: 0x6366f1)
    static let running = Color(hex: 0x22c55e)
    static let waiting = Color(hex: 0xf59e0b)
    static let error = Color(hex: 0xef4444)

    static let radiusCard: CGFloat = 10
    static let radiusButton: CGFloat = 8
    static let radiusPanel: CGFloat = 12
    static let row: CGFloat = 44

    static func inter(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        let name: String
        switch weight {
        case .medium: name = "Inter-Medium"
        case .semibold: name = "Inter-SemiBold"
        case .bold, .heavy, .black: name = "Inter-Bold"
        default: name = "Inter-Regular"
        }
        return .custom(name, size: size)
    }

    static func mono(_ size: CGFloat = 13.5) -> Font { .custom("JetBrainsMono-Regular", size: size) }

    static let body = inter(15)
    static let small = inter(13)
    static let title = inter(16, .semibold)
    static let display = inter(26, .bold)
    static let label = inter(15, .medium)
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xff) / 255, green: Double((hex >> 8) & 0xff) / 255, blue: Double(hex & 0xff) / 255)
    }
}

struct FleetButtonStyle: ButtonStyle {
    var primary = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(Fleet.inter(15, .semibold))
            .foregroundStyle(primary ? Color.white : Fleet.text)
            .lineLimit(1)
            .frame(maxWidth: .infinity, minHeight: Fleet.row)
            .padding(.horizontal, 12)
            .background(primary ? Fleet.accent : Fleet.bg, in: RoundedRectangle(cornerRadius: Fleet.radiusButton))
            .overlay(RoundedRectangle(cornerRadius: Fleet.radiusButton).stroke(primary ? Color.clear : Fleet.borderStrong))
            .opacity(configuration.isPressed ? 0.75 : 1)
    }
}

/// Fleet's "Quad": four dots, one dimming in turn while the agent works.
struct TickingDots: View {
    var color: Color = Fleet.text
    var size: CGFloat = 12

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.3)) { timeline in
            let phase = Int(timeline.date.timeIntervalSinceReferenceDate / 0.3) % 4
            let r = size * 0.17
            Canvas { ctx, canvas in
                let points = [
                    CGPoint(x: r * 1.3, y: r * 1.3), CGPoint(x: canvas.width - r * 1.3, y: r * 1.3),
                    CGPoint(x: canvas.width - r * 1.3, y: canvas.height - r * 1.3), CGPoint(x: r * 1.3, y: canvas.height - r * 1.3),
                ]
                for (i, p) in points.enumerated() {
                    let dot = Path(ellipseIn: CGRect(x: p.x - r, y: p.y - r, width: r * 2, height: r * 2))
                    ctx.fill(dot, with: .color(color.opacity(i == phase ? 0.25 : 1)))
                }
            }
            .frame(width: size, height: size)
        }
    }
}

func elapsed(from ms: Int64?, now: Date) -> String {
    guard let ms, ms > 0 else { return "" }
    let s = max(0, Int64(now.timeIntervalSince1970 * 1000) - ms) / 1000
    switch s {
    case ..<60: return "\(s)s"
    case ..<3600: return "\(s / 60)m \(s % 60)s"
    case ..<86_400: return "\(s / 3600)h \((s % 3600) / 60)m"
    default: return "\(s / 86_400)d \((s % 86_400) / 3600)h"
    }
}
