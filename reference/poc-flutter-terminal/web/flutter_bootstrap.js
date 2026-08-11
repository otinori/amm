{{flutter_js}}
{{flutter_build_config}}

// Sandbox override: this container's network policy blocks www.gstatic.com,
// so force the CanvasKit renderer to load from the locally bundled assets
// (build/web/canvaskit/) instead of Google's CDN.
_flutter.loader.load({
  config: {
    canvasKitBaseUrl: "canvaskit/",
  },
});
