// Fleet's preview bridge, protocol v1. The preview gateway adds it to every page a browser canvas shows, and
// Fleet's mock mode serves the same file. The page is another origin than the canvas, so they only talk with
// postMessage:
//   to the canvas    { fleet: 1, type: "hello", hmr, href, title }   once per page load
//                    { fleet: 1, type: "location", href, title }     after every navigation
//                    { fleet: 1, type: "update" }                    the page's hot reload applied a change
//   from the canvas  { fleet: 1, type: "nav", action: "back" | "forward" | "reload" }
// Both sides ignore types and versions they don't know, so later types don't break older pages.
// `hmr` names the page's hot-reload client: vite, next, bun, webpack, dotnet-watch or none.
(function () {
  if (window.top === window || window.__fleetPreviewBridge) return;
  window.__fleetPreviewBridge = true;

  var VERSION = 1;
  // Messages on a hot-reload socket that mean a change was applied: Vite, webpack/Next, dotnet watch.
  var UPDATE = /"type"\s*:\s*"(update|UpdateStaticFile|ApplyManagedCodeUpdates)"|"action"\s*:\s*"built"/;
  // A stylesheet that changes this soon after a hot-reload message is that update (Bun's messages are binary).
  var STYLE_AFTER_MESSAGE_MS = 1500;

  var socketHmr = null;
  var lastHmrMessage = 0;
  var updateTimer = 0;

  function send(type, fields) {
    var message = fields || {};
    message.fleet = VERSION;
    message.type = type;
    try {
      parent.postMessage(message, "*");
    } catch (e) {
      // The canvas went away.
    }
  }

  function reportLocation() {
    send("location", { href: location.href, title: document.title });
  }

  function reportUpdate() {
    clearTimeout(updateTimer);
    updateTimer = setTimeout(function () { send("update"); }, 150);
  }

  function hmrOfSocket(url, protocols) {
    if ([].concat(protocols || []).indexOf("vite-hmr") >= 0) return "vite";
    if (url.indexOf("/_next/webpack-hmr") >= 0) return "next";
    if (url.indexOf("/_bun/hmr") >= 0) return "bun";
    if (url.indexOf("/__fleet_browser/ws/") >= 0) return "dotnet-watch";
    return null;
  }

  function hmrOfPage() {
    if (socketHmr) return socketHmr;
    for (var i = 0; i < document.scripts.length; i++) {
      var src = document.scripts[i].src || "";
      if (src.indexOf("/@vite/client") >= 0) return "vite";
      if (src.indexOf("/_next/") >= 0) return "next";
      if (src.indexOf("/_bun/") >= 0) return "bun";
      if (src.indexOf("aspnetcore-browser-refresh.js") >= 0) return "dotnet-watch";
    }
    for (var key in window) {
      if (key.indexOf("webpackHotUpdate") === 0) return "webpack";
    }
    return "none";
  }

  // Hot-reload clients open their socket while the page's scripts run, after this one.
  var NativeSocket = window.WebSocket;
  if (NativeSocket) {
    var FleetSocket = function (url, protocols) {
      var socket = protocols === undefined ? new NativeSocket(url) : new NativeSocket(url, protocols);
      try {
        var hmr = hmrOfSocket(String(url), protocols);
        if (hmr) {
          socketHmr = socketHmr || hmr;
          socket.addEventListener("message", function (event) {
            lastHmrMessage = Date.now();
            if (typeof event.data === "string" && UPDATE.test(event.data)) reportUpdate();
          });
        }
      } catch (e) {
        // Never break the page's own socket.
      }
      return socket;
    };
    FleetSocket.prototype = NativeSocket.prototype;
    FleetSocket.CONNECTING = 0;
    FleetSocket.OPEN = 1;
    FleetSocket.CLOSING = 2;
    FleetSocket.CLOSED = 3;
    window.WebSocket = FleetSocket;
  }

  function isStyle(node) {
    return !!node && (node.nodeName === "STYLE" || (node.nodeName === "LINK" && /stylesheet/i.test(node.rel || "")));
  }

  function watchStyles() {
    if (!window.MutationObserver || !document.head) return;
    new MutationObserver(function (records) {
      if (Date.now() - lastHmrMessage > STYLE_AFTER_MESSAGE_MS) return;
      for (var i = 0; i < records.length; i++) {
        var record = records[i];
        var target = record.target.nodeType === 3 ? record.target.parentNode : record.target;
        var changed = isStyle(target);
        for (var j = 0; !changed && j < record.addedNodes.length; j++) changed = isStyle(record.addedNodes[j]);
        for (var k = 0; !changed && k < record.removedNodes.length; k++) changed = isStyle(record.removedNodes[k]);
        if (changed) {
          reportUpdate();
          return;
        }
      }
    }).observe(document.head, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["href"] });
  }

  ["pushState", "replaceState"].forEach(function (name) {
    var original = history[name];
    history[name] = function () {
      var result = original.apply(this, arguments);
      reportLocation();
      return result;
    };
  });
  addEventListener("popstate", reportLocation);
  addEventListener("hashchange", reportLocation);
  addEventListener("load", reportLocation);
  addEventListener("message", function (event) {
    var data = event.data;
    if (event.source !== parent || !data || data.fleet !== VERSION || data.type !== "nav") return;
    if (data.action === "back") history.back();
    else if (data.action === "forward") history.forward();
    else if (data.action === "reload") location.reload();
  });

  function hello() {
    send("hello", { hmr: hmrOfPage(), href: location.href, title: document.title });
    watchStyles();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", hello);
  else hello();
})();
