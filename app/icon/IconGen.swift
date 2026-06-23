import AppKit
import CoreGraphics

// Draws the BHServe app icon (1024×1024) → PNG. Usage: swift IconGen.swift out.png
let size = 1024.0
let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon_1024.png"

guard let rep = NSBitmapImageRep(
    bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size),
    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { fatalError("rep") }

NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext
let cs = CGColorSpaceCreateDeviceRGB()

// Squircle background with the BiswasHost purple gradient.
let margin = size * 0.085
let rect = CGRect(x: margin, y: margin, width: size - 2 * margin, height: size - 2 * margin)
let radius = rect.width * 0.2237
let bg = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)

ctx.saveGState()
ctx.addPath(bg); ctx.clip()
let top = CGColor(red: 0.051, green: 0.431, blue: 0.992, alpha: 1)  // BH blue #0d6efd
let bot = CGColor(red: 0.035, green: 0.282, blue: 0.702, alpha: 1)  // BH blue-dark #0948b3
let grad = CGGradient(colorsSpace: cs, colors: [top, bot] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(grad, start: CGPoint(x: rect.midX, y: rect.maxY),
                       end: CGPoint(x: rect.midX, y: rect.minY), options: [])
// soft top sheen
let sheen = CGGradient(colorsSpace: cs,
    colors: [CGColor(red: 1, green: 1, blue: 1, alpha: 0.20),
             CGColor(red: 1, green: 1, blue: 1, alpha: 0)] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(sheen, start: CGPoint(x: rect.midX, y: rect.maxY),
                       end: CGPoint(x: rect.midX, y: rect.midY), options: [])
ctx.restoreGState()

// Server-stack glyph: three rounded bars, each with a status LED.
let gi = rect.width * 0.24
let g = CGRect(x: rect.minX + gi, y: rect.minY + gi, width: rect.width - 2 * gi, height: rect.height - 2 * gi)
let bars = 3
let gap = g.height * 0.15
let barH = (g.height - gap * Double(bars - 1)) / Double(bars)
let barR = barH * 0.30
let leds = [CGColor(red: 0.37, green: 0.92, blue: 0.71, alpha: 1),   // teal (running)
            CGColor(red: 1, green: 1, blue: 1, alpha: 0.55),
            CGColor(red: 1, green: 1, blue: 1, alpha: 0.55)]

for i in 0..<bars {
    let y = g.maxY - Double(i + 1) * barH - Double(i) * gap
    let bar = CGRect(x: g.minX, y: y, width: g.width, height: barH)
    // soft shadow under each bar
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -size * 0.006), blur: size * 0.012,
                  color: CGColor(red: 0, green: 0, blue: 0, alpha: 0.18))
    ctx.addPath(CGPath(roundedRect: bar, cornerWidth: barR, cornerHeight: barR, transform: nil))
    ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 0.97)); ctx.fillPath()
    ctx.restoreGState()
    // LED
    let r = barH * 0.16
    let c = CGPoint(x: bar.minX + barH * 0.6, y: bar.midY)
    ctx.setFillColor(leds[i])
    ctx.fillEllipse(in: CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r))
    // two vent slots on the right
    let vw = g.width * 0.12, vh = barH * 0.13, vr = vh * 0.5
    for j in 0..<2 {
        let vx = bar.maxX - barH * 0.5 - Double(1 - j) * (vw + vw * 0.5)
        let vrect = CGRect(x: vx - vw, y: bar.midY - vh / 2, width: vw, height: vh)
        ctx.addPath(CGPath(roundedRect: vrect, cornerWidth: vr, cornerHeight: vr, transform: nil))
        ctx.setFillColor(CGColor(red: 0.035, green: 0.282, blue: 0.702, alpha: 0.35)); ctx.fillPath()
    }
}

NSGraphicsContext.restoreGraphicsState()
guard let data = rep.representation(using: .png, properties: [:]) else { fatalError("png") }
try! data.write(to: URL(fileURLWithPath: out))
print("wrote \(out)")
