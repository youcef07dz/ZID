if (typeof importScripts === "function") importScripts("common.js");

var APP_WS = "ws://127.0.0.1:10007/download";
var APP_PROTO = "neatextension.v1";
var MAX_PER_TAB = 80;

var byTab = new Map();
// Some CDNs sign the media URL against the request headers (User-Agent,
// Accept-Language, sec-ch-ua...). Keep the ones the player sent so the app can
// repeat them; Accept-Encoding stays out because aria2 would store gzip bodies
// raw. Keyed by tab, then by normalized URL.
var sentHeaders = new Map();
var FINGERPRINT_HEADER = /^(accept|accept-language|origin|sec-fetch-dest|sec-fetch-mode|sec-fetch-site|sec-ch-ua|sec-ch-ua-mobile|sec-ch-ua-platform)$/i;

// The content script owns the toggle; the worker only needs to stop capturing.
var enabled = true;
try {
  chrome.storage.local.get({ extEnabled: true }, function (res) {
    if (!chrome.runtime.lastError && res) enabled = res.extEnabled !== false;
  });
  chrome.storage.onChanged.addListener(function (changes, area) {
    if (area === "local" && changes.extEnabled) enabled = changes.extEnabled.newValue !== false;
  });
} catch (e) { }

// Normal browser downloads go to ZID unless the user turns this off.
var takeOver = true;
try {
  chrome.storage.local.get({ takeOverDownloads: true }, function (res) {
    if (!chrome.runtime.lastError && res) takeOver = res.takeOverDownloads !== false;
  });
  chrome.storage.onChanged.addListener(function (changes, area) {
    if (area === "local" && changes.takeOverDownloads)
      takeOver = changes.takeOverDownloads.newValue !== false;
  });
} catch (e) { }

function rememberHeaders(d) {
  if (!enabled) return;
  if (d.tabId < 0 || !d.requestHeaders) return;
  if (!IDMStyle.isMedia(d.url, "")) return;
  var keep = [];
  for (var i = 0; i < d.requestHeaders.length; i++) {
    var h = d.requestHeaders[i];
    if (h.name && h.value && FINGERPRINT_HEADER.test(h.name))
      keep.push(h.name + ": " + h.value);
  }
  if (!keep.length) return;
  var map = sentHeaders.get(d.tabId);
  if (!map) { map = new Map(); sentHeaders.set(d.tabId, map); }
  if (map.size > 40) map.delete(map.keys().next().value);
  map.set(IDMStyle.normalizeUrl(d.url), keep);
}

// Accept-Language needs extraHeaders on Chrome; Firefox rejects the option.
try {
  chrome.webRequest.onSendHeaders.addListener(rememberHeaders,
    { urls: ["http://*/*", "https://*/*"], types: ["media", "object", "xmlhttprequest", "other"] },
    ["requestHeaders", "extraHeaders"]);
} catch (e) {
  chrome.webRequest.onSendHeaders.addListener(rememberHeaders,
    { urls: ["http://*/*", "https://*/*"], types: ["media", "object", "xmlhttprequest", "other"] },
    ["requestHeaders"]);
}

function headerValue(headers, name) {
  if (!headers) return "";
  for (var i = 0; i < headers.length; i++) {
    if (headers[i].name && headers[i].name.toLowerCase() === name) return headers[i].value || "";
  }
  return "";
}

function trimTab(map) {
  while (map.size > MAX_PER_TAB) {
    var oldest = null;
    map.forEach(function (v, k) { if (oldest === null || v.ts < map.get(oldest).ts) oldest = k; });
    map.delete(oldest);
  }
}

chrome.webRequest.onResponseStarted.addListener(function (d) {
  if (!enabled) return;
  if (d.tabId < 0) return;
  if (d.statusCode !== 200 && d.statusCode !== 206) return;

  var ct = headerValue(d.responseHeaders, "content-type");
  var len = parseInt(headerValue(d.responseHeaders, "content-length"), 10) || 0;
  var range = headerValue(d.responseHeaders, "content-range");
  // Telegram's page-served file is neither a file to send nor page media.
  if (TG_FILE.test(d.url)) return;
  // Neat-style: files are handed off here; downloads.onCreated only cancels
  // the browser's duplicate copy of a URL zid just sent. A response the
  // browser is downloading is a file even when its MIME is a media type.
  if (takeOverResponse(d, ct, len, range)) return;
  if (!IDMStyle.isMedia(d.url, ct)) return;

  var key = IDMStyle.normalizeUrl(d.url);
  var map = byTab.get(d.tabId);
  if (!map) { map = new Map(); byTab.set(d.tabId, map); }

  var hmap = sentHeaders.get(d.tabId);
  var hdrs = (hmap && hmap.get(key)) || [];
  if (hmap) hmap.delete(key);

  var old = map.get(key);
  if (old) {
    if (!old.size && len && !range) old.size = len;
    if (!old.contentType && ct) old.contentType = ct;
    if (!old.headers || !old.headers.length) old.headers = hdrs;
    return;
  }

  map.set(key, {
    url: d.url,
    key: key,
    name: IDMStyle.nameFor(d.url, ct, "", null),
    contentType: ct,
    size: range ? 0 : len,
    kind: IDMStyle.kindOf(d.url, ct),
    pageUrl: d.initiator || "",
    headers: hdrs,
    source: "network",
    ts: Date.now()
  });
  trimTab(map);
  updateBadge(d.tabId);
}, { urls: ["http://*/*", "https://*/*"], types: ["main_frame", "media", "object", "xmlhttprequest", "other"] }, ["responseHeaders"]);

chrome.webNavigation.onCommitted.addListener(function (d) {
  if (d.frameId === 0) {
    byTab.delete(d.tabId);
    sentHeaders.delete(d.tabId);
    updateBadge(d.tabId);
  }
});

chrome.tabs.onRemoved.addListener(function (tabId) {
  byTab.delete(tabId);
  sentHeaders.delete(tabId);
});

chrome.tabs.onActivated.addListener(function (info) {
  updateBadge(info.tabId);
});

function listFor(tabId) {
  var map = byTab.get(tabId);
  var out = [];
  if (map) map.forEach(function (v) { out.push(v); });
  out.sort(function (a, b) { return a.ts - b.ts; });
  return out;
}

// Toolbar badge: how many media items the panel will offer for this tab.
function updateBadge(tabId) {
  if (tabId == null || tabId < 0) return;
  try {
    var api = chrome.action || chrome.browserAction;
    var n = listFor(tabId).length;
    api.setBadgeBackgroundColor({ tabId: tabId, color: "#0b63ce" });
    api.setBadgeText({ tabId: tabId, text: n ? (n > 99 ? "99+" : String(n)) : "" });
  } catch (e) { }
}

function sanitizeItem(raw) {
  if (!raw || typeof raw.url !== "string") return null;
  // Captions are WebVTT text, not media by extension or MIME. A browser
  // download (raw.file) can be any http(s) file, so it skips the media test.
  if (!raw.file && raw.kind !== "subtitle" && !IDMStyle.isMedia(raw.url, raw.contentType || "")) return null;
  return {
    url: raw.url,
    name: IDMStyle.safeFileName(raw.name || IDMStyle.nameFor(raw.url, raw.contentType || "", "")),
    contentType: String(raw.contentType || "").slice(0, 120),
    size: Math.max(0, parseInt(raw.size, 10) || 0),
    kind: String(raw.kind || "media").slice(0, 20),
    auto: !!raw.auto,
    muxed: !!raw.muxed,
    headers: (raw.headers || []).slice(0, 12).map(function (h) { return String(h).slice(0, 400); }),
    pair: String(raw.pair || "").slice(0, 40)
  };
}

function cookieHeader(url) {
  return new Promise(function (resolve) {
    try {
      chrome.cookies.getAll({ url: url }, function (list) {
        if (chrome.runtime.lastError) { resolve(""); return; }
        resolve((list || []).map(function (c) { return c.name + "=" + c.value; }).join("; "));
      });
    } catch (e) { resolve(""); }
  });
}

function sendWs(text) {
  return new Promise(function (resolve) {
    var done = false;
    var ws = null;
    var timer = setTimeout(function () { finish(false, "App is not running (127.0.0.1:10007)"); }, 2500);

    function finish(ok, error) {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try { if (ws) ws.close(); } catch (e) { }
      resolve(ok ? { ok: true } : { ok: false, error: error || "Cannot connect to the app" });
    }

    try {
      ws = new WebSocket(APP_WS, APP_PROTO);
    } catch (e) {
      finish(false, e.message);
      return;
    }
    ws.onopen = function () {
      try {
        ws.send(text);
      } catch (e) {
        finish(false, e.message);
      }
    };
    // The app answers "ok" once it queued the request, "err" when it
    // refused it. The browser-download hand-off waits for this word.
    ws.onmessage = function (e) {
      var reply = String((e && e.data) || "").trim().toLowerCase();
      if (reply === "ok") finish(true);
      else finish(false, reply === "err" ? "The app refused the download" : "Unexpected reply from the app");
    };
    ws.onerror = function () { finish(false, "App is not running (127.0.0.1:10007)"); };
    ws.onclose = function () { finish(false, "Connection closed by the app"); };
  });
}

function sendToApp(raw, sender) {
  var item = sanitizeItem(raw);
  if (!item) return Promise.resolve({ ok: false, error: "Unsupported link" });

  var pageUrl = (sender && sender.tab && sender.tab.url) || raw.pageUrl || "";
  // googlevideo.com has no login cookies; age-restricted media needs the
  // youtube.com session, so read cookies for the page URL instead.
  var cookieUrl = item.url;
  try {
    if (/(^|\.)googlevideo\.com$/i.test(new URL(item.url).hostname))
      cookieUrl = pageUrl || "https://www.youtube.com/";
  } catch (e) { }
  return cookieHeader(cookieUrl).then(function (cookies) {
    return sendWs(IDMStyle.buildMessage(item, pageUrl, cookies));
  });
}

// ---------------------------------------------------------------------
//  Normal browser downloads -> app hand-off (on by default; the panel
//  can turn it off)
//
//  Neat-style: webRequest spots the file and sends it to the app. The
//  browser's own download is cancelled only when its URL is one zid just
//  sent; unrelated downloads stay in the browser.
// ---------------------------------------------------------------------
var sentFiles = new Map();
var SENT_TTL = 60000;

var DOWNLOAD_MIME = /^(application\/(octet-stream|x-msdownload|x-msi|x-7z-compressed|x-rar-compressed|x-tar|gzip|x-gzip-compressed|x-apple-diskimage|zip|x-zip-compressed|x-iso9660-image|vnd\.android\.package-archive|x-bittorrent|epub\+zip)|binary\/octet-stream|application\/(force-download|download))$/i;

// Telegram Web K streams a file through its own page service worker and makes
// the id up in the page (/k/d/<random>). That URL does not exist on the server,
// so only the browser can read it; a downloader can just get a 302. Leave it to
// the browser instead of queueing a URL that will fail.
var TG_FILE = /^https:\/\/web\.telegram\.org\/k\/d\//i;

function httpUrl(u) {
  return /^https?:\/\//i.test(String(u || ""));
}

function pruneSentFiles() {
  var now = Date.now();
  sentFiles.forEach(function (v, k) {
    if (now - v.ts > SENT_TTL) sentFiles.delete(k);
  });
  while (sentFiles.size > 50) sentFiles.delete(sentFiles.keys().next().value);
}

// A failed send must only drop its own record: a newer download of the same
// URL may already have replaced it.
function dropRecord(url, rec) {
  if (sentFiles.get(url) === rec) sentFiles.delete(url);
}

function nameFromDisposition(cd) {
  var m = /filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(String(cd || ""));
  if (!m) return "";
  var s = m[1].trim();
  try { s = decodeURIComponent(s); } catch (e) { }
  return IDMStyle.safeFileName(s);
}

var FILE_EXT = {
  "application/zip": "zip", "application/x-zip-compressed": "zip",
  "application/x-7z-compressed": "7z", "application/x-rar-compressed": "rar",
  "application/x-tar": "tar", "application/gzip": "gz",
  "application/x-gzip-compressed": "gz", "application/x-msdownload": "exe",
  "application/x-msi": "msi", "application/x-apple-diskimage": "dmg",
  "application/x-iso9660-image": "iso",
  "application/vnd.android.package-archive": "apk",
  "application/x-bittorrent": "torrent", "application/epub+zip": "epub",
  "application/octet-stream": "bin", "binary/octet-stream": "bin"
};

// The response has no filename of its own; keep the URL's name and add the
// extension from the MIME only when the URL has none.
function fileNameFrom(url, ct) {
  var base = "";
  try { base = decodeURIComponent(new URL(url).pathname.split("/").pop() || ""); } catch (e) { }
  base = IDMStyle.safeFileName(base || "download");
  if (/\.[a-z0-9]{2,6}$/i.test(base)) return base;
  var ext = FILE_EXT[String(ct || "").split(";")[0].trim().toLowerCase()] || "";
  return ext ? base + "." + ext : base;
}

// The response has no page URL of its own; the tab has the full one, which
// matters for Referer/hotlink checks. The origin is the fallback.
function pageUrlOf(d, cb) {
  var fallback = d.originUrl || d.documentUrl || d.initiator || "";
  try {
    chrome.tabs.get(d.tabId, function (tab) {
      cb((tab && tab.url) || fallback, tab);
    });
  } catch (e) { cb(fallback, null); }
}

// Cancel + erase the browser's copy. The app owns the download now, so the
// browser must not leave a save window or a shelf entry behind it. The wait
// matters: Chrome opens its "Save As" window just after onCreated, and a
// cancel in the same tick leaves that window on screen.
function cancelDownload(id) {
  setTimeout(function () {
    try {
      chrome.downloads.cancel(id, function () {
        try {
          var p = chrome.downloads.erase({ id: id });
          if (p && p.catch) p.catch(function () { });
        } catch (e) { }
      });
    } catch (e) { }
  }, 250);
}

function takeOverResponse(d, ct, len, range) {
  if (!takeOver) return false;
  // Only a top-level navigation becomes a download. Manifest, favicon and
  // prefetch fetches are "other": Gmail's manifest.json is "other" with
  // Content-Disposition: attachment and Telegram's site.webmanifest is "other"
  // with application/octet-stream, so MIME or attachment alone would capture
  // them. A real download the request layer misses is caught by
  // downloads.onCreated.
  if (d.type !== "main_frame") return false;

  var cd = headerValue(d.responseHeaders, "content-disposition");
  var attachment = /^\s*attachment\b/i.test(cd);
  var direct = DOWNLOAD_MIME.test(String(ct || "").split(";")[0].trim().toLowerCase());
  if (!attachment && !direct) return false;

  var url = d.url;
  if (!httpUrl(url)) return false;

  // Remember the URL before the async send so the browser's download item can
  // match it (downloads.onCreated / onDeterminingFilename).
  var rec = { ts: Date.now(), id: null, ok: false, done: null };
  sentFiles.set(url, rec);
  pruneSentFiles();

  // The tab lookup is async; keep the promise on the record so
  // onDeterminingFilename can wait for the app's answer from the first tick.
  rec.done = new Promise(function (resolve) {
    pageUrlOf(d, function (pageUrl, tab) { resolve({ pageUrl: pageUrl, tab: tab }); });
  }).then(function (info) {
    // Private windows stay in the browser, as before.
    if (info.tab && info.tab.incognito) { dropRecord(url, rec); return false; }
    return sendToApp({
      url: url,
      name: nameFromDisposition(cd) || fileNameFrom(url, ct),
      contentType: String(ct || "").slice(0, 120),
      size: range ? 0 : len,
      kind: "file",
      auto: false,
      file: true,
      pageUrl: info.pageUrl
    }, null).then(function (res) {
      // Only after the app confirms; a file that already finished is left alone.
      if (!res || !res.ok) { dropRecord(url, rec); return false; }
      rec.ok = true;
      if (rec.id != null) cancelDownload(rec.id);
      return true;
    }, function () { dropRecord(url, rec); return false; });
  });
  return true;
}

// A download the request layer missed (Telegram, service-worker or no-tab
// downloads): the download item itself has the URL and the name the browser
// resolved. Its browser copy is cancelled once the app confirms.
function sendDownloadItem(item) {
  var url = item.finalUrl || item.url || "";
  if (!httpUrl(url)) return null;
  var rec = { ts: Date.now(), id: item.id, ok: false, done: null };
  sentFiles.set(url, rec);
  pruneSentFiles();
  rec.done = sendToApp({
    url: url,
    name: fileNameOf(item.filename, url),
    contentType: String(item.mime || ""),
    size: Math.max(0, parseInt(item.fileSize || item.totalBytes, 10) || 0),
    kind: "file",
    auto: false,
    file: true,
    pageUrl: item.referrer || ""
  }, null).then(function (res) {
    if (!res || !res.ok) { dropRecord(url, rec); return false; }
    rec.ok = true;
    cancelDownload(rec.id);
    return true;
  }, function () { dropRecord(url, rec); return false; });
  return rec;
}

function takeOverDownload(item) {
  if (!enabled || !takeOver || !item || item.id == null) return;
  // Someone else's extension download and private windows stay in the browser.
  if (item.byExtensionId || item.incognito) return;
  // Telegram's page-served file: the browser is the only one that can read it.
  if (TG_FILE.test(item.finalUrl || item.url || "")) return;

  var key = item.finalUrl && sentFiles.has(item.finalUrl) ? item.finalUrl :
            (item.url && sentFiles.has(item.url) ? item.url : "");
  var rec = key && sentFiles.get(key);
  if (rec && Date.now() - rec.ts > SENT_TTL) { sentFiles.delete(key); rec = null; }
  if (rec) {
    // Claim the item so onDeterminingFilename can cancel it before the
    // browser asks where to save. A second download of the same URL is a new
    // request and must reach the app again.
    if (rec.id == null) rec.id = item.id;
    if (rec.id === item.id) {
      if (rec.ok) cancelDownload(item.id);
      return;
    }
  }
  sendDownloadItem(item);
}

// Name the browser already resolved (Content-Disposition or the page's
// download attribute); fall back to the URL tail.
function fileNameOf(path, url) {
  var s = String(path || "");
  var i = Math.max(s.lastIndexOf("\\"), s.lastIndexOf("/"));
  if (i >= 0) s = s.substring(i + 1);
  if (!s) {
    try { s = decodeURIComponent(new URL(url).pathname.split("/").pop() || ""); } catch (e) { }
  }
  return IDMStyle.safeFileName(s || "download");
}

try {
  chrome.downloads.onCreated.addListener(function (item) {
    // A throwing listener must never break the browser's own download.
    try { takeOverDownload(item); } catch (e) { }
  });
} catch (e) { }

// The browser's "Ask where to save each file" window opens while the target
// is being determined, before onCreated's cancel can close it. Cancelling here,
// once the app has accepted the file, means the window never opens. If the app
// refused or never answered, suggest() lets the browser keep its own copy.
try {
  chrome.downloads.onDeterminingFilename.addListener(function (item, suggest) {
    var url = (item && (item.finalUrl || item.url)) || "";
    var rec = url && sentFiles.get(url);
    if (!rec) return;
    if (rec.id != null && rec.id !== item.id) return;
    rec.id = item.id;
    (rec.done || Promise.resolve(rec.ok)).then(function (ok) {
      if (ok) { try { chrome.downloads.cancel(item.id); } catch (e) { } }
      try { suggest(); } catch (e) { }
    }, function () { try { suggest(); } catch (e) { } });
    return true;
  });
} catch (e) { }

// ---------------------------------------------------------------------
//  Right-click a link -> "Download with ZID"
// ---------------------------------------------------------------------
function installContextMenu() {
  try {
    chrome.contextMenus.create({
      id: "zid-download-link",
      title: "Download with ZID",
      contexts: ["link"]
    }, function () {
      // Creating it again on a worker restart is expected; read the error so
      // the console stays quiet.
      try { void chrome.runtime.lastError; } catch (e) { }
    });
  } catch (e) { }
}

// The menu has no UI to report a dead app; the toolbar badge is enough.
function flashBadge(tabId) {
  try {
    var api = chrome.action || chrome.browserAction;
    api.setBadgeBackgroundColor({ tabId: tabId, color: "#dc2626" });
    api.setBadgeText({ tabId: tabId, text: "!" });
    setTimeout(function () { updateBadge(tabId); }, 4000);
  } catch (e) { }
}

try {
  installContextMenu();
  chrome.contextMenus.onClicked.addListener(function (info, tab) {
    if (!info || info.menuItemId !== "zid-download-link") return;
    var url = info.linkUrl || "";
    if (!httpUrl(url)) return;
    var pageUrl = (tab && tab.url) || "";
    sendToApp({
      url: url,
      name: fileNameFrom(url, ""),
      contentType: "",
      size: 0,
      kind: "file",
      auto: false,
      file: true,
      pageUrl: pageUrl
    }, { tab: { url: pageUrl } }).then(function (res) {
      if (!res || !res.ok) flashBadge(tab && tab.id);
    }, function () { flashBadge(tab && tab.id); });
  });
} catch (e) { }

// ---------------------------------------------------------------------
//  App start: first install, the panel's "Run the app" button, and the
//  retry after an offline send all go through here.
// ---------------------------------------------------------------------
function openAppTab(done) {
  try {
    chrome.tabs.create({ url: "zid://open" }, function (tab) {
      var err = chrome.runtime.lastError;
      var ok = !err && tab && tab.id != null;
      if (ok) {
        setTimeout(function () {
          // The protocol tab closes itself once the app starts, so a failed
          // remove is normal; swallow the promise rejection it reports.
          try {
            var p = chrome.tabs.remove(tab.id);
            if (p && p.catch) p.catch(function () { });
          } catch (e) { }
        }, 15000);
      }
      if (done) done(ok, err && err.message);
    });
  } catch (e) {
    if (done) done(false, e.message);
  }
}

// First install: start the app once so the extension works out of the box.
// Firefox can refuse a custom scheme from tabs.create; the panel's "Run the
// app" button is the user-gesture fallback and stays visible while the app
// is not running.
try {
  chrome.runtime.onInstalled.addListener(function (details) {
    try {
      if (details && details.reason === "install") openAppTab(null);
    } catch (e) { }
  });
} catch (e) { }

// A WebSocket handshake to the app's port is the only reliable "is it up?".
// The version query also tells us whether this extension matches the app.
function pingApp() {
  return new Promise(function (resolve) {
    var done = false;
    var ws = null;
    var timer = setTimeout(function () { finish(false, ""); }, 2000);

    function finish(ok, version) {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try { if (ws) ws.close(); } catch (e) { }
      resolve({ running: ok, version: version || "" });
    }

    try {
      ws = new WebSocket(APP_WS, APP_PROTO);
    } catch (e) {
      finish(false, "");
      return;
    }
    ws.onopen = function () {
      try { ws.send("zid-version"); } catch (e) { finish(true, ""); }
    };
    // An older app answers "err"; that is still a version mismatch and is
    // reported as such rather than looking like a dead app.
    ws.onmessage = function (e) { finish(true, String((e && e.data) || "").trim()); };
    ws.onerror = function () { finish(false, ""); };
    ws.onclose = function () { finish(false, ""); };
  });
}

chrome.runtime.onMessage.addListener(function (msg, sender, sendResponse) {
  if (!msg || !msg.type) return;

  // A dropped port must still answer, otherwise the panel waits forever.
  var answered = false;
  function answer(res) {
    if (answered) return;
    answered = true;
    try { sendResponse(res); } catch (e) { }
  }

  try {
    if (msg.type === "list") {
      var tabId = sender && sender.tab ? sender.tab.id : -1;
      answer({ items: listFor(tabId) });
      return;
    }

    if (msg.type === "cookies") {
      // The MAIN-world YouTube helper cannot call chrome.cookies, and
      // document.cookie misses HttpOnly ones, so the content script asks here.
      cookieHeader(msg.url || "https://www.youtube.com/").then(function (cookies) {
        answer({ cookies: cookies });
      });
      return true;
    }

    if (msg.type === "send") {
      sendToApp(msg.item, sender).then(function (res) { answer(res); },
        function (e) { answer({ ok: false, error: (e && e.message) || "Cannot send the link" }); });
      return true;
    }

    if (msg.type === "ping") {
      pingApp().then(function (r) {
        answer({ running: r.running, appVersion: r.version,
                 extVersion: chrome.runtime.getManifest().version });
      }, function () {
        answer({ running: false, appVersion: "", extVersion: chrome.runtime.getManifest().version });
      });
      return true;
    }

    if (msg.type === "open-app") {
      openAppTab(function (ok, error) {
        answer({ ok: ok, error: ok ? "" : (error || "Cannot open the app") });
      });
      return true;
    }
  } catch (e) {
    answer({ ok: false, error: e.message });
    return;
  }
});

try {
  chrome.action.onClicked.addListener(function (tab) {
    if (!tab || tab.id == null) return;
    try {
      var p = chrome.tabs.sendMessage(tab.id, { type: "toggle" });
      if (p && p.catch) p.catch(function () { });
    } catch (e) { }
  });
} catch (e) { }
