import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import Pagination from './Pagination.svelte';
import SystemBadge from './SystemBadge.svelte';

describe('Pagination', () => {
	it('shows the page and steps within bounds', async () => {
		const onpage = vi.fn();
		render(Pagination, { page: 1, pages: 3, onpage });

		expect(screen.getByText('Page 1 of 3')).toBeTruthy();
		expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Previous' }).disabled).toBe(true);
		await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
		expect(onpage).toHaveBeenCalledWith(2);
	});

	it('disables Next on the last page', () => {
		render(Pagination, { page: 3, pages: 3, onpage: () => {} });
		expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Next' }).disabled).toBe(true);
	});

	it('is hidden with one page and no selector', () => {
		render(Pagination, { page: 1, pages: 1, onpage: () => {} });
		expect(screen.queryByRole('navigation')).toBeNull();
	});

	it('the selector shows on one page and emits the chosen size', async () => {
		const onpagesize = vi.fn();
		render(Pagination, { page: 1, pages: 1, onpage: () => {}, pageSize: 20, onpagesize });

		expect(screen.queryByText(/Page 1 of/)).toBeNull();
		const select = screen.getByLabelText<HTMLSelectElement>('Rows per page');
		expect([...select.options].map((o) => o.value)).toEqual(['10', '20', '50', '100']);
		expect(select.value).toBe('20');
		await fireEvent.change(select, { target: { value: '50' } });
		expect(onpagesize).toHaveBeenCalledWith(50);
	});
});

describe('SystemBadge', () => {
	it('uses the system colour and name, neutral for an unknown system', () => {
		render(SystemBadge, { system: 'gba', name: 'Game Boy Advance' });
		render(SystemBadge, { system: 'nes' });
		render(SystemBadge, { system: 'snes' });
		render(SystemBadge, { system: 'a2600' });
		render(SystemBadge, { system: 'coleco' });
		render(SystemBadge, { system: 'gg' });
		render(SystemBadge, { system: 'sms' });
		render(SystemBadge, { system: 'md' });
		render(SystemBadge, { system: 'sg1000' });

		expect(screen.getByText('Game Boy Advance').classList.contains('text-bg-primary')).toBe(true);
		expect(screen.getByText('NES').classList.contains('text-bg-warning')).toBe(true);
		expect(screen.getByText('SNES').classList.contains('text-bg-snes')).toBe(true);
		expect(screen.getByText('A2600').classList.contains('text-bg-a2600')).toBe(true);
		expect(screen.getByText('COLECO').classList.contains('text-bg-coleco')).toBe(true);
		expect(screen.getByText('GG').classList.contains('text-bg-gg')).toBe(true);
		expect(screen.getByText('SMS').classList.contains('text-bg-sms')).toBe(true);
		expect(screen.getByText('MD').classList.contains('text-bg-md')).toBe(true);
		expect(screen.getByText('SG1000').classList.contains('text-bg-secondary')).toBe(true);
	});
});
