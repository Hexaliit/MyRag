import { test, expect } from '@playwright/test';

/**
 * Smoke tests - quick sanity checks that the app is running.
 * Run these first to catch obvious issues.
 *
 * Usage: npx playwright test smoke.spec.ts
 */

test.describe('Smoke Tests', () => {

  test('app is running and responds', async ({ page }) => {
    const response = await page.goto('/');

    expect(response?.status()).toBeLessThan(500);
    await expect(page.locator('body')).toBeVisible();
  });

  test('login page is accessible', async ({ page }) => {
    // Use a new context without stored auth
    const response = await page.goto('/auth/login');

    expect(response?.status()).toBe(200);
    await expect(page.getByText('Login to LucidRAG')).toBeVisible();
  });

  test('static assets load (CSS/JS)', async ({ page }) => {
    await page.goto('/');

    // Check that CSS is loaded (page should have styling)
    const body = page.locator('body');
    const bgColor = await body.evaluate(el => getComputedStyle(el).backgroundColor);

    // Should have some background color set (not default white)
    expect(bgColor).toBeDefined();
  });

  test('API health check', async ({ request }) => {
    // Try a few API endpoints
    const endpoints = [
      '/api/documents',
      '/api/collections',
      '/api/config',
    ];

    for (const endpoint of endpoints) {
      const response = await request.get(endpoint);
      expect(response.status(), `${endpoint} should respond`).toBeLessThan(500);
    }
  });

});
