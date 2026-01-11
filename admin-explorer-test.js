const puppeteer = require('puppeteer');
const path = require('path');

const BASE_URL = 'http://127.0.0.1:5080';
const ADMIN_SCREENSHOT = 'E:\\source\\lucidrag\\admin-test.png';
const EXPLORER_SCREENSHOT = 'E:\\source\\lucidrag\\explorer-mode.png';

async function runTest() {
    console.log('Launching browser...');
    const browser = await puppeteer.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox'],
        protocolTimeout: 60000
    });

    const page = await browser.newPage();
    await page.setViewport({ width: 1920, height: 1080 });

    try {
        // Step 1: Navigate to login page
        console.log('1. Navigating to login page...');
        await page.goto(`${BASE_URL}/auth/login`, { waitUntil: 'networkidle0', timeout: 30000 });

        // Step 2: Fill in login form
        console.log('2. Filling in login form...');

        // Wait for form elements to be available
        await page.waitForSelector('input[type="email"], input[name="email"], input[name="Email"], #email, #Email', { timeout: 10000 });

        // Try different selectors for email field
        const emailSelectors = ['input[type="email"]', 'input[name="email"]', 'input[name="Email"]', '#email', '#Email', 'input[name="Input.Email"]'];
        let emailFilled = false;
        for (const selector of emailSelectors) {
            try {
                const emailField = await page.$(selector);
                if (emailField) {
                    await emailField.click({ clickCount: 3 });
                    await emailField.type('admin@lucidrag.local');
                    emailFilled = true;
                    console.log(`   Email filled using selector: ${selector}`);
                    break;
                }
            } catch (e) { }
        }

        if (!emailFilled) {
            // Try to find any email-like input
            const allInputs = await page.$$('input');
            console.log(`   Found ${allInputs.length} input fields, examining...`);
            for (const input of allInputs) {
                const type = await input.evaluate(el => el.type);
                const name = await input.evaluate(el => el.name);
                const id = await input.evaluate(el => el.id);
                console.log(`   Input: type=${type}, name=${name}, id=${id}`);
            }
        }

        // Try different selectors for password field
        const passwordSelectors = ['input[type="password"]', 'input[name="password"]', 'input[name="Password"]', '#password', '#Password', 'input[name="Input.Password"]'];
        let passwordFilled = false;
        for (const selector of passwordSelectors) {
            try {
                const passwordField = await page.$(selector);
                if (passwordField) {
                    await passwordField.click({ clickCount: 3 });
                    await passwordField.type('Admin123!');
                    passwordFilled = true;
                    console.log(`   Password filled using selector: ${selector}`);
                    break;
                }
            } catch (e) { }
        }

        // Step 3: Submit the form
        console.log('3. Submitting login form...');

        // Try clicking submit button
        const submitSelectors = ['button[type="submit"]', 'input[type="submit"]', '.btn-primary', 'button.btn'];
        let submitted = false;
        for (const selector of submitSelectors) {
            try {
                const submitBtn = await page.$(selector);
                if (submitBtn) {
                    await submitBtn.click();
                    submitted = true;
                    console.log(`   Submitted using selector: ${selector}`);
                    break;
                }
            } catch (e) { }
        }

        // Wait for navigation/redirect
        console.log('4. Waiting for redirect...');
        await page.waitForNavigation({ waitUntil: 'networkidle0', timeout: 15000 }).catch(() => {
            console.log('   Navigation timeout - may already be on target page');
        });

        // Additional wait for page to fully load
        await new Promise(r => setTimeout(r, 2000));

        // Step 4: Take screenshot of admin page
        console.log('5. Taking admin page screenshot...');
        const currentUrl = page.url();
        console.log(`   Current URL: ${currentUrl}`);
        await page.screenshot({ path: ADMIN_SCREENSHOT, fullPage: true });
        console.log(`   Saved to: ${ADMIN_SCREENSHOT}`);

        // Step 5: Look for Explorer button/tab
        console.log('6. Looking for Explorer button/tab...');

        // Get page content for debugging
        const pageContent = await page.content();
        const hasExplorer = pageContent.toLowerCase().includes('explorer');
        console.log(`   Page contains 'explorer': ${hasExplorer}`);

        // Try multiple selectors for Explorer
        const explorerSelectors = [
            'button:has-text("Explorer")',
            'a:has-text("Explorer")',
            '[data-tab="explorer"]',
            '.tab:has-text("Explorer")',
            'button.tab',
            'a.tab',
            '.tabs button',
            '.tabs a',
            'nav button',
            'nav a'
        ];

        let explorerClicked = false;

        // First try to find by text content
        explorerClicked = await page.evaluate(() => {
            // Find all clickable elements
            const clickables = [...document.querySelectorAll('button, a, [role="tab"], .tab, [data-tab]')];
            for (const el of clickables) {
                if (el.textContent && el.textContent.toLowerCase().includes('explorer')) {
                    el.click();
                    return true;
                }
            }
            return false;
        });

        if (explorerClicked) {
            console.log('   Explorer button clicked!');
        } else {
            console.log('   Could not find Explorer button by text. Listing available buttons/tabs...');
            const buttons = await page.$$eval('button, a.btn, .tab, [role="tab"]', elements =>
                elements.map(el => ({
                    tag: el.tagName,
                    text: el.textContent?.trim().substring(0, 50),
                    classes: el.className,
                    href: el.href || ''
                }))
            );
            console.log('   Available clickable elements:');
            buttons.slice(0, 20).forEach(b => console.log(`     ${b.tag}: "${b.text}" (${b.classes})`));
        }

        // Step 6: Wait for explorer mode to load
        console.log('7. Waiting for explorer mode to load...');
        await new Promise(r => setTimeout(r, 3000));

        // Step 7: Take screenshot of explorer mode
        console.log('8. Taking explorer mode screenshot...');
        await page.screenshot({ path: EXPLORER_SCREENSHOT, fullPage: true });
        console.log(`   Saved to: ${EXPLORER_SCREENSHOT}`);

        // Report what we see
        console.log('\n=== Summary ===');
        console.log(`Admin page URL: ${currentUrl}`);
        console.log(`Admin screenshot: ${ADMIN_SCREENSHOT}`);
        console.log(`Explorer screenshot: ${EXPLORER_SCREENSHOT}`);

    } catch (error) {
        console.error('Error:', error.message);
        console.error(error.stack);
        await page.screenshot({ path: 'E:\\source\\lucidrag\\error-state.png', fullPage: true });
        console.log('Error screenshot saved to E:\\source\\lucidrag\\error-state.png');
    } finally {
        await browser.close();
    }
}

runTest().catch(console.error);
