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
