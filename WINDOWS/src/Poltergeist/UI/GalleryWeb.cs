using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Poltergeist;

/// <summary>One picture or video for the gallery page.</summary>
public sealed record WebEntry(string Name, string Url, string Direct, string Date, bool Video, string CopyText);

/// <summary>
/// The gallery, shown in an embedded browser (Edge's WebView2). Haunt's image host only serves its files to real browsers,
/// so this is what makes the previews load; it also gives video previews and a big viewer for free.
/// </summary>
sealed class GalleryWeb : Border, IDisposable
{
    readonly WebView2 _web = new();
    bool _disposed;

    public GalleryWeb(IReadOnlyList<WebEntry> entries, Action<string> copy, Action<string> open, Action<string> failed)
    {
        Background = Theme.Bg;
        Child = _web;
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(9, 9, 9);
        Loaded += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Temp, "webview"));
                await _web.EnsureCoreWebView2Async(env);
                if (_disposed) return;
                var s = _web.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                _web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                        var a = doc.RootElement.GetProperty("a").GetString();
                        var v = doc.RootElement.GetProperty("v").GetString() ?? "";
                        if (a == "copy") copy(v); else if (a == "open") open(v);
                    }
                    catch { }
                };
                _web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("data:") && e.Uri != "about:blank") e.Cancel = true; };   // links open in the real browser
                _web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                _web.NavigateToString(Html(entries));
            }
            catch (Exception e) { failed(e.Message); }
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _web.Dispose(); } catch { }
    }

    static string Html(IReadOnlyList<WebEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries.Select(e => new { n = e.Name, u = e.Url, d = e.Direct, t = e.Date, v = e.Video, c = e.CopyText }));
        return Template.Replace("/*DATA*/[]", json.Replace("</", "<\\/"));
    }

    // The page itself: the same dark theme as the app.
    const string Template = """
<!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="no-referrer">
<style>
:root{--bg:#090909;--card:#0f0f0f;--line:rgba(255,255,255,.08);--line2:rgba(255,255,255,.14);--accent:#f27db3;--soft:rgba(242,125,179,.14);--aline:rgba(242,125,179,.4);--text:#f2f2f2;--muted:#8a8a8a}
*{box-sizing:border-box;margin:0}
html,body{background:var(--bg);color:var(--text);font-family:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;font-size:13px;height:100%}
body{overflow-y:auto;padding:2px 6px 24px 2px}
::-webkit-scrollbar{width:8px}::-webkit-scrollbar-thumb{background:rgba(255,255,255,.18);border-radius:4px}::-webkit-scrollbar-thumb:hover{background:rgba(255,255,255,.3)}
.count{color:var(--muted);margin:0 0 12px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(176px,1fr));gap:12px}
.tile{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:10px;min-width:0}
.pic{position:relative;height:112px;border-radius:10px;overflow:hidden;background:rgba(255,255,255,.06);cursor:pointer;display:grid;place-items:center;color:var(--muted)}
.pic img,.pic video{position:absolute;inset:0;width:100%;height:100%;object-fit:cover}
.pic .play{position:absolute;right:8px;bottom:8px;width:26px;height:26px;border-radius:50%;background:rgba(0,0,0,.6);display:grid;place-items:center;font-size:10px;color:#fff;z-index:2}
.name{font-weight:600;margin-top:8px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.date{color:var(--muted);font-size:11px;margin:1px 0 8px}
.row{display:flex;gap:6px}
button{font:600 11.5px inherit;font-family:inherit;border-radius:999px;padding:5px 11px;cursor:pointer;border:1px solid var(--aline);background:var(--soft);color:var(--accent)}
button:hover{background:rgba(242,125,179,.24)}
button.g{border-color:var(--line);background:rgba(255,255,255,.05);color:var(--text)}button.g:hover{background:rgba(255,255,255,.1)}
.empty{color:var(--muted)}
.lb{position:fixed;inset:0;background:rgba(0,0,0,.82);display:none;flex-direction:column;z-index:10}
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
const post=(a,v)=>chrome.webview.postMessage({a,v});
const grid=document.getElementById('grid');
document.getElementById('count').textContent=D.length?D.length+' file'+(D.length==1?'':'s'):'';
if(!D.length){grid.innerHTML='<div class="empty">Nothing here yet.</div>'}
D.forEach(e=>{
  const t=document.createElement('div');t.className='tile';
  const pic=document.createElement('div');pic.className='pic';pic.title='Click for a bigger preview';
  const fallback=document.createElement('span');fallback.textContent=e.v?'▶':'🖼';pic.appendChild(fallback);
  if(e.v){const v=document.createElement('video');v.muted=true;v.preload='metadata';v.playsInline=true;v.src=e.d+'#t=0.5';v.onloadeddata=()=>{fallback.remove()};pic.appendChild(v);const p=document.createElement('i');p.className='play';p.textContent='▶';pic.appendChild(p)}
  else{const i=document.createElement('img');i.loading='lazy';i.referrerPolicy='no-referrer';i.src=e.d;i.onload=()=>fallback.remove();i.onerror=()=>{i.remove()};pic.appendChild(i)}
  pic.onclick=()=>show(e);
  t.appendChild(pic);
  const n=document.createElement('div');n.className='name';n.textContent=e.n;n.title=e.n;t.appendChild(n);
  const d=document.createElement('div');d.className='date';d.textContent=e.t||' ';t.appendChild(d);
  const r=document.createElement('div');r.className='row';
  const c=document.createElement('button');c.textContent='Copy link';c.onclick=()=>{post('copy',e.c);c.textContent='Copied ✓';setTimeout(()=>c.textContent='Copy link',1400)};
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
""";
}
