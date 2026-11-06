import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import JsonTree from './JsonTree.svelte';

const value = {
	title: 'Tetris',
	year: 1989,
	verified: true,
	notes: null,
	roms: [{ name: 'a.gb', regions: ['World'] }]
};

const summary = (text: string) =>
	[...document.querySelectorAll('summary')].find((s) => s.textContent?.startsWith(text))!;

describe('JsonTree', () => {
	it('renders nested objects and arrays; depth 0 and 1 start open, deeper ones closed with a count', () => {
		render(JsonTree, { value });

		const root = document.querySelector('details')!;
		expect(root.open).toBe(true);
		const roms = summary('roms').closest('details')!;
		expect(roms.open).toBe(true);
		const first = summary('0').closest('details')!;
		expect(first.open).toBe(false);
		expect(summary('0').textContent).toBe('0: {2}');
		expect(summary('roms').textContent).toBe('roms: ');
	});

	it('uses one Bootstrap emphasis class per type', () => {
		render(JsonTree, { value });

		expect(screen.getByText('"Tetris"').className).toBe('text-success-emphasis');
		expect(screen.getByText('1989').className).toBe('text-warning-emphasis');
		expect(screen.getByText('true').className).toBe('text-danger-emphasis');
		expect(screen.getByText('null').className).toBe('text-danger-emphasis');
		expect(screen.getByText('title').className).toBe('text-primary-emphasis');
		expect(document.querySelector('.font-monospace')).toBeTruthy();
	});

	it('Expand all opens every node and Collapse all closes them', async () => {
		render(JsonTree, { value });
		const all = () => [...document.querySelectorAll('details')].map((d) => d.open);

		await fireEvent.click(screen.getByRole('button', { name: 'Expand all' }));
		expect(all().every(Boolean)).toBe(true);

		await fireEvent.click(screen.getByRole('button', { name: 'Collapse all' }));
		expect(all().some(Boolean)).toBe(false);
	});
});
