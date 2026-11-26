import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { Me } from '#lib/types/index.js';
import { activity } from '#lib/stores/activity.svelte.js';
import { hotkeys } from '#lib/stores/hotkeys.svelte.js';
import { jobs } from '#lib/stores/jobs.svelte.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));
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
let held: unknown;
let calls: string[];

beforeEach(() => {
	resetClient();
	calls = [];
	answer = { status: 200, body: about };
	current = undefined;
	held = undefined;
	jobs.current = null; // the stores outlive each test
	activity.current = null;
	vi.stubGlobal('fetch', (url: string) => {
		calls.push(url);
		if (url === '/api/activity')
			return Promise.resolve(
				held ? new Response(JSON.stringify(held)) : new Response(null, { status: 204 })
			);
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
		expect(calls.filter((c) => c !== '/api/activity')).toEqual(['/api/about']);
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

		await vi.waitFor(() => expect(calls).toContain('/api/about'));
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

	describe('lock activity (L4)', () => {
		const admin = (): Me => ({ ...me(), roles: ['admin'] });

		it('a Site user sees what holds the lock, not as a link; the label is a footer-label', async () => {
			held = { activity: 'Saving changes', jobType: null };
			render(AppFooter, { me: me(), onhotkeys: vi.fn() });

			const status = await screen.findByRole('status');
			expect(status.textContent!.trim()).toBe('Saving changes…');
			expect(status.classList).toContain('ms-auto');
			expect(
				status.querySelector('.spinner-border.spinner-border-sm[aria-hidden="true"]')
			).not.toBeNull();
			expect(status.querySelector('.footer-label')!.textContent).toBe('Saving changes…');
			expect(status.querySelector('a')).toBeNull();
			expect(screen.getByRole('button', { name: 'Shortcuts' }).classList).not.toContain('ms-auto');
		});

		it('a job reads as its label + running', async () => {
			held = { activity: 'StorageMigration', jobType: 'StorageMigration' };
			render(AppFooter, { me: me(), onhotkeys: vi.fn() });
			expect((await screen.findByRole('status')).textContent!.trim()).toBe('Moving ROMs running');
		});

		it('an Admin with job progress sees the progress, not the activity', async () => {
			held = { activity: 'StorageMigration', jobType: 'StorageMigration' };
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
			render(AppFooter, { me: admin(), onhotkeys: vi.fn() });

			await screen.findByRole('link', { name: /Moving ROMs 1\/2/ });
			expect(screen.queryByRole('status')).toBeNull();
		});

		it('an Admin sees a job activity as a link to Jobs until the job progress loads', async () => {
			held = { activity: 'Integrity', jobType: 'Integrity' };
			activity.current = { activity: 'Integrity', jobType: 'Integrity' };
			render(AppFooter, { me: admin(), onhotkeys: vi.fn() });

			const status = screen.getByRole('status');
			expect(status.querySelector('a')!.getAttribute('href')).toBe('/jobs');
		});

		it('is not polled while the password must be changed', async () => {
			held = { activity: 'Saving changes', jobType: null };
			render(AppFooter, { me: me(true), onhotkeys: vi.fn() });

			await vi.waitFor(() => expect(calls).toContain('/api/about'));
			expect(calls).not.toContain('/api/activity');
			expect(screen.queryByRole('status')).toBeNull();
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

		it('an Admin gets the three buttons before Shortcuts, each running its hotkey', async () => {
			const run = vi.spyOn(hotkeys, 'run').mockResolvedValue();
			render(AppFooter, { me: admin(), onhotkeys: vi.fn() });

			const next = screen.getByRole('button', { name: 'Next missing image' });
			const igdb = screen.getByRole('button', { name: 'Next missing IGDB ID' });
			const review = screen.getByRole('button', { name: 'Open the first review item' });
			expect([next.title, igdb.title, review.title]).toEqual([
				'Next missing image',
				'Next missing IGDB ID',
				'Open the first review item'
			]);
			expect(next.querySelector('i')!.className.trim()).toBe('bi bi-image-alt');
			expect(igdb.querySelector('i')!.className.trim()).toBe('bi bi-link-45deg');
			expect(next.classList).toContain('ms-auto');
			expect(igdb.classList).not.toContain('ms-auto');
			expect(next.nextElementSibling).toBe(igdb);
			expect(igdb.nextElementSibling).toBe(review);
			expect(screen.getByRole('button', { name: 'Shortcuts' }).classList).not.toContain('ms-auto');
			expect(review.nextElementSibling).toBe(screen.getByRole('button', { name: 'Shortcuts' }));

			await fireEvent.click(next);
			await fireEvent.click(igdb);
			await fireEvent.click(review);
			expect(run.mock.calls).toEqual([
				['reports.missingImage.next'],
				['reports.missingIgdbId.next'],
				['review.next']
			]);
			run.mockRestore();
		});

		it('a Site user, or an Admin who must change the password, gets neither', () => {
			render(AppFooter, { me: me(), onhotkeys: vi.fn() });
			render(AppFooter, { me: admin(true), onhotkeys: vi.fn() });

			expect(screen.queryByRole('button', { name: 'Next missing image' })).toBeNull();
			expect(screen.queryByRole('button', { name: 'Next missing IGDB ID' })).toBeNull();
			expect(screen.queryByRole('button', { name: 'Open the first review item' })).toBeNull();
			expect(screen.getByRole('button', { name: 'Shortcuts' }).classList).toContain('ms-auto');
		});
	});
});
