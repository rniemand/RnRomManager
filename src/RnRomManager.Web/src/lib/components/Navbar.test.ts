import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';

const page = vi.hoisted(() => ({ url: new URL('http://localhost/review') }));
vi.mock('$app/state', () => ({ page }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: Navbar } = await import('./Navbar.svelte');

describe('Navbar', () => {
	beforeEach(() => {
		page.url = new URL('http://localhost/review');
	});

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

	it('an admin sees Library, Ingestion, Review, Deleted ROMs, Reports, Integrity, Admin, in that order (B4, D6)', () => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin', 'rom-manager'], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const links = [...document.querySelectorAll('a.nav-link')].map((a) => a.textContent?.trim());
		expect(links).toEqual([
			'Library',
			'Collections',
			'Ingestion',
			'Review',
			'Deleted ROMs',
			'Reports',
			'Integrity',
			'Admin'
		]);
		const deleted = screen.getByRole('link', { name: /Deleted ROMs/ });
		expect(deleted.getAttribute('href')).toBe('/deleted');
		expect(deleted.querySelector('.bi-trash')).toBeTruthy();
	});

	it('badges show only when the count is > 0, both formatted (D6)', () => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
			reviewCount: 1250,
			deletedCount: 3400,
			onlogout: () => {}
		});

		const review = screen.getByRole('link', { name: /Review/ }).querySelector('.badge')!;
		expect(review.className).toBe('badge rounded-pill text-bg-danger');
		expect(review.textContent).toBe('1,250 pending');
		const deleted = screen.getByRole('link', { name: /Deleted ROMs/ }).querySelector('.badge')!;
		expect(deleted.className).toBe('badge rounded-pill text-bg-secondary');
		expect(deleted.textContent).toBe('3,400 deleted');
		expect(deleted.querySelector('.visually-hidden')!.textContent).toBe(' deleted');
	});

	it.each([0, null])('no Deleted ROMs badge when the count is %s', (deletedCount) => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
			reviewCount: null,
			deletedCount,
			onlogout: () => {}
		});

		expect(screen.getByRole('link', { name: /Deleted ROMs/ }).querySelector('.badge')).toBeNull();
	});

	it('a non-admin sees Library and Reports, in that order (RP1)', () => {
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const links = [...document.querySelectorAll('a.nav-link')].map((a) => a.textContent?.trim());
		expect(links).toEqual(['Library', 'Collections', 'Reports']);
		expect(screen.getByRole('link', { name: /Reports/ }).getAttribute('href')).toBe('/reports');
		expect(screen.getByRole('link', { name: /Collections/ }).getAttribute('href')).toBe(
			'/collections'
		);
	});

	it.each(['/collections', '/collections/abc'])('Collections is active on %s (B20)', (path) => {
		page.url = new URL(`http://localhost${path}`);
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const link = screen.getByRole('link', { name: /Collections/ });
		expect(link.getAttribute('aria-current')).toBe('page');
		expect(link.querySelector('i')?.className).toContain('bi-bookmarks');
		expect(screen.getByRole('link', { name: /Library/ }).getAttribute('aria-current')).toBeNull();
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
