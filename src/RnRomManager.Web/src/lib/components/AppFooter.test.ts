import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { Me } from '../api/types';
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

		const version = await screen.findByRole('link', { name: 'RnRomManager 1.4.2' });
		expect(version.getAttribute('href')).toBe('https://github.com/rniemand/RnRomManager/releases');
		expect(version.getAttribute('target')).toBe('_blank');
		expect(version.getAttribute('rel')).toBe('noopener noreferrer');
		expect(document.querySelector('footer')!.classList.contains('fixed-bottom')).toBe(true);
		expect(calls).toEqual(['/api/about']);
	});

	it('without a version (loading or failed) the link reads RnRomManager alone', async () => {
		answer = { status: 500, body: { status: 500, title: 'Boom' } };
		render(AppFooter, { me: me(), onhotkeys: vi.fn() });

		await vi.waitFor(() => expect(calls).toEqual(['/api/about']));
		expect(screen.getByRole('link', { name: 'RnRomManager' }).textContent?.trim()).toBe(
			'RnRomManager'
		);
	});

	it('About opens the About modal with the version and licences', async () => {
		render(AppFooter, { me: me(), onhotkeys: vi.fn() });
		await screen.findByRole('link', { name: 'RnRomManager 1.4.2' });

		await fireEvent.click(screen.getByRole('button', { name: 'About' }));

		expect(screen.getByRole('heading', { name: 'About RnRomManager', hidden: true })).toBeTruthy();
		expect(screen.getByRole('link', { name: 'Svelte', hidden: true })).toBeTruthy();
	});

	it('the Shortcuts button opens the shortcuts; its visible text names it, title stays (B26)', async () => {
		const onhotkeys = vi.fn();
		render(AppFooter, { me: me(), onhotkeys });

		const button = screen.getByRole('button', { name: 'Shortcuts' });
		expect(button.hasAttribute('aria-label')).toBe(false);
		expect(button.getAttribute('title')).toBe('Keyboard shortcuts');
		expect(button.querySelector('.bi-question-circle')).not.toBeNull();
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
});
