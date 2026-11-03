/* Chrome 109 has no world:"MAIN" content scripts; load the page helper as a
   web-accessible script so it runs in the page world. */
(function () {
  var s = document.createElement("script");
  s.src = chrome.runtime.getURL("injected/youtube.js");
  (document.head || document.documentElement).appendChild(s);
})();
