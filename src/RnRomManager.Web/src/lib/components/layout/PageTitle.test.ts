import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import PageTitle from './PageTitle.svelte';

describe('PageTitle (B1)', () => {
	it('is the h1 with its icon and text, marked to hide from lg up', () => {
		render(PageTitle, { icon: 'library', text: 'Library' });

		const heading = screen.getByRole('heading', { level: 1, name: 'Library' });
		expect(heading.className).toBe('h3 page-title mb-3');
		expect(heading.querySelector('i')!.className).toContain('bi-');
	});

	it('takes other spacing', () => {
		render(PageTitle, { icon: 'collections', text: 'Collections', class: 'mb-0' });

		expect(screen.getByRole('heading', { level: 1 }).className).toBe('h3 page-title mb-0');
	});
});
