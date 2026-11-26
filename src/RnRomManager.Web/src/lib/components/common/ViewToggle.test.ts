import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import ViewToggle from './ViewToggle.svelte';

describe('ViewToggle', () => {
	it('marks the current view pressed and active', () => {
		render(ViewToggle, { view: 'list', onchange: () => {} });
		expect(screen.getByRole('group', { name: 'View' }).className).toBe('btn-group');
		const grid = screen.getByRole('button', { name: 'Grid view' });
		const list = screen.getByRole('button', { name: 'List view' });
		expect(grid.className).toBe('btn btn-outline-secondary');
		expect(list.className).toBe('btn btn-outline-secondary active');
		expect([grid.getAttribute('aria-pressed'), list.getAttribute('aria-pressed')]).toEqual([
			'false',
			'true'
		]);
	});

	it('size="sm" adds btn-sm before the variant', () => {
		render(ViewToggle, { view: 'grid', onchange: () => {}, size: 'sm' });
		expect(screen.getByRole('button', { name: 'Grid view' }).className).toBe(
			'btn btn-sm btn-outline-secondary active'
		);
	});

	it('reports the chosen view', async () => {
		const onchange = vi.fn();
		render(ViewToggle, { view: 'grid', onchange });
		await fireEvent.click(screen.getByRole('button', { name: 'List view' }));
		expect(onchange).toHaveBeenCalledWith('list');
	});
});
