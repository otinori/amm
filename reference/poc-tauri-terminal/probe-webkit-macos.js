const { webkit } = require('playwright');
const fs = require('fs');

(async () => {
  fs.mkdirSync('shots-macos-webkit', { recursive: true });

  const browser = await webkit.launch();
  const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
  await page.goto('http://localhost:5173/');
  await page.waitForTimeout(1000);

  // insertText models a paste/IME-commit event (single input event with the full
  // string) rather than per-key synthesis, which mangles multi-byte/emoji text.
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
  await page.screenshot({ path: 'shots-macos-webkit/01a-colors-unicode-emphasis.png' });

  await send('for i in $(seq 1 40); do echo "scrollback line $i"; done');
  await page.screenshot({ path: 'shots-macos-webkit/01b-after-scrollback.png' });

  await page.click('#terminal');
  await page.mouse.wheel(0, -2000);
  await page.waitForTimeout(300);
  await page.screenshot({ path: 'shots-macos-webkit/01c-scrolled-up.png' });
  await page.mouse.wheel(0, 2000);

  await page.setViewportSize({ width: 700, height: 500 });
  await page.waitForTimeout(500);
  await send('echo resized && tput cols');
  await page.screenshot({ path: 'shots-macos-webkit/02-resized.png' });

  await browser.close();
  console.log('done');
})();
