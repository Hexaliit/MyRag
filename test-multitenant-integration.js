const puppeteer = require('puppeteer');
const fs = require('fs');
const path = require('path');

const BASE_URL = 'http://127.0.0.1:5080';
const MARKDOWN_DIR = 'C:\\Blog\\mostlylucidweb\\Mostlylucid\\Markdown';

async function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

async function login(page) {
    console.log('Logging in...');
    await page.goto(`${BASE_URL}/auth/login`);
    await page.waitForSelector('input[name="Email"]');
    await page.type('input[name="Email"]', 'admin@lucidrag.local');
    await page.type('input[name="Password"]', 'Admin123!');
    await page.click('button[type="submit"]');
    await page.waitForNavigation();
    console.log('Logged in successfully');
}

async function createTenant(page, tenantId, displayName) {
    console.log(`\n=== Creating Tenant: ${tenantId} ===`);

    // Create via API since multi-tenant UI may not be fully built
    const result = await page.evaluate(async (tid, name) => {
        const response = await fetch('/api/tenants', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                tenantId: tid,
                displayName: name,
                contactEmail: 'scott@mostlylucid.net',
                plan: 'pro'
            })
        });
        const data = await response.json().catch(() => null);
        return { ok: response.ok, status: response.status, data };
    }, tenantId, displayName);

    console.log('Tenant creation result:', result);
    return result;
}

async function createCollection(page, name, description) {
    console.log(`\n=== Creating Collection: ${name} ===`);

    const result = await page.evaluate(async (collName, collDesc) => {
        const response = await fetch('/api/collections', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: collName, description: collDesc })
        });
        const data = await response.json().catch(() => null);
        return { ok: response.ok, status: response.status, data };
    }, name, description);

    console.log('Collection creation result:', result);
    return result;
}

async function uploadMarkdownFiles(page, collectionId, maxFiles = 20) {
    console.log(`\n=== Uploading Markdown Files (max ${maxFiles}) ===`);

    // Get markdown files (excluding summary files)
    const files = fs.readdirSync(MARKDOWN_DIR)
        .filter(f => f.endsWith('.md') && !f.includes('.summary.'))
        .slice(0, maxFiles);

    console.log(`Found ${files.length} files to upload`);

    let uploaded = 0;
    let failed = 0;

    for (const fileName of files) {
        const filePath = path.join(MARKDOWN_DIR, fileName);
        const content = fs.readFileSync(filePath, 'utf-8');

        const result = await page.evaluate(async (name, content, collId) => {
            const formData = new FormData();
            const blob = new Blob([content], { type: 'text/markdown' });
            formData.append('file', blob, name);  // Note: 'file' not 'files'
            if (collId) formData.append('collectionId', collId);

            const response = await fetch('/api/documents/upload', {
                method: 'POST',
                body: formData
            });
            const data = await response.json().catch(() => null);
            return { ok: response.ok, status: response.status, data };
        }, fileName, content, collectionId);

        if (result.ok) {
            uploaded++;
            console.log(`  [${uploaded}/${files.length}] Uploaded: ${fileName}`);
        } else {
            failed++;
            console.log(`  [FAIL] ${fileName}: ${result.status}`);
        }

        await delay(200); // Small delay between uploads
    }

    console.log(`Upload complete: ${uploaded} succeeded, ${failed} failed`);
    return { uploaded, failed };
}

async function waitForProcessing(page, maxWaitSec = 120) {
    console.log(`\n=== Waiting for Document Processing (max ${maxWaitSec}s) ===`);

    const startTime = Date.now();
    let lastPending = -1;

    while ((Date.now() - startTime) / 1000 < maxWaitSec) {
        const status = await page.evaluate(async () => {
            const response = await fetch('/api/documents?pageSize=100');
            const result = await response.json();
            const docs = result.data || result.items || result || [];
            if (!Array.isArray(docs)) return { total: 0, pending: 0, completed: 0, failed: 0 };
            return {
                total: result.pagination?.totalItems || docs.length,
                pending: docs.filter(d => d.status?.toLowerCase() === 'pending' || d.status?.toLowerCase() === 'processing').length,
                completed: docs.filter(d => d.status?.toLowerCase() === 'completed').length,
                failed: docs.filter(d => d.status?.toLowerCase() === 'failed').length
            };
        });

        if (status.pending !== lastPending) {
            console.log(`  Status: ${status.completed} completed, ${status.pending} pending, ${status.failed} failed (of ${status.total})`);
            lastPending = status.pending;
        }

        if (status.pending === 0) {
            console.log('All documents processed!');
            return status;
        }

        await delay(3000);
    }

    console.log('Timeout waiting for processing');
    return null;
}

async function testExplorerUI(page) {
    console.log('\n=== Testing Explorer UI ===');

    // Navigate to admin
    await page.goto(`${BASE_URL}/admin`);
    await delay(1500);

    // Take initial screenshot
    await page.screenshot({ path: 'test-1-admin-page.png', fullPage: true });
    console.log('Screenshot: test-1-admin-page.png');

    // Click Explorer tab - use XPath to find button containing "Explorer" text
    try {
        const explorerTab = await page.waitForSelector('button', { timeout: 5000 });
        // Find button with Explorer text using evaluate
        const clicked = await page.evaluate(() => {
            const buttons = Array.from(document.querySelectorAll('button'));
            const explorerBtn = buttons.find(b => b.textContent.includes('Explorer'));
            if (explorerBtn) { explorerBtn.click(); return true; }
            return false;
        });
        if (!clicked) throw new Error('Explorer button not found');
        await delay(1500);

        await page.screenshot({ path: 'test-2-explorer-view.png', fullPage: true });
        console.log('Screenshot: test-2-explorer-view.png');

        // Check for documents in view
        const docCount = await page.evaluate(() => {
            return document.querySelectorAll('[class*="document"], .cursor-pointer').length;
        });
        console.log(`Found ${docCount} document elements in Explorer`);

        // Try to select a document
        const firstDoc = await page.$('.cursor-pointer');
        if (firstDoc) {
            await firstDoc.click();
            await delay(500);
            await page.screenshot({ path: 'test-3-doc-selected.png', fullPage: true });
            console.log('Screenshot: test-3-doc-selected.png (document selected)');
        }

        return true;
    } catch (e) {
        console.log('Explorer test error:', e.message);
        return false;
    }
}

async function testSearch(page, query) {
    console.log(`\n=== Testing Search: "${query}" ===`);

    // Use search API
    const result = await page.evaluate(async (q) => {
        try {
            const response = await fetch('/api/search', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ query: q, maxResults: 5 })
            });
            const text = await response.text();
            try { return { status: response.status, data: JSON.parse(text) }; }
            catch { return { status: response.status, text, error: 'JSON parse failed' }; }
        } catch (e) {
            return { error: e.message };
        }
    }, query);

    if (result.data?.results) {
        console.log('Search results:', {
            count: result.data.results.length,
            topResults: result.data.results.slice(0, 3).map(r => ({
                title: r.title || r.fileName || 'Untitled',
                score: r.score?.toFixed(3)
            }))
        });
    } else {
        console.log('Search response:', result);
    }

    return result;
}

async function testChatUI(page, query) {
    console.log(`\n=== Testing Chat UI: "${query}" ===`);

    // Switch to Chat mode
    await page.goto(`${BASE_URL}/admin`);
    await delay(1000);

    // Find Chat button using evaluate
    const chatClicked = await page.evaluate(() => {
        const buttons = Array.from(document.querySelectorAll('button'));
        const chatBtn = buttons.find(b => b.textContent.includes('Chat'));
        if (chatBtn) { chatBtn.click(); return true; }
        return false;
    });
    if (chatClicked) {
        await delay(500);
    }

    // Find and use chat input
    const chatInput = await page.$('textarea, input[placeholder*="Ask"], #chat-input');
    if (chatInput) {
        await chatInput.click();
        await chatInput.type(query);
        await page.screenshot({ path: 'test-4-chat-query.png', fullPage: true });
        console.log('Screenshot: test-4-chat-query.png (query entered)');

        // Submit
        await page.keyboard.press('Enter');
        console.log('Query submitted, waiting for response...');

        // Wait for response
        await delay(10000);

        await page.screenshot({ path: 'test-5-chat-response.png', fullPage: true });
        console.log('Screenshot: test-5-chat-response.png (response)');

        return true;
    }

    console.log('Chat input not found');
    return false;
}

async function main() {
    console.log('========================================');
    console.log('LucidRAG Multi-Tenant Integration Test');
    console.log('========================================\n');

    const browser = await puppeteer.launch({
        headless: false,
        args: ['--window-size=1920,1080']
    });

    const page = await browser.newPage();
    await page.setViewport({ width: 1920, height: 1080 });

    try {
        // Step 1: Login
        await login(page);

        // Step 2: Create "mostly" tenant
        const tenantResult = await createTenant(page, 'mostly', 'Mostly Lucid Blog');

        // Step 3: Create collection
        const collectionResult = await createCollection(page, 'blog-posts', 'Blog posts from mostlylucid.net');
        const collectionId = collectionResult?.data?.id;

        // Step 4: Upload markdown files
        await uploadMarkdownFiles(page, collectionId, 20);

        // Step 5: Wait for processing
        await waitForProcessing(page, 120);

        // Step 6: Test Explorer UI
        await testExplorerUI(page);

        // Step 7: Test search
        await testSearch(page, 'ASP.NET Core');
        await testSearch(page, 'Docker');
        await testSearch(page, 'authentication');

        // Step 8: Test chat
        await testChatUI(page, 'How do I implement authentication in ASP.NET Core?');

        console.log('\n========================================');
        console.log('Integration Test Complete!');
        console.log('========================================');
        console.log('Screenshots saved: test-1 through test-5');

    } catch (error) {
        console.error('\nTest failed:', error);
        await page.screenshot({ path: 'test-error.png', fullPage: true });
    } finally {
        console.log('\nBrowser left open for inspection (30s)...');
        await delay(30000);
        await browser.close();
    }
}

main().catch(console.error);
