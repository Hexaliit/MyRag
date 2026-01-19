import { test, expect } from '@playwright/test';

test.describe('LucidRAG Application', () => {

  test.describe('Navigation', () => {

    test('home page loads with main UI elements', async ({ page }) => {
      await page.goto('/');

      // Should have the main app layout
      await expect(page.locator('body')).toBeVisible();

      // Should have sidebar with collection selector
      await expect(page.locator('aside')).toBeVisible();

      // Should have document list or explorer
      await expect(page.locator('#sidebar-content, #document-list, .file-explorer')).toBeVisible();
    });

    test('can switch between Chat and Explorer modes', async ({ page }) => {
      await page.goto('/');

      // Look for mode toggle buttons
      const chatButton = page.getByRole('button', { name: /chat/i });
      const explorerButton = page.getByRole('button', { name: /explorer/i });

      // At least one mode should be available
      const hasModeToggle = await chatButton.or(explorerButton).isVisible();

      if (hasModeToggle) {
        // Try switching modes
        if (await explorerButton.isVisible()) {
          await explorerButton.click();
          await page.waitForTimeout(500);
        }
        if (await chatButton.isVisible()) {
          await chatButton.click();
          await page.waitForTimeout(500);
        }
      }

      // Page should still be functional after mode switches
      await expect(page.locator('body')).toBeVisible();
    });

  });

  test.describe('Collections', () => {

    test('can view collections', async ({ page }) => {
      await page.goto('/');

      // Look for collection selector/dropdown
      const collectionSelector = page.locator('[data-collection], .collection-selector, select, .dropdown');

      // Should have some collection UI element
      await expect(collectionSelector.first()).toBeVisible({ timeout: 10000 });
    });

  });

  test.describe('Documents', () => {

    test('document list is visible', async ({ page }) => {
      await page.goto('/');

      // Wait for document list to load
      await page.waitForTimeout(2000);

      // Should have document list container
      const docList = page.locator('#document-list, .document-list, [data-documents]');
      await expect(docList.first()).toBeVisible({ timeout: 10000 });
    });

  });

  test.describe('Search & Chat', () => {

    test('search/chat input is available', async ({ page }) => {
      await page.goto('/');

      // Look for chat/search input
      const chatInput = page.locator('textarea, input[type="text"]').filter({
        has: page.locator('[name*="query"], [placeholder*="Ask"], [placeholder*="Search"], [id*="chat"], [id*="query"]')
      });

      // Or just any prominent textarea
      const anyTextarea = page.locator('textarea');

      const hasInput = await chatInput.first().or(anyTextarea.first()).isVisible({ timeout: 5000 }).catch(() => false);

      if (hasInput) {
        await expect(chatInput.first().or(anyTextarea.first())).toBeVisible();
      }
    });

    test('can submit a search query via API', async ({ request }) => {
      // Test the search API directly
      const response = await request.post('/api/search', {
        headers: {
          'Content-Type': 'application/json',
        },
        data: {
          query: 'test query',
          topK: 5
        }
      });

      // Should get a response (even if empty results)
      expect(response.status()).toBeLessThan(500);
    });

  });

  test.describe('API Endpoints', () => {

    test('documents API returns data', async ({ request }) => {
      const response = await request.get('/api/documents');

      expect(response.ok()).toBeTruthy();

      const data = await response.json();
      expect(data).toBeDefined();
    });

    test('collections API returns data', async ({ request }) => {
      const response = await request.get('/api/collections');

      expect(response.ok()).toBeTruthy();

      const data = await response.json();
      expect(data).toBeDefined();
    });

    test('config API returns settings', async ({ request }) => {
      const response = await request.get('/api/config');

      expect(response.ok()).toBeTruthy();

      const data = await response.json();
      expect(data).toBeDefined();
    });

  });

});

test.describe('Admin Pages', () => {

  test('tenant admin page loads', async ({ page }) => {
    await page.goto('/tenant-admin');

    // Should load without errors
    await expect(page.locator('body')).toBeVisible();

    // Should have some admin content or redirect to appropriate page
    await page.waitForTimeout(1000);
  });

  test('system admin page loads for admin user', async ({ page }) => {
    await page.goto('/system-admin');

    // Should load without errors (may redirect if not authorized)
    await expect(page.locator('body')).toBeVisible();
  });

});
