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

    // Wait for page to fully load
    await delay(2000);
    
    // Take screenshot of home
    await page.screenshot({ path: 'test-home-full.png', fullPage: true });
    console.log('Screenshot: test-home-full.png');

    // Click on Explorer tab
    console.log('Clicking Explorer tab...');
    const explorerTab = await page.$('a[href="/explorer"], button:contains("Explorer"), [role="tab"]:contains("Explorer")');
    if (explorerTab) {
        await explorerTab.click();
        await delay(2000);
    } else {
        // Try finding by text
        const tabs = await page.$$('a, button');
        for (const tab of tabs) {
            const text = await page.evaluate(el => el.textContent, tab);
            if (text && text.includes('Explorer')) {
                await tab.click();
                await delay(2000);
                break;
            }
        }
    }
    
    await page.screenshot({ path: 'test-explorer-tab.png', fullPage: true });
    console.log('Screenshot: test-explorer-tab.png');

    // Try typing in chat and sending
    console.log('Testing chat...');
    await page.goto('http://localhost:5019', { waitUntil: 'networkidle2' });
    await delay(1000);

    // Find and fill chat input
    const chatInput = await page.$('textarea, input[placeholder*="Ask"]');
    if (chatInput) {
        await chatInput.click();
        await chatInput.type('What topics are covered in these blog posts?');
        
        // Take screenshot before submit
        await page.screenshot({ path: 'test-chat-before.png', fullPage: true });
        console.log('Screenshot: test-chat-before.png');
        
        // Press Enter or click send
        await page.keyboard.press('Enter');
        
        // Wait for response
        console.log('Waiting for chat response...');
        await delay(15000);
        
        await page.screenshot({ path: 'test-chat-after.png', fullPage: true });
        console.log('Screenshot: test-chat-after.png');
    }

    console.log('\nBrowser open for 30 seconds...');
    await delay(30000);
    await browser.close();
})();
