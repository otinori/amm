const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const fallbackFont = fs.readFileSync(path.join(__dirname, '..', 'local-fonts', 'fallback.woff2'));

(async () => {
  const browser = await chromium.launch({
    executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome',
    args: ['--no-sandbox'],
  });
  const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
  await page.route('https://fonts.gstatic.com/**', (route) => {
    route.fulfill({ status: 200, contentType: 'font/woff2', body: fallbackFont });
  });
  const t0 = Date.now();
  await page.goto('http://localhost:5175/');

  // Empirically this sandbox's Flutter-Web boot (CJK-heavy content) takes on
  // the order of 90-120s to submit a first frame; poll generously rather than
  // treating slowness as a hang (see RESULTS.md for the full investigation).
  let booted = false;
  for (let i = 0; i < 45; i++) {
    await page.waitForTimeout(3000);
    const visible = await page.evaluate(() => document.querySelector('flt-glass-pane')?.children.length ?? -1);
    if (visible > 0) { booted = true; console.log(`booted at t=${((Date.now() - t0) / 1000).toFixed(1)}s`); break; }
  }
  if (!booted) {
    console.log('did not boot within budget, screenshotting current state anyway');
  }

  fs.mkdirSync('shots', { recursive: true });
  await page.waitForTimeout(1000);
  await page.screenshot({ path: 'shots/00-boot.png' });

  const send = async (text) => {
    await page.mouse.click(400, 200);
    await page.keyboard.insertText(text);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(600);
  };

  await send('printf "\\x1b[31mRED\\x1b[32mGREEN\\x1b[34mBLUE\\x1b[0m normal\\n"');
  await send('echo "日本語テスト 絵文字🎉🚀 罫線: ┌─┬─┐│ │ │└─┴─┘"');
  await send('echo "wide-char alignment: 幅広文字ABC123"');
  await send('printf "\\x1b[1mBOLD\\x1b[0m \\x1b[3mITALIC\\x1b[0m \\x1b[4mUNDERLINE\\x1b[0m\\n"');
  await page.screenshot({ path: 'shots/01a-colors-unicode-emphasis.png' });

  await send('for i in $(seq 1 40); do echo "scrollback line $i"; done');
  await page.screenshot({ path: 'shots/01b-after-scrollback.png' });

  await page.mouse.move(500, 300);
  await page.mouse.wheel(0, -2000);
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots/01c-scrolled-up.png' });
  await page.mouse.wheel(0, 2000);

  await page.setViewportSize({ width: 700, height: 500 });
  await page.waitForTimeout(1000);
  await send('echo resized && tput cols');
  await page.screenshot({ path: 'shots/02-resized.png' });

  await browser.close();
  console.log('done');
})();
