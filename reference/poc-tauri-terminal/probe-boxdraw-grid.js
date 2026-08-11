const { webkit } = require('playwright');

(async () => {
  const browser = await webkit.launch();
  const page = await browser.newPage({ viewport: { width: 1000, height: 650 }, deviceScaleFactor: 3 });
  await page.goto('http://localhost:5173/');
  await page.waitForTimeout(1000);

  const send = async (text) => {
    await page.click('#terminal');
    await page.keyboard.insertText(text);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(400);
  };

  await send('clear');
  await send('printf "┌─┬─┐\\n│A│B│\\n├─┼─┤\\n│C│D│\\n└─┴─┘\\n"');
  await page.screenshot({ path: 'shots-macos-webkit/04-boxdraw-grid.png', clip: { x: 0, y: 0, width: 300, height: 150 } });

  await browser.close();
  console.log('done');
})();
