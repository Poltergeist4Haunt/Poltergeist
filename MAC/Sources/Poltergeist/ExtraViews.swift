import SwiftUI
import WebKit
import AVKit
import AVFoundation
import AppKit
import UniformTypeIdentifiers

// MARK: - Sound picker (system sounds, or any file you choose)

struct SoundPicker: View {
    @Binding var value: String
    private let chooseTag = "\u{1}choose"

    var body: some View {
        Picker("", selection: Binding(get: { value }, set: { new in
            if new == chooseTag { chooseFile(); return }
            value = new
            Settings.shared.play(new)
        })) {
            Text("None").tag(Sounds.none)
            Divider()
            ForEach(Sounds.names, id: \.self) { Text($0).tag($0) }
            if Sounds.isCustom(value) {
                Divider()
                Text("Your file: " + (value as NSString).lastPathComponent).tag(value)
            }
            Divider()
            Text("Choose a file…").tag(chooseTag)
        }
        .labelsHidden().pickerStyle(.menu).fixedSize()
    }

    private func chooseFile() {
        DispatchQueue.main.async {
            let panel = NSOpenPanel()
            panel.canChooseDirectories = false
            panel.allowedContentTypes = [.audio]
            panel.prompt = "Use sound"
            if panel.runModal() == .OK, let url = panel.url {
                value = url.path
                Settings.shared.play(url.path)
            }
        }
    }
}

// MARK: - Gallery
//
// Haunt's image host only hands its files to real browsers, so the gallery is a web page shown in an embedded WebKit view:
// that's what makes the previews (and video previews) load, and it gives a big viewer for free.

private struct WebEntry: Encodable {
    let n: String      // name
    let u: String      // the link people share
    let d: String      // the file itself
    let t: String      // date
    let v: Bool        // video
    let c: String      // text to copy
}

private struct GalleryWebView: NSViewRepresentable {
    let html: String

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> WKWebView {
        let cfg = WKWebViewConfiguration()
        cfg.userContentController.add(context.coordinator, name: "p")
        let web = WKWebView(frame: .zero, configuration: cfg)
        web.setValue(false, forKey: "drawsBackground")
        web.navigationDelegate = context.coordinator
        web.loadHTMLString(html, baseURL: nil)
        context.coordinator.loaded = html
        return web
    }

    func updateNSView(_ web: WKWebView, context: Context) {
        if context.coordinator.loaded != html {
            context.coordinator.loaded = html
            web.loadHTMLString(html, baseURL: nil)
        }
    }

    final class Coordinator: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
        var loaded = ""
        func userContentController(_ c: WKUserContentController, didReceive message: WKScriptMessage) {
            guard let body = message.body as? [String: String], let a = body["a"], let v = body["v"] else { return }
            if a == "copy" { UploadManager.copy(v) }
            else if a == "open", let url = URL(string: v) { NSWorkspace.shared.open(url) }
        }
        func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            decisionHandler(action.navigationType == .other ? .allow : .cancel)       // links open in the real browser, not in here
        }
    }
}

struct GalleryView: View {
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var history = HistoryStore.shared
    @State private var mode = "haunt"
    @State private var remote: [RemoteUpload]?
    @State private var error: String?
    @State private var loading = false

    private func isVideoName(_ n: String) -> Bool { ["mp4", "webm", "mov", "mkv"].contains((n as NSString).pathExtension.lowercased()) }

    private var entries: [WebEntry] {
        let fmt = settings.linkFormat
        let day = DateFormatter(); day.dateFormat = "MMM d, h:mm a"
        if mode == "pc" {
            return history.items.map { WebEntry(n: $0.name, u: $0.url, d: LinkFormat.direct.format(url: $0.url, name: $0.name), t: day.string(from: $0.date),
                                                v: isVideoName($0.name), c: fmt.format(url: $0.url, name: $0.name)) }
        }
        return (remote ?? []).map { WebEntry(n: $0.name, u: $0.url, d: LinkFormat.direct.format(url: $0.url, name: $0.name), t: $0.created.map { day.string(from: $0) } ?? "",
                                             v: $0.isVideo, c: fmt.format(url: $0.url, name: $0.name)) }
    }

    private var html: String {
        let json = (try? JSONEncoder().encode(entries)).flatMap { String(data: $0, encoding: .utf8) } ?? "[]"
        return Self.template.replacingOccurrences(of: "/*DATA*/[]", with: json.replacingOccurrences(of: "</", with: "<\\/"))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 6) {
                Button("Haunt gallery") { mode = "haunt" }.buttonStyle(TabButtonStyle(selected: mode == "haunt"))
                Button("This Mac") { mode = "pc" }.buttonStyle(TabButtonStyle(selected: mode == "pc"))
                Spacer()
                if mode == "haunt" {
                    Button("Refresh") { remote = nil; error = nil; Task { await load() } }.buttonStyle(PillButtonStyle(prominent: false))
                } else if !history.items.isEmpty {
                    Button("Clear list") { history.clear() }.buttonStyle(PillButtonStyle(prominent: false))
                }
            }
            content
        }
        .task { if mode == "haunt" && remote == nil && error == nil { await load() } }
        .onChange(of: mode) { m in if m == "haunt" && remote == nil && error == nil { Task { await load() } } }
    }

    @ViewBuilder private var content: some View {
        if mode == "haunt" && settings.apiKey.isEmpty {
            Banner(text: "Add your API key in the Account tab to see your Haunt gallery.")
            Spacer()
        } else if mode == "haunt" && loading && remote == nil {
            Text("Loading your gallery…").font(.system(size: 13)).foregroundColor(Theme.muted)
            Spacer()
        } else if mode == "haunt", let error {
            Banner(text: error)
            Button("Try again") { self.error = nil; Task { await load() } }.buttonStyle(PillButtonStyle())
            Spacer()
        } else if entries.isEmpty {
            Text(mode == "haunt" ? "Nothing on your image host yet." : "Uploads you make from this Mac show up here.")
                .font(.system(size: 13)).foregroundColor(Theme.muted)
            Spacer()
        } else {
            GalleryWebView(html: html).frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func load() async {
        guard !loading, !settings.apiKey.isEmpty else { return }
        loading = true
        defer { loading = false }
        do { remote = try await HauntAPI.listUploads() }
        catch { self.error = error.localizedDescription }
    }

    private static let template = #"""
<!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="no-referrer">
<style>
:root{--bg:#090909;--card:#0f0f0f;--line:rgba(255,255,255,.08);--accent:#f27db3;--soft:rgba(242,125,179,.14);--aline:rgba(242,125,179,.4);--text:#f2f2f2;--muted:#8a8a8a}
*{box-sizing:border-box;margin:0}
html,body{background:transparent;color:var(--text);font-family:-apple-system,"SF Pro Text",system-ui,sans-serif;font-size:13px;height:100%}
body{overflow-y:auto;padding:2px 6px 24px 2px}
::-webkit-scrollbar{width:8px}::-webkit-scrollbar-thumb{background:rgba(255,255,255,.18);border-radius:4px}
.count{color:var(--muted);margin:0 0 12px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(176px,1fr));gap:12px}
.tile{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:10px;min-width:0}
.pic{position:relative;height:112px;border-radius:10px;overflow:hidden;background:rgba(255,255,255,.06);cursor:pointer;display:grid;place-items:center;color:var(--muted)}
.pic img,.pic video{position:absolute;inset:0;width:100%;height:100%;object-fit:cover}
.pic .play{position:absolute;right:8px;bottom:8px;width:26px;height:26px;border-radius:50%;background:rgba(0,0,0,.6);display:grid;place-items:center;font-size:10px;color:#fff;z-index:2;font-style:normal}
.name{font-weight:600;margin-top:8px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.date{color:var(--muted);font-size:11px;margin:1px 0 8px}
.row{display:flex;gap:6px}
button{font-family:inherit;font-weight:600;font-size:11.5px;border-radius:999px;padding:5px 11px;cursor:pointer;border:1px solid var(--aline);background:var(--soft);color:var(--accent)}
button:hover{background:rgba(242,125,179,.24)}
button.g{border-color:var(--line);background:rgba(255,255,255,.05);color:var(--text)}button.g:hover{background:rgba(255,255,255,.1)}
.empty{color:var(--muted)}
.lb{position:fixed;inset:0;background:rgba(0,0,0,.84);display:none;flex-direction:column;z-index:10}
.lb.on{display:flex}
.stage{flex:1;display:grid;place-items:center;min-height:0;padding:16px}
.stage img,.stage video{max-width:100%;max-height:100%;border-radius:10px}
.bar{display:flex;align-items:center;gap:8px;padding:12px 16px;background:var(--card);border-top:1px solid var(--line)}
.bar b{margin-right:auto;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
</style></head><body>
<div class="count" id="count"></div><div class="grid" id="grid"></div>
<div class="lb" id="lb"><div class="stage" id="stage"></div><div class="bar"><b id="lbname"></b><button id="lbcopy">Copy link</button><button class="g" id="lbopen">Open in browser</button><button class="g" id="lbclose">Close</button></div></div>
<script>
const D=/*DATA*/[];
const post=(a,v)=>webkit.messageHandlers.p.postMessage({a,v});
const grid=document.getElementById('grid');
document.getElementById('count').textContent=D.length?D.length+' file'+(D.length==1?'':'s'):'';
if(!D.length){grid.innerHTML='<div class="empty">Nothing here yet.</div>'}
D.forEach(e=>{
  const t=document.createElement('div');t.className='tile';
  const pic=document.createElement('div');pic.className='pic';pic.title='Click for a bigger preview';
  const fallback=document.createElement('span');fallback.textContent=e.v?'\u25B6':'\uD83D\uDDBC';pic.appendChild(fallback);
  if(e.v){const v=document.createElement('video');v.muted=true;v.preload='metadata';v.playsInline=true;v.src=e.d+'#t=0.5';v.onloadeddata=()=>{fallback.remove()};pic.appendChild(v);const p=document.createElement('i');p.className='play';p.textContent='\u25B6';pic.appendChild(p)}
  else{const i=document.createElement('img');i.loading='lazy';i.referrerPolicy='no-referrer';i.src=e.d;i.onload=()=>fallback.remove();i.onerror=()=>{i.remove()};pic.appendChild(i)}
  pic.onclick=()=>show(e);
  t.appendChild(pic);
  const n=document.createElement('div');n.className='name';n.textContent=e.n;n.title=e.n;t.appendChild(n);
  const d=document.createElement('div');d.className='date';d.textContent=e.t||'\u00a0';t.appendChild(d);
  const r=document.createElement('div');r.className='row';
  const c=document.createElement('button');c.textContent='Copy link';c.onclick=()=>{post('copy',e.c);c.textContent='Copied \u2713';setTimeout(()=>c.textContent='Copy link',1400)};
  const o=document.createElement('button');o.className='g';o.textContent='Open';o.onclick=()=>post('open',e.u);
  r.appendChild(c);r.appendChild(o);t.appendChild(r);grid.appendChild(t);
});
const lb=document.getElementById('lb'),stage=document.getElementById('stage');let cur=null;
function show(e){cur=e;stage.innerHTML='';let m;
  if(e.v){m=document.createElement('video');m.controls=true;m.autoplay=true;m.src=e.d}else{m=document.createElement('img');m.referrerPolicy='no-referrer';m.src=e.d}
  stage.appendChild(m);document.getElementById('lbname').textContent=e.n;lb.classList.add('on')}
function hide(){lb.classList.remove('on');stage.innerHTML=''}
document.getElementById('lbclose').onclick=hide;
document.getElementById('lbcopy').onclick=()=>{if(cur)post('copy',cur.c)};
document.getElementById('lbopen').onclick=()=>{if(cur)post('open',cur.u)};
lb.onclick=ev=>{if(ev.target===lb||ev.target===stage)hide()};
addEventListener('keydown',ev=>{if(ev.key==='Escape')hide()});
</script></body></html>
"""#
}

// MARK: - Effects

struct EffectsView: View {
    @ObservedObject private var s = Settings.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Card {
                Text("Image effects").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                Text("Touch-ups added to every screenshot you take, before it's edited or uploaded.").font(.system(size: 12)).foregroundColor(Theme.muted)
                Toggle(isOn: $s.fxOn) { Text("Use image effects").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text) }
            }.toggleStyle(HauntToggleStyle())

            Card {
                Toggle(isOn: $s.fxRound) { Text("Rounded corners").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text) }
                slider("Corner size", value: $s.fxRadius, range: 4...60)
                slider("Border", value: $s.fxBorder, range: 0...16, note: "0 for none")
                HStack {
                    Text("Border colour").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                    Spacer()
                    Circle().fill(Color(hexString: s.fxBorderColor) ?? Theme.accent).frame(width: 24, height: 24)
                    TextField("#f27db3", text: $s.fxBorderColor).textFieldStyle(HauntFieldStyle()).frame(width: 130)
                }
                Toggle(isOn: $s.fxShadow) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Drop shadow").font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                        Text("Adds a soft transparent margin around the shot.").font(.system(size: 12)).foregroundColor(Theme.muted)
                    }
                }
                slider("Shadow size", value: $s.fxShadowSize, range: 6...50)
            }.toggleStyle(HauntToggleStyle())

            Card {
                Text("Preview").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.text)
                HStack {
                    Spacer()
                    Image(nsImage: preview).resizable().scaledToFit().frame(maxHeight: 220)
                    Spacer()
                }
                .padding(18).background(Color(hex: 0x141416)).clipShape(RoundedRectangle(cornerRadius: 12))
            }
        }
    }

    private func slider(_ title: String, value: Binding<Int>, range: ClosedRange<Int>, note: String? = nil) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 14, weight: .medium)).foregroundColor(Theme.text)
                if let note { Text(note).font(.system(size: 12)).foregroundColor(Theme.muted) }
            }
            Spacer()
            Slider(value: Binding(get: { Double(value.wrappedValue) }, set: { value.wrappedValue = Int($0.rounded()) }),
                   in: Double(range.lowerBound)...Double(range.upperBound)).frame(width: 170)
            Text("\(value.wrappedValue) px").font(.system(size: 12, design: .monospaced)).foregroundColor(Theme.muted).frame(width: 52, alignment: .trailing)
        }
    }

    /// A made-up screenshot with the current effects on it.
    private var preview: NSImage {
        let w = 420, h = 240
        let base = NSImage(size: NSSize(width: w, height: h), flipped: false) { r in
            NSGradient(colors: [NSColor(hex: 0x282438), NSColor(hex: 0x121622)])?.draw(in: r, angle: -35)
            NSColor.white.withAlphaComponent(0.09).setFill()
            for rect in [NSRect(x: 24, y: 120, width: 180, height: 96), NSRect(x: 222, y: 172, width: 174, height: 44),
                         NSRect(x: 222, y: 24, width: 174, height: 136), NSRect(x: 24, y: 24, width: 180, height: 82)] {
                NSBezierPath(roundedRect: rect, xRadius: 10, yRadius: 10).fill()
            }
            NSColor(hex: 0xf27db3).setFill()
            NSBezierPath(ovalIn: NSRect(x: 44, y: 150, width: 32, height: 32)).fill()
            return true
        }
        guard let cg = base.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return base }
        if s.fxOn, let out = ImageEffects.apply(to: cg) { return NSImage(cgImage: out, size: NSSize(width: out.width, height: out.height)) }
        return base
    }
}

// MARK: - Stats

struct StatsView: View {
    @ObservedObject private var s = Settings.shared

    private var time: String {
        let t = Int(s.totalSeconds)
        return t >= 3600 ? "\(t / 3600)h \((t % 3600) / 60)m" : "\(t / 60)m"
    }
    private var data: String {
        s.bytesUploaded >= 1_000_000_000 ? String(format: "%.1f GB", Double(s.bytesUploaded) / 1e9) : String(format: "%.1f MB", Double(s.bytesUploaded) / 1e6)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 190), spacing: 12)], spacing: 12) {
                stat("Time with Poltergeist open", time)
                stat("Uploads", "\(s.totalUploads)")
                stat("Screenshots taken", "\(s.totalScreenshots)")
                stat("Recordings", "\(s.totalRecordings)")
                stat("Data uploaded", data)
                stat("Using it since", s.firstRun.formatted(date: .abbreviated, time: .omitted))
            }
            Text("Counted on this Mac only. Time adds up while Poltergeist runs in the menu bar.")
                .font(.system(size: 12)).foregroundColor(Theme.muted)
        }
    }

    private func stat(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value).font(.system(size: 26, weight: .semibold)).foregroundColor(Theme.text)
            Text(label).font(.system(size: 12.5)).foregroundColor(Theme.muted)
        }
        .padding(20).frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.card)
        .overlay(RoundedRectangle(cornerRadius: 18).stroke(Theme.border))
        .clipShape(RoundedRectangle(cornerRadius: 18))
    }
}
