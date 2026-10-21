import AppKit
import UserNotifications

struct UploadJob: Identifiable {
    enum State { case uploading, done(String), failed(String) }
    let id = UUID()
    let name: String
    var progress: Double = 0
    var state: State = .uploading
    var file: URL? = nil              // set for real uploads, so a failed or cancelled one can be retried
    var deleteAfter = false           // the file is a temporary one that goes away once it's uploaded (or cleared)

    var isUpload: Bool { file != nil }
    var isRunning: Bool { if case .uploading = state { return true } else { return false } }
    /// The whole file has been sent and Haunt hasn't answered yet.
    var isWaitingForServer: Bool { isUpload && isRunning && progress >= 0.999 }
}

/// Shared by the menu bar, hotkeys and the Upload tab, so every upload shows up in one list.
final class UploadManager: ObservableObject {
    static let shared = UploadManager()
    @Published private(set) var jobs: [UploadJob] = []

    /// Text for the menu-bar item while uploads run ("" when idle).
    var onStatus: ((String) -> Void)?

    private var tasks: [UUID: Task<Void, Never>] = [:]

    var hasActiveUploads: Bool { jobs.contains { $0.isUpload && $0.isRunning } }

    func enqueue(_ file: URL, deleteAfter: Bool = false) {
        DispatchQueue.main.async { self.start(file, deleteAfter: deleteAfter) }
    }

    /// Surfaces a problem that happened before an upload could start (e.g. a failed screenshot).
    func report(_ name: String, _ message: String) {
        DispatchQueue.main.async {
            var job = UploadJob(name: name)
            job.state = .failed(message)
            self.jobs.insert(job, at: 0)
        }
    }

    /// A row with a progress bar for work that isn't an upload (e.g. rendering an edited recording). Call on the main thread.
    func beginTask(_ name: String) -> UUID {
        let job = UploadJob(name: name)
        jobs.insert(job, at: 0)
        publishStatus()
        return job.id
    }

    func taskProgress(_ id: UUID, _ p: Double) {
        DispatchQueue.main.async {
            self.update(id) { $0.progress = p }
            self.publishStatus()
        }
    }

    /// Removes the row, or leaves it as a red error row if `error` is given.
    func endTask(_ id: UUID, error: String? = nil) {
        DispatchQueue.main.async {
            if let error { self.update(id) { $0.state = .failed(error) } }
            else { self.jobs.removeAll { $0.id == id } }
            self.publishStatus()
        }
    }

    func clearFinished() {
        for j in jobs where !j.isRunning && j.deleteAfter {           // temp files kept for a possible retry
            if let f = j.file { try? FileManager.default.removeItem(at: f) }
        }
        jobs.removeAll { !$0.isRunning }
    }

    /// Stops an upload that's sending or waiting for Haunt. Nothing is lost: the file stays so it can be retried.
    func cancel(_ id: UUID) {
        guard jobs.contains(where: { $0.id == id && $0.isUpload && $0.isRunning }) else { return }
        tasks[id]?.cancel()
        update(id) { $0.state = .failed("Cancelled. If it had already finished sending, it may still show up on Haunt.") }
        publishStatus()
    }

    func cancelAll() {
        for j in jobs where j.isUpload && j.isRunning { cancel(j.id) }
    }

    func retry(_ id: UUID) {
        guard let j = jobs.first(where: { $0.id == id }), j.isUpload, !j.isRunning else { return }
        guard let f = j.file, FileManager.default.fileExists(atPath: f.path) else {
            update(id) { $0.state = .failed("The file is gone, so it can't be retried.") }
            return
        }
        launch(id)
    }

    private func start(_ file: URL, deleteAfter: Bool) {
        var isDir: ObjCBool = false
        let exists = FileManager.default.fileExists(atPath: file.path, isDirectory: &isDir)
        var job = UploadJob(name: file.lastPathComponent)
        if !exists || isDir.boolValue {
            job.state = .failed(isDir.boolValue ? "Folders can't be uploaded." : "File not found.")
            jobs.insert(job, at: 0)
            return
        }
        job.file = file
        job.deleteAfter = deleteAfter
        jobs.insert(job, at: 0)
        launch(job.id)
    }

    /// Runs (or re-runs) the upload for a job. The temporary file is only deleted after a successful upload, so retry works.
    private func launch(_ id: UUID) {
        guard let job = jobs.first(where: { $0.id == id }), let file = job.file else { return }
        update(id) { $0.state = .uploading; $0.progress = 0 }
        publishStatus()
        let deleteAfter = job.deleteAfter

        tasks[id] = Task { @MainActor in
            let settings = Settings.shared
            // Strip location/camera info from photos unless the user turned it off.
            var toUpload = file
            var cleanedDir: URL?
            if settings.stripMetadata, let cleaned = await Task.detached(operation: { MetadataStripper.cleaned(file) }).value {
                toUpload = cleaned
                cleanedDir = cleaned.deletingLastPathComponent()
            }
            defer {
                if let cleanedDir { try? FileManager.default.removeItem(at: cleanedDir) }
                tasks[id] = nil
                publishStatus()
            }
            if Task.isCancelled { return }
            do {
                let result = try await HauntAPI.upload(fileURL: toUpload) { [weak self] p in
                    DispatchQueue.main.async {
                        guard let self, self.jobs.first(where: { $0.id == id })?.isRunning == true else { return }
                        self.update(id) { $0.progress = p }
                        self.publishStatus()
                    }
                }
                if Task.isCancelled { return }
                update(id) { $0.progress = 1; $0.state = .done(result.url) }
                HistoryStore.shared.add(url: result.url, name: result.fileName ?? file.lastPathComponent)

                let link = settings.linkFormat.format(url: result.url, name: file.lastPathComponent)
                if settings.copyLink {
                    if settings.copyImage, let img = NSImage(contentsOf: file), file.isImage {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.writeObjects([img])
                    } else {
                        Self.copy(link)
                    }
                }
                if settings.openInBrowser, let u = URL(string: result.url) { NSWorkspace.shared.open(u) }
                settings.play(settings.successSound)
                if settings.notifyOnSuccess { Self.notify("Uploaded", result.url) }
                if settings.showToast {
                    let thumb = await ToastController.thumbnail(for: file)
                    ToastController.shared.show(name: result.fileName ?? file.lastPathComponent, copyText: link, thumbnail: thumb)
                }
                if deleteAfter { try? FileManager.default.removeItem(at: file) }
            } catch {
                let cancelled = Task.isCancelled || (error as? HauntError).map { if case .cancelled = $0 { return true } else { return false } } == true
                    || (error as? URLError)?.code == .cancelled
                if cancelled { return }                       // cancel() already updated the row
                update(id) { $0.state = .failed(error.localizedDescription) }
                settings.play(settings.errorSound)
                if settings.notifyOnError { Self.notify("Upload failed", error.localizedDescription) }
            }
        }
    }

    private func update(_ id: UUID, _ change: (inout UploadJob) -> Void) {
        guard let i = jobs.firstIndex(where: { $0.id == id }) else { return }
        change(&jobs[i])
    }

    private func publishStatus() {
        let active = jobs.filter { $0.isRunning }
        guard !active.isEmpty else { onStatus?(""); return }
        let avg = active.map(\.progress).reduce(0, +) / Double(active.count)
        onStatus?(avg >= 0.999 ? " …" : " \(Int(avg * 100))%")           // "…" = all sent, waiting for Haunt
    }

    static func copy(_ s: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(s, forType: .string)
    }

    static func notify(_ title: String, _ body: String) {
        guard Bundle.main.bundleIdentifier != nil else { NSLog("\(title): \(body)"); return }
        let c = UNMutableNotificationContent()
        c.title = title
        c.body = body
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil))
    }
}

extension URL {
    var isImage: Bool { ["png", "jpg", "jpeg", "gif", "webp", "avif", "heic", "tiff"].contains(pathExtension.lowercased()) }
}
