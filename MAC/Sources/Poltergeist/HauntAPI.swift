import Foundation
import UniformTypeIdentifiers

enum HauntError: LocalizedError {
    case noKey
    case server(String)
    case badResponse
    case cancelled

    var errorDescription: String? {
        switch self {
        case .noKey: return "No API key set. Open Settings → Account."
        case .server(let m): return m
        case .badResponse: return "Haunt returned something unexpected."
        case .cancelled: return "Cancelled."
        }
    }
}

/// One file on the user's Haunt image host.
struct RemoteUpload: Identifiable {
    let id: String
    let name: String
    let url: String
    let thumbURL: String?
    let size: Int
    let created: Date?
    let isVideo: Bool
}

struct UploadResult {
    let url: String
    let id: String?
    let fileName: String?
}

private final class ProgressDelegate: NSObject, URLSessionTaskDelegate {
    let onProgress: (Double) -> Void
    init(_ onProgress: @escaping (Double) -> Void) { self.onProgress = onProgress }
    func urlSession(_ session: URLSession, task: URLSessionTask, didSendBodyData bytesSent: Int64,
                    totalBytesSent: Int64, totalBytesExpectedToSend: Int64) {
        guard totalBytesExpectedToSend > 0 else { return }
        onProgress(Double(totalBytesSent) / Double(totalBytesExpectedToSend))
    }
}

/// Same request ShareX's haunt.gg.sxcu makes:
/// POST /api/imagehost/upload, `x-api-key` header, multipart field `file`, link in `url`.
enum HauntAPI {
    static var uploadURL = URL(string: "https://haunt.gg/api/imagehost/upload")!   // var so tests can point it at a local server

    /// `timeoutIntervalForRequest` is an idle timeout: it also covers the wait for Haunt's reply after the file is sent,
    /// so a stalled upload fails after a few minutes instead of hanging.
    private static let session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForRequest = 240
        c.timeoutIntervalForResource = 3600
        return URLSession(configuration: c)
    }()

    static func upload(fileURL: URL, progress: @escaping (Double) -> Void) async throws -> UploadResult {
        let key = Settings.shared.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { throw HauntError.noKey }

        let boundary = "Poltergeist-\(UUID().uuidString)"
        let bodyURL = try writeMultipart(fileURL: fileURL, boundary: boundary)
        defer { try? FileManager.default.removeItem(at: bodyURL) }

        var req = URLRequest(url: uploadURL)
        req.httpMethod = "POST"
        req.setValue(key, forHTTPHeaderField: "x-api-key")
        req.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        let data: Data, resp: URLResponse
        do {
            (data, resp) = try await session.upload(for: req, fromFile: bodyURL, delegate: ProgressDelegate(progress))
        } catch let e as URLError {
            switch e.code {
            case .cancelled: throw HauntError.cancelled
            case .timedOut:
                throw HauntError.server("Haunt didn't answer in time. The file may still have gone through, so check haunt.gg before retrying.")
            case .notConnectedToInternet, .networkConnectionLost:
                throw HauntError.server("The connection dropped. Check your internet and retry.")
            default: throw e
            }
        }
        let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0

        if !(200..<300).contains(status) {
            let msg = (json?["error"] as? String) ?? (json?["message"] as? String) ?? "Upload failed (HTTP \(status))."
            throw HauntError.server(msg)
        }
        guard let url = json?["url"] as? String else { throw HauntError.badResponse }
        return UploadResult(url: url, id: json?["id"] as? String, fileName: json?["fileName"] as? String)
    }

    /// Your own files (GET /api/imagehost/uploads, needs the key's imagehost:read permission). The reply is read leniently:
    /// a bare array, or an object holding the list under uploads / files / items / data / results.
    static func listUploads(limit: Int = 100) async throws -> [RemoteUpload] {
        let key = Settings.shared.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { throw HauntError.noKey }
        var comps = URLComponents(url: uploadURL.deletingLastPathComponent().appendingPathComponent("uploads"), resolvingAgainstBaseURL: false)!
        comps.queryItems = [URLQueryItem(name: "limit", value: String(limit))]
        var req = URLRequest(url: comps.url!)
        req.setValue(key, forHTTPHeaderField: "x-api-key")
        req.timeoutInterval = 25
        let data: Data, resp: URLResponse
        do { (data, resp) = try await URLSession.shared.data(for: req) }
        catch { throw HauntError.server("Couldn't reach Haunt. Check your internet.") }
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if status == 401 || status == 403 {
            throw HauntError.server("Your API key can't read your uploads. Edit the key on the Haunt dashboard and turn on the Read permission for Image Host.")
        }
        if status >= 400 { throw HauntError.server("Haunt said no (HTTP \(status)).") }
        guard let root = try? JSONSerialization.jsonObject(with: data), let list = findList(root) else { throw HauntError.badResponse }
        func str(_ e: [String: Any], _ names: [String]) -> String? {
            for n in names { if let v = e[n] as? String, !v.trimmingCharacters(in: .whitespaces).isEmpty { return v } }
            return nil
        }
        func num(_ e: [String: Any], _ names: [String]) -> Int {
            for n in names { if let v = e[n] as? Int { return v }; if let v = e[n] as? Double { return Int(v) }; if let s = e[n] as? String, let v = Int(s) { return v } }
            return 0
        }
        let iso = ISO8601DateFormatter()
        iso.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let iso2 = ISO8601DateFormatter()
        return list.compactMap { e in
            guard let url = str(e, ["url", "fileUrl", "link", "rawUrl", "cdnUrl"]) else { return nil }
            var name = str(e, ["fileName", "name", "originalName", "filename"]) ?? ""
            let id = str(e, ["id", "uploadId", "_id"]) ?? name
            if name.isEmpty { name = id }
            let type = str(e, ["type", "mimeType", "mime", "contentType", "kind"]) ?? ""
            let ext = (name as NSString).pathExtension.lowercased()
            let when = str(e, ["createdAt", "uploadedAt", "uploadAt", "date", "created"])
            let date = when.flatMap { iso.date(from: $0) ?? iso2.date(from: $0) }
            return RemoteUpload(id: id, name: name, url: url, thumbURL: str(e, ["thumbnailUrl", "thumbnail", "thumb", "previewUrl", "preview"]),
                                size: num(e, ["size", "fileSize", "bytes"]), created: date,
                                isVideo: type.hasPrefix("video") || type == "video" || ["mp4", "webm", "mov", "mkv"].contains(ext))
        }
    }

    private static func findList(_ root: Any) -> [[String: Any]]? {
        if let a = root as? [[String: Any]] { return a }
        guard let o = root as? [String: Any] else { return nil }
        for k in ["uploads", "files", "items", "data", "results"] {
            if let a = o[k] as? [[String: Any]] { return a }
            if let inner = o[k], let a = findList(inner) { return a }
        }
        return nil
    }

    private static func mimeType(for url: URL) -> String {
        UTType(filenameExtension: url.pathExtension.lowercased())?.preferredMIMEType ?? "application/octet-stream"
    }

    /// Streams the multipart body to a temp file so large videos don't sit in memory.
    private static func writeMultipart(fileURL: URL, boundary: String) throws -> URL {
        let out = FileManager.default.temporaryDirectory.appendingPathComponent("poltergeist-\(UUID().uuidString).body")
        FileManager.default.createFile(atPath: out.path, contents: nil)
        let w = try FileHandle(forWritingTo: out)
        let r = try FileHandle(forReadingFrom: fileURL)
        defer { try? w.close(); try? r.close() }

        let name = fileURL.lastPathComponent.replacingOccurrences(of: "\"", with: "_")
        let head = "--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"\(name)\"\r\n"
            + "Content-Type: \(mimeType(for: fileURL))\r\n\r\n"
        try w.write(contentsOf: Data(head.utf8))
        while let chunk = try r.read(upToCount: 1 << 20), !chunk.isEmpty {
            try w.write(contentsOf: chunk)
        }
        try w.write(contentsOf: Data("\r\n--\(boundary)--\r\n".utf8))
        return out
    }
}
