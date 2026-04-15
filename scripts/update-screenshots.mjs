import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { chromium } from '@playwright/test';

const defaultPages = [
  { slug: 'marketing-home', path: '/' },
  { slug: 'marketing-platform', path: '/platform.html' },
  { slug: 'marketing-solutions', path: '/solutions.html' },
  { slug: 'marketing-pricing', path: '/pricing.html' },
  { slug: 'marketing-security', path: '/security.html' },
  { slug: 'marketing-contact', path: '/contact.html' },
  { slug: 'demo-home', path: '/demo/index.html' },
  { slug: 'admin-home', path: '/admin/index.html' },
  { slug: 'admin-interventions', path: '/admin/interventions.html' },
];

const desktop = { width: 1440, height: 1080, name: 'desktop' };
const mobile = { width: 430, height: 932, name: 'mobile' };

function readArg(name, fallback = undefined) {
  const cli = process.argv.find((arg) => arg.startsWith(`--${name}=`));
  if (cli) {
    return cli.slice(name.length + 3);
  }

  return process.env[name.toUpperCase().replace(/-/g, '_')] ?? fallback;
}

function parsePages(baseUrl) {
  const raw = readArg('pages');
  if (!raw) {
    return defaultPages.map((page) => ({
      ...page,
      url: new URL(page.path, baseUrl).toString(),
    }));
  }

  return raw
    .split(',')
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry, index) => {
      const [slug, target] = entry.includes('=') ? entry.split('=', 2) : [`page-${index + 1}`, entry];
      return {
        slug: slug.trim(),
        path: target.trim(),
        url: new URL(target.trim(), baseUrl).toString(),
      };
    });
}

async function ensureDir(dir) {
  await fs.mkdir(dir, { recursive: true });
}

async function capturePage(browser, pageConfig, viewport, outputDir, authHeader) {
  const context = await browser.newContext({
    viewport: { width: viewport.width, height: viewport.height },
    extraHTTPHeaders: authHeader ? { Authorization: authHeader } : undefined,
    deviceScaleFactor: viewport.name === 'mobile' ? 3 : 1,
    isMobile: viewport.name === 'mobile',
    hasTouch: viewport.name === 'mobile',
  });

  const page = await context.newPage();
  await page.goto(pageConfig.url, { waitUntil: 'networkidle' });
  await page.screenshot({
    path: path.join(outputDir, `${pageConfig.slug}-${viewport.name}.png`),
    fullPage: true,
  });
  await context.close();
}

async function main() {
  const repoRoot = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..', '..');
  const baseUrl = readArg('base-url', 'http://localhost:5050');
  const outputDir = path.resolve(repoRoot, readArg('output-dir', 'artifacts/ux-screenshots'));
  const pages = parsePages(baseUrl);
  const username = readArg('admin-user');
  const password = readArg('admin-password');
  const authHeader = username && password
    ? `Basic ${Buffer.from(`${username}:${password}`, 'utf8').toString('base64')}`
    : undefined;

  await ensureDir(outputDir);

  const browser = await chromium.launch({ headless: true });
  try {
    for (const pageConfig of pages) {
      await capturePage(browser, pageConfig, desktop, outputDir, authHeader);
      await capturePage(browser, pageConfig, mobile, outputDir, authHeader);
      console.log(`Captured ${pageConfig.slug}`);
    }
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
