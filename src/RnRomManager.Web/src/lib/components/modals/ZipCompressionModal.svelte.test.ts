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

const { default: ZipCompressionModal } = await import('./ZipCompressionModal.svelte');

type Level = RomStorageRow['zipCompression'];
const row = { displayName: 'Game Boy', zipCompression: 'Med' } as RomStorageRow;
const levels: { value: Level; label: string }[] = [
	{ value: 'Store', label: 'Store' },
	{ value: 'Med', label: 'Medium' },
	{ value: 'High', label: 'High' }
];

describe('ZipCompressionModal', () => {
	it('confirm is disabled until the level changes, then calls onconfirm with it bound', async () => {
		const onconfirm = vi.fn();
		const state = $state({ level: 'Med' as Level, open: true });
		render(ZipCompressionModal, {
			row,
			levels,
			onconfirm,
			get level() {
				return state.level;
			},
			set level(v) {
				state.level = v;
			},
			get open() {
				return state.open;
			},
			set open(v) {
				state.open = v;
			}
		});
		expect(
			screen.getByRole('heading', { name: 'ZIP compression: Game Boy', hidden: true })
		).toBeTruthy();
		expect(document.querySelector('.modal-body')?.textContent).toContain(
			'Every game ZIP of Game Boy is rewritten'
		);
		const confirm = screen.getByRole('button', { name: 'Change and repack', hidden: true });
		expect((confirm as HTMLButtonElement).disabled).toBe(true);
		const select = screen.getByLabelText('Compression level') as HTMLSelectElement;
		expect(select.className).toBe('form-select');
		select.value = 'High';
		await fireEvent.change(select);
		expect(state.level).toBe('High');
		expect((confirm as HTMLButtonElement).disabled).toBe(false);
		await fireEvent.click(confirm);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(state.open).toBe(false);
	});
});
