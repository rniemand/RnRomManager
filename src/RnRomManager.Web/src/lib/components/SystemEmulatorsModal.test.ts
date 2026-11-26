import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/svelte';
import type { SystemInfo } from '#lib/types/index.js';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: SystemEmulatorsModal } = await import('./SystemEmulatorsModal.svelte');

const system = (id: string, displayName: string): SystemInfo => ({
	id,
	displayName,
	shortName: id.toUpperCase(),
	gameCount: 0,
	romCount: 0,
	regions: [],
	logoUrl: null
});
const hidden = { hidden: true };

describe('SystemEmulatorsModal (Systems tab B7, B8)', () => {
	it('titles the dialog and labels it by the title', () => {
		const { container } = render(SystemEmulatorsModal, {
			open: true,
			system: system('gb', 'Game Boy')
		});
		const title = screen.getByRole('heading', { name: 'Game Boy Emulators', ...hidden });
		expect(container.querySelector('.modal')?.getAttribute('aria-labelledby')).toBe(title.id);
		expect(container.querySelector('.modal-dialog')?.classList.contains('modal-lg')).toBe(true);
	});

	it('shows only non-empty OS groups, in order, each a labelled list', () => {
		render(SystemEmulatorsModal, { open: true, system: system('x360', 'Xbox 360') });
		expect(
			screen.getAllByRole('heading', { level: 3, ...hidden }).map((h) => h.textContent?.trim())
		).toEqual(['Windows', 'Linux']);
		expect(screen.getAllByRole('list', hidden).map((l) => l.getAttribute('aria-label'))).toEqual([
			'Windows',
			'Linux'
		]);
	});

	it('links each emulator to its site in a new tab, with its note and host', () => {
		render(SystemEmulatorsModal, { open: true, system: system('gb', 'Game Boy') });
		const windows = screen.getByRole('list', { name: 'Windows', ...hidden });
		const mgba = within(windows).getByRole('link', {
			name: 'mGBA (opens in a new tab)',
			...hidden
		});
		expect(mgba.getAttribute('href')).toBe('https://mgba.io/downloads.html');
		expect(mgba.getAttribute('target')).toBe('_blank');
		expect(mgba.getAttribute('rel')).toBe('noopener noreferrer');
		expect(within(windows).getByText('mgba.io')).toBeTruthy();
		expect(within(windows).getByText('RetroArch core: Gambatte')).toBeTruthy();
		expect(within(windows).getByText('www.retroarch.com')).toBeTruthy();
		expect(
			within(screen.getByRole('list', { name: 'iOS', ...hidden }))
				.getAllByRole('link', hidden)
				.map((a) => a.textContent?.replace(/\s+/g, ' ').trim())
		).toEqual([
			'SameBoy (opens in a new tab)',
			'Delta (opens in a new tab)',
			'RetroArch (opens in a new tab)'
		]);
	});

	it('says when no emulators are known (B8)', () => {
		render(SystemEmulatorsModal, { open: true, system: system('zzz', 'Mystery') });
		expect(screen.getByText('There are no known emulators for Mystery.')).toBeTruthy();
		expect(screen.queryAllByRole('list', hidden)).toEqual([]);
	});

	it('ends with the links note', () => {
		render(SystemEmulatorsModal, { open: true, system: system('gb', 'Game Boy') });
		const body = document.querySelector('.modal-body')!;
		expect(body.lastElementChild?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
			"Links go to each project's own site. RnRomManager doesn't host or check these downloads."
		);
	});

	it('Close sets open to false', async () => {
		let open = true;
		render(SystemEmulatorsModal, {
			get open() {
				return open;
			},
			set open(value) {
				open = value;
			},
			system: system('gb', 'Game Boy')
		});
		// The footer's Close (the × shares its name).
		await fireEvent.click(screen.getAllByRole('button', { name: 'Close', ...hidden }).at(-1)!);
		expect(open).toBe(false);
	});
});
