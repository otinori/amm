const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: false });
  const page = await browser.newPage({ viewport: { width: 700, height: 300 } });
  await page.goto('http://localhost:5173/');
  await page.waitForTimeout(800);
  // reconfigure the live terminal's font family to explicitly include Segoe UI Emoji, then re-open
  await page.evaluate(() => {
    window.__pocTerm.options.fontFamily = 'Cascadia Code, Consolas, "Segoe UI Emoji", monospace';
  });
  await page.click('#terminal');
  await page.keyboard.insertText('echo "fallback test: 🎉🚀🔥😀👍"');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots-windows-headed/emoji-xterm-with-explicit-fallback-font.png' });
  await browser.close();
})();
