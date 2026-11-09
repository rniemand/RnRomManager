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

	it('an admin sees Library, Ingestion, Review, Reports, Integrity, Admin, in that order (B4)', () => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin', 'rom-manager'], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const links = [...document.querySelectorAll('a.nav-link')].map((a) => a.textContent?.trim());
		expect(links).toEqual(['Library', 'Ingestion', 'Review', 'Reports', 'Integrity', 'Admin']);
	});

	it('a non-admin sees Library and Reports, in that order (RP1)', () => {
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const links = [...document.querySelectorAll('a.nav-link')].map((a) => a.textContent?.trim());
		expect(links).toEqual(['Library', 'Reports']);
		expect(screen.getByRole('link', { name: /Reports/ }).getAttribute('href')).toBe('/reports');
	});

	it('the Search button has no Ctrl K hint (G2)', () => {
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {},
			onsearch: () => {}
		});
		expect(screen.getByRole('button', { name: /Search/ }).textContent?.trim()).toBe('Search');
		expect(document.querySelector('kbd')).toBeNull();
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

	it('is sticky at the top (B26)', () => {
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		expect(document.querySelector('nav')!.classList.contains('sticky-top')).toBe(true);
	});

	it('the (?) button opens the shortcuts, before the theme toggle (B25)', async () => {
		const onhotkeys = vi.fn();
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {},
			onhotkeys
		});

		const help = screen.getByRole('button', { name: 'Keyboard shortcuts' });
		expect(help.querySelector('i')!.className).toContain('bi-question-circle');
		const items = [...document.querySelectorAll('ul.navbar-nav.align-items-lg-center > li')];
		const helpIndex = items.findIndex((li) => li.contains(help));
		expect(items[helpIndex + 1].querySelector('button')!.getAttribute('aria-label')).toMatch(
			/theme/i
		);
		help.click();
		expect(onhotkeys).toHaveBeenCalledOnce();
	});

	it('the (?) button is hidden while the password must be changed', () => {
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: true },
			reviewCount: null,
			onlogout: () => {},
			onhotkeys: () => {}
		});
		expect(screen.queryByRole('button', { name: 'Keyboard shortcuts' })).toBeNull();
	});
});
