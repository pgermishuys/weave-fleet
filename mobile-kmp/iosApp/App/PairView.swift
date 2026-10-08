import FleetShared
import SwiftUI
import UIKit

struct PairView: View {
    @ObservedObject var model: AppModel
    @State private var address = ""
    @State private var code = ""
    @State private var name = UIDevice.current.name
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Pair with a machine").font(Fleet.display).foregroundStyle(Fleet.text).padding(.top, 24)
                Text("On your computer, open Fleet → Settings → Machines → This machine → Add a phone. Type its address and the 8-character code, or paste the pairing link.")
                    .font(Fleet.body).foregroundStyle(Fleet.muted)
                field("Address", text: $address, placeholder: "https://my-machine.example.com", mono: false, id: "address")
                    .keyboardType(.URL)
                field("Code", text: $code, placeholder: "XXXX-XXXX", mono: true, id: "code")
                field("This phone's name", text: $name, placeholder: "iPhone", mono: false, id: "device-name")
                if let error { Text(error).font(Fleet.small).foregroundStyle(Fleet.error) }
                Button(busy ? "Pairing…" : "Pair") { pair() }
                    .buttonStyle(FleetButtonStyle(primary: true))
                    .disabled(busy || address.isEmpty || (code.count < 8 && !address.contains("#p=")))
                    .accessibilityIdentifier("pair")
                Text("The code works once and runs out after a few minutes. Fleet keeps only a hash of this phone's token.")
                    .font(Fleet.small).foregroundStyle(Fleet.muted)
            }
            .padding(20)
        }
    }

    private func field(_ label: String, text: Binding<String>, placeholder: String, mono: Bool, id: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label).font(Fleet.small).foregroundStyle(Fleet.muted)
            TextField("", text: text, prompt: Text(placeholder).foregroundStyle(Fleet.faint))
                .font(mono ? Fleet.mono(15) : Fleet.body)
                .foregroundStyle(Fleet.text)
                .autocorrectionDisabled()
                .textInputAutocapitalization(mono ? .characters : .never)
                .accessibilityIdentifier(id)
                .padding(.horizontal, 14).padding(.vertical, 13)
                .background(Fleet.card, in: RoundedRectangle(cornerRadius: Fleet.radiusCard))
                .overlay(RoundedRectangle(cornerRadius: Fleet.radiusCard).stroke(Fleet.border))
        }
    }

    private func pair() {
        busy = true
        error = nil
        model.pairing.pair(address: address, code: code, deviceName: name.isEmpty ? "iPhone" : name, platform: "ios") { credentials, problem in
            busy = false
            if let credentials { model.connect(credentials) } else { error = problem ?? "Couldn't pair" }
        }
    }
}
