// npm's tar extraction on this machine (verified: real macOS 26.5.1 arm64) drops the
// executable bit from node-pty's prebuilt `spawn-helper` binary, which makes
// pty.spawn() fail with "posix_spawnp failed" (not a code-signing/quarantine issue —
// confirmed via `codesign -dv` and `xattr -l`, both clean). Restore it after install.
const fs = require('fs');
const path = require('path');

if (process.platform !== 'darwin') process.exit(0);

const prebuildsDir = path.join(__dirname, 'node_modules', 'node-pty', 'prebuilds');
if (!fs.existsSync(prebuildsDir)) process.exit(0);

for (const dir of fs.readdirSync(prebuildsDir).filter((d) => d.startsWith('darwin-'))) {
  const helper = path.join(prebuildsDir, dir, 'spawn-helper');
  if (fs.existsSync(helper)) fs.chmodSync(helper, 0o755);
}
