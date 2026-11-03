(function () {
  "use strict";

  if (window.__idmStyleLoaded) return;
  window.__idmStyleLoaded = true;
  if (window !== window.top) return;

  var S = self.IDMStyle;
  var networkItems = [];
  var domItems = [];
  var ytItems = [];
  var ytCaptions = [];
  var ytNotice = "";
  var sent = {};
  var pending = {};
  var panelOpen = false;
  var closed = false;
  var busy = false;
  // Bumped by every send and by the 3 s watchdog, so a late reply from an
  // abandoned send can never touch the buttons again.
  var sendRun = 0;
  var lockTimer = 0;
  var appUp = true;
  var appCheckAt = 0;
  var lastHref = location.href;
  var isYouTube = /(^|\.)youtube\.com$/i.test(location.hostname);

  var host = document.createElement("div");
  host.id = "idm-style-downloader-host";
  host.style.cssText = "all:initial;position:fixed;right:0;bottom:0;width:0;height:0;z-index:2147483646;direction:ltr;";
  (document.documentElement || document.body).appendChild(host);

  var shadow = host.attachShadow({ mode: "open" });
  shadow.innerHTML =
    '<style>' +
    '#fab-wrap{position:fixed;right:18px;bottom:18px;z-index:2147483646;opacity:.5;transition:opacity .25s}' +
    '#fab-wrap:hover{opacity:1}' +
    '#fab{position:relative;display:flex;align-items:center;gap:6px;padding:7px 26px 7px 10px;' +
    'border:0;border-radius:16px;' +
    'background:linear-gradient(135deg,#818cf8,#4338ca 55%,#312e81);color:#fff;' +
    'font:800 12.5px/1.2 system-ui,-apple-system,Segoe UI,sans-serif;letter-spacing:.2px;cursor:pointer;' +
    'user-select:none;box-shadow:0 14px 34px rgba(67,56,202,.5),0 0 0 1px rgba(255,255,255,.12) inset;' +
    'transition:transform .16s,filter .16s}' +
    '#fab:hover{filter:brightness(1.12);transform:translateY(-1px)}' +
    '#fab svg{width:18px;height:18px;flex:0 0 auto;display:block;color:#fff}' +
    '#count{min-width:18px;height:18px;padding:0 5px;border-radius:9px;box-sizing:border-box;' +
    'background:linear-gradient(135deg,#fb7185,#e11d48);color:#fff;font:700 10px/18px system-ui,sans-serif;' +
    'text-align:center;box-shadow:0 3px 10px rgba(225,29,72,.4)}' +
    '#panel{position:fixed;right:18px;bottom:84px;width:min(380px,calc(100vw - 36px));max-height:65vh;' +
    'display:none;flex-direction:column;background:#111827;color:#e5e7eb;border:1px solid #2b3550;border-radius:14px;' +
    'box-shadow:0 24px 60px rgba(2,6,23,.55);overflow:hidden;z-index:2147483646;' +
    'font:13px/1.4 system-ui,-apple-system,Segoe UI,sans-serif;direction:ltr;text-align:left}' +
    '#head{display:flex;align-items:center;gap:8px;padding:11px 12px;border-bottom:1px solid #2b3550;background:#151d2e}' +
    '#title{font-weight:700;font-size:13px;color:#e0e7ff;white-space:nowrap}' +
    '#sub{flex:1;font-size:11px;color:#94a3b8;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '#close{border:0;background:transparent;color:#94a3b8;font:700 20px/1 system-ui;cursor:pointer;padding:0 2px}' +
    '#close:hover{color:#fff}' +
    '#hide{position:absolute;right:5px;top:50%;margin-top:-9px;width:18px;height:18px;border:0;border-radius:50%;background:transparent;' +
    'color:rgba(255,255,255,.7);font:700 14px/1 system-ui,sans-serif;cursor:pointer;z-index:1;display:none;' +
    'align-items:center;justify-content:center;padding:0;transition:background .12s,color .12s}' +
    '#hide:hover{background:rgba(255,93,108,.85);color:#fff}' +
    '#video-icons{position:fixed;left:0;top:0;width:0;height:0;z-index:2147483645;pointer-events:none}' +
    '.vdl{position:fixed;display:none;width:34px;height:34px;border:0;border-radius:10px;' +
    'background:linear-gradient(135deg,#818cf8,#4338ca 55%,#312e81);color:#fff;cursor:pointer;' +
    'box-shadow:0 8px 20px rgba(2,6,23,.45),0 0 0 1px rgba(255,255,255,.18) inset;' +
    'align-items:center;justify-content:center;padding:0;pointer-events:auto;z-index:2147483645;' +
    'transition:transform .15s,filter .15s}' +
    '.vdl:hover{filter:brightness(1.12);transform:translateY(-1px)}' +
    '.vdl svg{width:18px;height:18px;display:block;pointer-events:none;color:#fff}' +
    '#list{overflow:auto;padding:6px;min-height:52px}' +
    '.row{display:flex;align-items:center;gap:10px;padding:8px;border-radius:10px}' +
    '.row:hover{background:#1b2336}' +
    '.meta{flex:1;min-width:0}' +
    '.name{font-weight:600;color:#e5e7eb;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.info{font-size:11px;color:#94a3b8;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:2px}' +
    '.warn{color:#fbbf24}' +
    '.dl{border:0;border-radius:8px;background:#4f46e5;color:#fff;font:600 11px system-ui,sans-serif;' +
    'padding:6px 10px;cursor:pointer;white-space:nowrap}' +
    '.dl:hover{background:#6366f1}' +
    '.dl:disabled{background:#334155;color:#94a3b8;cursor:default}' +
    '.empty{padding:18px 12px;color:#94a3b8;text-align:center;font-size:12px}' +
    '.group{padding:9px 8px 3px;font:700 10px system-ui,sans-serif;color:#818cf8;text-transform:uppercase;letter-spacing:.08em}' +
    '#foot{display:flex;align-items:center;gap:8px;padding:9px 12px;border-top:1px solid #2b3550;background:#151d2e;min-height:22px}' +
    '#all{border:1px solid #4338ca;background:#312e81;color:#e0e7ff;border-radius:8px;font:600 11px system-ui,sans-serif;' +
    'padding:6px 10px;cursor:pointer}' +
    '#all:hover{background:#3730a3}' +
    '#all:disabled{opacity:.5;cursor:default}' +
    '#status{flex:1;font-size:11px;color:#94a3b8;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
    '#status.ok{color:#34d399}#status.err{color:#fca5a5}' +
    '#panel{max-height:min(72vh,620px);background:linear-gradient(160deg,#0f172a,#0b1020 55%,#111827);border-color:rgba(129,140,248,.35);border-radius:16px;box-shadow:0 28px 70px rgba(2,6,23,.72),0 0 0 1px rgba(99,102,241,.08) inset}' +
    '#list{padding:6px 6px 10px}' +
    '#head{background:linear-gradient(135deg,#1e1b4b,#111827);border-bottom-color:rgba(129,140,248,.25)}' +
    '#title{background:linear-gradient(90deg,#f8fafc,#a5b4fc);-webkit-background-clip:text;background-clip:text;color:transparent}' +
    '.row{border:1px solid transparent;transition:background .15s,border-color .15s}' +
    '.row:hover{background:linear-gradient(135deg,#1b2336,#151d2e);border-color:rgba(129,140,248,.25)}' +
    '.name{display:flex;align-items:center;gap:7px}' +
    '.label{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;text-shadow:0 1px 2px rgba(2,6,23,.6)}' +
    '.tag{flex:0 0 auto;min-width:30px;padding:3px 6px;border-radius:5px;color:#fff;font:800 9px/1 system-ui,sans-serif;letter-spacing:.05em;text-align:center;text-transform:uppercase;text-shadow:0 1px 2px rgba(2,6,23,.7);box-shadow:0 2px 8px rgba(2,6,23,.35)}' +
    '.tag-4k{background:linear-gradient(135deg,#f59e0b,#dc2626)}' +
    '.tag-2k{background:linear-gradient(135deg,#06b6d4,#2563eb)}' +
    '.tag-fhd{background:linear-gradient(135deg,#6366f1,#8b5cf6)}' +
    '.tag-hd{background:linear-gradient(135deg,#10b981,#0d9488)}' +
    '.tag-sd{background:linear-gradient(135deg,#64748b,#475569)}' +
    '.tag-ld{background:linear-gradient(135deg,#71717a,#3f3f46)}' +
    '.tag-audio{color:#1f2937;text-shadow:0 1px 1px rgba(255,255,255,.45);background:linear-gradient(135deg,#fcd34d,#f59e0b)}' +
    '.tag-av{background:linear-gradient(135deg,#10b981,#0d9488)}' +
    '.tag-cc{background:linear-gradient(135deg,#7c3aed,#4c1d95)}' +
    '.dl{background:linear-gradient(135deg,#6366f1,#4338ca);box-shadow:0 6px 14px rgba(67,56,202,.35)}' +
    '.dl:hover{background:linear-gradient(135deg,#818cf8,#4f46e5)}' +
    '.group{display:flex;align-items:center;gap:8px;color:#a5b4fc}' +
    '.group:after{content:"";height:1px;flex:1;background:linear-gradient(90deg,rgba(129,140,248,.3),transparent)}' +
    '#foot{background:linear-gradient(135deg,#151d2e,#111827);border-top-color:rgba(129,140,248,.25)}' +
    '#all{background:linear-gradient(135deg,#4f46e5,#312e81);box-shadow:0 6px 14px rgba(67,56,202,.3)}' +
    '#all:hover{background:linear-gradient(135deg,#6366f1,#3730a3)}' +
    '#list::-webkit-scrollbar{width:8px}' +
    '#list::-webkit-scrollbar-thumb{background:#334155;border-radius:4px}' +
    '#list::-webkit-scrollbar-track{background:transparent}' +
    '#lang-pop{position:absolute;left:0;right:0;top:0;bottom:0;display:none;align-items:center;justify-content:center;background:rgba(2,6,23,.72);z-index:3;padding:14px}' +
    '#lang-box{width:100%;max-height:100%;overflow:auto;background:linear-gradient(160deg,#111827,#0b1020);border:1px solid rgba(129,140,248,.4);border-radius:12px;padding:12px;box-shadow:0 18px 40px rgba(2,6,23,.7);box-sizing:border-box}' +
    '#lang-title{font:700 12px system-ui,sans-serif;color:#e0e7ff;margin:0 0 8px}' +
    '#lang-list{display:flex;flex-direction:column;gap:6px}' +
    '.lang{border:1px solid rgba(129,140,248,.3);background:#1b2336;color:#e5e7eb;border-radius:8px;font:600 12px system-ui,sans-serif;padding:8px 10px;text-align:left;cursor:pointer}' +
    '.lang:hover{background:#312e81;border-color:#6366f1}' +
    '.lang small{display:block;font:400 10px system-ui,sans-serif;color:#94a3b8;margin-top:2px}' +
    '#gear{border:0;background:transparent;color:#94a3b8;font:600 15px/1 system-ui,sans-serif;cursor:pointer;padding:0 4px}' +
    '#gear:hover{color:#fff}' +
    '#runapp{display:none;border:1px solid #4338ca;background:#312e81;color:#e0e7ff;border-radius:8px;font:700 10px system-ui,sans-serif;padding:4px 8px;cursor:pointer;margin-left:auto}' +
    '#runapp:hover{background:#3730a3}' +
    '#set-pop{position:absolute;left:0;right:0;top:0;bottom:0;display:none;align-items:center;justify-content:center;background:rgba(2,6,23,.72);z-index:3;padding:14px}' +
    '#set-box{width:100%;max-height:100%;overflow:auto;background:linear-gradient(160deg,#111827,#0b1020);border:1px solid rgba(129,140,248,.4);border-radius:12px;padding:12px;box-shadow:0 18px 40px rgba(2,6,23,.7);box-sizing:border-box}' +
    '#set-title{font:700 12px system-ui,sans-serif;color:#e0e7ff;margin:0 0 8px}' +
    '#set-enable,#set-takeover{display:flex;align-items:center;gap:8px;font:600 12px system-ui,sans-serif;color:#e5e7eb;margin:0 0 10px;cursor:pointer}' +
    '#set-takeover input{accent-color:#6366f1;margin:0}' +
    '#set-enable{position:relative;justify-content:space-between;margin:0 0 12px}' +
    '#set-enable input{position:absolute;opacity:0;width:1px;height:1px}' +
    '#set-enable .sw{position:relative;flex:0 0 auto;width:46px;height:26px;border-radius:13px;background:#334155;box-shadow:inset 0 0 0 1px rgba(129,140,248,.35);transition:background .15s}' +
    '#set-enable .sw:after{content:"";position:absolute;top:3px;left:3px;width:20px;height:20px;border-radius:50%;background:#e2e8f0;transition:transform .15s}' +
    '#set-enable input:checked + .sw{background:#6366f1}' +
    '#set-enable input:checked + .sw:after{transform:translateX(20px)}' +
    '#set-enable input:focus + .sw{box-shadow:inset 0 0 0 1px rgba(129,140,248,.35),0 0 0 2px rgba(99,102,241,.45)}' +
    '#set-label{font:600 11px system-ui,sans-serif;color:#a5b4fc;margin:0 0 4px}' +
    '#set-row{display:flex;align-items:center;gap:8px}' +
    '#set-kb{flex:1;min-width:0;box-sizing:border-box;border:1px solid rgba(129,140,248,.35);background:#0b1020;color:#e5e7eb;border-radius:8px;font:600 12px system-ui,sans-serif;padding:8px 10px}' +
    '#set-kb:focus{outline:none;border-color:#6366f1;box-shadow:0 0 0 2px rgba(99,102,241,.25)}' +
    '#set-unit{font:600 11px system-ui,sans-serif;color:#94a3b8}' +
    '#set-done{border:1px solid #4338ca;background:#312e81;color:#e0e7ff;border-radius:8px;font:600 11px system-ui,sans-serif;padding:8px 12px;cursor:pointer}' +
    '#set-done:hover{background:#3730a3}' +
    '#set-note{font:400 10px system-ui,sans-serif;color:#94a3b8;margin-top:6px}' +
    '</style>' +
    '<div id="fab-wrap">' +
    '<button id="fab" title="ZyZoom Internet Downloader">' +
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 21h16"/></svg>' +
    '<span>ZID</span><span id="count"></span></button>' +
    '<button id="hide" title="Close extension" aria-label="Close extension">&times;</button>' +
    '</div>' +
    '<div id="video-icons"></div>' +
    '<div id="panel">' +
    '<div id="head"><span id="title">ZyZoom Internet Downloader</span><span id="sub"></span>' +
    '<button id="runapp" title="Start the ZID app">Run the app</button>' +
    '<button id="gear" title="Settings" aria-label="Settings">&#9881;</button>' +
    '<button id="close" title="Close">&times;</button></div>' +
    '<div id="list"></div>' +
    '<div id="foot"><button id="all">Send all</button><span id="status"></span></div>' +
    '<div id="lang-pop"><div id="lang-box"><div id="lang-title">Audio language</div>' +
    '<div id="lang-list"></div></div></div>' +
    '<div id="set-pop"><div id="set-box"><div id="set-title">Settings</div>' +
    '<label id="set-enable"><span>Enable extension</span><input id="set-on" type="checkbox" /><i class="sw"></i></label>' +
    '<label id="set-takeover"><input id="set-take" type="checkbox" /> Take over browser downloads</label>' +
    '<div id="set-label">Hide media smaller than</div>' +
    '<div id="set-row"><input id="set-kb" type="number" min="0" step="10" inputmode="numeric" />' +
    '<span id="set-unit">KB</span>' +
    '<button id="set-done" type="button">Done</button></div>' +
    '<div id="set-note">0 shows everything. Items with no known size always show.</div>' +
    '</div></div>' +
    '</div>';

  var fab = shadow.getElementById("fab");
  var countEl = shadow.getElementById("count");
  var panel = shadow.getElementById("panel");
  var subEl = shadow.getElementById("sub");
  var listEl = shadow.getElementById("list");
  var statusEl = shadow.getElementById("status");
  var allBtn = shadow.getElementById("all");
  var hideBtn = shadow.getElementById("hide");
  var fabWrap = shadow.getElementById("fab-wrap");
  var langPop = shadow.getElementById("lang-pop");
  var langList = shadow.getElementById("lang-list");
  var gearBtn = shadow.getElementById("gear");
  var runBtn = shadow.getElementById("runapp");
  var setPop = shadow.getElementById("set-pop");
  var setOn = shadow.getElementById("set-on");
  var setTake = shadow.getElementById("set-take");
  var setKb = shadow.getElementById("set-kb");
  var setDone = shadow.getElementById("set-done");
  var videoLayer = shadow.getElementById("video-icons");
  var videoIcons = [];
  var iconCount = 0;
  var totalCount = 0;
  var minSizeKb = 100;
  var extEnabled = true;
  var takeOver = true;
  var fadeTimer = 0;

  function holdOpen() {
    if (fadeTimer) { clearTimeout(fadeTimer); fadeTimer = 0; }
    fabWrap.style.opacity = "1";
  }

  function fadeIdle() {
    if (fadeTimer) clearTimeout(fadeTimer);
    fadeTimer = setTimeout(function () {
      fadeTimer = 0;
      if (!panelOpen) fabWrap.style.opacity = "";
    }, 2000);
  }

  fabWrap.addEventListener("mouseenter", holdOpen);
  fabWrap.addEventListener("mouseleave", fadeIdle);

  function hostOf(url) {
    try { return new URL(url).hostname; } catch (e) { return ""; }
  }

  function applyChrome() {
    // totalCount keeps the bar reachable when every item is filtered out.
    var show = totalCount || panelOpen || (isYouTube && ytNotice);
    // The x lives on the fab's trailing edge, so it must vanish with it.
    var fabOn = show && !iconCount;
    fab.style.display = fabOn ? "flex" : "none";
    hideBtn.style.display = fabOn ? "flex" : "none";
  }

  function videoIcon(video) {
    for (var i = 0; i < videoIcons.length; i++) {
      if (videoIcons[i].video === video) return videoIcons[i].btn;
    }
    var btn = document.createElement("button");
    btn.className = "vdl";
    btn.type = "button";
    btn.title = "Download with ZyZoom";
    btn.setAttribute("aria-label", "Download with ZyZoom");
    btn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 21h16"/></svg>';
    btn.addEventListener("mousedown", function (e) { e.stopPropagation(); });
    btn.addEventListener("click", function (e) {
      e.preventDefault();
      e.stopPropagation();
      toggle(true);
    });
    videoLayer.appendChild(btn);
    videoIcons.push({ video: video, btn: btn });
    return btn;
  }

  function syncVideoIcons() {
    if (!extEnabled) return;
    var videos = document.querySelectorAll("video");
    var seen = [];
    var vw = window.innerWidth || document.documentElement.clientWidth;
    var vh = window.innerHeight || document.documentElement.clientHeight;
    var visible = 0;

    for (var i = 0; i < videos.length; i++) {
      var video = videos[i];
      seen.push(video);
      var btn = videoIcon(video);
      var r = video.getBoundingClientRect();
      var onScreen = r.width >= 140 && r.height >= 80 &&
                     r.bottom > 0 && r.right > 0 && r.top < vh && r.left < vw;
      var left = Math.min(r.right - 42, vw - 42);
      if (!onScreen || left < 8) {
        btn.style.display = "none";
        continue;
      }
      btn.style.left = Math.round(left) + "px";
      btn.style.top = Math.round(Math.max(r.top + 8, 8)) + "px";
      btn.style.display = "flex";
      visible++;
    }

    for (var j = videoIcons.length - 1; j >= 0; j--) {
      var icon = videoIcons[j];
      if (seen.indexOf(icon.video) >= 0 && document.documentElement.contains(icon.video)) continue;
      if (icon.btn.parentNode) icon.btn.parentNode.removeChild(icon.btn);
      videoIcons.splice(j, 1);
    }

    iconCount = visible;
    applyChrome();
  }

  var iconTimer = 0;
  function queueVideoIcons() {
    if (iconTimer) return;
    iconTimer = setTimeout(function () {
      iconTimer = 0;
      syncVideoIcons();
    }, 80);
  }
  window.addEventListener("scroll", queueVideoIcons, true);
  window.addEventListener("resize", queueVideoIcons);

  function makeItem(raw, source) {
    if (!raw || !raw.url) return null;
    var url = String(raw.url);
    if (!/^https?:/i.test(url)) return null;

    var itag = String(raw.itag || "");
    if (!itag) {
      try { itag = new URL(url).searchParams.get("itag") || ""; } catch (e) { }
    }

    if (source === "youtube") {
      var h = hostOf(url);
      if (!/(^|\.)googlevideo\.com$/i.test(h) && !/(^|\.)youtube\.com$/i.test(h)) return null;
    } else if (!S.isMedia(url, raw.contentType || "")) {
      return null;
    }

    return {
      url: url,
      key: raw.key || S.normalizeUrl(url),
      name: raw.name || S.nameFor(url, raw.contentType || "", document.title, null),
      contentType: raw.contentType || "",
      size: Number(raw.size) || 0,
      kind: raw.kind || S.kindOf(url, raw.contentType || ""),
      source: source,
      pageUrl: raw.pageUrl || location.href,
      auto: !!raw.auto,
      muxed: !!raw.muxed,
      headers: raw.headers || [],
      audioLang: raw.audioLang || "",
      audioName: raw.audioName || "",
      audioDefault: !!raw.audioDefault,
      itag: itag,
      label: raw.label || "",
      tier: raw.tier || "",
      bitrate: Number(raw.bitrate) || 0,
      height: Number(raw.height) || 0
    };
  }

  function allItems() {
    var map = {};
    var ytItags = {};
    ytItems.forEach(function (it) { if (it.itag) ytItags[it.itag] = true; });
    function add(it) {
      if (!it || map[it.key]) return;
      if (it.source !== "youtube" && it.itag && ytItags[it.itag]) return;
      map[it.key] = it;
    }
    ytItems.forEach(add);
    networkItems.forEach(add);
    domItems.forEach(add);

    var list = [];
    for (var k in map) list.push(map[k]);
    list.sort(function (a, b) {
      var sa = a.source === "youtube" ? (a.muxed ? 0 : 1) : 2;
      var sb = b.source === "youtube" ? (b.muxed ? 0 : 1) : 2;
      if (sa !== sb) return sa - sb;
      if (a.size !== b.size) return b.size - a.size;
      return a.name.localeCompare(b.name);
    });
    return list.slice(0, 120);
  }

  function scanDom() {
    var out = [];
    var seen = {};
    function add(url) {
      if (!url || !/^https?:/i.test(url)) return;
      if (!S.isMedia(url, "")) return;
      var key = S.normalizeUrl(url);
      if (seen[key]) return;
      seen[key] = true;
      out.push({
        url: url,
        key: key,
        name: S.nameFor(url, "", document.title, null),
        contentType: "",
        size: 0,
        kind: S.kindOf(url, ""),
        source: "page",
        pageUrl: location.href
      });
    }

    var media = document.querySelectorAll("video, audio, source");
    for (var i = 0; i < media.length; i++) {
      var el = media[i];
      add(el.currentSrc || el.src || el.getAttribute("src"));
    }

    var links = document.querySelectorAll("a[href]");
    for (var j = 0; j < links.length && j < 600; j++) add(links[j].href);

    domItems = out.slice(0, 60);
  }

  function onFormats(formats, caps) {
    var out = [];
    var seen = {};
    var title = (document.title || "YouTube").replace(/\s*-\s*YouTube\s*$/i, "").trim();

    (formats || []).slice().sort(function (a, b) {
      return (b && b.muxed ? 1 : 0) - (a && a.muxed ? 1 : 0);
    }).forEach(function (f) {
      if (!f || !f.url) return;
      var ct = f.mimeType || "";
      var audio = /^audio\//i.test(ct);
      // The muxer only handles MP4 video; AV1 inside MP4 is kept, VP9/WebM hidden.
      if (!audio && !/mp4/i.test(ct)) return;
      var ext = audio ? (/mp4/i.test(ct) ? "m4a" : "webm")
                      : (/webm/i.test(ct) ? "webm" : "mp4");
      var q = audio
        ? (f.bitrate ? Math.round(f.bitrate / 1000) + "kbps"
                     : String(f.audioQuality || "audio").replace(/^AUDIO_QUALITY_/i, "").toLowerCase())
        : (f.qualityLabel || (f.itag ? "itag " + f.itag : "video"));
      // The complete A+V row wins the same-quality slot (360p/720p MP4).
      var dedupe = (audio ? "a" : "v") + "|" + q + "|" + ext +
                   (audio && f.audioTrack
                      ? "|" + (f.audioTrack.id || f.audioTrack.displayName || "") : "");
      if (seen[dedupe]) return;
      seen[dedupe] = true;

      var tier = audio ? "Audio" : "";
      if (!audio) {
        var h = Number(f.height) || parseInt(String(q).replace(/p.*$/i, ""), 10) || 0;
        tier = h >= 2160 ? "4K" : h >= 1440 ? "2K" : h >= 1080 ? "FHD" : h >= 720 ? "HD" : h >= 360 ? "SD" : "LD";
      }

      var item = makeItem({
        url: f.url,
        contentType: ct,
        size: f.contentLength || 0,
        kind: audio ? "audio" : "video",
        name: S.safeFileName(title + " - " + q + "." + ext),
        label: q + " " + ext.toUpperCase(),
        tier: tier.trim(),
        bitrate: f.bitrate || 0,
        height: f.height || parseInt(String(q).replace(/p.*$/i, ""), 10) || 0,
        muxed: !!f.muxed,
        audioLang: f.audioTrack ? (f.audioTrack.id || f.audioTrack.displayName || "") : "",
        audioName: f.audioTrack ? (f.audioTrack.displayName || f.audioTrack.id || "") : "",
        audioDefault: !!(f.audioTrack && f.audioTrack.audioIsDefault),
        itag: String(f.itag || "")
      }, "youtube");
      if (item) out.push(item);
    });

    out.sort(function (a, b) {
      if (a.kind !== b.kind) return a.kind === "video" ? -1 : 1;
      if (a.kind === "video") return (b.height - a.height) || (b.size - a.size);
      return (b.bitrate - a.bitrate) || (b.size - a.size);
    });

    // Captions ride along as Auto subtitle rows: the app starts them with the
    // video, hides them in the grid and skips the finish dialog.
    var capItems = [];
    (caps || []).forEach(function (c) {
      if (!c || !c.url) return;
      var lang = String(c.lang || "und");
      var name = String(c.name || lang).trim() || lang;
      var item = makeItem({
        url: c.url,
        contentType: "text/vtt",
        kind: "subtitle",
        name: S.safeFileName(title + "." + lang.toLowerCase() + ".vtt"),
        label: name,
        tier: "CC",
        auto: true
      }, "youtube");
      if (!item) return;
      item.captionAuto = !!c.auto;
      capItems.push(item);
    });
    // A human-made track beats the machine transcript.
    capItems.sort(function (a, b) {
      return (a.captionAuto ? 1 : 0) - (b.captionAuto ? 1 : 0);
    });
    ytCaptions = capItems;
    ytItems = out.concat(capItems);
    ytNotice = "";
    render();
  }

  function rawItems() {
    if (isYouTube && ytNotice) return [];
    return (isYouTube && ytItems.length) ? ytItems : allItems();
  }

  // Size filtering lives here so the list, count badge and Send all agree.
  function visibleItems() {
    var items = rawItems();
    if (!minSizeKb) return items;
    var limit = minSizeKb * 1024;
    return items.filter(function (it) {
      // Unknown size (page links, HLS/DASH) and subtitles always stay visible.
      return !(it.size > 0) || it.kind === "subtitle" || it.size >= limit;
    });
  }

  function row(item) {
    var wrap = document.createElement("div");
    wrap.className = "row";

    var meta = document.createElement("div");
    meta.className = "meta";

    var name = document.createElement("div");
    name.className = "name";
    if (item.tier) {
      var tag = document.createElement("span");
      tag.className = "tag tag-" + item.tier.toLowerCase();
      tag.textContent = item.tier;
      name.appendChild(tag);
    }
    if (item.source === "youtube" && item.kind === "video" && item.muxed) {
      var av = document.createElement("span");
      av.className = "tag tag-av";
      av.textContent = "A+V";
      av.title = "Video + audio in one file";
      name.appendChild(av);
    }
    var label = document.createElement("span");
    label.className = "label";
    label.textContent = item.label || item.name;
    name.appendChild(label);
    name.title = item.url;

    var info = document.createElement("div");
    info.className = "info";
    var bits = item.label ? [] : [item.kind, hostOf(item.url)];
    if (item.source === "youtube") {
      if (item.kind === "subtitle") bits.unshift("Subtitles");
      else bits.unshift(item.kind === "audio" ? "Audio only"
                   : item.muxed ? "Video+Audio" : "Video only");
    }
    if (item.size) bits.push(S.sizeText(item.size));
    info.textContent = bits.join(" \u00b7 ");
    if (item.kind === "playlist") {
      var warn = document.createElement("span");
      warn.className = "warn";
      warn.textContent = "  HLS/DASH";
      info.appendChild(warn);
    }

    meta.appendChild(name);
    meta.appendChild(info);

    var btn = document.createElement("button");
    btn.className = "dl";
    btn.textContent = pending[item.key] ? "Waiting..." : sent[item.key] ? "Sent" : "Download";
    btn.disabled = !!pending[item.key] || !!sent[item.key] || busy;
    btn.addEventListener("click", function () { try { sendItem(item); } catch (e) { } });

    wrap.appendChild(meta);
    wrap.appendChild(btn);
    return wrap;
  }

  function render() {
    // A broken row must never stop the buttons from coming back.
    try { renderPanel(); } catch (e) { }
  }

  function renderPanel() {
    var items = visibleItems();
    totalCount = rawItems().length;
    countEl.textContent = items.length ? String(items.length) : "";
    countEl.style.display = items.length ? "block" : "none";
    applyChrome();

    if (!panelOpen) return;

    listEl.textContent = "";
    if (!items.length) {
      var empty = document.createElement("div");
      empty.className = "empty";
      empty.textContent = !extEnabled
        ? "Extension is disabled. Turn it back on in Settings."
        : ytNotice || (totalCount && minSizeKb
          ? "All found media is under " + minSizeKb + " KB. Open Settings to change the limit."
          : "No media found yet. Play the video and try again.");
      listEl.appendChild(empty);
    } else {
      var groups = [
        ["Video", items.filter(function (it) { return it.kind === "video"; })],
        ["Audio", items.filter(function (it) { return it.kind === "audio"; })],
        ["Subtitles", items.filter(function (it) { return it.kind === "subtitle"; })],
        ["Other", items.filter(function (it) {
          return it.kind !== "video" && it.kind !== "audio" && it.kind !== "subtitle";
        })]
      ];
      groups.forEach(function (group) {
        if (!group[1].length) return;
        var head = document.createElement("div");
        head.className = "group";
        head.textContent = group[0] + " (" + group[1].length + ")";
        listEl.appendChild(head);
        group[1].forEach(function (it) { listEl.appendChild(row(it)); });
      });
    }

    subEl.textContent = items.length + (items.length === 1 ? " item" : " items") + " \u00b7 127.0.0.1:10007";
    allBtn.style.display = items.length ? "block" : "none";
    allBtn.disabled = !items.length || busy;
  }

  function refresh() {
    try { refreshPanel(); } catch (e) { }
  }

  // The MAIN-world YouTube helper has no chrome.cookies access and
  // document.cookie misses HttpOnly cookies, so read the session through the
  // background and hand it over.
  function pushYouTubeCookies() {
    try {
      chrome.runtime.sendMessage({ type: "cookies", url: "https://www.youtube.com/" }, function (res) {
        if (chrome.runtime.lastError || !res || !res.cookies) return;
        try {
          window.postMessage({ source: "idm-style-downloader", type: "cookies", cookies: res.cookies }, "*");
        } catch (e) { }
      });
    } catch (e) { }
  }

  function refreshPanel() {
    if (!extEnabled) return;
    if (location.href !== lastHref) {
      lastHref = location.href;
      ytItems = [];
      ytCaptions = [];
      ytNotice = "";
      networkItems = [];
      domItems = [];
      if (isYouTube) pushYouTubeCookies();
    }

    scanDom();

    if (isYouTube) {
      try {
        window.postMessage({ source: "idm-style-downloader", type: "ping" }, "*");
      } catch (e) { }
    }

    try {
      chrome.runtime.sendMessage({ type: "list" }, function (res) {
        if (chrome.runtime.lastError || !res || !res.items || !res.items.length) return;
        // Merge, do not replace: an MV3 service-worker restart forgets its tab
        // map, and an empty reply must not wipe what the panel already shows.
        var seen = {};
        networkItems.forEach(function (it) { seen[it.key] = it; });
        res.items.forEach(function (raw) {
          var item = makeItem(raw, "network");
          if (item) seen[item.key] = item;
        });
        networkItems = [];
        for (var k in seen) networkItems.push(seen[k]);
        render();
      });
    } catch (e) { }

    render();
    syncVideoIcons();
    if (panelOpen) checkApp();
  }

  function setStatus(text, ok) {
    statusEl.textContent = text || "";
    statusEl.className = "status" + (ok === true ? " ok" : ok === false ? " err" : "");
  }

  function appOffline(error) {
    return !error || /not running|connect|closed/i.test(error);
  }

  function setAppUp(up) {
    appUp = up;
    runBtn.style.display = up ? "none" : "block";
  }

  // A rebuilt or stale extension can talk to the app but expect another
  // protocol. Compare major.minor; patch/build differences are harmless.
  function versionMismatch(app, ext) {
    if (!app || !ext) return false;
    function mm(v) {
      var p = String(v).split(".");
      return (p[0] || "0") + "." + (p[1] || "0");
    }
    return mm(app) !== mm(ext);
  }

  // The app is up when its WebSocket accepts the connection.
  function checkApp(force) {
    var now = Date.now();
    if (!force && now - appCheckAt < 5000) return;
    appCheckAt = now;
    try {
      chrome.runtime.sendMessage({ type: "ping" }, function (res) {
        if (chrome.runtime.lastError) return;
        var up = !!(res && res.running);
        setAppUp(up);
        if (up && versionMismatch(res.appVersion, res.extVersion))
          setStatus("Update the extension: v" + res.extVersion +
                    " does not match app " + res.appVersion + ".", false);
      });
    } catch (e) { }
  }

  function openApp() {
    function viaLink() {
      // Chrome opens a custom scheme from tabs.create; Firefox can refuse a
      // non-web scheme there, so a plain link click is the fallback.
      try {
        var a = document.createElement("a");
        a.href = "zid://open";
        a.style.display = "none";
        document.documentElement.appendChild(a);
        a.click();
        setTimeout(function () { if (a.parentNode) a.parentNode.removeChild(a); }, 2000);
      } catch (e) { }
    }
    try {
      chrome.runtime.sendMessage({ type: "open-app" }, function (res) {
        if (chrome.runtime.lastError || !res || !res.ok) viaLink();
      });
    } catch (e) { viaLink(); }
  }

  // Every video gets a hidden audio link. The app drops it when the downloaded
  // video already contains audio; otherwise it is merged.
  function audioLanguageList() {
    var map = {}, out = [];
    ytItems.forEach(function (it) {
      if (it.kind !== "audio" || it.source !== "youtube" || !it.audioLang) return;
      if (map[it.audioLang]) return;
      map[it.audioLang] = true;
      out.push({ id: it.audioLang, name: it.audioName || it.audioLang, def: !!it.audioDefault });
    });
    return out;
  }

  function isDefaultLang(id) {
    var found = false;
    ytItems.forEach(function (it) {
      if (it.kind === "audio" && it.audioLang === id && it.audioDefault) found = true;
    });
    return found;
  }

  function bestAudioFor(video, lang) {
    var pool = ytItems.filter(function (it) {
      return it.kind === "audio" && it.source === "youtube" && it.url;
    });
    if (lang) {
      var same = pool.filter(function (it) { return it.audioLang === lang; });
      if (same.length) pool = same;
    } else {
      var def = pool.filter(function (it) { return it.audioDefault; });
      if (def.length) pool = def;
    }
    if (!pool.length) return null;
    var m4a = pool.filter(function (it) {
      return /\.m4a$/i.test(it.name) || /audio\/mp4/i.test(it.contentType);
    });
    if (m4a.length) pool = m4a;

    var target = (video.height && video.height < 360) ? 48000 : 128000;
    var best = pool[0], bestDelta = Infinity;
    pool.forEach(function (it) {
      var br = it.bitrate || (parseInt(String(it.label || it.name), 10) * 1000) || 0;
      var d = Math.abs(br - target);
      if (d < bestDelta) { best = it; bestDelta = d; }
    });
    return best;
  }

  // A muxed row only carries the default language; use an adaptive video of
  // the same quality when another language is picked.
  function videoOnlyFor(video) {
    var pool = ytItems.filter(function (it) {
      return it.source === "youtube" && it.kind === "video" && !it.muxed && it.url;
    });
    if (!pool.length) return null;
    var best = pool[0], bestDelta = Infinity;
    pool.forEach(function (it) {
      var d = Math.abs((it.height || 0) - (video.height || 0));
      if (d < bestDelta) { best = it; bestDelta = d; }
    });
    return best;
  }

  function bestCaption() {
    var pool = ytCaptions.filter(function (it) { return it.kind === "subtitle" && it.url; });
    if (!pool.length) return null;
    return pool[0];
  }

  function sendVideo(item, lang) {
    var actual = item;
    if (lang && item.muxed && !isDefaultLang(lang)) {
      var alt = videoOnlyFor(item);
      if (alt) actual = alt;
    }
    // One pairing token per click, so two qualities of the same video page merge
    // independently. Muxed rows already contain audio, so they need no hidden one.
    var pair = "p" + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
    var video = {};
    for (var k in actual) video[k] = actual[k];
    video.pair = pair;
    var batch = [video];
    if (!actual.muxed) {
      var audio = bestAudioFor(actual, lang);
      if (audio && !pending[audio.key] && !sent[audio.key]) {
        var hidden = {};
        for (var k in audio) hidden[k] = audio[k];
        hidden.auto = true;
        hidden.pair = pair;
        batch.push(hidden);
      }
    }
    // The first manual caption track (else the first track) follows the video.
    var single = batch.length === 1;
    var cap = bestCaption();
    if (cap && !pending[cap.key] && !sent[cap.key]) {
      var sub = {};
      for (var k in cap) sub[k] = cap[k];
      sub.pair = pair;
      batch.push(sub);
    }
    sendQueue(batch, single);
  }

  function closeLangPicker() {
    langPop.style.display = "none";
    langList.innerHTML = "";
  }

  function closeSettings() {
    setPop.style.display = "none";
  }

  function openSettings() {
    closeLangPicker();
    setOn.checked = extEnabled;
    setTake.checked = takeOver;
    setKb.value = String(minSizeKb);
    setPop.style.display = "flex";
    try { setKb.focus(); setKb.select(); } catch (e) { }
  }

  function isFullscreenNow() {
    return !!(document.fullscreenElement || document.webkitFullscreenElement ||
              document.msFullscreenElement);
  }

  function applyEnabled(on) {
    extEnabled = on !== false;
    if (!extEnabled) {
      panelOpen = false;
      panel.style.display = "none";
      closeLangPicker();
      closeSettings();
      host.style.display = "none";
      return;
    }
    if (!closed && !isFullscreenNow()) {
      host.style.display = "block";
      render();
      queueVideoIcons();
    }
  }

  function setEnabled(on) {
    applyEnabled(on);
    try { chrome.storage.local.set({ extEnabled: extEnabled }); } catch (e) { }
    if (extEnabled) refresh();
  }

  function setTakeOver(on) {
    takeOver = on === true;
    try { chrome.storage.local.set({ takeOverDownloads: takeOver }); } catch (e) { }
  }

  function setMinSize(v, persist) {
    v = Math.floor(Number(v));
    if (!isFinite(v) || v < 0) v = 0;
    minSizeKb = v;
    if (persist) {
      try { chrome.storage.local.set({ minSizeKb: v }); } catch (e) { }
    }
    if (extEnabled) render();
  }

  function loadSettings() {
    try {
      chrome.storage.local.get({ minSizeKb: 100, extEnabled: true, takeOverDownloads: true }, function (res) {
        if (chrome.runtime.lastError || !res) return;
        var v = Math.floor(Number(res.minSizeKb));
        if (isFinite(v) && v >= 0) minSizeKb = v;
        takeOver = res.takeOverDownloads !== false;
        applyEnabled(res.extEnabled !== false);
        if (extEnabled) refresh();
      });
    } catch (e) { }
  }

  function showLangPicker(video, langs) {
    langList.innerHTML = "";
    function addLang(id, name, note) {
      var b = document.createElement("button");
      b.type = "button";
      b.className = "lang";
      b.textContent = name;
      if (note) {
        var s = document.createElement("small");
        s.textContent = note;
        b.appendChild(s);
      }
      b.addEventListener("click", function () {
        closeLangPicker();
        sendVideo(video, id);
      });
      langList.appendChild(b);
    }
    addLang("", "Keep default", "Use the video's default audio");
    langs.forEach(function (l) {
      addLang(l.id, l.name, l.def ? "Default" : "Audio track");
    });
    langPop.style.display = "flex";
  }

  function sendItem(item) {
    if (item.source === "youtube" && item.kind === "video") {
      var langs = audioLanguageList();
      if (langs.length > 1 && (!item.muxed || videoOnlyFor(item))) {
        showLangPicker(item, langs);
        return;
      }
      sendVideo(item, "");
      return;
    }
    sendQueue([item], true);
  }

  function sendAll() {
    var items = visibleItems().filter(function (it) { return !sent[it.key]; });
    if (items.length) sendQueue(items, false);
  }

  function sendQueue(items, single) {
    if (busy) return;
    busy = true;
    var run = ++sendRun;
    clearTimeout(lockTimer);
    // If the app never answers, give the buttons back instead of locking the
    // panel until the page is reloaded.
    lockTimer = setTimeout(unlock, 3000);

    var i = 0, tries = 0, offline = false;

    function live() { return run === sendRun; }

    function finish(text, ok) {
      if (!live()) return;
      clearTimeout(lockTimer);
      lockTimer = 0;
      busy = false;
      setStatus(text, ok);
      render();
    }

    function fail(text) {
      if (!live()) return;
      pending = {};
      finish(text, false);
    }

    function step() {
      if (!live()) return;
      if (i >= items.length) {
        finish(single ? "Sent to the app. Confirm it in the download dialog." :
               "All " + items.length + " links sent.", true);
        return;
      }

      var item = items[i];
      pending[item.key] = true;
      render();
      if (!offline) {
        setStatus(single ? "Sending to the app..." :
                 "Sending " + (i + 1) + " of " + items.length + " links...");
      }

      try {
        chrome.runtime.sendMessage({ type: "send", item: item }, function (res) {
          if (!live()) return;
          if (chrome.runtime.lastError) { fail(chrome.runtime.lastError.message); return; }

          if (res && res.ok) {
            delete pending[item.key];
            sent[item.key] = true;
            setTimeout(function () { delete sent[item.key]; render(); }, 2500);
            i++;
            tries = 0;
            step();
            return;
          }

          var err = (res && res.error) || "Cannot send the link.";
          if (appOffline(err)) {
            setAppUp(false);
            if (tries === 0) openApp();
            tries++;
            if (tries <= 2) {
              offline = true;
              setStatus("Opening ZID... allow the browser prompt. The download will continue automatically.");
              setTimeout(step, 1000);
              return;
            }
            delete pending[item.key];
            fail("ZID did not start. Click Run the app, then try again.");
            return;
          }
          delete pending[item.key];
          fail(err);
        });
      } catch (e) {
        fail(e.message);
      }
    }

    step();
  }

  function unlock() {
    lockTimer = 0;
    // Drop the abandoned run: its late replies find run !== sendRun and return.
    sendRun++;
    busy = false;
    pending = {};
    setStatus("ZID did not answer. Click Run the app, then try again.", false);
    render();
  }

  function toggle(force) {
    panelOpen = (force === undefined) ? !panelOpen : !!force;
    panel.style.display = panelOpen ? "flex" : "none";
    if (!panelOpen) { closeLangPicker(); closeSettings(); }
    if (panelOpen) holdOpen();
    else fadeIdle();
    render();
    if (panelOpen) { refresh(); checkApp(true); }
  }

  function hideWidget() {
    closed = true;
    panelOpen = false;
    panel.style.display = "none";
    host.style.display = "none";
  }

  // Every handler is wrapped: one failing feature must not take out the others
  // registered in this same script.
  fab.addEventListener("click", function () { try { toggle(); } catch (e) { } });
  hideBtn.addEventListener("click", function () { try { hideWidget(); } catch (e) { } });
  runBtn.addEventListener("click", function () { try { openApp(); setStatus("Starting ZID..."); } catch (e) { } });
  langPop.addEventListener("click", function (e) {
    try { if (e.target === langPop) closeLangPicker(); } catch (err) { }
  });
  gearBtn.addEventListener("click", function () { try { openSettings(); } catch (e) { } });
  setPop.addEventListener("click", function (e) {
    try { if (e.target === setPop) closeSettings(); } catch (err) { }
  });
  setDone.addEventListener("click", function () {
    try { setMinSize(setKb.value, true); closeSettings(); } catch (e) { }
  });
  setOn.addEventListener("change", function () {
    try { setEnabled(setOn.checked); } catch (e) { }
  });
  setTake.addEventListener("change", function () {
    try { setTakeOver(setTake.checked); } catch (e) { }
  });
  setKb.addEventListener("change", function () {
    try { setMinSize(setKb.value, true); } catch (e) { }
  });
  setKb.addEventListener("keydown", function (e) {
    try {
      if (e.key === "Enter") { setMinSize(setKb.value, true); closeSettings(); }
    } catch (err) { }
  });
  shadow.getElementById("close").addEventListener("click", function () {
    try { toggle(false); } catch (e) { }
  });
  allBtn.addEventListener("click", function () { try { sendAll(); } catch (e) { } });
  document.addEventListener("keydown", function (e) {
    try {
      if (e.key !== "Escape") return;
      if (setPop.style.display === "flex") { closeSettings(); return; }
      if (langPop.style.display === "flex") { closeLangPicker(); return; }
      if (panelOpen) toggle(false);
    } catch (err) { }
  }, true);

  function syncFullscreen() {
    try { syncFullscreenNow(); } catch (e) { }
  }

  function syncFullscreenNow() {
    var fs = document.fullscreenElement || document.webkitFullscreenElement ||
             document.msFullscreenElement;
    if (fs) {
      panelOpen = false;
      panel.style.display = "none";
      host.style.display = "none";
    } else if (!closed && extEnabled) {
      host.style.display = "block";
      render();
      queueVideoIcons();
    }
  }
  document.addEventListener("fullscreenchange", syncFullscreen, true);
  document.addEventListener("webkitfullscreenchange", syncFullscreen, true);
  syncFullscreen();

  window.addEventListener("message", function (e) {
    try {
      if (!isYouTube || e.source !== window) return;
      var d = e.data;
      if (!d || d.source !== "idm-style-downloader") return;
      if (d.type === "formats") onFormats(d.formats, d.captions);
      else if (d.type === "notice") {
        ytNotice = String(d.text || "");
        ytItems = [];
        ytCaptions = [];
        render();
      }
    } catch (err) { }
  }, false);

  chrome.runtime.onMessage.addListener(function (msg) {
    try {
      if (!msg || msg.type !== "toggle") return;
      if (!extEnabled) {
        // The toolbar icon is the way back in while the widget is disabled.
        host.style.display = "block";
        closed = false;
        toggle(true);
        openSettings();
        return;
      }
      if (closed) {
        closed = false;
        host.style.display = "block";
        toggle(true);
      } else {
        toggle();
      }
    } catch (e) { }
  });

  try {
    chrome.storage.onChanged.addListener(function (changes, area) {
      try {
        if (area !== "local") return;
        if (changes.extEnabled) applyEnabled(changes.extEnabled.newValue !== false);
        if (changes.takeOverDownloads) takeOver = changes.takeOverDownloads.newValue !== false;
        if (changes.minSizeKb) {
          var v = Math.floor(Number(changes.minSizeKb.newValue));
          if (isFinite(v) && v >= 0) setMinSize(v, false);
        }
      } catch (e) { }
    });
  } catch (e) { }

  loadSettings();
  refresh();
  setInterval(function () { refresh(); }, 3000);
})();
