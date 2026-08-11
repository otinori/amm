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
  if (!page) throw new Error('conpty-native.html page not found among: ' + contexts.flatMap(c => c.pages().map(p => p.url())).join(', '));

  await page.waitForTimeout(1000);
  await page.screenshot({ path: 'shots-windows-headed/conpty-native-01-initial.png' });

  await page.click('#terminal');
  await page.keyboard.insertText('echo native-conpty-portable-pty-works');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots-windows-headed/conpty-native-02-echo.png' });

  await page.keyboard.insertText('echo 日本語もConPTY経由で問題なし');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots-windows-headed/conpty-native-03-japanese.png' });

  console.log('done');
  await browser.close();
})();
