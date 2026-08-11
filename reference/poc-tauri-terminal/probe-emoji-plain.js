const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: false });
  const page = await browser.newPage({ viewport: { width: 600, height: 200 } });
  await page.setContent('<body style="background:#1e1e1e;color:#fff;font-family:Consolas,monospace;font-size:28px;">plain html emoji test: 🎉🚀🔥😀👍</body>');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots-windows-headed/emoji-plain-html.png' });
  await browser.close();
})();
