import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import SortHeader from './SortHeader.svelte';

const columns = [
	{ label: 'Game', sort: 'name' },
	{ label: 'Size', sort: 'size', class: 'text-end' },
	{ label: 'Developer', sort: null }
] as const;

function cells(sort: string | null, dir: 'asc' | 'desc', onsort = vi.fn()) {
	const table = document.createElement('table');
	const row = table
		.appendChild(document.createElement('thead'))
		.appendChild(document.createElement('tr'));
	document.body.appendChild(table);
	render(SortHeader, { target: row, props: { columns, sort, dir, onsort } });
	return { ths: [...row.querySelectorAll('th')], onsort };
}

describe('SortHeader', () => {
	it('renders one th per column with its class and aria-sort on the active one', () => {
		const { ths } = cells('size', 'desc');
		expect(ths).toHaveLength(3);
		expect(ths.every((th) => th.getAttribute('scope') === 'col')).toBe(true);
		expect(ths.map((th) => th.getAttribute('aria-sort'))).toEqual([null, 'descending', null]);
		expect(ths[1].className).toBe('text-end');
		expect(ths[0].hasAttribute('class')).toBe(false);
	});

	it('sortable columns hold a link button with the direction icon on the active one', () => {
		const { ths } = cells('name', 'asc');
		const button = ths[0].querySelector('button')!;
		expect(button.className).toBe('btn btn-link p-0 text-reset text-decoration-none fw-semibold');
		expect(button.querySelector('i')).not.toBeNull();
		expect(ths[1].querySelector('button i')).toBeNull();
		expect(ths[0].getAttribute('aria-sort')).toBe('ascending');
	});

	it('a sort: null column is plain text', () => {
		const { ths } = cells('name', 'asc');
		expect(ths[2].querySelector('button')).toBeNull();
		expect(ths[2].textContent?.trim()).toBe('Developer');
	});

	it('clicking a column reports its key', async () => {
		const { onsort } = cells('name', 'asc');
		await fireEvent.click(screen.getByRole('button', { name: 'Size' }));
		expect(onsort).toHaveBeenCalledWith('size');
	});
});
