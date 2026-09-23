import CoreImage
import CoreImage.CIFilterBuiltins
import Metal

/// The cursor bitmap we draw on top of the video.
struct CursorArt {
    var image: CGImage
    var pointSize: CGSize  // size of the cursor in screen points
    var hotSpot: CGPoint  // in points, top-left origin
}

/// Builds one output frame (zoom, cursor, motion blur, background) from one source frame.
///
/// Shared by the live preview and the exporter, so what you preview is what you export.
/// Immutable after init, so it is safe to call from any thread.
final class FrameComposer: @unchecked Sendable {
    let path: CameraPath
    /// Output size in pixels.
    let canvas: CGRect

    private let session: RecordingSession
    private let settings: RenderSettings
    private let cursor: CursorArt
    private let maxBlurSamples: Int
    private let inner: CGRect
    private let background: CIImage
    private let mask: CIImage

    init(session: RecordingSession, settings: RenderSettings, cursor: CursorArt, duration: Double,
         outputWidth: Int, maxBlurSamples: Int = 16) {
        self.session = session
        self.settings = settings
        self.cursor = cursor
        self.maxBlurSamples = maxBlurSamples
        path = CameraPath(session: session, settings: settings, duration: duration)

        let outW = Double(min(outputWidth, max(session.width, 640)) & ~1)
        let pad = (outW * settings.padding).rounded()
        let innerW = outW - pad * 2
        let innerH = (innerW * Double(session.height) / Double(session.width)).rounded()
        let outH = Double(Int(innerH + pad * 2) & ~1)
        canvas = CGRect(x: 0, y: 0, width: outW, height: outH)
        inner = CGRect(x: pad, y: ((outH - innerH) / 2).rounded(), width: innerW, height: innerH)
        (background, mask) = Self.makeStaticLayers(canvas: canvas, inner: inner, settings: settings)
    }

    func frame(source: CIImage, time t: Double) -> CIImage {
        let pulse = path.clickPulse(at: t)

        var content: CIImage
        if settings.motionBlur {
            // Motion blur = average several sub-frames across the shutter interval.
            // The number of sub-frames adapts to how far things move, so still frames stay cheap.
            let shutter = 1.0 / Double(settings.fps)
            let a = path.state(at: t - shutter / 2)
            let b = path.state(at: t + shutter / 2)
            let samples = min(maxBlurSamples, max(1, Int(motionInPixels(a, b) / 1.5) + 1))
            if samples == 1 {
                content = layer(source: source, state: path.state(at: t), pulse: pulse)
            } else {
                let weight = 1.0 / Double(samples)
                content = .empty()
                for i in 0..<samples {
                    let st = t - shutter / 2 + shutter * (Double(i) + 0.5) / Double(samples)
                    // CIColorMatrix works on un-premultiplied color, so scaling alpha alone
                    // scales the premultiplied pixel by `weight`.
                    let sub = layer(source: source, state: path.state(at: st), pulse: pulse)
                        .applyingFilter("CIColorMatrix", parameters: [
                            "inputAVector": CIVector(x: 0, y: 0, z: 0, w: weight),
                        ])
                    content = i == 0 ? sub : sub.applyingFilter("CIAdditionCompositing",
                                                                parameters: [kCIInputBackgroundImageKey: content])
                }
            }
        } else {
            content = layer(source: source, state: path.state(at: t), pulse: pulse)
        }

        return content.applyingFilter("CIBlendWithMask", parameters: [
            kCIInputBackgroundImageKey: background,
            kCIInputMaskImageKey: mask,
        ]).cropped(to: canvas)
    }

    /// Screen content + cursor for one camera state, cropped to the inner rect.
    private func layer(source: CIImage, state: CameraState, pulse: Double) -> CIImage {
        let W = Double(session.width), H = Double(session.height)
        let viewW = W / state.zoom, viewH = H / state.zoom
        let originX = state.centerX - viewW / 2
        let originY = H - (state.centerY + viewH / 2)  // flip to bottom-left origin
        let scale = inner.width / viewW

        // The source frame may not match the recorded size exactly (e.g. decoder padding).
        let sourceScale = W / source.extent.width
        let screen = source
            .transformed(by: CGAffineTransform(translationX: -source.extent.minX, y: -source.extent.minY)
                .concatenating(CGAffineTransform(scaleX: sourceScale, y: sourceScale))
                .concatenating(CGAffineTransform(translationX: -originX, y: -originY))
                .concatenating(CGAffineTransform(scaleX: scale, y: scale))
                .concatenating(CGAffineTransform(translationX: inner.minX, y: inner.minY)))

        // Cursor, scaled with the zoom and "pressed" on click.
        let cursorPoints = cursor.pointSize.height * settings.cursorScale * (1 - 0.18 * pulse)
        let cursorH = cursorPoints * session.pixelsPerPoint * scale
        let cursorScale = cursorH / Double(cursor.image.height)
        let cursorW = Double(cursor.image.width) * cursorScale
        let hotX = cursor.hotSpot.x / cursor.pointSize.width * cursorW
        let hotY = (1 - cursor.hotSpot.y / cursor.pointSize.height) * cursorH
        let px = inner.minX + (state.cursorX - originX) * scale
        let py = inner.minY + ((H - state.cursorY) - originY) * scale
        let cursorImage = CIImage(cgImage: cursor.image)
            .transformed(by: CGAffineTransform(scaleX: cursorScale, y: cursorScale)
                .concatenating(CGAffineTransform(translationX: px - hotX, y: py - hotY)))

        return cursorImage.composited(over: screen).cropped(to: inner)
    }

    /// Rough on-screen movement between two camera states, in output pixels.
    private func motionInPixels(_ a: CameraState, _ b: CameraState) -> Double {
        let W = Double(session.width)
        let scaleA = inner.width / (W / a.zoom), scaleB = inner.width / (W / b.zoom)
        let pan = hypot(a.centerX - b.centerX, a.centerY - b.centerY) * max(scaleA, scaleB)
        let zoom = abs(log(a.zoom) - log(b.zoom)) * inner.width
        let cursor = hypot(a.cursorX - b.cursorX, a.cursorY - b.cursorY) * max(scaleA, scaleB)
        return max(pan + zoom, cursor)
    }

    private static let staticContext: CIContext = {
        if let device = MTLCreateSystemDefaultDevice() { return CIContext(mtlDevice: device) }
        return CIContext()
    }()

    /// Background gradient + drop shadow never change, so render them once.
    private static func makeStaticLayers(canvas: CGRect, inner: CGRect,
                                         settings: RenderSettings) -> (background: CIImage, mask: CIImage) {
        let radius = inner.width * 0.012

        let shadowShape = CIFilter.roundedRectangleGenerator()
        shadowShape.extent = inner.offsetBy(dx: 0, dy: -inner.height * 0.012)
        shadowShape.radius = Float(radius)
        shadowShape.color = CIColor(red: 0, green: 0, blue: 0, alpha: 0.45)
        let shadow = shadowShape.outputImage!.applyingGaussianBlur(sigma: inner.width * 0.015)

        let backdrop = backdropImage(settings: settings, canvas: canvas)
        let composed = shadow.composited(over: backdrop).cropped(to: canvas)
        let background = staticContext.createCGImage(composed, from: canvas).map { CIImage(cgImage: $0) } ?? composed

        let maskShape = CIFilter.roundedRectangleGenerator()
        maskShape.extent = inner
        maskShape.radius = Float(radius)
        maskShape.color = .white
        return (background, maskShape.outputImage!)
    }

    /// The gradient or custom image behind the screen, filling the whole canvas.
    private static func backdropImage(settings: RenderSettings, canvas: CGRect) -> CIImage {
        if case .image(let url) = settings.background,
           let image = CIImage(contentsOf: url, options: [.applyOrientationProperty: true]),
           image.extent.width > 0, image.extent.height > 0 {
            // Aspect-fill: scale to cover the canvas, centred, then crop.
            let extent = image.extent
            let scale = max(canvas.width / extent.width, canvas.height / extent.height)
            let scaledW = extent.width * scale, scaledH = extent.height * scale
            var filled = image
                .transformed(by: CGAffineTransform(translationX: -extent.minX, y: -extent.minY)
                    .concatenating(CGAffineTransform(scaleX: scale, y: scale))
                    .concatenating(CGAffineTransform(translationX: (canvas.width - scaledW) / 2,
                                                     y: (canvas.height - scaledH) / 2)))
            if settings.backgroundBlur > 0 {
                filled = filled.clampedToExtent()
                    .applyingGaussianBlur(sigma: settings.backgroundBlur * canvas.width * 0.03)
            }
            return filled.cropped(to: canvas)
        }

        let index: Int
        if case .gradient(let i) = settings.background { index = i } else { index = 0 }
        let preset = GradientPreset.all[min(max(index, 0), GradientPreset.all.count - 1)]
        let gradient = CIFilter.linearGradient()
        gradient.point0 = CGPoint(x: 0, y: canvas.height)
        gradient.point1 = CGPoint(x: canvas.width, y: 0)
        gradient.color0 = CIColor(red: preset.from.r, green: preset.from.g, blue: preset.from.b)
        gradient.color1 = CIColor(red: preset.to.r, green: preset.to.g, blue: preset.to.b)
        return gradient.outputImage!.cropped(to: canvas)
    }
}
