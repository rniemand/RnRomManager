import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { BackupFile } from '#lib/types/index.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: RestoreBackupModal } = await import('./RestoreBackupModal.svelte');

const backup = { fileName: 'rnrom-2026.sql.gz', createdAt: '2026-11-01T10:00:00Z' } as BackupFile;

describe('RestoreBackupModal', () => {
	it('names the backup and shows the error', () => {
		render(RestoreBackupModal, {
			open: true,
			backup,
			error: { title: 'Restore failed', detail: 'Bad file' },
			onconfirm: () => {}
		});
		const body = document.querySelector('.modal-body')!;
		expect(body.querySelector('strong.font-monospace')?.textContent).toBe('rnrom-2026.sql.gz');
		expect(screen.getByRole('alert', { hidden: true }).textContent).toContain('Restore failed');
		expect(screen.getByRole('alert', { hidden: true }).textContent).toContain('Bad file');
	});

	it('Restore calls onconfirm; busy disables it; Cancel closes', async () => {
		const onconfirm = vi.fn();
		let open = true;
		const { rerender } = render(RestoreBackupModal, {
			backup,
			onconfirm,
			get open() {
				return open;
			},
			set open(v) {
				open = v;
			}
		});
		const restore = screen.getByRole('button', { name: 'Restore', hidden: true });
		expect(restore.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(restore);
		expect(onconfirm).toHaveBeenCalledOnce();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(open).toBe(false);
		await rerender({ busy: true });
		expect((restore as HTMLButtonElement).disabled).toBe(true);
	});
});
