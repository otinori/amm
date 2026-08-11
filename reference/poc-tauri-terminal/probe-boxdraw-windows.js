const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: false });
  const page = await browser.newPage({ viewport: { width: 700, height: 400 } });
  await page.goto('http://localhost:5173/');
  await page.waitForTimeout(800);
  await page.click('#terminal');
  const send = async (text) => {
    await page.keyboard.insertText(text);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(300);
  };
  await send("echo '┌─┬─┐'");
  await send("echo '│A│B│'");
  await send("echo '├─┼─┤'");
  await send("echo '│C│D│'");
  await send("echo '└─┴─┘'");
  await page.waitForTimeout(300);
  await page.screenshot({ path: 'shots-windows-headed/boxdraw-grid.png' });
  await browser.close();
})();
