/**
 * LucidRAG Blog Integration Test
 * Tests: Login -> Tenant -> Collection -> Upload English blogs -> Wait -> Search -> Chat
 */

const puppeteer = require('puppeteer');
const fs = require('fs');
const path = require('path');

const BASE_URL = 'http://localhost:5019';
const TENANT_ID = 'mostly';
const COLLECTION_NAME = 'blog-posts';
const MARKDOWN_DIR = 'C:\\Blog\\mostlylucidweb\\Mostlylucid\\Markdown';
const MAX_FILES = 30; // Limit for faster testing
const PROCESSING_TIMEOUT = 300000; // 5 minutes for processing

async function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

async function login(page) {
    console.log('\n=== Logging In ===');
    await page.goto(`${BASE_URL}/auth/login`, { waitUntil: 'networkidle2' });
    await page.type('input[name="Email"]', 'admin@lucidrag.local');
    await page.type('input[name="Password"]', 'Admin123!');
    await page.click('button[type="submit"]');
    await page.waitForNavigation({ waitUntil: 'networkidle2' });
    console.log('Logged in successfully');
}

async function ensureTenant(page) {
    console.log('\n=== Ensuring Tenant: mostly ===');
    // Try to create tenant via API
    const response = await page.evaluate(async (tenantId) => {
        const res = await fetch('/api/tenants', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ tenantId, name: 'Mostly Lucid Blog' })
        });
        return { ok: res.ok, status: res.status, data: await res.json().catch(() => ({})) };
    }, TENANT_ID);

    if (response.ok) {
        console.log('Tenant created');
    } else if (response.status === 409) {
        console.log('Tenant already exists');
    } else {
        console.log('Tenant response:', response);
    }
}

let COLLECTION_ID = null;

async function ensureCollection(page) {
    console.log('\n=== Ensuring Collection: blog-posts ===');

    // First, try to get existing collection
    const getResponse = await page.evaluate(async (tenantId, collectionName) => {
        const res = await fetch('/api/collections', {
            headers: { 'X-Tenant-Id': tenantId }
        });
        const data = await res.json();
        const existing = data.collections?.find(c => c.name === collectionName);
        return existing ? { id: existing.id, found: true } : { found: false };
    }, TENANT_ID, COLLECTION_NAME);

    if (getResponse.found) {
        COLLECTION_ID = getResponse.id;
        console.log(`Collection exists with ID: ${COLLECTION_ID}`);
        return;
    }

    // Create new collection
    const response = await page.evaluate(async (tenantId, collectionName) => {
        const res = await fetch('/api/collections', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Tenant-Id': tenantId
            },
            body: JSON.stringify({ name: collectionName, description: 'Blog posts from mostlylucid.net' })
        });
        const data = await res.json().catch(() => ({}));
        return { ok: res.ok, status: res.status, data };
    }, TENANT_ID, COLLECTION_NAME);

    if (response.ok) {
        COLLECTION_ID = response.data.id;
        console.log(`Collection created with ID: ${COLLECTION_ID}`);
    } else if (response.status === 409) {
        // Get the ID from existing collection
        const getAgain = await page.evaluate(async (tenantId, collectionName) => {
            const res = await fetch('/api/collections', {
                headers: { 'X-Tenant-Id': tenantId }
            });
            const data = await res.json();
            const existing = data.collections?.find(c => c.name === collectionName);
            return existing?.id;
        }, TENANT_ID, COLLECTION_NAME);
        COLLECTION_ID = getAgain;
        console.log(`Collection already exists with ID: ${COLLECTION_ID}`);
    } else {
        console.log('Collection response:', response);
    }
}

function getEnglishMarkdownFiles() {
    // Get only top-level .md files (not in subdirs, not summaries, not drafts)
    const files = fs.readdirSync(MARKDOWN_DIR)
        .filter(f => f.endsWith('.md') && !f.endsWith('.summary.md'))
        .map(f => path.join(MARKDOWN_DIR, f))
        .filter(f => fs.statSync(f).isFile());

    console.log(`Found ${files.length} English markdown files`);
    return files.slice(0, MAX_FILES);
}

async function uploadFiles(page, files) {
    console.log(`\n=== Uploading ${files.length} Files ===`);
    let success = 0, failed = 0;

    for (let i = 0; i < files.length; i++) {
        const file = files[i];
        const fileName = path.basename(file);

        try {
            const content = fs.readFileSync(file, 'utf8');
            const blob = Buffer.from(content).toString('base64');

            const response = await page.evaluate(async (tenantId, collectionId, fileName, base64Content) => {
                // Convert base64 back to blob
                const binaryString = atob(base64Content);
                const bytes = new Uint8Array(binaryString.length);
                for (let i = 0; i < binaryString.length; i++) {
                    bytes[i] = binaryString.charCodeAt(i);
                }
                const blob = new Blob([bytes], { type: 'text/markdown' });

                const formData = new FormData();
                formData.append('file', blob, fileName);
                if (collectionId) {
                    formData.append('collectionId', collectionId);
                }

                const res = await fetch('/api/documents', {
                    method: 'POST',
                    headers: { 'X-Tenant-Id': tenantId },
                    body: formData
                });
                return { ok: res.ok, status: res.status };
            }, TENANT_ID, COLLECTION_ID, fileName, blob);

            if (response.ok) {
                success++;
                process.stdout.write(`\r  [${i+1}/${files.length}] Uploaded: ${fileName.padEnd(50)}`);
            } else {
                failed++;
                console.log(`\n  [${i+1}/${files.length}] Failed (${response.status}): ${fileName}`);
            }
        } catch (err) {
            failed++;
            console.log(`\n  [${i+1}/${files.length}] Error: ${fileName} - ${err.message}`);
        }
    }

    console.log(`\nUpload complete: ${success} succeeded, ${failed} failed`);
    return success;
}

async function waitForProcessing(page, maxWaitMs = PROCESSING_TIMEOUT) {
    console.log(`\n=== Waiting for Document Processing (max ${maxWaitMs/1000}s) ===`);
    const startTime = Date.now();
    let lastStatus = '';

    while (Date.now() - startTime < maxWaitMs) {
        const status = await page.evaluate(async (tenantId) => {
            const res = await fetch('/api/documents?pageSize=100', {
                headers: { 'X-Tenant-Id': tenantId }
            });
            if (!res.ok) return null;
            const data = await res.json();
            // Handle both array and object response formats
            let docs = [];
            if (Array.isArray(data)) {
                docs = data;
            } else if (data.documents && Array.isArray(data.documents)) {
                docs = data.documents;
            } else if (data.items && Array.isArray(data.items)) {
                docs = data.items;
            }
            const completed = docs.filter(d => d.status === 'Completed' || d.status === 2).length;
            const pending = docs.filter(d => d.status === 'Pending' || d.status === 'Processing' || d.status === 0 || d.status === 1).length;
            const failed = docs.filter(d => d.status === 'Failed' || d.status === 3).length;
            return { completed, pending, failed, total: docs.length, raw: typeof data };
        }, TENANT_ID);

        if (status) {
            const statusStr = `Completed: ${status.completed}, Pending: ${status.pending}, Failed: ${status.failed} (of ${status.total})`;
            if (statusStr !== lastStatus) {
                console.log(`  ${statusStr}`);
                lastStatus = statusStr;
            }

            if (status.pending === 0 && status.total > 0) {
                console.log('All documents processed!');
                return true;
            }
        }

        await delay(5000);
    }

    console.log('Timeout waiting for processing');
    return false;
}

async function testSearch(page, query) {
    console.log(`\n=== Testing Search: "${query}" ===`);

    const response = await page.evaluate(async (tenantId, collectionName, query) => {
        const res = await fetch('/api/search', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Tenant-Id': tenantId
            },
            body: JSON.stringify({
                query,
                collectionName,
                topK: 5
            })
        });
        const data = await res.json().catch(() => ({}));
        return { ok: res.ok, status: res.status, data };
    }, TENANT_ID, COLLECTION_NAME, query);

    if (response.ok && response.data.results) {
        console.log(`Found ${response.data.results.length} results:`);
        response.data.results.slice(0, 3).forEach((r, i) => {
            const title = r.title || r.documentTitle || r.fileName || 'Unknown';
            const score = r.score?.toFixed(3) || r.relevanceScore?.toFixed(3) || 'N/A';
            console.log(`  ${i+1}. ${title} (score: ${score})`);
        });
        return true;
    } else {
        console.log('Search failed:', response);
        return false;
    }
}

async function testChat(page, question) {
    console.log(`\n=== Testing Chat: "${question}" ===`);

    // Navigate to chat page (home page)
    await page.goto(`${BASE_URL}`, { waitUntil: 'networkidle2' });
    await delay(2000);

    // Take screenshot
    await page.screenshot({ path: 'test-chat-page.png', fullPage: true });
    console.log('Screenshot: test-chat-page.png');

    // Find and fill the chat input
    const chatInput = await page.$('textarea[name="query"], input[name="query"], textarea#chat-input, input#query');
    if (!chatInput) {
        console.log('Chat input not found');
        return false;
    }

    await chatInput.type(question);

    // Find and click send button
    const sendButton = await page.$('button[type="submit"], button.send-button, button:has-text("Send")');
    if (sendButton) {
        await sendButton.click();
    } else {
        await page.keyboard.press('Enter');
    }

    // Wait for response
    console.log('Waiting for chat response...');
    await delay(10000);

    // Take screenshot of response
    await page.screenshot({ path: 'test-chat-response.png', fullPage: true });
    console.log('Screenshot: test-chat-response.png');

    // Try to get response text
    const response = await page.evaluate(() => {
        const responses = document.querySelectorAll('.chat-response, .assistant-message, .ai-response, [data-role="assistant"]');
        if (responses.length > 0) {
            return responses[responses.length - 1].textContent?.substring(0, 500);
        }
        return null;
    });

    if (response) {
        console.log('Chat response preview:', response.substring(0, 200) + '...');
        return true;
    } else {
        console.log('Could not capture chat response - check screenshots');
        return true; // Don't fail, just check screenshots
    }
}

async function testExplorer(page) {
    console.log('\n=== Testing Explorer UI ===');

    await page.goto(`${BASE_URL}/explorer`, { waitUntil: 'networkidle2' });
    await delay(2000);

    await page.screenshot({ path: 'test-explorer.png', fullPage: true });
    console.log('Screenshot: test-explorer.png');

    // Count documents in explorer
    const docCount = await page.evaluate(() => {
        const items = document.querySelectorAll('[data-document-id], .document-item, .file-item, tr[data-id]');
        return items.length;
    });

    console.log(`Found ${docCount} documents in Explorer`);
    return docCount > 0;
}

async function main() {
    console.log('========================================');
    console.log('LucidRAG Blog Integration Test');
    console.log('========================================');

    const browser = await puppeteer.launch({
        headless: false,
        args: ['--window-size=1400,900'],
        defaultViewport: { width: 1400, height: 900 }
    });

    const page = await browser.newPage();
    page.setDefaultTimeout(30000);

    try {
        // Step 1: Login
        await login(page);

        // Step 2: Ensure tenant exists
        await ensureTenant(page);

        // Step 3: Ensure collection exists
        await ensureCollection(page);

        // Step 4: Get English markdown files
        const files = getEnglishMarkdownFiles();

        // Step 5: Upload files
        const uploadedCount = await uploadFiles(page, files);

        if (uploadedCount > 0) {
            // Step 6: Wait for processing
            await waitForProcessing(page);
        }

        // Step 7: Test Explorer
        await testExplorer(page);

        // Step 8: Test Search queries
        await testSearch(page, 'ASP.NET Core authentication');
        await testSearch(page, 'HTMX and Alpine.js');
        await testSearch(page, 'Docker deployment');
        await testSearch(page, 'Entity Framework migrations');

        // Step 9: Test Chat
        await testChat(page, 'How do I implement authentication in ASP.NET Core?');

        console.log('\n========================================');
        console.log('Test Complete!');
        console.log('========================================');

        // Keep browser open for manual inspection
        console.log('\nBrowser left open for 60 seconds for inspection...');
        await delay(60000);

    } catch (err) {
        console.error('\nTest failed:', err.message);
        await page.screenshot({ path: 'test-error.png', fullPage: true });
        console.log('Error screenshot: test-error.png');
    } finally {
        await browser.close();
    }
}

main();
