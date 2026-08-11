const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: false });
  const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
  await page.goto('http://localhost:5173/');
  await page.waitForTimeout(800);
  await page.setViewportSize({ width: 700, height: 500 });
  await page.waitForTimeout(800);
  await page.click('#terminal');
  await page.waitForTimeout(300);
  await page.keyboard.insertText("echo resized && tput cols");
  await page.keyboard.press('Enter');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots-windows-headed/02-resized-retry.png' });
  await browser.close();
})();
