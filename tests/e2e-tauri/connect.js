// Generalizes the connectOverCDP pattern from
// reference/poc-tauri-terminal/probe-sysmenu.js for the real app. See
// README.md for the full method (launch flag, popup-window limitation).
const { chromium } = require('playwright');

// Connects to the app's main window over CDP. The app must already be
// running with WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>.
async function connectMainPage({ port = 9333, urlIncludes = 'tauri.localhost', timeoutMs = 10000 } = {}) {
  const browser = await chromium.connectOverCDP(`http://localhost:${port}`);
  const deadline = Date.now() + timeoutMs;
  let page = null;
  while (!page && Date.now() < deadline) {
    for (const ctx of browser.contexts()) {
      for (const p of ctx.pages()) {
        if (p.url().includes(urlIncludes)) {
          page = p;
          break;
        }
      }
      if (page) break;
    }
    if (!page) await new Promise((r) => setTimeout(r, 200));
  }
  if (!page) {
    await browser.close();
    throw new Error(`no page with URL containing "${urlIncludes}" found on port ${port} within ${timeoutMs}ms`);
  }
  return { browser, page };
}

module.exports = { connectMainPage };
