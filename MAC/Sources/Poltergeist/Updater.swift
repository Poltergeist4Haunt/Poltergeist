import AppKit

enum AppInfo {
    /// The version this build calls itself (from Info.plist). The website's downloads/version.txt holds the newest one.
    static let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0"
    static let site = "https://poltergeist4haunt.github.io/Poltergeist-Site/"
    static let versionURL = URL(string: site + "downloads/version.txt")!
    static let downloadURL = URL(string: site + "downloads/Poltergeist-mac.zip")!
    static let creatorURL = URL(string: "https://haunt.gg/pix")!
    static let friendURL = URL(string: "https://haunt.gg/SandMan")!
}

/// Looks at the website for a newer version, and swaps the app for it (then opens it again) if the user says yes.
enum Updater {
    /// The version on the website, or nil if it can't be reached.
    static func latest() async -> String? {
        var req = URLRequest(url: URL(string: AppInfo.versionURL.absoluteString + "?t=\(Int(Date().timeIntervalSince1970))")!)
        req.cachePolicy = .reloadIgnoringLocalCacheData
        req.timeoutInterval = 15
        guard let (data, resp) = try? await URLSession.shared.data(for: req),
              (resp as? HTTPURLResponse)?.statusCode == 200,
              let text = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !text.isEmpty, text.count <= 24, text.allSatisfy({ $0.isLetter || $0.isNumber || $0 == "." || $0 == "-" }) else { return nil }
        return text
    }

    /// On launch (or "Check for updates"). With `manual` it also says "you're up to date".
    @MainActor static func check(manual: Bool) async {
        guard let newest = await latest() else {
            if manual { UploadManager.notify("Couldn't check for updates", "Check your internet and try again.") }
            return
        }
        if newest == AppInfo.version {
            if manual { UploadManager.notify("You're up to date", "Poltergeist \(AppInfo.version) is the newest version.") }
            return
        }
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Your version is out of date"
        alert.informativeText = "You have \(AppInfo.version), the newest is \(newest). Want to update and relaunch? It only takes a moment."
        alert.addButton(withTitle: "Yeah")
        alert.addButton(withTitle: "Nah")
        alert.icon = NSApp.applicationIconImage
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        do { try await install() }
        catch { UploadManager.notify("Couldn't update", error.localizedDescription + " You can download it again from the site.") }
    }

    /// Downloads the new app, then hands over to a small script that waits for us to quit, swaps the folder and relaunches.
    @MainActor private static func install() async throws {
        UploadManager.notify("Updating Poltergeist…", "Downloading the new version.")
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("poltergeist-update-\(UUID().uuidString.prefix(8))")
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        let (tmp, resp) = try await URLSession.shared.download(from: AppInfo.downloadURL)
        guard (resp as? HTTPURLResponse)?.statusCode == 200 else { throw HauntError.server("The download failed.") }
        let zip = work.appendingPathComponent("new.zip")
        try FileManager.default.moveItem(at: tmp, to: zip)

        let unzip = Process()
        unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        unzip.arguments = ["-x", "-k", zip.path, work.path]
        try unzip.run(); unzip.waitUntilExit()
        let newApp = work.appendingPathComponent("Poltergeist.app")
        guard unzip.terminationStatus == 0, FileManager.default.fileExists(atPath: newApp.path) else { throw HauntError.server("The download looks wrong.") }

        let current = Bundle.main.bundlePath
        let script = work.appendingPathComponent("update.sh")
        let text = """
        #!/bin/sh
        while kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do sleep 0.5; done
        rm -rf "\(current).old"
        mv "\(current)" "\(current).old" && mv "\(newApp.path)" "\(current)" && rm -rf "\(current).old"
        xattr -dr com.apple.quarantine "\(current)" 2>/dev/null
        open "\(current)"
        rm -rf "\(work.path)"
        """
        try text.write(to: script, atomically: true, encoding: .utf8)
        let run = Process()
        run.executableURL = URL(fileURLWithPath: "/bin/sh")
        run.arguments = ["-c", "nohup sh '\(script.path)' >/dev/null 2>&1 &"]
        try run.run()
        NSApp.terminate(nil)
    }
}
