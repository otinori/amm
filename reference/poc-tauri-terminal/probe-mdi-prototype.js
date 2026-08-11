const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: false });
  const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
  await page.goto('http://localhost:5173/mdi-prototype.html');
  await page.waitForTimeout(1000);
  await page.screenshot({ path: 'shots-windows-headed/mdi-01-initial.png' });

  // drag pane #2 by its title bar to a new position
  const title2 = page.locator('.pane').nth(1).locator('.pane-title');
  const box = await title2.boundingBox();
  await page.mouse.move(box.x + 40, box.y + 8);
  await page.mouse.down();
  await page.mouse.move(box.x + 200, box.y + 150, { steps: 10 });
  await page.mouse.up();
  await page.waitForTimeout(300);
  await page.screenshot({ path: 'shots-windows-headed/mdi-02-after-drag.png' });

  // tile layout
  await page.click('#btn-tile');
  await page.waitForTimeout(400);
  await page.screenshot({ path: 'shots-windows-headed/mdi-03-tiled.png' });

  // cascade layout
  await page.click('#btn-cascade');
  await page.waitForTimeout(400);
  await page.screenshot({ path: 'shots-windows-headed/mdi-04-cascaded.png' });

  // add a 4th pane, then reload to confirm layout (incl. new pane) is restored from localStorage
  await page.click('#btn-add');
  await page.waitForTimeout(400);
  const countBeforeReload = await page.locator('.pane').count();
  await page.screenshot({ path: 'shots-windows-headed/mdi-05-before-reload.png' });

  await page.reload();
  await page.waitForTimeout(1000);
  const countAfterReload = await page.locator('.pane').count();
  await page.screenshot({ path: 'shots-windows-headed/mdi-06-after-reload.png' });

  console.log('panes before reload:', countBeforeReload, 'panes after reload:', countAfterReload);

  // verify the topmost restored pane (last in DOM/z-order, fully unobstructed after
  // the cascade) still has a live PTY after reload
  await page.locator('.pane').last().locator('.pane-term').click();
  await page.keyboard.insertText('echo pty-alive-after-reload');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(500);
  await page.screenshot({ path: 'shots-windows-headed/mdi-07-pty-alive-check.png' });

  await browser.close();
  console.log('done');
})();
