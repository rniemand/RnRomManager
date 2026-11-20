import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import { navCounts } from '../stores/navCounts.svelte';

const page = vi.hoisted(() => ({ url: new URL('http://localhost/review') }));
vi.mock('$app/state', () => ({ page }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: RomToolsTabs } = await import('./RomToolsTabs.svelte');

describe('RomToolsTabs', () => {
	beforeEach(() => {
		page.url = new URL('http://localhost/review');
		navCounts.reset();
	});

	it('lists the tools in order and marks the current page', () => {
		render(RomToolsTabs);

		const tabs = screen.getAllByRole('link');
		expect(tabs.map((a) => [a.textContent?.trim(), a.getAttribute('href')])).toEqual([
			['Ingestion', '/ingestion'],
			['Review', '/review'],
			['Deleted ROMs', '/deleted'],
			['Integrity', '/integrity']
		]);
		expect(tabs.map((a) => a.getAttribute('aria-current'))).toEqual([null, 'page', null, null]);
		expect(screen.getByRole('navigation', { name: 'ROM Tools' })).toBeTruthy();
	});

	it('each tab has its icon before the label (release editing B32)', () => {
		render(RomToolsTabs);

		expect(
			screen
				.getAllByRole('link')
				.map((a) => [a.firstElementChild!.className.trim(), a.textContent?.trim()])
		).toEqual([
			['bi bi-box-arrow-in-down', 'Ingestion'],
			['bi bi-clipboard-check', 'Review'],
			['bi bi-trash3', 'Deleted ROMs'],
			['bi bi-shield-check', 'Integrity']
		]);
	});

	it('badges show only when the count is > 0, both formatted (D6)', () => {
		navCounts.review = 1250;
		navCounts.deleted = 3400;
		render(RomToolsTabs);

		const review = screen.getByRole('link', { name: /Review/ }).querySelector('.badge')!;
		expect(review.className).toBe('badge rounded-pill text-bg-danger');
		expect(review.textContent).toBe('1,250 pending');
		const deleted = screen.getByRole('link', { name: /Deleted ROMs/ }).querySelector('.badge')!;
		expect(deleted.className).toBe('badge rounded-pill text-bg-secondary');
		expect(deleted.textContent).toBe('3,400 deleted');
	});

	it.each([0, null])('no badges when the counts are %s', (count) => {
		navCounts.review = count;
		navCounts.deleted = count;
		render(RomToolsTabs);

		expect(document.querySelector('.badge')).toBeNull();
	});
});
