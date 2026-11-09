import { crc32, deflateSync } from 'node:zlib';
import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { expectAccessible } from './axe';

/**
 * Spec §15.1 "E2E": the MVP smoke path against the real image with the seeded `Basic` set (§15.2), in order:
 * login + forced password change → ingest → browse/search/filter → game detail + downloads → global search +
 * ratings → review resolve/delete → images → Admin edit/move/delete → users and logs, with axe on every page and
 * modal visited. The seeded admin has both roles (`admin` + `rom-manager`).
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

/** A real square RGB PNG: the API checks magic bytes and dimensions, and the browser must render it. */
function png(size: number, rgb: [number, number, number]): Buffer {
	const chunk = (type: string, data: Buffer) => {
		const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
		const out = Buffer.alloc(data.length + 12);
		out.writeUInt32BE(data.length, 0);
		body.copy(out, 4);
		out.writeUInt32BE(crc32(body), data.length + 8);
		return out;
	};
	const header = Buffer.alloc(13);
	header.writeUInt32BE(size, 0);
	header.writeUInt32BE(size, 4);
	header[8] = 8; // bit depth
	header[9] = 2; // RGB
	const row = Buffer.from([0, ...Array.from({ length: size }, () => rgb).flat()]);
	return Buffer.concat([
		Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
		chunk('IHDR', header),
		chunk('IDAT', deflateSync(Buffer.concat(Array.from({ length: size }, () => row)))),
		chunk('IEND', Buffer.alloc(0))
	]);
}

async function openGame(title: string): Promise<void> {
	await page.goto('/');
	await page.getByRole('searchbox', { name: 'Search', exact: true }).fill(title);
	await expect(page).toHaveURL(/[?&]q=/);
	// The title link is stretched over the card (spec B21).
	await page.getByRole('link', { name: title, exact: true }).click();
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
	await expect(page.getByRole('cell', { name: '91-unmatched.gb', exact: true })).toBeVisible();
	await expectAccessible(page, 'ingestion');

	await page.getByRole('button', { name: 'Import all' }).click();

	const run = page.getByRole('row').filter({ hasText: 'Completed' });
	await expect(run).toContainText('Ingested 12 · Duplicate 1 · Review 2 · Failed 1', {
		timeout: 60_000
	});

	await run.getByRole('link').click();
	await expect(page.getByRole('heading', { name: /Ingest job/ })).toBeVisible();
	await expect(page.getByRole('cell', { name: /92-invalid\.gb/ })).toBeVisible();
	await expectAccessible(page, 'job detail');

	// D4/D5: the invalid file is listed under Failed files with its reason, and deleting it goes through the modal.
	await page.goto('/ingestion');
	const failed = page.locator('section[aria-labelledby="failed-heading"]');
	const invalid = failed.getByRole('row').filter({ hasText: '92-invalid.gb' });
	await expect(invalid).toContainText('InvalidHeader');
	await expectAccessible(page, 'ingestion with failed files');
	await invalid.getByRole('button', { name: 'Delete 92-invalid.gb' }).click();
	await expect(dialog()).toHaveAccessibleName('Delete 92-invalid.gb');
	await expect(dialog()).toContainText('gb/92-invalid.gb');
	await expectAccessible(page, 'ingest delete modal');
	await dialog().getByRole('button', { name: 'Delete', exact: true }).click();
	await expect(dialog()).toBeHidden();
	await expect(failed.getByText('No failed imports.')).toBeVisible();
});

test('reports and the ingest audit trail', async () => {
	await page.getByRole('link', { name: 'Reports', exact: true }).click();
	await expect(page).toHaveURL(/\/reports\/library-size$/);
	await expect(page.getByRole('table')).toContainText('Game Boy');
	await expectAccessible(page, 'library size report');
	await page.getByRole('table').getByRole('link', { name: 'Game Boy', exact: true }).click();
	await expect(page).toHaveURL(/system=gb/);
	await expect(page.getByRole('table')).toContainText('Orbit Blocks');
	await expectAccessible(page, 'library size system detail');
	await page.getByRole('link', { name: 'Missing Images', exact: true }).click();
	await expect(page.getByRole('table')).toContainText('Placeholder art');
	await expectAccessible(page, 'missing images report');
	await page.getByRole('link', { name: 'Duplicates', exact: true }).click();
	await expect(page.getByText('No duplicate games found.')).toBeVisible();
	await expectAccessible(page, 'duplicates report');

	await page.getByRole('link', { name: 'Admin', exact: true }).click();
	await page.getByRole('link', { name: 'Auditing', exact: true }).click();
	const ingested = page
		.getByRole('row')
		.filter({ has: page.getByRole('cell', { name: 'Ingested', exact: true }) });
	await expect(ingested).toHaveCount(12);
	await expect(
		page
			.getByRole('row')
			.filter({ has: page.getByRole('cell', { name: 'IngestFileDeleted', exact: true }) })
	).toContainText('ingest/gb/_failed/92-invalid.gb');
	await expect(ingested.first()).toContainText('admin');
	await expect(ingested.first()).toContainText('ingest/');
	await expectAccessible(page, 'audit trail');
});

test('browse, search and filter the library', async () => {
	await page.goto('/');
	await expect(page.getByText('10 games')).toBeVisible();
	await expectAccessible(page, 'library grid');

	await page.getByRole('searchbox', { name: 'Search', exact: true }).fill('orb');
	await expect(page).toHaveURL(/q=orb/);
	// Orbit Blocks and Orbit Kart 64.
	await expect(page.getByText('2 games', { exact: true })).toBeVisible();

	await page.goto('/');
	await page.getByLabel('System').selectOption({ label: 'Game Boy Color' });
	await expect(page).toHaveURL(/system=gbc/);
	await expect(page.getByText('2 games')).toBeVisible();

	await page.getByLabel('System').selectOption({ label: 'Game Boy Advance' });
	await expect(page).toHaveURL(/system=gba/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();

	// Seeded byte-swapped (.v64): it only shows up if ingestion matched it to the DAT after conversion.
	await page.getByLabel('System').selectOption({ label: 'Nintendo 64' });
	await expect(page).toHaveURL(/system=n64/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Orbit Kart 64', exact: true })).toBeVisible();

	await page.getByLabel('System').selectOption({ label: 'Nintendo Entertainment System' });
	await expect(page).toHaveURL(/system=nes/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Pixel Pioneer', exact: true })).toBeVisible();

	// Seeded copier-headered (.smc): it only shows up if ingestion matched it to the DAT after stripping the header.
	await page.getByLabel('System').selectOption({ label: 'Super Nintendo Entertainment System' });
	await expect(page).toHaveURL(/system=snes/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Mode Seven Rally', exact: true })).toBeVisible();

	// Seeded as a loose .bin: it only shows up if ingestion accepted it and matched it to the Atari DAT (A3).
	await page.getByLabel('System').selectOption({ label: 'Atari 2600' });
	await expect(page).toHaveURL(/system=a2600/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Laser Lanes', exact: true })).toBeVisible();

	// "Quest, The" is shown as "The Quest" but filed under Q (§5, §6.1).
	await page.goto('/');
	await page.getByLabel('Letter').selectOption('Q');
	await expect(page.getByRole('img', { name: 'The Quest box art' })).toBeVisible();

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
	await expect(dialog()).toHaveAccessibleName(/^Header — Orbit Blocks .*\.gb$/);
	await expect(dialog().getByText('Logo valid')).toBeVisible();
	await expectAccessible(page, 'header modal');
	await page.keyboard.press('Escape');
	await expect(dialog()).toBeHidden();

	const zip = page.waitForEvent('download');
	await page.getByRole('button', { name: 'Download ZIP' }).click();
	expect((await zip).suggestedFilename()).toBe('Orbit Blocks.zip');

	// Three releases → a dropdown, the primary (Rev 1) first, each with its size.
	await page.getByRole('button', { name: 'Download ROM' }).click();
	const items = page.locator('.dropdown-menu.show .dropdown-item');
	await expect(items).toHaveCount(3);
	await expect(items.first()).toHaveText(/^Orbit Blocks \(World\) \(Rev 1\)\.gb — .*KiB$/);
	const rom = page.waitForEvent('download');
	await items.first().click();
	expect((await rom).suggestedFilename()).toBe('Orbit Blocks (World) (Rev 1).gb');

	// G2: release cards, no sideways page scroll on a phone (Orbit Blocks has the longest seeded file name).
	await expect(page.getByRole('article', { name: 'Orbit Blocks (World) (Rev 1).gb' })).toBeVisible();
	// R1: each release card has a 6px stripe in the system colour.
	await expect(page.getByRole('article', { name: 'Orbit Blocks (World) (Rev 1).gb' })).toHaveCSS(
		'border-left-width',
		'6px'
	);
	const viewport = page.viewportSize()!;
	await page.setViewportSize({ width: 375, height: 812 });
	expect(
		await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)
	).toBe(true);
	await page.setViewportSize(viewport);
});

test('global search, ratings and favourites', async () => {
	// Ctrl+K from the library opens the search; part of a title finds it; Enter opens it.
	await page.goto('/');
	await expect(page.getByText('10 games')).toBeVisible();
	await page.keyboard.press('Control+k');
	await expect(dialog()).toBeVisible();
	await expect(dialog().getByRole('combobox')).toBeFocused();
	await dialog().getByRole('combobox').fill('orbit');
	await expect(dialog().getByRole('option').first()).toContainText('Orbit Blocks');
	await expectAccessible(page, 'global search modal');
	await page.keyboard.press('Enter');
	await expect(page.getByRole('heading', { level: 1, name: 'Orbit Blocks' })).toBeVisible();

	// Rate and favourite it from its library card; both survive a reload.
	await page.goto('/');
	const card = page.getByRole('group', { name: 'Rating for Orbit Blocks', exact: true });
	const star = card.getByRole('button', { name: 'Rate 4 of 5' });
	const heart = card.getByRole('button', { name: 'Favourite' });
	await star.click();
	await expect(star).toHaveAttribute('aria-pressed', 'true');
	await expect(heart).toBeEnabled();
	await heart.click();
	await expect(heart).toHaveAttribute('aria-pressed', 'true');
	await expect(heart).toBeEnabled();
	await page.reload();
	await expect(star).toHaveAttribute('aria-pressed', 'true');
	await expect(heart).toHaveAttribute('aria-pressed', 'true');

	// The Favourites filter shows only it.
	await page.getByRole('button', { name: 'Favourites' }).click();
	await expect(page).toHaveURL(/favourite=true/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Orbit Blocks', exact: true })).toBeVisible();

	// Back to the seed state for the later tests.
	await page.goto('/');
	await star.click();
	await expect(star).toHaveAttribute('aria-pressed', 'false');
	await expect(heart).toBeEnabled();
	await heart.click();
	await expect(heart).toHaveAttribute('aria-pressed', 'false');
	await expect(heart).toBeEnabled();
	await page.reload();
	await expect(star).toHaveAttribute('aria-pressed', 'false');
	await expect(heart).toHaveAttribute('aria-pressed', 'false');
});

test('review queue: resolve with a suggestion', async () => {
	await page.goto('/review');
	await expectAccessible(page, 'review queue');
	await page.getByRole('link', { name: '91-unmatched.gb' }).click();
	// It has suggestions, so that tab is open first (W2).
	await expect(page.getByRole('tab', { name: 'Suggestions' })).toHaveAttribute('aria-selected', 'true');
	await expectAccessible(page, 'review detail');

	await page.getByRole('button', { name: 'Use' }).first().click();
	await expect(page).toHaveURL(/\/games\//);
	await expect(page.getByRole('article').getByText(/\(Unverified\)/)).toBeVisible();
});

test('review queue: delete a file', async () => {
	await page.goto('/review');
	await expect(page.getByRole('link', { name: '91-unmatched.gb' })).toHaveCount(0);
	await page.getByRole('button', { name: 'Delete 93-mystery.gb' }).click();
	await expect(dialog()).toHaveAccessibleName('Delete File: 93-mystery.gb');
	await expect(dialog().getByText('93-mystery.gb').first()).toBeVisible();
	await expectAccessible(page, 'review delete modal');
	await dialog().getByRole('button', { name: 'Delete file' }).click();
	await expect(dialog()).toBeHidden();
	await expect(page.getByText('Nothing to review.')).toBeVisible();
});

test('images: upload, set cover, delete', async () => {
	await openGame('Star Courier');
	const viewport = page.viewportSize()!;
	await page.setViewportSize({ width: 1280, height: 800 });
	await page.getByRole('button', { name: 'View info.json' }).click();
	await expect(dialog()).toHaveAccessibleName('info.json');
	await expect(dialog().getByRole('button', { name: 'Expand all' })).toBeVisible();
	await dialog().getByRole('button', { name: 'Expand all' }).click();
	// J2: the JSON pane scrolls, the dialog doesn't.
	const fit = await page.evaluate(() => {
		const modal = document.querySelector('.modal.show')!;
		const pane = modal.querySelector('.border.rounded.overflow-auto')!;
		return {
			modal: modal.scrollHeight <= modal.clientHeight,
			overflow: getComputedStyle(pane).overflow
		};
	});
	expect(fit).toEqual({ modal: true, overflow: 'auto' });
	await expectAccessible(page, 'info.json modal');
	await page.setViewportSize(viewport);
	await page.keyboard.press('Escape');
	await expect(dialog()).toBeHidden();
	await page.getByRole('tab', { name: /^Images \(/ }).click();
	await expect(page).toHaveURL(/tab=images/);
	const images = page.getByRole('region', { name: 'Images' });
	const card = (fileName: string) =>
		images
			.locator('.card')
			.filter({ has: page.getByRole('button', { name: `View ${fileName} full size` }) });
	await expect(images.getByText('Placeholder')).toBeVisible();

	// A box art upload replaces the placeholder and takes over as cover.
	await images.getByRole('button', { name: 'Upload image' }).click();
	await expect(dialog()).toHaveAccessibleName('Upload image');
	await expectAccessible(page, 'upload image modal');
	// U6: a loopback URL is refused by the server; the fallback switches back to the file input.
	await dialog().getByText('URL', { exact: true }).click();
	await dialog().getByLabel('Image URL').fill('http://127.0.0.1/x.png');
	await dialog().getByRole('button', { name: 'Upload', exact: true }).click();
	await expect(
		dialog().getByText('The URL points to a private or local network address.')
	).toBeVisible();
	await expectAccessible(page, 'upload image modal, URL error');
	await dialog().getByRole('button', { name: 'Upload a file instead' }).click();
	await expect(dialog().getByLabel('Image URL')).toHaveCount(0);
	await dialog()
		.getByLabel('Upload an image')
		.setInputFiles({ name: 'art.png', mimeType: 'image/png', buffer: png(64, [200, 40, 40]) });
	await dialog().getByRole('button', { name: 'Upload', exact: true }).click();
	await expect(dialog()).toBeHidden();
	await expect(card('boxart-01.png').getByText('Cover')).toBeVisible();
	await expect(images.getByText('Placeholder')).toHaveCount(0);
	await expect(card('boxart-01.png')).toContainText('64×64');

	await images.getByRole('button', { name: 'Upload image' }).click();
	await dialog()
		.getByLabel('Upload an image')
		.setInputFiles({ name: 'shot.png', mimeType: 'image/png', buffer: png(32, [40, 40, 200]) });
	await dialog().getByLabel('Type').selectOption({ label: 'Screenshot' });
	await dialog().getByRole('button', { name: 'Upload', exact: true }).click();
	await expect(dialog()).toBeHidden();
	await expect(card('snap-01.png').getByText('Screenshot')).toBeVisible();
	await expectAccessible(page, 'game images');

	await card('snap-01.png').getByRole('button', { name: 'View snap-01.png full size' }).click();
	await expect(dialog()).toHaveAccessibleName('snap-01.png');
	await expectAccessible(page, 'image view modal');
	await page.keyboard.press('Escape');
	await expect(dialog()).toBeHidden();

	await card('snap-01.png').getByRole('button', { name: 'Use as cover: snap-01.png' }).click();
	await expect(card('snap-01.png').getByText('Cover')).toBeVisible();
	await expect(page.getByRole('img', { name: 'Star Courier cover' })).toHaveAttribute('width', '32');

	// Deleting the cover hands it back to the box art.
	await card('snap-01.png').getByRole('button', { name: 'Delete snap-01.png' }).click();
	await expect(dialog()).toHaveAccessibleName('Delete image');
	await expectAccessible(page, 'delete image modal');
	await dialog().getByRole('button', { name: 'Delete image' }).click();
	await expect(dialog()).toBeHidden();
	await expect(card('snap-01.png')).toHaveCount(0);
	await expect(card('boxart-01.png').getByText('Cover')).toBeVisible();
});

test('release notes: add, edit and delete', async () => {
	await openGame('Star Courier');
	const add = page.getByRole('button', { name: /^Add note for / }).first();
	const fileName = (await add.getAttribute('aria-label'))!.replace('Add note for ', '');

	// No note yet: the modal opens in Edit, View is disabled.
	await add.click();
	await expect(dialog()).toHaveAccessibleName(`Note — ${fileName}`);
	await expect(dialog().getByRole('radio', { name: 'View', exact: true })).toBeDisabled();
	const text = dialog().getByLabel('Note (Markdown)');
	await text.fill('# Courier tips\n\nPress **Start** twice.');
	await expect(dialog().getByText(/^\d+ \/ 65,536 bytes$/)).toBeVisible();
	await dialog().locator('label').filter({ hasText: /^Preview$/ }).click();
	await expect(dialog().getByRole('heading', { name: 'Courier tips' })).toBeVisible();
	await expectAccessible(page, 'release note modal');
	await dialog().getByRole('button', { name: 'Save' }).click();
	await expect(dialog()).toBeHidden();

	// Now it has one: View shows it rendered; edit it.
	await page.getByRole('button', { name: `Note for ${fileName}`, exact: true }).click();
	await expect(dialog().getByRole('heading', { name: 'Courier tips' })).toBeVisible();
	await dialog().locator('label').filter({ hasText: /^Edit$/ }).click();
	await text.fill('## Updated tips');
	await dialog().getByRole('button', { name: 'Save' }).click();
	await expect(dialog()).toBeHidden();
	await page.reload();
	await page.getByRole('button', { name: `Note for ${fileName}`, exact: true }).click();
	await expect(dialog().getByRole('heading', { name: 'Updated tips' })).toBeVisible();

	// Delete it with the inline confirmation.
	await dialog().getByRole('button', { name: 'Delete note' }).click();
	await expect(dialog().getByText('Delete this note?')).toBeVisible();
	await dialog().getByRole('button', { name: 'Yes' }).click();
	await expect(dialog()).toBeHidden();
	await expect(page.getByRole('button', { name: `Add note for ${fileName}`, exact: true })).toBeVisible();
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
	await expect(page.locator('body')).not.toHaveClass(/modal-open/);
	await expect(page.locator('.modal-backdrop')).toHaveCount(0);
	// A shorter viewport guarantees overflow for the small seed library. A wheel must scroll after navigation.
	const viewport = page.viewportSize()!;
	await page.setViewportSize({ width: viewport.width, height: 400 });
	await page.mouse.move(400, 200);
	await page.mouse.wheel(0, 500);
	await expect.poll(() => page.evaluate(() => window.scrollY)).toBeGreaterThan(0);
	await page.setViewportSize(viewport);

	await openGame('Color Racer');
	await page.getByRole('button', { name: 'Delete game' }).click();
	await dialog().getByRole('button', { name: 'Delete' }).click();
	await expect(page).toHaveURL(/\/$/);
	await page.getByRole('searchbox', { name: 'Search', exact: true }).fill('Color Racer');
	await expect(page.getByText('No games match these filters.')).toBeVisible();
});

test('Admin: roles and the create-user modal', async () => {
	await page.goto('/admin/users');
	await expect(page.getByLabel('admin role of admin')).toBeChecked();
	await expect(page.getByLabel('rom-manager role of admin')).toBeChecked();

	await page.getByRole('button', { name: 'Create user' }).click();
	await expect(dialog()).toHaveAccessibleName('Create user');
	await expectAccessible(page, 'create user modal');
	await dialog().getByLabel('Username').fill('editor');
	await dialog().getByLabel('Initial password').fill(`${newPassword}-editor`);
	await dialog().getByLabel('rom-manager').check();
	await dialog().getByRole('button', { name: 'Create' }).click();
	await expect(dialog()).toBeHidden();
	await expect(page.getByLabel('rom-manager role of editor')).toBeChecked();
	await expect(page.getByLabel('admin role of editor')).not.toBeChecked();
});

test('Admin: live logs', async () => {
	await page.goto('/admin/logs');
	await expect(page.getByRole('link', { name: 'Logs', exact: true })).toHaveAttribute('aria-current', 'page');
	// The buffer already holds startup and request events.
	await expect(page.getByRole('log', { name: 'Live log' }).locator('.badge').first()).toBeVisible();
	await expectAccessible(page, 'logs');
});

test('keyboard shortcuts: use, rebind and use again', async () => {
	await page.goto('/');
	await expect(page.locator('nav.navbar')).toHaveClass(/sticky-top/);
	await expect(page.getByRole('button', { name: 'Keyboard shortcuts' })).toBeVisible();

	// The default Alt+Shift+I goes to Ingestion (bindings load after sign-in, so retry until they have).
	await expect(async () => {
		await page.keyboard.press('Alt+Shift+KeyI');
		await expect(page).toHaveURL(/\/ingestion$/, { timeout: 1_000 });
	}).toPass();

	await page.getByRole('button', { name: 'Keyboard shortcuts' }).click();
	await expect(dialog()).toHaveAccessibleName('Keyboard shortcuts');
	await expect(dialog().getByRole('row', { name: /Go to Library/ })).toContainText('Alt+Shift+L');
	await expectAccessible(page, 'keyboard shortcuts modal');

	await dialog().getByRole('button', { name: 'Edit Go to Library' }).click();
	await expect(dialog().getByText('Press keys…')).toBeVisible();
	// Escape only stops capturing; the dialog stays open.
	await page.keyboard.press('Escape');
	await expect(dialog()).toBeVisible();
	await dialog().getByRole('button', { name: 'Edit Go to Library' }).click();
	await page.keyboard.press('Alt+Shift+KeyK');
	await expect(dialog().getByRole('row', { name: /Go to Library/ })).toContainText('Alt+Shift+K');
	await dialog().getByRole('button', { name: 'Save' }).click();
	await expect(dialog()).toBeHidden();

	// A full load fetches the saved bindings again: retry until they're in.
	await page.goto('/reports/library-size');
	await expect(async () => {
		await page.keyboard.press('Alt+Shift+KeyK');
		await expect(page).toHaveURL(/\/$/, { timeout: 1_000 });
	}).toPass();

	// Back to the defaults for anything after this.
	await page.getByRole('button', { name: 'Keyboard shortcuts' }).click();
	await dialog().getByRole('button', { name: 'Reset to defaults' }).click();
	await dialog().getByRole('button', { name: 'Save' }).click();
	await expect(dialog()).toBeHidden();
});

test('Admin: database backups', async () => {
	await page.goto('/admin/backups');
	await expect(page.getByRole('link', { name: 'Backups', exact: true })).toHaveAttribute('aria-current', 'page');
	await expect(page.getByLabel(/^Daily at \(server time, /)).toHaveValue('03:30');
	await expect(page.getByText('No backups yet.')).toBeVisible();
	await expectAccessible(page, 'backups');

	await page.getByRole('button', { name: /Back up now/ }).click();
	await expect(page.getByRole('cell', { name: /^rnrommanager-\d{8}-\d{6}\.zip$/ })).toBeVisible({
		timeout: 30_000
	});
	await expect(page.getByTestId('last-result')).toContainText('rnrommanager-');
	await expectAccessible(page, 'backups with a backup');
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
