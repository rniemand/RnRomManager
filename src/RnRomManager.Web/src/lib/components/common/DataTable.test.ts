import { describe, expect, it } from 'vitest';
import { createRawSnippet } from 'svelte';
import { render, screen } from '@testing-library/svelte';
import DataTable from './DataTable.svelte';

const head = createRawSnippet(() => ({ render: () => '<tr><th scope="col">Name</th></tr>' }));
const row = createRawSnippet((item: () => unknown) => ({
	render: () => `<tr><td>${String(item())}</td></tr>`
}));

describe('DataTable', () => {
	it('renders the card, the responsive wrapper, the table and its dark head', () => {
		const { container } = render(DataTable, {
			items: ['a', 'b'],
			head,
			row,
			class: 'mb-3',
			tableClass: 'clickable-rows',
			'aria-label': 'Things'
		});
		const card = container.firstElementChild!;
		expect(card.className).toBe('card mb-3');
		expect(card.firstElementChild!.className).toBe('table-responsive');
		const table = screen.getByRole('table', { name: 'Things' });
		expect(table.className).toBe(
			'table table-sm table-striped table-hover align-middle mb-0 clickable-rows'
		);
		expect(table.querySelector('thead')!.className).toBe('table-dark');
		expect([...table.querySelectorAll('tbody td')].map((td) => td.textContent)).toEqual(['a', 'b']);
	});

	it('default classes have no extras', () => {
		const { container } = render(DataTable, { items: [], head, row });
		expect(container.firstElementChild!.className).toBe('card');
		expect(container.querySelector('table')!.className).toBe(
			'table table-sm table-striped table-hover align-middle mb-0'
		);
	});

	it('empty replaces the card with EmptyState when there are no items', () => {
		const { container } = render(DataTable, { items: [], head, row, empty: 'Nothing here.' });
		expect(container.querySelector('.card')).toBeNull();
		expect(screen.getByText('Nothing here.')).toBeTruthy();
	});
});
