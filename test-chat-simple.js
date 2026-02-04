const puppeteer = require('puppeteer');

async function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

(async () => {
    const browser = await puppeteer.launch({
        headless: false,
        args: ['--window-size=1400,900'],
        defaultViewport: {width: 1400, height: 900}
    });

    const page = await browser.newPage();

    // Login
    console.log('Logging in...');
    await page.goto('http://localhost:5019/auth/login', {waitUntil: 'networkidle2'});
    await page.type('input[name="Email"]', 'admin@lucidrag.local');
    await page.type('input[name="Password"]', 'Admin123!');
    await page.click('button[type="submit"]');
    await page.waitForNavigation({waitUntil: 'networkidle2'});
    console.log('Logged in');

    await delay(2000);

    // Select some documents for chat
    console.log('Selecting documents...');
    const checkboxes = await page.$$('input[type="checkbox"]');
    if (checkboxes.length > 2) {
        await checkboxes[1].click(); // Skip "All" checkbox
        await checkboxes[2].click();
        await delay(500);
    }

    // Find chat input
    console.log('Typing question...');
    const chatInput = await page.$('textarea');
    if (chatInput) {
        await chatInput.click();
        await chatInput.type('What topics are covered in these documents?');

        await page.screenshot({path: 'test-chat-typed.png', fullPage: true});
        console.log('Screenshot: test-chat-typed.png');

        // Submit
        await page.keyboard.press('Enter');

        console.log('Waiting for response (30s)...');
        await delay(30000);

        await page.screenshot({path: 'test-chat-response.png', fullPage: true});
        console.log('Screenshot: test-chat-response.png');
    }

    console.log('\nBrowser closing...');
    await browser.close();
})();
