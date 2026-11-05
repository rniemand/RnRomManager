import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { expectAccessible } from './axe';

/**
 * Spec §15.1 "E2E": the MVP smoke path against the real image with the seeded `Basic` set (§15.2), in order:
 * login + forced password change → ingest → browse/search/filter → game detail + download → review resolve →
 * Admin edit/move/delete, with axe on every page visited.
 */

const adminPassword = process.env.E2E_ADMIN_PASSWORD;
if (!adminPassword) throw new Error('E2E_ADMIN_PASSWORD is not set (run scripts/test-e2e.ps1).');
// The forced change needs a different password; derived so no credential sits in the repo.
const newPassword = `${adminPassword}-changed`;

test.describe.configure({ mode: 'serial' });

let context: BrowserContext;
let page: Page;

// One page for the whole walk (the session cookie carries over); axe needs an explicit context.
test.beforeAll(async ({ browser }) => {
	context = await browser.newContext();
	page = await context.newPage();
});

test.afterAll(async () => {
	await context.close();
});

const dialog = () => page.getByRole('dialog');

async function openGame(title: string): Promise<void> {
	await page.goto('/');
	await page.getByLabel('Search').fill(title);
	await expect(page).toHaveURL(/[?&]q=/);
	await page.getByRole('img', { name: `${title} box art`, exact: true }).click();
	await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible();
}

test('login + forced password change', async () => {
	await page.goto('/');
	await expect(page).toHaveURL(/\/login\?returnUrl=/);
	await expectAccessible(page, 'login');

	await page.getByLabel('Username').fill('admin');
	await page.getByLabel('Password').fill('wrong-password-for-e2e');
	await page.getByRole('button', { name: 'Sign in' }).click();
	await expect(page.getByRole('alert')).toHaveText('Invalid username or password.');

	await page.getByLabel('Password').fill(adminPassword);
	await page.getByRole('button', { name: 'Sign in' }).click();
	await expect(page).toHaveURL(/\/change-password$/);
	await expectAccessible(page, 'change password');

	await page.getByLabel('Current password').fill(adminPassword);
	await page.getByLabel('New password', { exact: true }).fill(newPassword);
	await page.getByLabel('Confirm new password').fill(newPassword);
	await page.getByRole('button', { name: 'Change password' }).click();

	await expect(page.getByText('The library is empty.')).toBeVisible();
	await expectAccessible(page, 'empty library');
});

test('ingestion of the seeded Basic set', async () => {
	await page.getByRole('link', { name: 'Go to Ingestion' }).click();
	await expect(page.getByRole('cell', { name: '91-unmatched.gb' })).toBeVisible();
	await expectAccessible(page, 'ingestion');

	await page.getByRole('button', { name: 'Start ingestion' }).click();

	const run = page.getByRole('row').filter({ hasText: 'Completed' });
	await expect(run).toContainText('Ingested 7 · Duplicate 1 · Review 1 · Failed 1', {
		timeout: 60_000
	});

	await run.getByRole('link').click();
	await expect(page.getByRole('heading', { name: /Ingest job/ })).toBeVisible();
	await expect(page.getByRole('cell', { name: /92-invalid\.gb/ })).toBeVisible();
	await expectAccessible(page, 'job detail');
});

test('browse, search and filter the library', async () => {
	await page.goto('/');
	await expect(page.getByText('5 games')).toBeVisible();
	await expectAccessible(page, 'library grid');

	await page.getByLabel('Search').fill('orb');
	await expect(page).toHaveURL(/q=orb/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();

	await page.goto('/');
	await page.getByLabel('System').selectOption({ label: 'Game Boy Color' });
	await expect(page).toHaveURL(/system=gbc/);
	await expect(page.getByText('2 games')).toBeVisible();

	// "Quest, The" is shown as "The Quest" but filed under Q (§5, §6.1).
	await page.goto('/');
	await page.getByLabel('Letter').selectOption('Q');
	await expect(page.getByRole('link', { name: 'The Quest box art' })).toBeVisible();

	await page.getByRole('button', { name: 'List view' }).click();
	await expect(page.getByRole('table')).toContainText('The Quest');
	await expectAccessible(page, 'library list');
	await page.getByRole('button', { name: 'Grid view' }).click();
});

test('game detail + downloads', async () => {
	await openGame('Orbit Blocks');
	await expect(page.getByText('Primary')).toBeVisible();
	await expectAccessible(page, 'game detail');

	await page.getByRole('button', { name: 'Header' }).first().click();
	await expect(page.getByText('Logo valid')).toBeVisible();

	const zip = page.waitForEvent('download');
	await page.getByRole('button', { name: 'Download ZIP' }).click();
	expect((await zip).suggestedFilename()).toBe('Orbit Blocks.zip');

	const rom = page.waitForEvent('download');
	await page.getByRole('button', { name: /^Download Orbit Blocks .*\.gb$/ }).first().click();
	expect((await rom).suggestedFilename()).toMatch(/^Orbit Blocks .*\.gb$/);
});

test('review queue: resolve with a suggestion', async () => {
	await page.goto('/review');
	await expectAccessible(page, 'review queue');
	await page.getByRole('link', { name: '91-unmatched.gb' }).click();
	await expect(page.getByRole('heading', { name: 'Suggestions' })).toBeVisible();
	await expectAccessible(page, 'review detail');

	await page.getByRole('button', { name: 'Use' }).first().click();
	await expect(page).toHaveURL(/\/games\//);
	await expect(page.getByText(/\(Unverified\)/)).toBeVisible();

	await page.goto('/review');
	await expect(page.getByText('Nothing to review.')).toBeVisible();
});

test('Admin: edit metadata, move a release, delete a release and a game', async () => {
	await openGame('Orbit Blocks');

	await page.getByRole('button', { name: 'Edit metadata' }).click();
	await expect(dialog()).toBeVisible();
	await expectAccessible(page, 'edit metadata modal');
	await dialog().getByLabel('Release year').fill('1995');
	await dialog().getByRole('button', { name: 'Edit metadata' }).click();
	await expect(dialog()).toBeHidden();
	await expect(page.getByText('1995')).toBeVisible();

	// Split the Japanese release off into its own game (§11 Move release → new game).
	await page.getByRole('button', { name: 'Move Orbit Blocks (Japan).gb', exact: true }).click();
	await dialog().getByLabel('A new game').check();
	await dialog().getByLabel('New game title').fill('Orbit Blocks Japan');
	await dialog().getByRole('button', { name: 'Move release' }).click();
	await expect(dialog()).toBeHidden();
	await expect(page.getByRole('button', { name: 'Move Orbit Blocks (Japan).gb', exact: true })).toHaveCount(0);

	// Its only release goes to _removed/, so the game goes too and we land on the Library.
	await openGame('Orbit Blocks Japan');
	await page.getByRole('button', { name: 'Delete Orbit Blocks (Japan).gb', exact: true }).click();
	await dialog().getByRole('button', { name: 'Delete' }).click();
	await expect(page).toHaveURL(/\/$/);

	await openGame('Color Racer');
	await page.getByRole('button', { name: 'Delete game' }).click();
	await dialog().getByRole('button', { name: 'Delete' }).click();
	await expect(page).toHaveURL(/\/$/);
	await page.getByLabel('Search').fill('Color Racer');
	await expect(page.getByText('No games match these filters.')).toBeVisible();
});

test('Admin and Integrity pages are accessible', async () => {
	for (const [path, heading] of [
		['/integrity', 'Integrity'],
		['/admin/users', 'Admin'],
		['/admin/dats', 'Admin'],
		['/admin/settings', 'Admin'],
		['/admin/about', 'Admin']
	]) {
		await page.goto(path);
		await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
		await expect(page.locator('[role="status"]')).toHaveCount(0); // finished loading
		await expectAccessible(page, path);
	}
});
