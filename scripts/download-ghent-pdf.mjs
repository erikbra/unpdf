#!/usr/bin/env node
/**
 * Downloads the Ghent PDF Output Suite V50 by automating the license-acceptance
 * form on the GWG download page with Playwright.
 *
 * Prerequisites:
 *   npm install playwright
 *   npx playwright install chromium
 *
 * Usage:
 *   node scripts/download-ghent-pdf.mjs [output-dir] [--test-pages]
 *
 * The default output directory is "target/pdfs" relative to the repository root.
 * After extraction the following file must exist:
 *   <output-dir>/Ghent_PDF_Output_Suite_V50_Full/Categories/1-CMYK/Test pages/Ghent_PDF-Output-Test-V50_CMYK_X4.pdf
 */

import { createWriteStream, existsSync, mkdirSync, readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { get as httpsGet } from 'node:https';
import { get as httpGet } from 'node:http';

const execFileAsync = promisify(execFile);
const playwrightPackageArgument = process.argv.find(argument => argument.startsWith('--playwright-package='));
const playwrightModule = playwrightPackageArgument
  ? pathToFileURL(path.join(path.resolve(playwrightPackageArgument.split('=', 2)[1]), 'index.js')).href
  : 'playwright';
const importedPlaywright = await import(playwrightModule);
const chromium = importedPlaywright.chromium ?? importedPlaywright.default?.chromium;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const PAGE_LOAD_TIMEOUT = 60_000;   // ms — allow for slow CI networks
const DOWNLOAD_TIMEOUT  = 120_000;  // ms — large ZIP download
const MAX_REDIRECTS     = 10;       // maximum HTTP redirects when fetching a direct link

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot  = path.resolve(__dirname, '..');
const outputDir = path.resolve(process.argv[2] ?? path.join(repoRoot, 'target', 'pdfs'));
const testPages = process.argv.includes('--test-pages');

const GWG_DOWNLOAD_URL = testPages
  ? 'https://gwg.org/download/ghent-output-suite-v50-testpages/'
  : 'https://gwg.org/download/ghentpdfoutputsuitev50/';
const expectedFiles = testPages
  ? [
      {
        path: path.join(outputDir, 'Ghent_PDF_Output_Suite_V50_Testpages', 'Ghent_PDF-Output-Test-V50_ALL_X4.pdf'),
        sha256: '6d9686303ea9049350bc1b3253c22a3cc8578bd7dfcc6afeaef89cbbe37d4cbe'
      },
      {
        path: path.join(outputDir, 'Ghent_PDF_Output_Suite_V50_Testpages', 'PR_Ghent-PDF-Output-Suite-5-Conformance-Certification.pdf'),
        sha256: 'a781534480c39d40ef4c6bddce3afb8bbcc8e62bb6007f4db0ebe78e4ee54417'
      }
    ]
  : [
      {
        path: path.join(
          outputDir,
          'Ghent_PDF_Output_Suite_V50_Full',
          'Categories',
          '1-CMYK',
          'Test pages',
          'Ghent_PDF-Output-Test-V50_CMYK_X4.pdf'
        ),
        sha256: null
      }
    ];
const EXPECTED_FILE = expectedFiles[0].path;

// ---------------------------------------------------------------------------
// Early-exit if already downloaded
// ---------------------------------------------------------------------------

mkdirSync(outputDir, { recursive: true });

if (expectedFiles.every(file => existsSync(file.path) && (!file.sha256 || sha256(file.path) === file.sha256))) {
  console.log('Ghent PDF already present, skipping download.');
  console.log(`  ${EXPECTED_FILE}`);
  process.exit(0);
}

// ---------------------------------------------------------------------------
// Browser automation
// ---------------------------------------------------------------------------

console.log('Launching browser to download Ghent PDF Output Suite V50...');

const browser = await chromium.launch({ headless: true });
const context  = await browser.newContext({ acceptDownloads: true });
const page     = await context.newPage();
let zipPath = null;

try {
  if (testPages) {
    zipPath = path.join(outputDir, 'GhentV50-TestPages.zip');
    console.log('  Downloading checksum-pinned test-pages ZIP...');
    await downloadFile('https://gwg.org/?wpdmdl=9080', zipPath);
  } else {
    await page.goto(GWG_DOWNLOAD_URL, { waitUntil: 'domcontentloaded', timeout: PAGE_LOAD_TIMEOUT });

  // -------------------------------------------------------------------------
  // Accept the license / terms-of-use form.
  // The GWG download page presents a form with a checkbox and a submit button.
  // Try several common selector patterns in priority order.
  // -------------------------------------------------------------------------

  const checkboxSelectors = [
    'input[type="checkbox"][name*="accept"]',
    'input[type="checkbox"][id*="accept"]',
    'input[type="checkbox"][name*="agree"]',
    'input[type="checkbox"][id*="agree"]',
    'input[type="checkbox"][name*="terms"]',
    'input[type="checkbox"]',
  ];

  let checked = false;
  for (const sel of checkboxSelectors) {
    const el = page.locator(sel).first();
    if (await el.count() > 0) {
      await el.check();
      console.log(`  Checked license checkbox: ${sel}`);
      checked = true;
      break;
    }
  }
  if (!checked) {
    console.warn('  Warning: could not find a license checkbox — the page may have changed.');
  }

  // -------------------------------------------------------------------------
  // Click the download / submit button and capture the resulting download.
  // -------------------------------------------------------------------------

  const downloadButtonSelectors = [
    'a.wpdm-download-link',
    'button.wpdm-download-link',
    'input[type="submit"]',
    'button[type="submit"]',
    'a[href*="ghent"]',
    'a[href*="Ghent"]',
    'a[href*=".zip"]',
    'button:has-text("Download")',
    'a:has-text("Download")',
  ];

    for (const sel of downloadButtonSelectors) {
    const el = page.locator(sel).first();
    if (await el.count() === 0 || !(await el.isVisible())) continue;

    // getAttribute returns null when the attribute is absent; any other error
    // is unexpected and should propagate so the caller can diagnose it.
    let href = null;
    try {
      href = await el.getAttribute('href');
    } catch {
      // Element may have disappeared between count() and getAttribute(); skip it.
      continue;
    }

    if (href && href.includes('.zip')) {
      // Direct link — download without triggering browser download dialog
      const resolvedUrl = new URL(href, GWG_DOWNLOAD_URL).toString();
      console.log(`  Downloading ZIP from direct link: ${resolvedUrl}`);
      zipPath = path.join(outputDir, testPages ? 'GhentV50-TestPages.zip' : 'GhentV50.zip');
      await downloadFile(resolvedUrl, zipPath);
      break;
    }

    // Otherwise submit/click and wait for a browser-initiated download
    console.log(`  Clicking download element: ${sel}`);
    const [download] = await Promise.all([
      page.waitForEvent('download', { timeout: DOWNLOAD_TIMEOUT }),
      el.click(),
    ]);
    zipPath = path.join(outputDir, testPages ? 'GhentV50-TestPages.zip' : 'GhentV50.zip');
    await download.saveAs(zipPath);
    console.log(`  Saved download to: ${zipPath}`);
    break;
    }
  }

  if (!zipPath || !existsSync(zipPath)) {
    throw new Error('Could not locate or download the Ghent ZIP file. The page structure may have changed.');
  }

  // -------------------------------------------------------------------------
  // Extract the ZIP.
  // Paths are passed as environment variables (Windows) or explicit argv
  // (POSIX) to avoid any shell-string injection.
  // -------------------------------------------------------------------------
  console.log(`Extracting ${zipPath} into ${outputDir}...`);
  if (process.platform === 'win32') {
    // Pass paths through environment variables so no path content ever lands
    // in the PowerShell script text itself.
    await execFileAsync(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-Command',
       'Expand-Archive -Force -LiteralPath $env:GHENT_ZIP -DestinationPath $env:GHENT_OUT'],
      { env: { ...process.env, GHENT_ZIP: zipPath, GHENT_OUT: outputDir } }
    );
  } else {
    // execFile passes argv directly to the kernel — no shell expansion.
    await execFileAsync('unzip', ['-o', zipPath, '-d', outputDir]);
  }

  const invalidFile = expectedFiles.find(file =>
    !existsSync(file.path) || (file.sha256 && sha256(file.path) !== file.sha256));
  if (invalidFile) {
    throw new Error(
      `Extraction completed but an expected file is missing or has the wrong SHA-256:\n  ${invalidFile.path}\n` +
      `Check the ZIP contents in ${outputDir} and adjust the path in Rendering.cs if needed.`
    );
  }

  console.log('Ghent PDF Output Suite V50 ready:');
  console.log(`  ${EXPECTED_FILE}`);
} finally {
  await browser.close();
}

function sha256(file) {
  return createHash('sha256').update(readFileSync(file)).digest('hex');
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Downloads a URL to a local file, following redirects up to MAX_REDIRECTS.
 * @param {string} url
 * @param {string} dest
 * @param {number} [redirects=0]
 * @returns {Promise<void>}
 */
function downloadFile(url, dest, redirects = 0) {
  return new Promise((resolve, reject) => {
    if (redirects > MAX_REDIRECTS) {
      return reject(new Error(`Too many redirects (> ${MAX_REDIRECTS}) for ${url}`));
    }
    const file   = createWriteStream(dest);
    const getter = url.startsWith('https') ? httpsGet : httpGet;

    file.on('error', reject);

    getter(url, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        file.close();
        // Resolve relative Location headers against the current URL.
        const next = new URL(res.headers.location, url).toString();
        downloadFile(next, dest, redirects + 1).then(resolve).catch(reject);
        return;
      }
      if (res.statusCode !== 200) {
        file.close();
        reject(new Error(`HTTP ${res.statusCode} for ${url}`));
        return;
      }
      res.pipe(file);
      file.on('finish', () => file.close(resolve));
    }).on('error', reject);
  });
}
