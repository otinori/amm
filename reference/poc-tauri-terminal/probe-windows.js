const { chromium } = require('playwright');
const fs = require('fs');

(async () => {
  fs.mkdirSync('shots-windows-headed', { recursive: true });
  const browser = await chromium.launch({
    channel: 'msedge', headless: false,
    args: ['--no-sandbox'],
  });
  const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
  const consoleErrors = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') consoleErrors.push(msg.text());
  });
  await page.goto('http://localhost:5173/');
  await page.waitForTimeout(1000);

  const send = async (text) => {
    await page.click('#terminal');
    await page.keyboard.insertText(text);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(400);
  };

  await send('printf "\\x1b[31mRED\\x1b[32mGREEN\\x1b[34mBLUE\\x1b[0m normal\\n"');
  await send('echo "日本語テスト 絵文字🎉🚀 罫線: ┌─┬─┐│ │ │└─┴─┘"');
  await send('echo "wide-char alignment: 幅広文字ABC123"');
  await send('printf "\\x1b[1mBOLD\\x1b[0m \\x1b[3mITALIC\\x1b[0m \\x1b[4mUNDERLINE\\x1b[0m\\n"');
  await page.screenshot({ path: 'shots-windows-headed/01a-colors-unicode-emphasis.png' });

  await send('for i in $(seq 1 40); do echo "scrollback line $i"; done');
  await page.screenshot({ path: 'shots-windows-headed/01b-after-scrollback.png' });

  await page.click('#terminal');
  await page.mouse.wheel(0, -2000);
  await page.waitForTimeout(300);
  await page.screenshot({ path: 'shots-windows-headed/01c-scrolled-up.png' });
  await page.mouse.wheel(0, 2000);

  await page.setViewportSize({ width: 700, height: 500 });
  await page.waitForTimeout(500);
  await send('echo resized && tput cols');
  await page.screenshot({ path: 'shots-windows-headed/02-resized.png' });

  await browser.close();
  console.log('console errors:', JSON.stringify(consoleErrors));
  console.log('done');
})();
