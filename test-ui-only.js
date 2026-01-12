const puppeteer = require('puppeteer');

async function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

(async () => {
    const browser = await puppeteer.launch({
        headless: false,
        args: ['--window-size=1400,900'],
        defaultViewport: { width: 1400, height: 900 }
    });

    const page = await browser.newPage();

    // Login
    console.log('Logging in...');
    await page.goto('http://localhost:5019/auth/login', { waitUntil: 'networkidle2' });
    await page.type('input[name="Email"]', 'admin@lucidrag.local');
    await page.type('input[name="Password"]', 'Admin123!');
    await page.click('button[type="submit"]');
    await page.waitForNavigation({ waitUntil: 'networkidle2' });
    console.log('Logged in');

    // Take screenshot of home/chat page
    await page.screenshot({ path: 'test-ui-home.png', fullPage: true });
    console.log('Screenshot: test-ui-home.png');

    // Go to explorer
    await page.goto('http://localhost:5019/explorer', { waitUntil: 'networkidle2' });
    await delay(2000);
    await page.screenshot({ path: 'test-ui-explorer.png', fullPage: true });
    console.log('Screenshot: test-ui-explorer.png');

    // Count documents
    const docCount = await page.evaluate(() => {
        const items = document.querySelectorAll('[data-document-id], .document-row, tr[data-id]');
        return items.length;
    });
    console.log(`Documents in explorer: ${docCount}`);

    // Try to type in chat
    await page.goto('http://localhost:5019', { waitUntil: 'networkidle2' });
    await delay(1000);

    // Find chat input
    const chatInput = await page.$('textarea, input[type="text"]');
    if (chatInput) {
        await chatInput.type('What are these blog posts about?');
        await page.screenshot({ path: 'test-ui-chat-input.png', fullPage: true });
        console.log('Screenshot: test-ui-chat-input.png');
    }

    console.log('\nBrowser open for 30 seconds...');
    await delay(30000);
    await browser.close();
})();
