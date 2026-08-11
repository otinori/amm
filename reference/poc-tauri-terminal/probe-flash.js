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
  if (!page) throw new Error('conpty-native.html page not found');
  await page.waitForTimeout(300);
  await page.click('#btn-flash');
  console.log('flash_window invoked');
  await browser.close();
})();
