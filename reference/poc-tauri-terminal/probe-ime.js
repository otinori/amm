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
  await page.waitForTimeout(500);

  const cdp = await page.context().newCDPSession(page);

  // Scenario 1: compose + confirm in the shared prompt input bar (the
  // WinForms bug's TextBox-equivalent), verify it fires exactly once and
  // doesn't leak into the terminal.
  await page.click('#prompt-input');
  await cdp.send('Input.imeSetComposition', { text: 'にほんご', selectionStart: 4, selectionEnd: 4 });
  await page.waitForTimeout(200);
  await cdp.send('Input.imeSetComposition', { text: '', selectionStart: 0, selectionEnd: 0 });
  await cdp.send('Input.insertText', { text: '日本語' });
  await page.waitForTimeout(200);
  await page.screenshot({ path: 'shots-windows-headed/ime-01-prompt-composed.png' });

  await page.keyboard.press('Enter');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots-windows-headed/ime-02-prompt-submitted.png' });

  // Scenario 2: compose + confirm directly in the terminal (xterm's hidden
  // textarea), same check.
  await page.click('#terminal');
  await page.keyboard.insertText('echo ');
  await cdp.send('Input.imeSetComposition', { text: 'にほんご', selectionStart: 4, selectionEnd: 4 });
  await page.waitForTimeout(200);
  await cdp.send('Input.imeSetComposition', { text: '', selectionStart: 0, selectionEnd: 0 });
  await cdp.send('Input.insertText', { text: '日本語' });
  await page.waitForTimeout(200);
  await page.keyboard.press('Enter');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots-windows-headed/ime-03-terminal-composed.png' });

  const imeLogText = await page.locator('#ime-log').innerText();
  console.log('--- ime-log contents ---');
  console.log(imeLogText);

  await browser.close();
})();
