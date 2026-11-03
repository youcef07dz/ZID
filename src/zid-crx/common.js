/* Shared helpers: media detection, naming and the Neat Link message format. */
(function (g) {
  "use strict";

  var MEDIA_EXT = /\.(mp4|m4v|webm|mkv|mov|avi|flv|wmv|mp3|m4a|aac|ogg|oga|opus|wav|flac|m3u8|mpd|f4m)$/i;
  var SEGMENT_EXT = /\.(ts|m4s|m4f)$/i;
  var YOUTUBE_UI_AUDIO = /^\/s\/search\/audio\//i;

  var MIME_EXT = {
    "video/mp4": "mp4", "video/webm": "webm", "video/quicktime": "mov",
    "video/x-matroska": "mkv", "video/x-flv": "flv", "video/x-msvideo": "avi",
    "audio/mpeg": "mp3", "audio/mp4": "m4a", "audio/x-m4a": "m4a",
    "audio/aac": "aac", "audio/ogg": "ogg", "audio/opus": "opus",
    "audio/wav": "wav", "audio/x-wav": "wav", "audio/flac": "flac",
    "application/vnd.apple.mpegurl": "m3u8",
    "application/x-mpegurl": "m3u8",
    "application/dash+xml": "mpd"
  };

  function mimeOf(contentType) {
    return String(contentType || "").split(";")[0].trim().toLowerCase();
  }

  function extFromMime(contentType) {
    return MIME_EXT[mimeOf(contentType)] || "";
  }

  function pathOf(url) {
    try { return new URL(url).pathname.toLowerCase(); } catch (e) { return ""; }
  }

  function extFromUrl(url) {
    var m = /\.([a-z0-9]{2,6})$/i.exec(pathOf(url));
    return m ? m[1].toLowerCase() : "";
  }

  function isMedia(url, contentType) {
    if (!url) return false;
    var u;
    try { u = new URL(url); } catch (e) { return false; }
    if (u.protocol !== "http:" && u.protocol !== "https:") return false;

    var path = u.pathname.toLowerCase();
    // YouTube's voice-search UI sounds are real MP3s but not page media.
    if (YOUTUBE_UI_AUDIO.test(path) && /(^|\.)youtube\.com$/i.test(u.hostname)) return false;
    if (SEGMENT_EXT.test(path)) return false;
    if (MEDIA_EXT.test(path)) return true;

    var ct = mimeOf(contentType);
    if (!ct) return false;
    if (ct.indexOf("video/") === 0 || ct.indexOf("audio/") === 0) return true;
    return ct === "application/vnd.apple.mpegurl" ||
           ct === "application/x-mpegurl" ||
           ct === "application/dash+xml";
  }

  function kindOf(url, contentType) {
    var ct = mimeOf(contentType);
    if (ct.indexOf("video/") === 0) return "video";
    if (ct.indexOf("audio/") === 0) return "audio";
    if (ct.indexOf("mpegurl") >= 0 || ct === "application/dash+xml") return "playlist";
    var ext = extFromUrl(url);
    if (/^(mp4|m4v|webm|mkv|mov|avi|flv|wmv)$/.test(ext)) return "video";
    if (/^(mp3|m4a|aac|ogg|oga|opus|wav|flac)$/.test(ext)) return "audio";
    if (/^(m3u8|mpd|f4m)$/.test(ext)) return "playlist";
    return "file";
  }

  // YouTube re-signs the same file per request (range/rn/rbuf); strip those
  // so one video is one entry, not one entry per chunk.
  function normalizeUrl(url) {
    try {
      var u = new URL(url);
      u.searchParams.delete("range");
      u.searchParams.delete("rn");
      u.searchParams.delete("rbuf");
      return u.href;
    } catch (e) { return url || ""; }
  }

  function baseName(url) {
    try {
      var n = new URL(url).pathname.split("/").pop() || "";
      return decodeURIComponent(n);
    } catch (e) { return ""; }
  }

  function safeFileName(name) {
    var s = String(name || "download")
      .replace(/[\\/:*?"<>|\u0000-\u001f]/g, "_")
      .replace(/[. ]+$/, "")
      .replace(/^\s+|\s+$/g, "")
      .slice(0, 150);
    return s || "download";
  }

  function nameFor(url, contentType, title, index) {
    var ext = extFromUrl(url) || extFromMime(contentType) || "mp4";
    var base = baseName(url);
    var itag = "";
    try { itag = new URL(url).searchParams.get("itag") || ""; } catch (e) { }

    var generic = !base || !/\.[a-z0-9]{2,6}$/i.test(base) ||
                  /^(videoplayback|playlist|manifest|index|master|media)$/i.test(base);
    if (generic) {
      var clean = String(title || "").replace(/[\\/:*?"<>|]+/g, " ").replace(/\s+/g, " ").trim();
      base = (clean && clean.length <= 70) ? clean : (itag ? "media-" + itag : "media");
      if (index != null) base += " " + (index + 1);
      base += "." + ext;
    }
    return safeFileName(base);
  }

  function buildMessage(item, pageUrl, cookies) {
    var lines = [];
    lines.push("1:GET");
    lines.push("2:" + item.url);
    lines.push("3:");
    var kind = item.kind || "media";
    if (kind === "video" && item.muxed) kind = "muxed";
    lines.push("6:" + kind);
    lines.push("4:" + (item.name || "Media"));
    if (pageUrl) {
      try { lines.push("Origin: " + new URL(pageUrl).origin); } catch (e) { }
      lines.push("Referer: " + pageUrl);
      lines.push("5:" + pageUrl);
    }
    if (typeof navigator !== "undefined" && navigator.userAgent)
      lines.push("User-Agent: " + navigator.userAgent);
    if (cookies) lines.push("Cookie: " + cookies);
    if (item.contentType) lines.push("Content-Type: " + item.contentType);
    // Replayed so the app sends the same Accept-Language / sec-* the CDN signed
    // the URL against (see background.js).
    if (item.headers && item.headers.length) {
      for (var i = 0; i < item.headers.length; i++) lines.push(item.headers[i]);
    }
    if (item.size > 0) lines.push("7:" + item.size);
    if (item.auto) lines.push("9:1");
    if (item.pair) lines.push("10:" + item.pair);
    lines.push('Content-Disposition: attachment; filename="' + safeFileName(item.name) + '"');
    return lines.join("\r\n") + "\r\n";
  }

  function sizeText(bytes) {
    var b = Number(bytes) || 0;
    if (b <= 0) return "";
    var u = ["B", "KB", "MB", "GB", "TB"], i = 0;
    while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
    return b.toFixed(1) + " " + u[i];
  }

  g.IDMStyle = {
    isMedia: isMedia,
    kindOf: kindOf,
    normalizeUrl: normalizeUrl,
    nameFor: nameFor,
    safeFileName: safeFileName,
    buildMessage: buildMessage,
    sizeText: sizeText
  };
})(typeof self !== "undefined" ? self : this);
