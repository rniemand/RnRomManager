import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

vi.mock('$app/state', () => ({ page: { url: new URL('http://localhost/review') } }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: Navbar } = await import('./Navbar.svelte');

describe('Navbar', () => {
	it('marks the active link with aria-current and the accent (active) class', () => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin', 'rom-manager'], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});

		const review = screen.getByRole('link', { name: /Review/ });
		expect(review.getAttribute('aria-current')).toBe('page');
		expect(review.classList.contains('active')).toBe(true);
		const library = screen.getByRole('link', { name: /Library/ });
		expect(library.getAttribute('aria-current')).toBeNull();
		expect(library.classList.contains('active')).toBe(false);
	});

	it('the brand is the icon only, named for screen readers (N1)', () => {
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});

		const brand = screen.getByRole('link', { name: 'RnRomManager home' });
		expect(brand.textContent?.trim()).toBe('');
	});
});
