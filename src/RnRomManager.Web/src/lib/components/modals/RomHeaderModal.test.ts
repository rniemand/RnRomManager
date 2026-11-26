import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { RomDetail } from '#lib/types/index.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: RomHeaderModal } = await import('./RomHeaderModal.svelte');
/** `extra` plus a bindable `open` (a spread would copy the getter's value and drop the setter). */
function bound<T extends object>(extra: T) {
	let open = true;
	const props = Object.defineProperty({ ...extra }, 'open', {
		get: () => open,
		set: (v: boolean) => (open = v),
		enumerable: true,
		configurable: true
	}) as T & { open: boolean };
	return { props, isOpen: () => open };
}

describe('RomHeaderModal', () => {
	it('lays the header out two pairs per row, padding an odd last pair', async () => {
		const { props, isOpen } = bound({
			rom: {
				fileName: 'a.gb',
				header: { title: 'X', cgb: true, region: 'J' }
			} as unknown as RomDetail
		});
		render(RomHeaderModal, props);
		expect(screen.getByRole('heading', { name: 'Header — a.gb', hidden: true })).toBeTruthy();
		const rows = document.querySelectorAll('.modal-body tbody tr');
		expect(rows).toHaveLength(2);
		expect(rows[0].querySelectorAll('th')).toHaveLength(2);
		expect(rows[1].querySelector('td[colspan="2"]')).not.toBeNull();
		await fireEvent.click(screen.getAllByRole('button', { name: 'Close', hidden: true }).at(-1)!);
		expect(isOpen()).toBe(false);
	});
});
