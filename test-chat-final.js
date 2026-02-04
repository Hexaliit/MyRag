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
    await page.goto('http://localhost:5019/auth/login', {waitUntil: 'networkidle2'});
    await page.type('input[name="Email"]', 'admin@lucidrag.local');
    await page.type('input[name="Password"]', 'Admin123!');
    await page.click('button[type="submit"]');
    await page.waitForNavigation({waitUntil: 'networkidle2'});
    console.log('Logged in');

    await delay(2000);

    // Find chat input by placeholder
    const chatInput = await page.$('input[placeholder="Ask about your documents..."]');
    if (chatInput) {
        console.log('Found chat input!');
        await chatInput.click();
        await chatInput.type('What programming topics are covered in these blog posts?');

        await page.screenshot({path: 'chat-1-typed.png', fullPage: true});
        console.log('Screenshot: chat-1-typed.png');

        // Find and click submit button
        const submitBtn = await page.$('button.btn-primary.btn-circle');
        if (submitBtn) {
            console.log('Clicking submit...');
            await submitBtn.click();
        } else {
            console.log('Pressing Enter...');
            await page.keyboard.press('Enter');
        }

        console.log('Waiting for response (45s)...');
        await delay(45000);

        await page.screenshot({path: 'chat-2-response.png', fullPage: true});
        console.log('Screenshot: chat-2-response.png');
    } else {
        console.log('Chat input not found!');
        await page.screenshot({path: 'chat-error.png', fullPage: true});
    }

    await browser.close();
})();
