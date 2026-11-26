import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { RomStorageRow } from '#lib/types/index.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: StorageMigrationModal } = await import('./StorageMigrationModal.svelte');

const row = (embedRoms: boolean) => ({ displayName: 'Game Boy', embedRoms }) as RomStorageRow;

describe('StorageMigrationModal', () => {
	it.each([
		[true, 'Store beside ZIP: Game Boy', 'Each ROM becomes a separate file'],
		[false, 'Store in ZIP: Game Boy', 'saves get slower']
	])('embedRoms %s → "%s"', (embed, title, text) => {
		render(StorageMigrationModal, { open: true, row: row(embed), onconfirm: () => {} });
		expect(screen.getByRole('heading', { name: title, hidden: true })).toBeTruthy();
		expect(document.querySelector('.modal-body')?.textContent).toContain(text);
	});

	it('Start moving calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		let open = true;
		render(StorageMigrationModal, {
			row: row(false),
			onconfirm,
			get open() {
				return open;
			},
			set open(v) {
				open = v;
			}
		});
		const start = screen.getByRole('button', { name: 'Start moving', hidden: true });
		expect(start.classList.contains('btn-warning')).toBe(true);
		await fireEvent.click(start);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(open).toBe(false);
	});
});
