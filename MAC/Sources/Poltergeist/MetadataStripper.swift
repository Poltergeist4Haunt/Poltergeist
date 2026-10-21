import Foundation
import ImageIO

/// Removes EXIF/GPS/XMP/IPTC from photos before upload. JPEG/PNG/HEIC/TIFF only; GIF, WebP, AVIF and video are left untouched.
enum MetadataStripper {
    private static let handled: Set<String> = ["public.jpeg", "public.png", "public.heic", "public.tiff"]

    /// Returns a cleaned copy in a temp folder (same file name), or nil if there's nothing to do or it failed.
    /// The caller deletes the returned file's parent folder.
    static func cleaned(_ url: URL) -> URL? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
              let type = CGImageSourceGetType(src) as String?,
              handled.contains(type),
              CGImageSourceGetCount(src) == 1 else { return nil }

        let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any] ?? [:]
        let orientation = (props[kCGImagePropertyOrientation] as? Int) ?? 1

        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("polt-\(UUID().uuidString)")
        guard (try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)) != nil else { return nil }
        let out = dir.appendingPathComponent(url.lastPathComponent)
        func fail() -> URL? { try? FileManager.default.removeItem(at: dir); return nil }

        guard let dest = CGImageDestinationCreateWithURL(out as CFURL, type as CFString, 1, nil) else { return fail() }

        if orientation == 1 {
            // Lossless: copy the pixels, replace all metadata with an empty set.
            let opts: [CFString: Any] = [
                kCGImageDestinationMetadata: CGImageMetadataCreateMutable(),
                kCGImageDestinationMergeMetadata: false,
                kCGImageMetadataShouldExcludeGPS: true,
                kCGImageMetadataShouldExcludeXMP: true,
            ]
            var err: Unmanaged<CFError>?
            guard CGImageDestinationCopyImageSource(dest, src, opts as CFDictionary, &err) else { return fail() }
        } else {
            // The orientation tag is metadata too; drop it by rotating the pixels instead so the photo isn't turned sideways.
            let w = (props[kCGImagePropertyPixelWidth] as? Int) ?? 0
            let h = (props[kCGImagePropertyPixelHeight] as? Int) ?? 0
            let opts: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: max(w, h, 1),
            ]
            guard let img = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary) else { return fail() }
            CGImageDestinationAddImage(dest, img, [kCGImageDestinationLossyCompressionQuality: 0.95] as CFDictionary)
            guard CGImageDestinationFinalize(dest) else { return fail() }
        }
        return out
    }
}
