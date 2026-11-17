import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));

const { default: AboutModal } = await import('./AboutModal.svelte');

const about = { version: '1.0', licenses: [{ name: 'Svelte', license: 'MIT', url: 'https://x' }] };
const hidden = { hidden: true };

describe('AboutModal (B12)', () => {
	it('shows the version, GitHub link, attribution and the licences table in the standard style', () => {
		render(AboutModal, { open: true, about });

		expect(screen.getByText('1.0')).toBeTruthy();
		expect(
			screen
				.getByRole('link', { name: /rniemand\/RnRomManager on GitHub/, ...hidden })
				.getAttribute('href')
		).toBe('https://github.com/rniemand/RnRomManager');
		expect(screen.getByRole('heading', { name: 'Attribution', ...hidden })).toBeTruthy();
		expect(screen.getByRole('link', { name: 'Svelte', ...hidden }).getAttribute('href')).toBe(
			'https://x'
		);
		const table = document.querySelector('table')!;
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(table.closest('.table-responsive')?.parentElement?.classList.contains('card')).toBe(
			true
		);
		expect(table.querySelector('thead tr')?.className).toBe(
			'small text-uppercase text-body-secondary'
		);
	});

	it('is an info modal: only Close, no confirm button', () => {
		render(AboutModal, { open: true, about });

		expect(screen.queryByRole('button', { name: 'Confirm', ...hidden })).toBeNull();
		expect(screen.getAllByRole('button', { name: 'Close', ...hidden }).length).toBeGreaterThan(0);
		expect(document.querySelector('.modal-dialog')!.classList.contains('modal-lg')).toBe(true);
	});

	it('shows loading without the data, and a failure with a retry', async () => {
		const { unmount } = render(AboutModal, { open: true, about: null });
		expect(document.querySelector('table')).toBeNull();
		unmount();

		const onretry = vi.fn();
		render(AboutModal, {
			open: true,
			about: null,
			error: new Error('down'),
			onretry
		});
		await fireEvent.click(screen.getByRole('button', { name: /Retry/, ...hidden }));
		expect(onretry).toHaveBeenCalledOnce();
	});
});
