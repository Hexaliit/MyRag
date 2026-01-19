import { test as setup, expect } from '@playwright/test';

const authFile = 'playwright/.auth/user.json';

/**
 * Authentication setup - runs once before all tests.
 * Logs in and saves session state for reuse.
 */
setup('authenticate', async ({ page }) => {
  // Go to login page
  await page.goto('/auth/login');

  // Fill login form
  await page.getByLabel('Email').fill('admin@lucidrag.local');
  await page.getByLabel('Password').fill('Admin123!');

  // Submit form
  await page.getByRole('button', { name: 'Login' }).click();

  // Wait for redirect to home page (successful login)
  await page.waitForURL('/', { timeout: 15000 });

  // Verify we're logged in by checking for a logged-in indicator
  // The app should show something indicating the user is authenticated
  await expect(page.locator('body')).not.toContainText('Login to LucidRAG');

  // Save signed-in state for reuse
  await page.context().storageState({ path: authFile });
});
