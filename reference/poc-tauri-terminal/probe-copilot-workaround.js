const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.connectOverCDP('http://localhost:9333');
  const contexts = browser.contexts();
  let page = null;
  for (const ctx of contexts) {
    for (const p of ctx.pages()) {
      if (p.url().includes('conpty-native.html')) page = p;
    }
  }
  if (!page) throw new Error('not found');
  await page.click('#terminal');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(1500);
  await page.screenshot({ path: 'shots-windows-headed/copilot-13-after-manual-enter-workaround.png' });
  console.log('done');
  await browser.close();
})();
