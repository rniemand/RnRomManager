import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { Me } from '../api/types';

const page = vi.hoisted(() => ({ url: new URL('http://localhost/review') }));
vi.mock('$app/state', () => ({ page }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));
const nav = vi.hoisted(() => ({
	after: [] as (() => void)[],
	hides: [] as Element[]
}));
vi.mock('$app/navigation', () => ({ afterNavigate: (fn: () => void) => nav.after.push(fn) }));
vi.mock('../bootstrap', () => ({
	Collapse: {
		getOrCreateInstance: (el: Element, options: unknown) => {
			expect(options).toEqual({ toggle: false });
			return { hide: () => nav.hides.push(el) };
		}
	}
}));

const { default: Navbar } = await import('./Navbar.svelte');

describe('Navbar', () => {
	beforeEach(() => {
		page.url = new URL('http://localhost/review');
	});

	it.each(['/ingestion', '/review', '/review/7', '/deleted', '/integrity'])(
		'ROM Tools is the active link on %s',
		(path) => {
			page.url = new URL(`http://localhost${path}`);
			render(Navbar, {
				me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
				reviewCount: null,
				onlogout: () => {}
			});

			const tools = screen.getByRole('link', { name: 'ROM Tools' });
			expect(tools.getAttribute('aria-current')).toBe('page');
			expect(tools.classList.contains('active')).toBe(true);
			const library = screen.getByRole('link', { name: /Library/ });
			expect(library.getAttribute('aria-current')).toBeNull();
			expect(library.classList.contains('active')).toBe(false);
		}
	);

	it('ROM Tools is a plain link to Ingestion for admins, with no dropdown', () => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const links = [...document.querySelectorAll('ul.me-auto a.nav-link')].map((a) =>
			a.textContent?.trim()
		);
		expect(links).toEqual(['Library', 'Collections', 'Timeline', 'Reports', 'ROM Tools']);
		expect(screen.getByRole('link', { name: 'ROM Tools' }).getAttribute('href')).toBe('/ingestion');
		expect(document.querySelector('ul.me-auto .dropdown-menu')).toBeNull();
	});

	it('ROM Tools shows the pending review badge only when the count is > 0', () => {
		const { unmount } = render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
			reviewCount: 1250,
			onlogout: () => {}
		});
		const badge = screen.getByRole('link', { name: /^ROM Tools/ }).querySelector('.badge')!;
		expect(badge.className).toBe('badge rounded-pill text-bg-danger');
		expect(badge.textContent).toBe('1,250 pending review');
		unmount();

		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
			reviewCount: 0,
			onlogout: () => {}
		});
		expect(screen.getByRole('link', { name: 'ROM Tools' }).querySelector('.badge')).toBeNull();
	});

	it('a non-admin sees Library, Collections, Timeline and Reports (RP1, T1)', () => {
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const links = [...document.querySelectorAll('a.nav-link')].map((a) => a.textContent?.trim());
		expect(links).toEqual(['Library', 'Collections', 'Timeline', 'Reports']);
		expect(screen.queryByRole('link', { name: 'ROM Tools' })).toBeNull();
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

	it('Search is an icon-only button named Search, with its key in the tooltip (B4)', () => {
		const onsearch = vi.fn();
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {},
			onsearch
		});
		// Release editing B30: one outside the menu (below lg), one inside it (lg up); CSS picks, jsdom renders both.
		const searches = screen.getAllByRole('button', { name: 'Search' });
		expect(searches).toHaveLength(2);
		for (const search of searches) {
			expect(search.textContent?.trim()).toBe('');
			expect(search.getAttribute('title')).toBe('Search (Ctrl+K)');
			expect(search.querySelector('i')!.className).toContain('bi-search');
			search.click();
		}
		expect(document.querySelector('kbd')).toBeNull();
		expect(onsearch).toHaveBeenCalledTimes(2);
	});

	it('the phone search sits between the brand and the toggler; the menu one is lg-only (release editing B30)', () => {
		render(Navbar, {
			me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {},
			onsearch: vi.fn()
		});
		const [outside, inside] = screen.getAllByRole('button', { name: 'Search' });
		expect(outside.className).toContain('ms-auto me-2 d-lg-none');
		expect(outside.previousElementSibling!.classList).toContain('navbar-brand');
		expect(outside.nextElementSibling!.classList).toContain('navbar-toggler');
		expect(inside.closest('li')!.className).toBe('nav-item d-none d-lg-block');
	});

	it('the user menu is li.user-menu with a phone-only username header (release editing B31)', () => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: [], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		const menu = document.querySelector('li.user-menu')!;
		expect(menu.classList).toContain('dropdown');
		const header = menu.querySelector('.dropdown-menu > li.d-lg-none .dropdown-header')!;
		expect(header.textContent!.trim()).toBe('boss');
		expect(header.querySelector('i')!.className).toContain('bi-person');
	});

	it('ROM Tools uses the adjustable wrench icon (B8)', () => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {}
		});
		expect(screen.getByRole('link', { name: 'ROM Tools' }).querySelector('i')!.className).toContain(
			'bi-wrench-adjustable'
		);
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

	it('has no keyboard shortcuts button, theme toggle or Admin link outside the user menu (B6, B7, B9)', () => {
		render(Navbar, {
			me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
			reviewCount: null,
			onlogout: () => {},
			onsearch: () => {}
		});
		expect(screen.queryByRole('button', { name: 'Keyboard Shortcuts' })).toBeNull();
		expect(screen.queryByRole('button', { name: /^Theme/ })).toBeNull();
		const outside = [...document.querySelectorAll('#main-nav a, #main-nav button')].filter(
			(el) => !el.closest('.dropdown-menu')
		);
		expect(outside.map((el) => el.textContent?.trim())).not.toContain('Admin');
	});

	describe('user menu (B6, B7)', () => {
		const menuItems = () =>
			// The phone-only username header (release editing B31) isn't a menu item.
			[...document.querySelectorAll('.dropdown-menu > li:not(.d-lg-none)')].map((li) =>
				li.querySelector('hr') ? '---' : li.textContent?.trim()
			);

		it('an Admin gets Admin first, then the Theme choices, Change password and Logout', () => {
			render(Navbar, {
				me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
				reviewCount: null,
				onlogout: () => {}
			});
			expect(menuItems()).toEqual([
				'Admin',
				'---',
				'Theme',
				'Light',
				'Dark',
				'Auto',
				'---',
				'Change password',
				'---',
				'Logout'
			]);
			expect(screen.getByRole('link', { name: 'Admin' }).getAttribute('href')).toBe('/admin/users');
		});

		it('a non-admin has no Admin item', () => {
			render(Navbar, {
				me: { id: 1, username: 'u', roles: [], mustChangePassword: false },
				reviewCount: null,
				onlogout: () => {}
			});
			expect(menuItems()[0]).toBe('Theme');
			expect(screen.queryByRole('link', { name: 'Admin' })).toBeNull();
		});

		it.each([
			['/admin/users', true],
			['/admin/audit', true],
			['/admin/backups', true],
			['/reports', false]
		])('Admin is active on %s: %s', (path, active) => {
			page.url = new URL(`http://localhost${path}`);
			render(Navbar, {
				me: { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false },
				reviewCount: null,
				onlogout: () => {}
			});
			const admin = screen.getByRole('link', { name: 'Admin' });
			expect(admin.classList.contains('active')).toBe(active);
			expect(admin.getAttribute('aria-current')).toBe(active ? 'page' : null);
		});
	});

	describe('Bundle Creator cart link (B1)', () => {
		const user = { id: 1, username: 'u', roles: [], mustChangePassword: false };

		it('names the count for screen readers and shows it as a badge', () => {
			render(Navbar, { me: user, reviewCount: null, onlogout: () => {}, cartCount: 1250 });

			const link = screen.getByRole('link', { name: 'Bundle 1,250 files in the cart' });
			expect(link.getAttribute('href')).toBe('/bundle-creator');
			expect(link.querySelector('.badge')!.textContent).toBe('1,250');
			expect(link.querySelector('i')!.className).toContain('bi-cart3');
			// B5: icon only; "Bundle" stays in the name as hidden text.
			expect(link.querySelector('.visually-hidden')!.textContent).toBe('Bundle');
		});

		it('says one file, and shows no badge for an empty cart', () => {
			const { unmount } = render(Navbar, {
				me: user,
				reviewCount: null,
				onlogout: () => {},
				cartCount: 1
			});
			expect(screen.getByRole('link', { name: 'Bundle 1 file in the cart' })).toBeTruthy();
			unmount();

			render(Navbar, { me: user, reviewCount: null, onlogout: () => {}, cartCount: 0 });
			const empty = screen.getByRole('link', { name: 'Bundle' });
			expect(empty.querySelector('.badge')).toBeNull();
		});

		it('is hidden while the password must be changed, like Search', () => {
			render(Navbar, {
				me: { ...user, mustChangePassword: true },
				reviewCount: null,
				onlogout: () => {},
				onsearch: () => {},
				cartCount: 3
			});
			expect(screen.queryByRole('link', { name: /^Bundle/ })).toBeNull();
			expect(screen.queryByRole('button', { name: /Search/ })).toBeNull();
		});

		it('is a plain keyboard-focusable link inside the collapsible menu the mobile toggler opens', () => {
			render(Navbar, { me: user, reviewCount: null, onlogout: () => {}, cartCount: 2 });

			const link = screen.getByRole('link', { name: /^Bundle/ });
			link.focus();
			expect(document.activeElement).toBe(link);
			expect(link.getAttribute('tabindex')).toBeNull();
			const menu = link.closest('#main-nav')!;
			expect(menu.classList.contains('navbar-collapse')).toBe(true);
			expect(
				screen.getByRole('button', { name: 'Toggle navigation' }).getAttribute('aria-controls')
			).toBe('main-nav');
			expect(link.closest('.d-none, [hidden]')).toBeNull();
		});

		it('is active on the Bundle Creator page', () => {
			page.url = new URL('http://localhost/bundle-creator');
			render(Navbar, { me: user, reviewCount: null, onlogout: () => {}, cartCount: 0 });

			expect(screen.getByRole('link', { name: 'Bundle' }).getAttribute('aria-current')).toBe(
				'page'
			);
		});

		it('does not join the main page links', () => {
			render(Navbar, { me: user, reviewCount: null, onlogout: () => {}, cartCount: 2 });
			const main = [...document.querySelectorAll('ul.me-auto a.nav-link')].map((a) =>
				a.textContent?.trim()
			);
			expect(main).toEqual(['Library', 'Collections', 'Timeline', 'Reports']);
		});
	});

	describe('the phone menu closes after a navigation (U1)', () => {
		const admin: Me = { id: 1, username: 'boss', roles: ['admin'], mustChangePassword: false };
		beforeEach(() => {
			nav.after.length = 0;
			nav.hides.length = 0;
		});
		const menu = () => document.getElementById('main-nav')!;

		it('a link click hides the open collapse, dropdown items included', async () => {
			render(Navbar, { me: admin, reviewCount: null, onlogout: () => {} });
			menu().classList.add('show');

			await fireEvent.click(screen.getByRole('link', { name: 'Reports' }));
			expect(nav.hides).toEqual([menu()]);
			await fireEvent.click(screen.getByRole('link', { name: /Admin/ }));
			expect(nav.hides).toHaveLength(2);
		});

		it('the user menu toggle and a closed collapse leave it alone', async () => {
			render(Navbar, { me: admin, reviewCount: null, onlogout: () => {} });
			menu().classList.add('show');
			await fireEvent.click(screen.getByRole('button', { name: 'boss' }));
			expect(nav.hides).toEqual([]);

			menu().classList.remove('show');
			await fireEvent.click(screen.getByRole('link', { name: 'Reports' }));
			expect(nav.hides).toEqual([]);
		});

		it('afterNavigate hides it too', () => {
			render(Navbar, { me: admin, reviewCount: null, onlogout: () => {} });
			menu().classList.add('show');
			nav.after.forEach((fn) => fn());
			expect(nav.hides).toEqual([menu()]);
		});
	});
});
