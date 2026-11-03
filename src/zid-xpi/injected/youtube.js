/* MAIN-world helper: reads YouTube's player response and asks InnerTube for
   direct media URLs. It never downloads anything itself. */
(function () {
  "use strict";
  if (window.__idmStyleYouTube) return;
  window.__idmStyleYouTube = true;

  var TAG = "idm-style-downloader";
  var access = atob("QUl6YVN5QU9fRkoyU2xxVThRNFNURUhMR0NpbHdfWTlfMTFxY1c4");
  // VISIONOS media URLs accept a plain GET; the ANDROID ones answer 403 unless
  // the request carries a Range header, which aria2 does not send up front.
  // shortcut: YouTube retires clients; if this returns HTTP 400 or no formats,
  // switch the client (yt-dlp's youtube extractor lists current ones).
  var ANDROID_VERSION = "20.10.38";
  var VISIONOS_VERSION = "1.02";
  var VISIONOS_UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) " +
                    "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";

  var last = "";
  var lastNotice = "";
  var cookieHeader = "";
  // Readable from the page console when a gate/teaser survives, so the exact
  // client answer can be inspected without another build.
  var diag = { page: null, sapisid: false, auth: false, po: false, clients: [] };
  try { window.__idmStyleYouTubeDiag = diag; } catch (e) { }

  // The player's own /player request carries a BotGuard PO token; YouTube
  // rejects a signed-in WEB call without it ("Video unavailable"). Capture the
  // token as the page sends it, since it cannot be generated here.
  var poToken = "";
  try {
    var realFetch = window.fetch;
    window.fetch = function (input, init) {
      try {
        var body = init && init.body;
        if (typeof body === "string" && body.indexOf("poToken") >= 0) {
          var m = /"poToken":"([^"]+)"/.exec(body);
          if (m) { poToken = m[1]; diag.po = true; }
        }
      } catch (e) { }
      return realFetch.apply(this, arguments);
    };
  } catch (e) { }
  var apiId = "";
  var apiFormats = [];
  var apiCaptions = [];
  var apiBusy = false;
  var apiTried = false;
  var apiAt = 0;

  // The live player response is what is actually playing. The page's initial
  // response can be a stale preview (age gate cleared after load) or the
  // previous video (SPA navigation), and the player re-fetches it with the
  // signed-in session, so ask the player first.
  function playerResponse() {
    try {
      var p = document.querySelector("#movie_player");
      var r = p && p.getPlayerResponse ? p.getPlayerResponse() : null;
      if (r && (r.videoDetails || r.playabilityStatus)) return r;
    } catch (e) { }
    try {
      var initial = window.ytInitialPlayerResponse;
      if (initial && (initial.videoDetails || initial.playabilityStatus)) return initial;
    } catch (e) { }
    return null;
  }

  function currentId() {
    try {
      if (window.ytInitialPlayerResponse && window.ytInitialPlayerResponse.videoDetails)
        return window.ytInitialPlayerResponse.videoDetails.videoId || "";
    } catch (e) { }
    try { return new URL(location.href).searchParams.get("v") || ""; } catch (e) { }
    return "";
  }

  function formatsFrom(sd) {
    var out = [];
    var groups = [[sd && sd.formats, true], [sd && sd.adaptiveFormats, false]];
    for (var g = 0; g < groups.length; g++) {
      var list = groups[g][0] || [];
      for (var i = 0; i < list.length; i++) {
        var f = list[i];
        if (!f || !f.url) continue;
        out.push({
          url: f.url,
          mimeType: f.mimeType || "",
          itag: f.itag || 0,
          qualityLabel: f.qualityLabel || "",
          audioQuality: f.audioQuality || "",
          bitrate: parseInt(f.averageBitrate || f.bitrate, 10) || 0,
          height: parseInt(f.height, 10) || 0,
          durationMs: parseInt(f.approxDurationMs, 10) || 0,
          contentLength: parseInt(f.contentLength, 10) || 0,
          xtags: f.xtags || "",
          audioTrack: f.audioTrack || null,
          muxed: groups[g][1]
        });
      }
    }
    return out;
  }

  function merge(primary, fallback) {
    var seen = {};
    var out = [];
    function add(list) {
      for (var i = 0; i < list.length; i++) {
        var f = list[i];
        var at = f.audioTrack;
        var key = String(f.itag || 0) + "|" + (f.xtags || "") + "|" +
                  (at ? (at.id || at.displayName || "") : "");
        if (seen[key]) continue;
        seen[key] = true;
        out.push(f);
      }
    }
    add(primary);
    add(fallback);
    return out;
  }

  // Caption tracks as WebVTT. fmt=vtt turns the timedtext endpoint into a
  // plain .vtt file; kind:"asr" marks the auto-generated track.
  function captionsFrom(p) {
    var out = [];
    try {
      var r = p && p.captions && p.captions.playerCaptionsTracklistRenderer;
      var list = (r && r.captionTracks) || [];
      for (var i = 0; i < list.length && out.length < 40; i++) {
        var c = list[i];
        if (!c || !c.baseUrl) continue;
        var u;
        try { u = new URL(c.baseUrl); u.searchParams.set("fmt", "vtt"); }
        catch (e) { continue; }
        var name = (c.name && (c.name.simpleText ||
                    (c.name.runs && c.name.runs[0] && c.name.runs[0].text))) || "";
        out.push({
          url: u.href,
          lang: c.languageCode || "und",
          name: name || c.languageCode || "",
          auto: c.kind === "asr"
        });
      }
    } catch (e) { }
    return out;
  }

  function cfg(name) {
    try {
      if (window.ytcfg && window.ytcfg.get) {
        var v = window.ytcfg.get(name);
        if (v) return v;
      }
      var d = window.ytcfg && window.ytcfg.data_;
      return (d && d[name]) || "";
    } catch (e) { return ""; }
  }

  function cookieValue(name) {
    // The content script's chrome.cookies copy also has HttpOnly cookies;
    // document.cookie is the fallback until that message arrives.
    var jars = [cookieHeader, document.cookie || ""];
    for (var j = 0; j < jars.length; j++) {
      var parts = String(jars[j]).split(";");
      for (var i = 0; i < parts.length; i++) {
        var p = parts[i].replace(/^\s+/, "");
        var eq = p.indexOf("=");
        if (eq > 0 && p.slice(0, eq).trim() === name) return p.slice(eq + 1).trim();
      }
    }
    return "";
  }

  // The WEB client needs SAPISIDHASH; without it InnerTube treats the request
  // as logged out and an age gate never clears.
  function sapisidHash() {
    var sapisid = cookieValue("SAPISID") || cookieValue("__Secure-3PAPISID");
    diag.sapisid = !!sapisid;
    var c = window.crypto;
    if (!sapisid || !c || !c.subtle || !window.TextEncoder) return Promise.resolve("");
    var ts = Math.floor(Date.now() / 1000);
    var text = ts + " " + sapisid + " " + location.origin;
    return c.subtle.digest("SHA-1", new window.TextEncoder().encode(text)).then(function (buf) {
      var hex = "";
      var bytes = new Uint8Array(buf);
      for (var i = 0; i < bytes.length; i++) hex += ("0" + bytes[i].toString(16)).slice(-2);
      diag.auth = true;
      return "SAPISIDHASH " + ts + "_" + hex;
    }).catch(function () { return ""; });
  }

  function refreshApi(id) {
    if (!id || apiBusy) return;
    // Keep the result; retry while empty (age checks sometimes clear after
    // sign-in) and throttle the retries so the 1 s tick does not hammer the API.
    if (id === apiId && (apiFormats.length || Date.now() - apiAt < 3000)) return;
    apiBusy = true;
    apiAt = Date.now();
    if (id !== apiId) {
      apiId = id;
      apiFormats = [];
      apiCaptions = [];
      apiTried = false;
      diag.clients = [];
    }

    var key = cfg("INNERTUBE_API_KEY") || access;
    var visitor = cfg("VISITOR_DATA");
    var android = {
      clientName: "ANDROID",
      clientVersion: ANDROID_VERSION,
      androidSdkVersion: 30,
      hl: cfg("HL") || "en",
      gl: cfg("GL") || "US",
      visitorData: visitor
    };
    var visionos = {
      clientName: "VISIONOS",
      clientVersion: VISIONOS_VERSION,
      deviceMake: "Apple",
      deviceModel: "RealityDevice17,1",
      userAgent: VISIONOS_UA,
      osName: "visionOS",
      osVersion: "26.5.23O471",
      hl: cfg("HL") || "en",
      timeZone: "UTC",
      utcOffsetMinutes: 0,
      visitorData: visitor
    };
    var web = {
      clientName: "WEB",
      clientVersion: cfg("INNERTUBE_CLIENT_VERSION") || "2.20261028.01.00",
      hl: cfg("HL") || "en",
      gl: cfg("GL") || "US",
      visitorData: visitor,
      userAgent: navigator.userAgent,
      originalUrl: location.href,
      platform: "DESKTOP",
      clientFormFactor: "UNKNOWN_FORM_FACTOR",
      user: { lockedSafetyMode: false },
      request: { useSsl: true }
    };

    // The signed-in WEB player answers "Video unavailable" when the signature
    // timestamp is missing; the page exposes it as STS.
    function playbackContext() {
      var p = { html5Preference: "HTML5_PREF_WANTS" };
      var sts = parseInt(cfg("STS"), 10) || 0;
      if (sts) p.signatureTimestamp = sts;
      return p;
    }

    function player(client, clientName, clientVersion, extra) {
      var headers = { "Content-Type": "application/json" };
      if (clientName) {
        headers["X-Youtube-Client-Name"] = clientName;
        headers["X-Youtube-Client-Version"] = clientVersion;
      }
      if (client.visitorData) headers["X-Goog-Visitor-Id"] = client.visitorData;
      if (extra) for (var k in extra) headers[k] = extra[k];
      var body = {
        context: {
          client: client,
          user: { lockedSafetyMode: false },
          request: { useSsl: true }
        },
        videoId: id,
        playbackContext: { contentPlaybackContext: playbackContext() },
        contentCheckOk: true,
        racyCheckOk: true
      };
      if (poToken) body.serviceIntegrityDimensions = { poToken: poToken };
      return fetch(location.origin + "/youtubei/v1/player?key=" + key + "&prettyPrint=false", {
        method: "POST",
        credentials: "include",
        headers: headers,
        body: JSON.stringify(body)
      }).then(function (r) {
        return r.json().then(function (j) {
          var ps = (j && j.playabilityStatus) || {};
          var sd = (j && j.streamingData) || {};
          diag.clients.push({
            client: client.clientName,
            http: r.status,
            status: ps.status || "",
            reason: ps.reason || "",
            formats: (sd.formats || []).length,
            adaptive: (sd.adaptiveFormats || []).length,
            urls: (sd.formats || []).concat(sd.adaptiveFormats || []).filter(function (f) {
              return f && f.url;
            }).length
          });
          return r.ok ? j : null;
        }).catch(function () { return null; });
      }).catch(function () { return null; });
    }

    function done(formats, caps) {
      apiBusy = false;
      if (id !== apiId) return;
      apiFormats = formats;
      // A client that answers without captions (VISIONOS usually does) must not
      // wipe the list another client already returned.
      if (caps && caps.length) apiCaptions = caps;
      apiTried = true;
      if (formats.length || apiCaptions.length) post(true);
    }

    // The Android call supplies a visitor id (VisionOS rejects requests without
    // one on some videos); VisionOS URLs accept a plain GET, the adaptive
    // Android ones answer 403 unless the request carries a Range header.
    player(android, null, null).then(function (a) {
      if (id !== apiId) { done([]); return; }
      var formats = a && a.streamingData ? formatsFrom(a.streamingData) : [];
      var caps = a ? captionsFrom(a) : [];
      var v = a && a.responseContext ? a.responseContext.visitorData : "";
      if (v) {
        android.visitorData = v;
        visionos.visitorData = v;
        web.visitorData = v;
      }
      return player(visionos, "101", VISIONOS_VERSION).then(function (j) {
        if (id !== apiId) { done([]); return; }
        if (j && j.streamingData) formats = merge(formatsFrom(j.streamingData), formats);
        if (j) caps = caps.concat(captionsFrom(j));
        if (formats.length) { done(formats, caps); return; }
        // Age-restricted videos can reject anonymous clients; the signed-in WEB
        // client (same cookies + SAPISIDHASH) still returns media URLs.
        return sapisidHash().then(function (auth) {
          var extra = auth ? {
            "Authorization": auth,
            "X-Origin": location.origin,
            "X-Goog-AuthUser": "0"
          } : null;
          return player(web, "1", web.clientVersion, extra);
        }).then(function (w) {
          if (id !== apiId) { done([]); return; }
          if (w && w.streamingData) formats = merge(formatsFrom(w.streamingData), formats);
          if (w) caps = caps.concat(captionsFrom(w));
          done(formats, caps);
        });
      });
    });
  }

  function notice(text) {
    if (text === lastNotice) return;
    lastNotice = text;
    try { window.postMessage({ source: TAG, type: "notice", text: text }, "*"); } catch (e) { }
  }

  function post(force) {
    var r = playerResponse() || {};
    var id = (r.videoDetails && r.videoDetails.videoId) || currentId();
    if (id) refreshApi(id);

    var pageFormats = formatsFrom(r.streamingData);
    var pageCaps = captionsFrom(r);
    // When the API has real formats, a lone page muxed stream is the gate
    // teaser; drop it so the panel does not offer a second, 403 URL.
    if (apiFormats.length && pageFormats.length === 1 && pageFormats[0].muxed) pageFormats = [];
    var out = merge(apiFormats, pageFormats);
    var seenLang = {};
    var caps = [];
    var allCaps = (apiCaptions.length ? apiCaptions : []).concat(pageCaps);
    for (var ci = 0; ci < allCaps.length; ci++) {
      var ck = allCaps[ci].lang + "|" + (allCaps[ci].auto ? "a" : "m") +
               "|" + allCaps[ci].name;
      if (seenLang[ck]) continue;
      seenLang[ck] = true;
      caps.push(allCaps[ci]);
    }
    var ps = r.playabilityStatus || {};
    diag.page = {
      status: ps.status || "",
      reason: ps.reason || "",
      formats: pageFormats.length,
      muxed: pageFormats.length === 1 && !!pageFormats[0].muxed
    };
    // A lone muxed format is only a gate teaser when it is shorter than the
    // video. YouTube now serves the adaptive tracks through SABR (no direct
    // URLs), so a full-length 360p stream is the real, downloadable file.
    var loneMuxed = out.length === 1 && out[0].muxed;
    var pageLen = parseInt(r.videoDetails && r.videoDetails.lengthSeconds, 10) || 0;
    var fullMuxed = loneMuxed && pageLen > 0 && out[0].durationMs > 0 &&
                    Math.abs(out[0].durationMs / 1000 - pageLen) <= 5;
    var gate = ps.status && ps.status !== "OK" && !apiFormats.length;
    var teaser = !apiFormats.length && !apiBusy && apiTried && loneMuxed && !fullMuxed;
    if (gate || teaser) {
      if (!apiBusy) {
        notice(gate
          ? "YouTube blocked this video: " + (ps.reason || ps.status) +
            ". Sign in with an age-verified account, then reload."
          : "YouTube only served a preview stream. If this video is age-restricted, " +
            "finish age verification for this account on YouTube (or sign in with " +
            "a verified one), then reload.");
      }
      return;
    }
    // A real teaser must not flash back into the panel on a retry tick.
    if (!apiFormats.length && loneMuxed && !fullMuxed) return;

    if (!out.length) return;
    if (lastNotice) notice("");

    var key = out.map(function (f) {
      return f.itag + ":" + f.url.length + ":" + f.contentLength;
    }).join("|") + "||" + caps.map(function (c) {
      return c.lang + (c.auto ? "a" : "m");
    }).join(",");
    if (!force && key === last) return;
    last = key;

    try {
      window.postMessage({ source: TAG, type: "formats", formats: out, captions: caps }, "*");
    } catch (e) { }
  }

  post(false);
  var tries = 0;
  var timer = setInterval(function () {
    post(false);
    if (++tries >= 40) clearInterval(timer);
  }, 1000);

  window.addEventListener("yt-navigate-finish", function () {
    last = "";
    lastNotice = "";
    apiId = "";
    apiFormats = [];
    apiCaptions = [];
    post(true);
  }, true);
  document.addEventListener("yt-page-data-updated", function () { post(false); }, true);
  window.addEventListener("message", function (e) {
    if (e.source !== window || !e.data || e.data.source !== TAG) return;
    if (e.data.type === "cookies") {
      cookieHeader = String(e.data.cookies || "");
      post(true);
    } else if (e.data.type === "ping") post(true);
  }, false);
})();
