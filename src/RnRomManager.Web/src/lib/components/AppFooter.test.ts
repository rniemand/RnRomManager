import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { Me } from '../api/types';
import { hotkeys } from '../stores/hotkeys.svelte';
import { jobs } from '../stores/jobs.svelte';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: AppFooter } = await import('./AppFooter.svelte');

const me = (mustChangePassword = false): Me => ({
	id: 1,
	username: 'u',
	roles: [],
	mustChangePassword
});
const about = {
	version: '1.4.2',
	licenses: [{ name: 'Svelte', license: 'MIT', url: 'https://svelte.dev' }]
};
let answer: { status: number; body?: unknown };
let current: unknown;
let calls: string[];

beforeEach(() => {
	resetClient();
	calls = [];
	answer = { status: 200, body: about };
	current = undefined;
	jobs.current = null; // the store outlives each test
	vi.stubGlobal('fetch', (url: string) => {
		calls.push(url);
		if (url === '/api/jobs/current')
			return Promise.resolve(
				current ? new Response(JSON.stringify(current)) : new Response(null, { status: 204 })
			);
		return Promise.resolve(
			new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
				status: answer.status
			})
		);
	});
});
afterEach(() => vi.unstubAllGlobals());

describe('AppFooter (B10–B12)', () => {
	it('is fixed to the bottom and links the version to the releases page, loaded once', async () => {
		render(AppFooter, { me: me(), onhotkeys: vi.fn() });

		const version = await screen.findByRole('link', { name: /^RnRomManager ?1.4.2$/ });
		expect(version.getAttribute('href')).toBe('https://github.com/rniemand/RnRomManager/releases');
		expect(version.getAttribute('target')).toBe('_blank');
		expect(version.getAttribute('rel')).toBe('noopener noreferrer');
		expect(document.querySelector('footer')!.classList.contains('fixed-bottom')).toBe(true);
		expect(calls).toEqual(['/api/about']);
	});

	it('below sm only the version shows: the name sits in a d-none d-sm-inline span inside the link (U9)', async () => {
		render(AppFooter, { me: me(), onhotkeys: vi.fn() });

		const version = await screen.findByRole('link', { name: /^RnRomManager ?1.4.2$/ });
		const name = version.querySelector('span')!;
		expect(name.className).toBe('d-none d-sm-inline');
		expect(name.textContent).toBe('RnRomManager ');
		expect(version.textContent?.replace(name.textContent!, '')).toBe('1.4.2');
	});

	it('without a version (loading or failed) the link reads RnRomManager alone', async () => {
		answer = { status: 500, body: { status: 500, title: 'Boom' } };
		render(AppFooter, { me: me(), onhotkeys: vi.fn() });

		await vi.waitFor(() => expect(calls).toEqual(['/api/about']));
		expect(screen.getByRole('link', { name: 'RnRomManager' }).textContent?.trim()).toBe(
			'RnRomManager'
		);
		expect(screen.getByRole('link', { name: 'RnRomManager' }).querySelector('span')).toBeNull();
	});

	it('About shows only the app logo; its aria-label names it (U2)', () => {
		render(AppFooter, { me: me(), onhotkeys: vi.fn() });

		const button = screen.getByRole('button', { name: 'About' });
		expect(button.getAttribute('title')).toBe('About');
		expect(button.querySelector('img[src="/favicon.svg"][alt=""]')).not.toBeNull();
		expect(button.querySelector('.footer-label')).toBeNull();
		expect(button.textContent!.trim()).toBe('');
	});

	it('About opens the About modal with the version and licences', async () => {
		render(AppFooter, { me: me(), onhotkeys: vi.fn() });
		await screen.findByRole('link', { name: /^RnRomManager ?1.4.2$/ });

		await fireEvent.click(screen.getByRole('button', { name: 'About' }));

		expect(screen.getByRole('heading', { name: 'About RnRomManager', hidden: true })).toBeTruthy();
		expect(screen.getByRole('link', { name: 'Svelte', hidden: true })).toBeTruthy();
	});

	it('the Shortcuts button opens the shortcuts; its aria-label names it, title stays (B26, U1)', async () => {
		const onhotkeys = vi.fn();
		render(AppFooter, { me: me(), onhotkeys });

		const button = screen.getByRole('button', { name: 'Shortcuts' });
		expect(button.getAttribute('aria-label')).toBe('Shortcuts');
		expect(button.getAttribute('title')).toBe('Keyboard Shortcuts');
		expect(button.querySelector('i.bi.bi-keyboard')).not.toBeNull();
		expect(button.querySelector('.footer-label')).toBeNull();
		expect(button.textContent!.trim()).toBe('');
		await fireEvent.click(button);

		expect(onhotkeys).toHaveBeenCalledOnce();
	});

	it('hides the Shortcuts button while the password must be changed, keeping About', () => {
		render(AppFooter, { me: me(true), onhotkeys: vi.fn() });

		expect(screen.queryByRole('button', { name: 'Shortcuts' })).toBeNull();
		expect(screen.getByRole('button', { name: 'About' })).toBeTruthy();
	});

	describe('job indicator (ROM Zip Config B17)', () => {
		const admin = (mustChangePassword = false): Me => ({
			...me(mustChangePassword),
			roles: ['admin']
		});
		const running = (total: number) => ({
			id: 'j1',
			type: 'StorageMigration',
			mode: null,
			status: 'Running',
			current: 3,
			total,
			currentItem: null,
			startedAt: '2026-11-18T10:00:00Z'
		});

		it('an Admin sees the running job with its progress, linking to the Jobs page', async () => {
			current = running(10);
			render(AppFooter, { me: admin(), onhotkeys: vi.fn() });

			const link = await screen.findByRole('link', { name: /Moving ROMs 3\/10/ });
			expect(link.getAttribute('href')).toBe('/jobs');
			const bar = screen.getByRole('progressbar', { name: 'Moving ROMs progress' });
			expect(bar.getAttribute('aria-valuenow')).toBe('30');
		});

		it('an unknown total shows an indeterminate bar', async () => {
			current = running(0);
			render(AppFooter, { me: admin(), onhotkeys: vi.fn() });

			const bar = await screen.findByRole('progressbar', { name: 'Moving ROMs progress' });
			expect(bar.hasAttribute('aria-valuenow')).toBe(false);
			expect(bar.querySelector('.progress-bar-animated')).not.toBeNull();
		});

		it('shows nothing when idle', async () => {
			render(AppFooter, { me: admin(), onhotkeys: vi.fn() });

			await vi.waitFor(() => expect(calls).toContain('/api/jobs/current'));
			expect(screen.queryByRole('progressbar')).toBeNull();
		});

		it('a non-Admin, or an Admin who must change the password, neither sees nor polls it', async () => {
			current = running(10);
			render(AppFooter, { me: me(), onhotkeys: vi.fn() });
			render(AppFooter, { me: admin(true), onhotkeys: vi.fn() });

			await vi.waitFor(() => expect(calls.filter((c) => c === '/api/about')).toHaveLength(2));
			expect(calls).not.toContain('/api/jobs/current');
			expect(screen.queryByRole('progressbar')).toBeNull();
		});
	});

	describe('mobile and Admin quick actions (release editing B28, B29)', () => {
		const admin = (mustChangePassword = false): Me => ({
			...me(mustChangePassword),
			roles: ['admin']
		});

		it('keeps one unwrapped row; only the job link has a footer-label', async () => {
			current = {
				id: 'j1',
				type: 'StorageMigration',
				mode: null,
				status: 'Running',
				current: 1,
				total: 2,
				currentItem: null,
				startedAt: '2026-11-18T10:00:00Z'
			};
			const { container } = render(AppFooter, { me: admin(), onhotkeys: vi.fn() });

			const link = await screen.findByRole('link', { name: /Moving ROMs 1\/2/ });
			expect(link.querySelector('.footer-label')!.textContent).toBe('Moving ROMs 1/2');
			const row = container.querySelector('footer > div')!;
			expect(row.classList).toContain('flex-nowrap');
			expect(row.classList).toContain('overflow-hidden');
			expect(row.querySelectorAll('.footer-label')).toHaveLength(1);
		});

		it('an Admin gets both buttons before Shortcuts, each running its hotkey', async () => {
			const run = vi.spyOn(hotkeys, 'run').mockResolvedValue();
			render(AppFooter, { me: admin(), onhotkeys: vi.fn() });

			const next = screen.getByRole('button', { name: 'Next missing image' });
			const review = screen.getByRole('button', { name: 'Open the first review item' });
			expect([next.title, review.title]).toEqual([
				'Next missing image',
				'Open the first review item'
			]);
			expect(next.querySelector('i')!.className.trim()).toBe('bi bi-image-alt');
			expect(next.classList).toContain('ms-auto');
			expect(screen.getByRole('button', { name: 'Shortcuts' }).classList).not.toContain('ms-auto');
			expect(review.nextElementSibling).toBe(screen.getByRole('button', { name: 'Shortcuts' }));

			await fireEvent.click(next);
			await fireEvent.click(review);
			expect(run.mock.calls).toEqual([['reports.missingImage.next'], ['review.next']]);
			run.mockRestore();
		});

		it('a Site user, or an Admin who must change the password, gets neither', () => {
			render(AppFooter, { me: me(), onhotkeys: vi.fn() });
			render(AppFooter, { me: admin(true), onhotkeys: vi.fn() });

			expect(screen.queryByRole('button', { name: 'Next missing image' })).toBeNull();
			expect(screen.queryByRole('button', { name: 'Open the first review item' })).toBeNull();
			expect(screen.getByRole('button', { name: 'Shortcuts' }).classList).toContain('ms-auto');
		});
	});
});
