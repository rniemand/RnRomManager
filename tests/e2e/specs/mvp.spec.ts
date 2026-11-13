import { readFile, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { crc32, deflateSync } from 'node:zlib';
import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { expectAccessible } from './axe';

/**
 * Spec §15.1 "E2E": the MVP smoke path against the real image with the seeded `Basic` set (§15.2), in order:
 * login + forced password change → ingest → browse/search/filter → game detail + downloads → global search +
 * ratings → collections → review resolve/delete → images → Admin edit/move/delete → users and logs, with axe on every page and
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

/** The seeded Switch base game's bytes, read before ingestion moves the file next to its ZIP (step 28a). */
let switchBase: Buffer;
/** The seeded ticketless `[UPD]` update (game updates B7), read before ingestion. */
let switchUpdate: Buffer;
/** The seeded PlayStation set's audio track, read before ingestion moves it (disc systems B20). */
let discTrack2: Buffer;

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

/**
 * L7: a bare `/` restores the last Library filter. A Library URL with a filter parameter wins and is saved, and
 * `page=1` is the default, so this opens the unfiltered Library and forgets the saved filter.
 */
async function openLibrary(): Promise<void> {
	await page.goto('/?page=1');
	await expect(page.getByRole('button', { name: 'System: All' })).toBeVisible();
}

/** L1: picks `value` (an option's id) from a Library filter menu. */
async function chooseFilter(field: 'System' | 'Region' | 'Letter' | 'Sort', value: string): Promise<void> {
	await page.getByRole('button', { name: new RegExp(`^${field}: `) }).click();
	await page.locator(`.dropdown-menu.show [data-value="${value}"]`).click();
}

async function openGame(title: string): Promise<void> {
	await openLibrary();
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

	// I1: Pending / Failed / Last runs are tabs under the always-visible Run section.
	await expect(page.getByRole('tab', { name: /^Pending files \(\d+\)$/ })).toHaveAttribute('aria-selected', 'true');
	const ingest = process.env.E2E_INGEST;
	if (!ingest) throw new Error('E2E_INGEST is not set (run scripts/test-e2e.ps1).');
	switchBase = await readFile(join(ingest, 'switch', '01-orbit-expedition.nsp'));
	switchUpdate = await readFile(
		join(ingest, 'switch', '04-orbit-expedition [0100000000010000][UPD][1.1.0].nsp')
	);
	discTrack2 = await readFile(join(ingest, 'psx', 'Disc Runner (USA) (Track 2).bin'));
	await page.getByRole('button', { name: 'Import all' }).click();

	await page.getByRole('tab', { name: 'Last runs' }).click();
	await expect(page).toHaveURL(/\/ingestion\?tab=runs$/);
	await expect(page.getByRole('button', { name: 'Import all' })).toBeVisible();
	const run = page.getByRole('row').filter({ hasText: 'Completed' });
	await expect(run).toContainText('Ingested 32 · Duplicate 1 · Review 2 · Failed 1', {
		timeout: 60_000
	});

	await run.getByRole('link').click();
	await expect(page.getByRole('heading', { name: /Ingest job/ })).toBeVisible();
	await expect(page.getByRole('cell', { name: /92-invalid\.gb/ })).toBeVisible();
	await expectAccessible(page, 'job detail');

	await page.goto('/ingestion?tab=runs');
	await expect(page.getByRole('tab', { name: 'Last runs' })).toHaveAttribute('aria-selected', 'true');
	await expect(page.getByRole('row').filter({ hasText: 'Completed' })).toBeVisible();
	await expectAccessible(page, 'ingestion last runs tab');

	// D4/D5: the invalid file is listed under Failed files with its reason, and deleting it goes through the modal.
	await page.goto('/ingestion');
	await page.getByRole('tab', { name: 'Failed files (1)' }).click();
	await expect(page).toHaveURL(/\/ingestion\?tab=failed$/);
	await expect(page.getByRole('button', { name: 'Import all' })).toBeVisible();
	const failed = page.locator('#failed-panel');
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
	// One Ingested event per release: a bin/cue set is one event (B7a).
	await expect(ingested).toHaveCount(28);
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
	await openLibrary();
	await expect(page.getByText('23 games')).toBeVisible();
	await expectAccessible(page, 'library grid');
	// S1/L3: systems in display-name order after "All systems"; F3/L2: each with its game count, right-aligned.
	await page.getByRole('button', { name: 'System: All' }).click();
	const systemMenu = page.locator('.dropdown-menu.show');
	await expect(systemMenu.getByPlaceholder('Filter systems…')).toBeFocused();
	await expect(systemMenu.locator('.dropdown-item').first()).toContainText('All systems');
	await expect(systemMenu.locator('.dropdown-item').first().locator('.font-monospace')).toHaveText('23');
	expect(await systemMenu.locator('[data-value]').evaluateAll((rows) => rows.map((r) => (r as HTMLElement).dataset.value))).toEqual([
		's32x', 'a2600', 'coleco', 'gb', 'gba', 'gbc', 'gg', 'gc', 'sms', 'md', 'n64', 'nes', 'switch', 'psx', 'psp', 'saturn',
		'snes', 'tg16', 'wii', 'x360'
	]);
	await expect(systemMenu.locator('[data-value="gb"] .font-monospace')).toHaveText('3');
	await expect(systemMenu.locator('[data-value="gbc"] .font-monospace')).toHaveText('2');
	await expectAccessible(page, 'library with the System menu open');
	await page.keyboard.press('Escape');
	await page.getByRole('button', { name: 'Letter: All' }).click();
	await expect(page.locator('.dropdown-menu.show [data-value="#"] .font-monospace')).toHaveText(/^\d+$/);
	await page.keyboard.press('Escape');

	await page.getByRole('searchbox', { name: 'Search', exact: true }).fill('orb');
	await expect(page).toHaveURL(/q=orb/);
	// Orbit Blocks, Orbit Kart 64 and Orbit Expedition (Switch).
	await expect(page.getByText('3 games', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: /Orbit Expedition/ }).first()).toBeVisible();

	await openLibrary();
	await chooseFilter('System', 'gbc');
	await expect(page).toHaveURL(/system=gbc/);
	await expect(page.getByText('2 games')).toBeVisible();

	await chooseFilter('System', 'gba');
	await expect(page).toHaveURL(/system=gba/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();

	// Seeded byte-swapped (.v64): it only shows up if ingestion matched it to the DAT after conversion.
	await chooseFilter('System', 'n64');
	await expect(page).toHaveURL(/system=n64/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Orbit Kart 64', exact: true })).toBeVisible();

	await chooseFilter('System', 'nes');
	await expect(page).toHaveURL(/system=nes/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Pixel Pioneer', exact: true })).toBeVisible();

	// Seeded copier-headered (.smc): it only shows up if ingestion matched it to the DAT after stripping the header.
	await chooseFilter('System', 'snes');
	await expect(page).toHaveURL(/system=snes/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Mode Seven Rally', exact: true })).toBeVisible();

	// Seeded as a loose .bin: it only shows up if ingestion accepted it and matched it to the Atari DAT (A3).
	await chooseFilter('System', 'a2600');
	await expect(page).toHaveURL(/system=a2600/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Laser Lanes', exact: true })).toBeVisible();

	// Seeded as a loose .rom: it only shows up if ingestion accepted it and matched it to the ColecoVision DAT (C4).
	await chooseFilter('System', 'coleco');
	await expect(page).toHaveURL(/system=coleco/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Pixel Pursuit', exact: true })).toBeVisible();

	// G4: the seeded .gg with a TMR SEGA header, matched to the Game Gear DAT.
	await chooseFilter('System', 'gg');
	await expect(page).toHaveURL(/system=gg/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Pocket Drift', exact: true })).toBeVisible();

	// M3: the seeded .sms with a TMR SEGA header, matched to the Master System DAT.
	await chooseFilter('System', 'sms');
	await expect(page).toHaveURL(/system=sms/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Harbor Patrol', exact: true })).toBeVisible();

	// D3: seeded as a loose .gen, matched to the Mega Drive DAT and stored as .md.
	await chooseFilter('System', 'md');
	await expect(page).toHaveURL(/system=md/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Turbo Comet', exact: true })).toBeVisible();

	// X4: the seeded .32x with a Mega Drive-style header, matched to the 32X DAT.
	await chooseFilter('System', 's32x');
	await expect(page).toHaveURL(/system=s32x/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Star Relay', exact: true })).toBeVisible();

	// T2: seeded as a copier-headered .pce, stripped and matched to the TurboGrafx-16 DAT.
	await chooseFilter('System', 'tg16');
	await expect(page).toHaveURL(/system=tg16/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Neon Circuit', exact: true })).toBeVisible();

	// "Quest, The" is shown as "The Quest" but filed under Q (§5, §6.1).
	await openLibrary();
	await chooseFilter('Letter', 'Q');
	await expect(page.getByRole('img', { name: 'The Quest box art' })).toBeVisible();

	await page.getByRole('button', { name: 'List view' }).click();
	await expect(page.getByRole('table')).toContainText('The Quest');
	await expectAccessible(page, 'library list');
	await page.getByRole('button', { name: 'Grid view' }).click();

	// L6/L7: a chip per filter; the filter survives a trip to a game and back; Reset clears it.
	await openLibrary();
	await chooseFilter('System', 'gb');
	await expect(page).toHaveURL(/system=gb/);
	await expect(page.getByTestId('active-filters')).toContainText('System: Game Boy (3)');
	await expect(page.getByRole('button', { name: 'System: Game Boy' })).toBeVisible();
	await page.getByRole('link', { name: 'Orbit Blocks', exact: true }).click();
	await expect(page.getByRole('heading', { level: 1, name: 'Orbit Blocks' })).toBeVisible();
	await page.getByRole('link', { name: '← Library' }).click();
	await expect(page).toHaveURL(/system=gb/);
	await expect(page.getByText('3 games', { exact: true })).toBeVisible();
	await page.getByRole('button', { name: 'Reset filters' }).click();
	await expect(page.getByText('23 games')).toBeVisible();
	await expect(page.getByRole('button', { name: 'Reset filters' })).toHaveCount(0);
});

test('game detail + downloads', async () => {
	await openGame('Orbit Blocks');
	await expect(page.getByText('Primary')).toBeVisible();
	await expectAccessible(page, 'game detail');

	// R1/R2: release cards start collapsed; Header details is in the body.
	const release = page.getByRole('article', { name: 'Orbit Blocks (World) (Rev 1).gb' });
	await expect(release.getByRole('button', { name: 'Copy CRC32' })).toBeHidden();
	await release.getByRole('button', { name: 'Show details for Orbit Blocks (World) (Rev 1).gb' }).click();
	await expect(release.getByText('CRC32')).toBeVisible();
	await expect(release.getByRole('button', { name: 'Copy CRC32' })).toBeVisible();
	await expect(release.getByText('Identified by')).toBeVisible();
	await release.getByRole('button', { name: 'Header details' }).click();
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

	await expect(page.getByRole('button', { name: 'Expand all' })).toBeVisible();
	await page.getByRole('button', { name: 'Expand all' }).click();
	await expect(page.getByRole('button', { name: 'Collapse all' })).toBeVisible();
	await expectAccessible(page, 'game detail with releases expanded');

	// Browser emulators B3–B5, B7, B10: play the primary release in the Gambatte core, as served by the image.
	const romDownload = page.waitForResponse((r) => /\/api\/games\/[^/]+\/roms\/\d+\/download$/.test(r.url()));
	await release.getByRole('button', { name: 'Play Orbit Blocks (World) (Rev 1).gb' }).click();
	await expect(dialog()).toHaveAccessibleName('Play Orbit Blocks (World) (Rev 1).gb');
	const emulator = dialog().locator('iframe');
	await expect(emulator).toHaveAttribute('title', 'Emulator: Orbit Blocks (World) (Rev 1).gb');
	expect((await romDownload).status()).toBe(200);
	const frame = page.frameLocator('iframe');
	await expect(frame.getByText('Start Game')).toBeVisible();
	// EmulatorJS's own UI may not pass axe, so the scan covers the modal chrome only.
	await expectAccessible(page, 'emulator modal', 'iframe');
	const core = page.waitForResponse((r) => /\/emulator\/vendor\/data\/cores\/gambatte(-legacy)?-wasm\.data$/.test(r.url()));
	await frame.getByText('Start Game').click();
	expect((await core).status()).toBe(200);
	// EmulatorJS removes .ejs_loading_text when the game starts; on a failure it stays, as .ejs_error_text.
	await expect(frame.locator('.ejs_loading_text')).toHaveCount(0, { timeout: 30_000 });
	await frame.locator('#game').click();
	await page.keyboard.press('Escape');
	await expect(dialog()).toBeVisible();
	await dialog().locator('.modal-footer').getByRole('button', { name: 'Close' }).click();
	await expect(dialog()).toBeHidden();
	await expect(page.locator('iframe')).toHaveCount(0);

	// Z1–Z4: the Zip Contents tab lists the ZIP and downloads one entry.
	await page.getByRole('tab', { name: 'Zip Contents' }).click();
	await expect(page).toHaveURL(/tab=zip/);
	const folders = page.getByRole('list', { name: 'Folders' });
	await expect(folders.getByRole('button', { name: 'Orbit Blocks.zip', exact: true })).toHaveAttribute('aria-current', 'true');
	await expect(page.getByRole('table', { name: 'Contents of Orbit Blocks.zip' })).toContainText('info.json');
	await expectAccessible(page, 'zip contents');
	await folders.getByRole('button', { name: 'roms', exact: true }).click();
	const contents = page.getByRole('table', { name: 'Contents of roms' });
	const romName = 'Orbit Blocks (World) (Rev 1).gb';
	await expect(contents).toContainText(romName);
	const gameId = new URL(page.url()).pathname.split('/')[2];
	const listing = await (await page.request.get(`/api/games/${gameId}/zip-entries`)).json();
	const entry = listing.entries.find((e: { path: string }) => e.path === `roms/${romName}`);
	const entryDownload = page.waitForEvent('download');
	await contents.getByRole('link', { name: `Download ${romName}` }).click();
	const downloaded = await entryDownload;
	expect(downloaded.suggestedFilename()).toBe(romName);
	const { size } = await stat(await downloaded.path());
	expect(size).toBe(entry.sizeBytes);
	await page.getByRole('tab', { name: /^Releases/ }).click();

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

	// G1: the information card wraps the longest system name instead of scrolling on a phone.
	await openGame('Mode Seven Rally');
	await page.setViewportSize({ width: 375, height: 812 });
	const info = page.locator('.card', {
		has: page.getByRole('table', { name: 'Game information' })
	});
	await expect(info).toContainText('Super Nintendo Entertainment System');
	expect(await info.evaluate((card) => card.scrollWidth <= card.clientWidth)).toBe(true);
	await page.setViewportSize(viewport);
});

test('Switch: external releases and a per-release download', async () => {
	await openLibrary();
	await chooseFilter('System', 'switch');
	await expect(page).toHaveURL(/system=switch/);
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await page.getByRole('link', { name: 'Orbit Expedition', exact: true }).click();
	await expect(page.getByRole('heading', { level: 1, name: 'Orbit Expedition' })).toBeVisible();

	// G7: base and DLC are each stored next to the ZIP; the two updates have their own tab (game updates B9).
	await expect(page.getByRole('tab', { name: 'Releases (2)' })).toHaveAttribute('aria-selected', 'true');
	await expect(page.getByText('External', { exact: true })).toHaveCount(2);
	await expectAccessible(page, 'Switch game detail');

	// G4: the primary (base) release first; the download is the original file, byte for byte. No updates (B10).
	await page.getByRole('button', { name: 'Download ROM' }).click();
	const items = page.locator('.dropdown-menu.show .dropdown-item');
	await expect(items).toHaveCount(2);
	const download = page.waitForEvent('download');
	await items.first().click();
	const file = await download;
	expect(file.suggestedFilename()).toBe('Orbit Expedition [0100000000010000][v0].nsp');
	expect((await readFile((await file.path())!)).equals(switchBase)).toBe(true);

	// B9–B11: the Updates tab lists both updates newest first; the [UPD] file is version 1.1.0.
	await page.getByRole('tab', { name: 'Updates (2)' }).click();
	await expect(page).toHaveURL(/tab=updates/);
	const updates = page.locator('#releases-panel article');
	await expect(updates).toHaveCount(2);
	await expect(updates.first()).toContainText('Version 1.1.0');
	await expect(updates.first()).toContainText('Orbit Expedition [0100000000010800][v0][1.1.0].nsp');
	await expect(updates.nth(1)).toContainText('[v65536]');
	await expectAccessible(page, 'Switch game updates');
	const updateDownload = page.waitForEvent('download');
	await updates
		.first()
		.getByRole('button', { name: 'Download Orbit Expedition [0100000000010800][v0][1.1.0].nsp' })
		.click();
	const updateFile = await updateDownload;
	expect(updateFile.suggestedFilename()).toBe('Orbit Expedition [0100000000010800][v0][1.1.0].nsp');
	expect((await readFile((await updateFile.path())!)).equals(switchUpdate)).toBe(true);
});

test('PlayStation: a disc set lists its files and downloads a track byte-exact', async () => {
	await openGame('Disc Runner');
	await expect(page.getByRole('heading', { level: 3, name: 'Disc Runner (USA) (Track 1).bin' })).toBeVisible();
	await page.getByRole('button', { name: 'Show details for Disc Runner (USA) (Track 1).bin' }).click();

	// B20: the toggle counts the data track, the cue and Track 2; the list puts the main file first.
	const toggle = page.getByRole('button', { name: '3 files' });
	await expect(toggle).toHaveAttribute('aria-expanded', 'false');
	await toggle.click();
	await expect(toggle).toHaveAttribute('aria-expanded', 'true');
	const rows = page.locator('#releases-panel table tbody tr');
	await expect(rows).toHaveCount(3);
	await expect(rows.first()).toContainText('Disc Runner (USA) (Track 1).bin');
	await expectAccessible(page, 'disc set file list');

	const download = page.waitForEvent('download');
	await page.getByRole('button', { name: 'Download Disc Runner (USA) (Track 2).bin' }).click();
	const file = await download;
	expect(file.suggestedFilename()).toBe('Disc Runner (USA) (Track 2).bin');
	expect((await readFile((await file.path())!)).equals(discTrack2)).toBe(true);
});

test('global search, ratings and favourites', async () => {
	// Ctrl+K from the library opens the search; part of a title finds it; Enter opens it.
	await openLibrary();
	await expect(page.getByText('23 games')).toBeVisible();
	await page.keyboard.press('Control+k');
	await expect(dialog()).toBeVisible();
	await expect(dialog().getByRole('combobox')).toBeFocused();
	await dialog().getByRole('combobox').fill('orbit');
	await expect(dialog().getByRole('option').first()).toContainText('Orbit Blocks');
	await expectAccessible(page, 'global search modal');
	await page.keyboard.press('Enter');
	await expect(page.getByRole('heading', { level: 1, name: 'Orbit Blocks' })).toBeVisible();

	// Rate and favourite it from its library card; both survive a reload.
	await openLibrary();
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
	await openLibrary();
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

test('collections: create, add from game page and bulk select, search, remove, delete', async () => {
	await page.goto('/collections');
	await expect(page.getByText('You have no collections yet.')).toBeVisible();
	await expectAccessible(page, 'collections page (empty)');

	// B23: create with Markdown notes and an uploaded cover, then land on the new collection.
	await page.getByRole('button', { name: 'New collection' }).first().click();
	await expect(dialog()).toHaveAccessibleName('New collection');
	await dialog().getByLabel('Name').fill('Puzzle picks');
	await dialog().getByLabel('Notes (Markdown)').fill('The **best** puzzles');
	await dialog()
		.getByLabel('Cover image file')
		.setInputFiles({ name: 'cover.png', mimeType: 'image/png', buffer: png(32, [40, 120, 200]) });
	await expectAccessible(page, 'collection modal');
	await dialog().getByRole('button', { name: 'Create' }).click();
	await expect(page).toHaveURL(/\/collections\/[0-9a-f-]{36}$/);
	await expect(page.getByRole('heading', { level: 1, name: 'Puzzle picks' })).toBeVisible();
	await expect(page.getByTestId('collection-notes').locator('strong')).toHaveText('best');
	await expect(page.getByRole('img', { name: 'Puzzle picks cover' })).toBeVisible();

	// B25: add Orbit Blocks from its game page; a badge links back.
	await openGame('Orbit Blocks');
	await page.getByRole('button', { name: 'Collections', exact: true }).click();
	await dialog().getByRole('checkbox', { name: /^Puzzle picks/ }).check();
	await expect(page.getByRole('list', { name: 'In collections' }).getByRole('link')).toHaveText(
		'Puzzle picks'
	);
	await expectAccessible(page, 'game collections modal');
	await page.keyboard.press('Escape');
	await expect(dialog()).toBeHidden();

	// B26: add a game from another system with the Library's bulk select.
	await openLibrary();
	await chooseFilter('System', 'n64');
	await expect(page).toHaveURL(/system=n64/);
	await page.getByRole('button', { name: 'Select', exact: true }).click();
	await page.getByRole('checkbox', { name: 'Select Orbit Kart 64' }).check();
	await page.getByRole('button', { name: /Add to collection/ }).click();
	await dialog().getByRole('radio', { name: /^Puzzle picks/ }).check();
	await dialog().getByRole('button', { name: 'Add', exact: true }).click();
	await expect(page.getByText('Added 1 game to Puzzle picks')).toBeVisible();

	// B27: global search finds the collection; Enter opens it.
	await page.keyboard.press('Control+k');
	await dialog().getByRole('combobox').fill('puzzle');
	await expect(dialog().getByRole('option').first()).toContainText('Puzzle picks');
	await page.keyboard.press('Enter');
	await expect(page.getByRole('heading', { level: 1, name: 'Puzzle picks' })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Orbit Blocks', exact: true })).toBeVisible();
	await expect(page.getByRole('link', { name: 'Orbit Kart 64', exact: true })).toBeVisible();
	await expectAccessible(page, 'collection page');

	// P1–P4: Add games leaves out members; results show a cover and a short system name.
	const addGames = page.getByLabel('Add games');
	await addGames.fill('orbit');
	const results = page.locator('.list-group').getByRole('button');
	// Orbit Expedition is suggested; both members stay excluded.
	await expect(results.filter({ hasText: 'Orbit Expedition' })).toBeVisible();
	await expect(results.filter({ hasText: 'Orbit Blocks' })).toHaveCount(0);
	await expect(results.filter({ hasText: 'Orbit Kart 64' })).toHaveCount(0);
	await addGames.fill('zzz no such game');
	await expect(page.getByText('No matching games.')).toBeVisible();
	await addGames.fill('pocket');
	const drift = results.filter({ hasText: 'Pocket Drift' });
	await expect(drift).toBeVisible();
	await expect(drift.locator('img')).toHaveCount(1);
	await expect(drift.locator('.badge')).toHaveText('GG');
	await expect(drift.locator('.badge')).toHaveAttribute('title', 'Game Gear');
	await addGames.fill('');

	// B24: remove one game.
	await page.getByRole('button', { name: 'Remove Orbit Blocks from collection' }).click();
	await expect(page.getByRole('link', { name: 'Orbit Blocks', exact: true })).toBeHidden();
	await expect(page.getByRole('link', { name: 'Orbit Kart 64', exact: true })).toBeVisible();

	await page.goto('/collections');
	await expect(page.getByRole('link', { name: 'Puzzle picks' })).toBeVisible();
	await expect(page.getByText('1 game', { exact: true })).toBeVisible();
	await expectAccessible(page, 'collections page');

	// B5: delete it; the games stay in the Library.
	await page.getByRole('link', { name: 'Puzzle picks' }).click();
	await page.getByRole('button', { name: 'Delete', exact: true }).click();
	await expect(dialog()).toHaveAccessibleName('Delete Puzzle picks?');
	await dialog().getByRole('button', { name: 'Delete', exact: true }).click();
	await expect(page).toHaveURL(/\/collections$/);
	await expect(page.getByText('You have no collections yet.')).toBeVisible();
	await openLibrary();
	await expect(page.getByText('23 games')).toBeVisible();
});

test('review queue: resolve with a suggestion', async () => {
	await page.goto('/review');
	await expectAccessible(page, 'review queue');
	await page.getByRole('link', { name: '91-unmatched.gb' }).click();
	// It has suggestions, so that tab is open first (W2).
	await expect(page.getByRole('tab', { name: 'Suggestions' })).toHaveAttribute('aria-selected', 'true');
	await expectAccessible(page, 'review detail');

	await page.getByRole('button', { name: 'Use' }).first().click();
	// B7: the resolution runs as a job; the page shows it until the first poll finds it done.
	await expect(page.getByText('Resolving 91-unmatched.gb…')).toBeVisible();
	await expect(page.getByRole('button', { name: /Delete/ })).toBeDisabled();
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
	await dialog().getByRole('button', { name: 'Cancel' }).click();
	await expect(dialog()).toBeHidden();

	// Q2–Q4: the same file through the bulk delete.
	await page.getByRole('checkbox', { name: 'Select 93-mystery.gb' }).check();
	await expect(page.getByText('1 selected')).toBeVisible();
	await page.getByRole('button', { name: 'Delete selected (1)' }).click();
	await expect(dialog()).toHaveAccessibleName('Delete 1 review file?');
	await expect(dialog()).toContainText('93-mystery.gb');
	await expectAccessible(page, 'review bulk delete modal');
	await dialog().getByRole('button', { name: 'Delete', exact: true }).click();
	await expect(dialog()).toBeHidden();
	await expect(page.getByText('Deleted 1 file.')).toBeVisible();
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
	// U6/I2: URL is the default source; a loopback URL is refused by the server; the fallback switches to the file input.
	await expect(dialog().getByLabel('URL')).toBeChecked();
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
	await dialog().getByText('File', { exact: true }).click();
	await dialog()
		.getByLabel('Upload an image')
		.setInputFiles({ name: 'shot.png', mimeType: 'image/png', buffer: png(32, [40, 40, 200]) });
	await dialog().getByText('Screenshot', { exact: true }).click();
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

	// Split the Japanese release off into its own game (§11 Move release → new game); Move is in the release body.
	await page.getByRole('button', { name: 'Show details for Orbit Blocks (Japan).gb' }).click();
	await page.getByRole('button', { name: 'Move release Orbit Blocks (Japan).gb', exact: true }).click();
	await dialog().getByLabel('A new game').check();
	await dialog().getByLabel('New game title').fill('Orbit Blocks Japan');
	await dialog().getByRole('button', { name: 'Move release' }).click();
	await expect(dialog()).toBeHidden();
	await expect(page.getByRole('button', { name: 'Move release Orbit Blocks (Japan).gb', exact: true })).toHaveCount(0);

	// Its only release goes to _removed/, so the game goes too and we land on the Library.
	await openGame('Orbit Blocks Japan');
	await page.getByRole('button', { name: 'Delete Orbit Blocks (Japan).gb', exact: true }).click();
	await dialog().getByRole('button', { name: 'Delete' }).click();
	await expect(page).toHaveURL(/\/(\?[^/]*)?$/);
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
	await expect(page).toHaveURL(/\/(\?[^/]*)?$/);
	await page.getByRole('searchbox', { name: 'Search', exact: true }).fill('Color Racer');
	await expect(page.getByText('No games match these filters.')).toBeVisible();
});

test('Deleted ROMs: restore one release, delete the rest for good', async () => {
	await page.goto('/deleted');
	await expect(page.getByRole('heading', { level: 1, name: 'Deleted ROMs' })).toBeVisible();
	await expect(page.getByRole('link', { name: /Deleted ROMs/ }).locator('.badge')).toBeVisible();
	await expect(page.getByText('Orbit Blocks (Japan).gb', { exact: true })).toBeVisible();
	await expectAccessible(page, 'deleted ROMs');

	// D5: back to Ingestion and imported by a job (a just-deleted file may wait there as too recent).
	await page.getByRole('button', { name: 'Restore Orbit Blocks (Japan).gb' }).click();
	await expect(page.getByText(/^Restored 1 file; importing\./)).toBeVisible();
	await page.getByRole('link', { name: 'View job' }).click();
	await expect(page).toHaveURL(/\/jobs\//);
	await page.goto('/deleted');
	await expect(page.getByRole('heading', { level: 1, name: 'Deleted ROMs' })).toBeVisible();
	await expect(page.getByText('Orbit Blocks (Japan).gb', { exact: true })).toHaveCount(0);

	// D4: the rest (Color Racer's ROMs) go permanently.
	await page.getByRole('button', { name: 'Select all' }).click();
	await page.getByRole('button', { name: /^Delete selected/ }).click();
	await expect(dialog()).toHaveAccessibleName(/^Permanently delete \d+ files?\?$/);
	await expectAccessible(page, 'permanent delete modal');
	await dialog().getByRole('button', { name: 'Delete', exact: true }).click();
	await expect(page.getByText('No deleted ROMs')).toBeVisible();
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
	await openLibrary();
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
