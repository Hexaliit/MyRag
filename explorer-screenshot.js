const puppeteer = require('puppeteer');
const path = require('path');

const BASE_URL = 'http://127.0.0.1:5080';
const SCREENSHOT_PATH = path.join(__dirname, 'explorer-test.png');

// Demo credentials (from DemoAdminSeeder.cs)
const DEMO_EMAIL = 'admin@lucidrag.local';
const DEMO_PASSWORD = 'Admin123!';

async function takeExplorerScreenshot() {
    console.log('Launching browser...');
    const browser = await puppeteer.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox'],
        protocolTimeout: 60000
    });

    const page = await browser.newPage();
    await page.setViewport({ width: 1920, height: 1080 });

    try {
        // Navigate to admin page (will redirect to login)
        console.log('1. Navigating to admin page...');
        await page.goto(`${BASE_URL}/admin`, { waitUntil: 'networkidle0', timeout: 30000 });
        console.log('   Page loaded successfully');

        // Check if we're on login page
        const currentUrl = page.url();
        console.log('   Current URL:', currentUrl);

        if (currentUrl.includes('/auth/login') || await page.$('input[type="email"]')) {
            console.log('2. Login required, attempting to login...');

            // Fill in the login form
            const emailInput = await page.$('input[type="email"]') || await page.$('input[name="email"]');
            const passwordInput = await page.$('input[type="password"]') || await page.$('input[name="password"]');

            if (emailInput && passwordInput) {
                await emailInput.type(DEMO_EMAIL);
                await passwordInput.type(DEMO_PASSWORD);
            }

            // Click login button
            const loginButton = await page.$('button[type="submit"]') || await page.$('.btn-primary');
            if (loginButton) {
                await loginButton.click();
            }

            // Wait for navigation
            await page.waitForNavigation({ waitUntil: 'networkidle0', timeout: 30000 }).catch(() => {
                console.log('   Navigation after login completed or timed out');
            });

            console.log('   Login attempt completed, current URL:', page.url());
        }

        // Wait a bit for the page to fully render
        await new Promise(r => setTimeout(r, 2000));

        // First, let's investigate the page structure
        console.log('3. Investigating page structure...');
        const pageStructure = await page.evaluate(() => {
            const info = {
                sidebarToggle: null,
                sidebar: null,
                buttons: [],
                alpineComponents: []
            };

            // Check for sidebar mode toggle
            const toggleDiv = document.querySelector('#sidebar-mode-toggle');
            if (toggleDiv) {
                info.sidebarToggle = {
                    found: true,
                    buttons: [...toggleDiv.querySelectorAll('button')].map(b => b.textContent.trim())
                };
            } else {
                info.sidebarToggle = { found: false };
            }

            // Check aside element (sidebar)
            const sidebar = document.querySelector('aside');
            if (sidebar) {
                info.sidebar = {
                    classes: sidebar.className,
                    children: [...sidebar.children].map(c => ({
                        tag: c.tagName,
                        id: c.id,
                        text: c.textContent.substring(0, 100)
                    }))
                };
            }

            // Find all buttons with relevant text
            const buttons = [...document.querySelectorAll('button')];
            info.buttons = buttons.map(b => ({
                text: b.textContent.trim().substring(0, 50),
                classes: b.className.substring(0, 50)
            })).filter(b => b.text.toLowerCase().includes('chat') ||
                           b.text.toLowerCase().includes('explorer') ||
                           b.text.toLowerCase().includes('mode'));

            // Check Alpine.js components
            const alpineEls = document.querySelectorAll('[x-data]');
            info.alpineComponents = [...alpineEls].map(el => {
                const xData = el.getAttribute('x-data');
                return {
                    tag: el.tagName,
                    xData: xData ? xData.substring(0, 100) : null
                };
            });

            return info;
        });

        console.log('   Sidebar toggle found:', pageStructure.sidebarToggle.found);
        if (pageStructure.sidebarToggle.buttons) {
            console.log('   Toggle buttons:', pageStructure.sidebarToggle.buttons);
        }
        console.log('   Sidebar:', pageStructure.sidebar ? 'found' : 'not found');
        console.log('   Relevant buttons:', pageStructure.buttons);
        console.log('   Alpine components:', pageStructure.alpineComponents.length);

        // Look for the sidebar mode toggle and click Explorer
        console.log('\n4. Looking for Explorer toggle button...');

        // Find and click the Explorer button in the sidebar mode toggle
        const explorerClicked = await page.evaluate(() => {
            // Look in #sidebar-mode-toggle first
            const toggleDiv = document.querySelector('#sidebar-mode-toggle');
            if (toggleDiv) {
                const btns = [...toggleDiv.querySelectorAll('button')];
                const explorerBtn = btns.find(b => b.textContent.includes('Explorer'));

                if (explorerBtn) {
                    explorerBtn.click();
                    return { found: true, location: 'sidebar-mode-toggle', text: explorerBtn.textContent.trim() };
                }
            }

            // Look for button containing "Explorer" text anywhere
            const buttons = [...document.querySelectorAll('button')];
            const explorerBtn = buttons.find(b => b.textContent.includes('Explorer'));

            if (explorerBtn) {
                explorerBtn.click();
                return { found: true, location: 'global', text: explorerBtn.textContent.trim() };
            }

            // Try setting Alpine.js sidebarMode directly
            const alpineRoot = document.querySelector('[x-data*="ragApp"]') ||
                               document.querySelector('[x-data]');
            if (alpineRoot && alpineRoot._x_dataStack) {
                const data = alpineRoot._x_dataStack[0];
                if (typeof data.sidebarMode !== 'undefined') {
                    data.sidebarMode = 'explorer';
                    return { found: true, location: 'alpine-direct', text: 'Set via Alpine.js' };
                }
            }

            return {
                found: false,
                toggleFound: !!toggleDiv,
                buttonsInToggle: toggleDiv ? [...toggleDiv.querySelectorAll('button')].map(b => b.textContent.trim()) : [],
                allButtons: buttons.map(b => b.textContent.trim().substring(0, 50)).slice(0, 15)
            };
        });

        if (explorerClicked.found) {
            console.log('   Found and clicked Explorer via:', explorerClicked.location);
            console.log('   Button text:', explorerClicked.text);
        } else {
            console.log('   Explorer button not found.');
            console.log('   Toggle div found:', explorerClicked.toggleFound);
            console.log('   Buttons in toggle:', explorerClicked.buttonsInToggle);
            console.log('   All buttons (first 15):', explorerClicked.allButtons);
        }

        // Wait for the explorer content to load
        console.log('\n5. Waiting for explorer content to load...');
        await new Promise(r => setTimeout(r, 3000));

        // Take screenshot
        console.log('6. Taking screenshot...');
        await page.screenshot({ path: SCREENSHOT_PATH, fullPage: true });
        console.log('   Screenshot saved to:', SCREENSHOT_PATH);

        // Report what we see
        console.log('\n7. Analyzing the final UI state...');
        const uiInfo = await page.evaluate(() => {
            const info = {
                title: document.title,
                sidebarMode: null,
                sidebarContent: null,
                explorerElements: [],
                visibleText: null
            };

            // Check Alpine.js sidebar mode
            const alpineRoot = document.querySelector('[x-data*="ragApp"]') ||
                               document.querySelector('[x-data]');
            if (alpineRoot && alpineRoot._x_dataStack) {
                const data = alpineRoot._x_dataStack[0];
                if (typeof data.sidebarMode !== 'undefined') {
                    info.sidebarMode = data.sidebarMode;
                }
            }

            // Check sidebar content
            const sidebarContent = document.querySelector('#sidebar-content');
            if (sidebarContent) {
                info.sidebarContent = sidebarContent.textContent.substring(0, 300);
            }

            // Look for explorer-related elements
            const explorerEls = document.querySelectorAll('[class*="explorer"], [id*="explorer"], [x-data*="fileExplorer"]');
            info.explorerElements = [...explorerEls].map(el => ({
                tag: el.tagName,
                id: el.id,
                classes: el.className ? el.className.substring(0, 100) : ''
            })).slice(0, 5);

            // Get visible text in sidebar
            const sidebar = document.querySelector('aside');
            if (sidebar) {
                info.visibleText = sidebar.textContent.replace(/\s+/g, ' ').substring(0, 500);
            }

            return info;
        });

        console.log('   Page title:', uiInfo.title);
        console.log('   Current sidebar mode:', uiInfo.sidebarMode || 'unknown');
        console.log('   Explorer elements found:', uiInfo.explorerElements.length);
        if (uiInfo.explorerElements.length > 0) {
            uiInfo.explorerElements.forEach(el => {
                console.log(`     - ${el.tag} id="${el.id}" class="${el.classes}"`);
            });
        }
        console.log('   Sidebar visible text:', uiInfo.visibleText ? uiInfo.visibleText.substring(0, 200) + '...' : 'none');

    } catch (error) {
        console.error('Error:', error.message);
        await page.screenshot({ path: SCREENSHOT_PATH, fullPage: true });
        console.log('Error screenshot saved to:', SCREENSHOT_PATH);
    } finally {
        await browser.close();
    }
}

takeExplorerScreenshot().catch(console.error);
