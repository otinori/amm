# e2e-tauri — GUI-launch integration test method (migrate-to-tauri 6.5)

Formalizes the CDP-based verification approach used throughout the
`migrate-to-tauri` implementation (see
`openspec/changes/archive/2026-07-26-migrate-to-tauri/`, archived
2026-07-26) for `src/apps/Amm`, so it doesn't stay tribal session
knowledge.
`reference/poc-tauri-terminal/probe-*.js` already used the same
`connectOverCDP` pattern against the PoC; `connect.js` here generalizes it
for the real app.

## How it works

WebView2 (the Tauri app's renderer) is a real Chromium under the hood, so
it exposes the Chrome DevTools Protocol when launched with the right
environment variable. Playwright can then drive it exactly like a normal
browser page - click, type, screenshot, read DOM/textContent, etc.

1. Launch the built app with CDP enabled:

   ```powershell
   $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9333"
   artifacts\target\debug\amm.exe
   ```

2. Connect from a Playwright script:

   ```js
   const { connectMainPage } = require('./connect');
   const { browser, page } = await connectMainPage();
   await page.screenshot({ path: 'shots/01.png' });
   await browser.close();
   ```

3. Run any Playwright `Page` API against it: `page.click`, `page.keyboard`,
   `page.evaluate`, `page.locator(...).textContent()`, etc. This is how
   every "実機で確認" claim in `tasks.md` for phases 1-5 was actually
   verified, not just theorized.

## Known limitation: popup windows don't show up over CDP

`WebviewWindowBuilder`-created secondary windows (e.g. the approval-hub
popup, `show_approval_popup`) do **not** reliably appear as a distinct page
in `browser.contexts()` in this app - `probe-approval-popup.js`-style
lookups return `about:blank` for it even though the native HWND exists
(confirmed via `EnumWindows`). Root cause undetermined; see
`tasks/pending-real-machine-verification.md`. For those windows, fall back
to:

- **Native window presence/geometry**: Win32 `EnumWindows` /
  `GetWindowText` (see `reference/poc-tauri-terminal/probe-sysmenu.js`
  and this session's ad-hoc Node `ffi`/PowerShell probes for the pattern)
- **Visual content**: `PrintWindow` (or a plain screen region screenshot
  when the window is on top) since CDP screenshot APIs need a reachable
  page

Anything rendered in the **main** window (all pane content, the common
input bar, dialogs implemented as in-DOM overlays rather than separate
native windows) is reachable over CDP and should be tested that way in
preference to native-window techniques - it's faster and gives you real
DOM assertions instead of pixel-diffing.

## Known limitation: `window.confirm()`/`alert()`/`prompt()` don't actually show

In this session's execution environment, calling `window.confirm()` (etc.)
from the page logs `"dialog.confirm not allowed. Command not found"` and
the call returns without ever showing a real native dialog or firing
Playwright's `page.on('dialog', ...)` event - same class of issue already
documented for `dialog.accept()` (`tasks/pending-real-machine-verification.md`),
likely rooted in the same non-interactive/background desktop session
constraint. Whether it returns a truthy or falsy value isn't something to
rely on. To test app logic that branches on `confirm()`, monkey-patch it
directly instead of depending on the native dialog:

```js
await page.evaluate(() => { window.confirm = () => false; }); // or true
```

This tests your own branching logic correctly even though it doesn't prove
the native dialog itself renders - that part still needs a real interactive
desktop session (a human, or a differently-launched test environment).

**⚠️ This limitation does NOT apply in a real interactive desktop session**
(check with `query session` - if the `console` session shows `Active`, a
human is logged in at the physical/RDP console and WebView2's
`confirm()`/`alert()`/`prompt()` show **real native modal dialogs** that
block the renderer thread until dismissed. Discovered 2026-07-21 verifying
tasks.md 8.6.9: a script patched `window.confirm` but not `window.prompt`,
then triggered a code path (`runGitGuard`) that called `prompt()` - the
real dialog appeared with nobody to click it, the CDP connection timed out,
and the app was unresponsive until `taskkill /F /IM amm.exe`. **Always
monkey-patch all three (`confirm`/`alert`/`prompt`) together before
exercising any code path that might call any of them**, even if your
script only intends to hit one - a single missed one hangs the whole app.

**⚠️ Native dialog APIs called without a preceding user gesture are
unreliable even in a real interactive session.** Discovered 2026-07-21
verifying tasks.md 8.6.1: `window.confirm()` called from script that runs
automatically (e.g. app startup logic, not inside a click handler) failed
immediately with the same `"dialog.confirm not allowed. Command not
found"` error seen in non-interactive sessions - every other `confirm()`
in this app lives inside a button's click handler (a real user gesture)
and works fine; this was the one exception, called by app.js on load with
nothing preceding it. Root cause not fully isolated (Chromium/WebView2
gesture-requirement heuristics for JS dialog APIs are the leading
suspect), but the practical implication is clear: **don't rely on
`confirm()`/`alert()`/`prompt()` for anything that has to run without a
prior click** - use an in-DOM modal (this app's `openModal`) instead, the
same fix already applied to `show_approval_popup`'s dialog for an
unrelated reason. If you're auditing whether some other automatic
(non-click-triggered) code path in this app still uses a raw native
dialog call, that's worth flagging - it may not reliably reach a real
human either.

## Files

- `connect.js` - `connectMainPage({port})` returns `{ browser, page }` for
  the main window, matched by its `tauri.localhost` custom-protocol URL
  (found via live testing, phase 8.2: the built app serves from
  `http://tauri.localhost/`, not a literal `index.html` path - the default
  `urlIncludes` was fixed to match; override it if a dev-server build ever
  serves from a different URL).
- `package.json` - `playwright` devDependency only; no test runner is
  wired up here on purpose. This phase establishes the *method*, not a
  full E2E suite - see tasks.md 7.x for using it in full parity
  verification.
- `fixture-mcp-server.js` - minimal stdio JSON-RPC MCP server (initialize,
  tools/list with one `echo` tool, tools/call) used to verify mcp-gateway's
  full success path (Running status, tool aggregation, call forwarding)
  without any network/npm-registry dependency. Wire it into a test
  `profiles.amm`'s `mcpServers` array (`command: "node"`,
  `args: ["<repo>/tests/e2e-tauri/fixture-mcp-server.js"]`) to exercise a
  real child-process MCP server end to end.
