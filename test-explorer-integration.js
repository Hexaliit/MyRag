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

async function createCollection(page, name, description) {
    console.log(`Creating collection: ${name}`);

    // Navigate to admin page
    await page.goto(`${BASE_URL}/admin`);
    await delay(1000);

    // Look for collection creation UI - check for "New Collection" button or modal
    const newCollectionBtn = await page.$('button:has-text("New Collection"), [data-action="new-collection"], .btn:has-text("New")');

    if (newCollectionBtn) {
        await newCollectionBtn.click();
        await delay(500);
    } else {
        // Try clicking on a dropdown or settings area
        console.log('Looking for collection creation UI...');
    }

    // Try via API if UI is complex
    const result = await page.evaluate(async (collName, collDesc) => {
        const response = await fetch('/api/collections', {
            method: 'POST',
            headers: {'Content-Type': 'application/json'},
            body: JSON.stringify({name: collName, description: collDesc})
        });
        return {ok: response.ok, status: response.status, data: await response.json().catch(() => null)};
    }, name, description);

    console.log('Collection creation result:', result);
    return result;
}

async function uploadFilesToCollection(page, collectionId, files) {
    console.log(`Uploading ${files.length} files to collection ${collectionId}...`);

    // Navigate to admin and switch to Explorer mode
    await page.goto(`${BASE_URL}/admin`);
    await delay(1000);

    // Click Explorer tab
    const explorerTab = await page.waitForSelector('button:has-text("Explorer")');
    await explorerTab.click();
    await delay(500);

    // Look for file input
    const fileInput = await page.$('input[type="file"]');

    if (fileInput) {
        // Upload files in batches
        const batchSize = 5;
        for (let i = 0; i < files.length && i < 20; i += batchSize) { // Limit to 20 for test
            const batch = files.slice(i, i + batchSize);
            console.log(`Uploading batch ${Math.floor(i / batchSize) + 1}: ${batch.length} files`);

            // Set files on the input
            await fileInput.uploadFile(...batch);
            await delay(2000); // Wait for upload to process
        }
    } else {
        console.log('File input not found, using API...');

        // Use API to upload
        for (let i = 0; i < files.length && i < 20; i++) {
            const file = files[i];
            const content = fs.readFileSync(file, 'utf-8');
            const fileName = path.basename(file);

            const result = await page.evaluate(async (name, content, collId) => {
                const formData = new FormData();
                const blob = new Blob([content], {type: 'text/markdown'});
                formData.append('files', blob, name);
                if (collId) formData.append('collectionId', collId);

                const response = await fetch('/api/documents/upload', {
                    method: 'POST',
                    body: formData
                });
                return {ok: response.ok, status: response.status};
            }, fileName, content, collectionId);

            if (result.ok) {
                console.log(`  Uploaded: ${fileName}`);
            } else {
                console.log(`  Failed: ${fileName} (${result.status})`);
            }

            await delay(300);
        }
    }
}

async function testExplorerBrowsing(page) {
    console.log('\n=== Testing Explorer Browsing ===');

    await page.goto(`${BASE_URL}/admin`);
    await delay(1000);

    // Switch to Explorer mode
    const explorerTab = await page.waitForSelector('button:has-text("Explorer")');
    await explorerTab.click();
    await delay(1500);

    // Take screenshot
    await page.screenshot({path: 'explorer-browse-test.png', fullPage: true});
    console.log('Screenshot saved: explorer-browse-test.png');

    // Check for documents in the Explorer
    const docCount = await page.evaluate(() => {
        const docs = document.querySelectorAll('[class*="document"], [class*="file-item"], .cursor-pointer');
        return docs.length;
    });
    console.log(`Found ${docCount} document elements in Explorer`);

    // Try to get stats from API
    const stats = await page.evaluate(async () => {
        const response = await fetch('/api/explorer/stats');
        return response.json();
    });
    console.log('Explorer stats:', stats);

    // Test document selection
    const firstDoc = await page.$('.cursor-pointer[x-on\\:click*="toggleSelection"], [@@click*="toggleSelection"]');
    if (firstDoc) {
        await firstDoc.click();
        await delay(300);
        console.log('Clicked on first document');

        // Check if selection state changed
        const selectionCount = await page.evaluate(() => {
            const selected = document.querySelectorAll('.ring-2, .border-primary, [class*="selected"]');
            return selected.length;
        });
        console.log(`Selection count: ${selectionCount}`);
    }

    return {docCount, stats};
}

async function testExplorerFilters(page) {
    console.log('\n=== Testing Explorer Filters ===');

    // Test signal filters
    const signals = await page.evaluate(async () => {
        // Test hasImages filter
        const withImages = await fetch('/api/explorer?hasImages=true').then(r => r.json());
        const withTables = await fetch('/api/explorer?hasTables=true').then(r => r.json());
        const withCode = await fetch('/api/explorer?hasCode=true').then(r => r.json());
        const recent = await fetch('/api/explorer?dateRange=7d').then(r => r.json());

        return {
            withImages: withImages.items?.length || 0,
            withTables: withTables.items?.length || 0,
            withCode: withCode.items?.length || 0,
            recent: recent.items?.length || 0
        };
    });

    console.log('Filter results:', signals);

    // Test entities endpoint
    const entities = await page.evaluate(async () => {
        const response = await fetch('/api/explorer/entities');
        return response.json();
    });
    console.log('Entities:', Object.keys(entities).length > 0 ? `${Object.keys(entities).length} entity types` : 'No entities');

    return {signals, entities};
}

async function testSearchQuery(page, query) {
    console.log(`\n=== Testing Search: "${query}" ===`);

    // Use the chat/search API
    const result = await page.evaluate(async (q) => {
        const response = await fetch('/api/chat/search', {
            method: 'POST',
            headers: {'Content-Type': 'application/json'},
            body: JSON.stringify({query: q, maxResults: 5})
        });
        return response.json();
    }, query);

    console.log('Search results:', {
        count: result.results?.length || 0,
        topResults: result.results?.slice(0, 3).map(r => ({
            title: r.title || r.fileName,
            score: r.score?.toFixed(3)
        }))
    });

    return result;
}

async function testChatQuery(page, query) {
    console.log(`\n=== Testing Chat Query: "${query}" ===`);

    // Navigate to admin and ensure we're in chat mode
    await page.goto(`${BASE_URL}/admin`);
    await delay(1000);

    // Click Chat tab if in Explorer
    const chatTab = await page.$('button:has-text("Chat")');
    if (chatTab) {
        await chatTab.click();
        await delay(500);
    }

    // Find chat input and type query
    const chatInput = await page.$('textarea[placeholder*="Ask"], input[placeholder*="Ask"], #chat-input, textarea');
    if (chatInput) {
        await chatInput.type(query);
        await delay(200);

        // Submit with Enter or button
        await page.keyboard.press('Enter');

        // Wait for response
        console.log('Waiting for chat response...');
        await delay(5000);

        // Take screenshot of response
        await page.screenshot({path: 'chat-response-test.png', fullPage: true});
        console.log('Screenshot saved: chat-response-test.png');

        // Try to extract response text
        const response = await page.evaluate(() => {
            const messages = document.querySelectorAll('.prose, [class*="message"], [class*="response"]');
            const lastMessage = messages[messages.length - 1];
            return lastMessage?.textContent?.substring(0, 500) || 'No response found';
        });

        console.log('Chat response preview:', response.substring(0, 200) + '...');
    } else {
        console.log('Chat input not found');
    }
}

async function getDocumentList(page) {
    const docs = await page.evaluate(async () => {
        const response = await fetch('/api/documents');
        return response.json();
    });
    return docs;
}

async function main() {
    console.log('=== LucidRAG Full Integration Test ===\n');

    const browser = await puppeteer.launch({
        headless: false, // Show browser for debugging
        args: ['--window-size=1920,1080']
    });

    const page = await browser.newPage();
    await page.setViewport({width: 1920, height: 1080});

    try {
        // Step 1: Login
        await login(page);

        // Step 2: Check existing documents
        console.log('\n=== Checking Existing Documents ===');
        const existingDocs = await getDocumentList(page);
        console.log(`Found ${existingDocs.length || 0} existing documents`);

        // Step 3: Create "mostly" collection
        console.log('\n=== Creating Collection ===');
        const collectionResult = await createCollection(page, 'mostly', 'Blog posts from mostlylucid.net');

        // Step 4: Get markdown files
        const mdFiles = fs.readdirSync(MARKDOWN_DIR)
            .filter(f => f.endsWith('.md') && !f.includes('.summary.'))
            .slice(0, 15) // Start with 15 files for testing
            .map(f => path.join(MARKDOWN_DIR, f));

        console.log(`\nFound ${mdFiles.length} markdown files to upload`);

        // Step 5: Upload files
        if (mdFiles.length > 0) {
            const collectionId = collectionResult?.data?.id;
            await uploadFilesToCollection(page, collectionId, mdFiles);
        }

        // Wait for processing
        console.log('\nWaiting for document processing...');
        await delay(5000);

        // Step 6: Test Explorer browsing
        const browseResult = await testExplorerBrowsing(page);

        // Step 7: Test filters
        const filterResult = await testExplorerFilters(page);

        // Step 8: Test search queries
        await testSearchQuery(page, 'authentication');
        await testSearchQuery(page, 'ASP.NET Core');
        await testSearchQuery(page, 'Docker');

        // Step 9: Test chat query
        await testChatQuery(page, 'How do I implement authentication in ASP.NET Core?');

        // Final screenshot
        await page.screenshot({path: 'integration-test-final.png', fullPage: true});
        console.log('\n=== Integration Test Complete ===');
        console.log('Screenshots saved: explorer-browse-test.png, chat-response-test.png, integration-test-final.png');

    } catch (error) {
        console.error('Test failed:', error);
        await page.screenshot({path: 'test-error.png', fullPage: true});
    } finally {
        // Keep browser open for manual inspection
        console.log('\nBrowser left open for inspection. Press Ctrl+C to exit.');
        await delay(30000); // Keep open for 30 seconds
        await browser.close();
    }
}

main().catch(console.error);
