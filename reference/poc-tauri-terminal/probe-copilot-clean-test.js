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

  const send = async (text) => {
    await page.click('#terminal');
    await page.keyboard.insertText(text);
    await page.keyboard.press('Enter');
  };

  // Launch copilot, no auto-tools flag this time to avoid extra permission prompts.
  await send('copilot');
  await page.waitForTimeout(5000);
  await page.screenshot({ path: 'shots-windows-headed/copilot-10-launched.png' });

  // Confirm folder trust ("1. Yes" is pre-selected).
  await page.click('#terminal');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(2000);
  await page.screenshot({ path: 'shots-windows-headed/copilot-11-idle-prompt.png' });

  // Now the session should be genuinely idle. Send the bracketed-paste sequence
  // exactly as the WinForms app does, and screenshot immediately - no extra
  // keypress - to see whether it lands in the input box unsubmitted.
  const pasteSeq = '\x1b[200~' + 'this is a bracketed paste submit test' + '\x1b[201~' + '\r\r';
  await page.evaluate((seq) => window.__TAURI__.core.invoke('pty_write', { data: seq }), pasteSeq);
  await page.waitForTimeout(1500);
  await page.screenshot({ path: 'shots-windows-headed/copilot-12-immediately-after-paste.png' });

  console.log('done');
  await browser.close();
})();
