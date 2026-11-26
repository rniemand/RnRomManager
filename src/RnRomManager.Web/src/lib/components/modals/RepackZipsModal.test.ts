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

const { default: RepackZipsModal } = await import('./RepackZipsModal.svelte');

describe('RepackZipsModal', () => {
	it('warns about the repack; Repack again calls onconfirm; Cancel closes', async () => {
		const onconfirm = vi.fn();
		let open = true;
		render(RepackZipsModal, {
			row: { displayName: 'NES' } as RomStorageRow,
			onconfirm,
			get open() {
				return open;
			},
			set open(v) {
				open = v;
			}
		});
		expect(screen.getByRole('heading', { name: 'Repack NES ZIPs', hidden: true })).toBeTruthy();
		const paragraphs = document.querySelectorAll('.modal-body p');
		expect(paragraphs).toHaveLength(3);
		expect(paragraphs[0].textContent).toContain('Every game ZIP of NES is rewritten');
		await fireEvent.click(screen.getByRole('button', { name: 'Repack again', hidden: true }));
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(open).toBe(false);
	});
});
