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
    await page.goto('http://localhost:5019/auth/login', { waitUntil: 'networkidle2' });
    await page.type('input[name="Email"]', 'admin@lucidrag.local');
    await page.type('input[name="Password"]', 'Admin123!');
    await page.click('button[type="submit"]');
    await page.waitForNavigation({ waitUntil: 'networkidle2' });
    console.log('Logged in');

    await delay(2000);

    // Inspect form elements
    const inputs = await page.evaluate(() => {
        const elements = [];
        document.querySelectorAll('input, textarea, button').forEach(el => {
            elements.push({
                tag: el.tagName,
                type: el.type,
                name: el.name,
                id: el.id,
                placeholder: el.placeholder,
                class: el.className?.substring(0, 50)
            });
        });
        return elements;
    });

    console.log('Form elements found:', JSON.stringify(inputs, null, 2));

    await page.screenshot({ path: 'test-inspect.png', fullPage: true });
    console.log('Screenshot: test-inspect.png');

    await browser.close();
})();
