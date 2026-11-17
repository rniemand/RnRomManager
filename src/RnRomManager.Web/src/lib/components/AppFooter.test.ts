import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { Me } from '../api/types';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));

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
let calls: string[];

beforeEach(() => {
	resetClient();
	calls = [];
	answer = { status: 200, body: about };
	vi.stubGlobal('fetch', (url: string) => {
		calls.push(url);
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

	it('the Keyboard shortcuts button opens the shortcuts', async () => {
		const onhotkeys = vi.fn();
		render(AppFooter, { me: me(), onhotkeys });

		await fireEvent.click(screen.getByRole('button', { name: 'Keyboard shortcuts' }));

		expect(onhotkeys).toHaveBeenCalledOnce();
	});

	it('hides the Keyboard shortcuts button while the password must be changed, keeping About', () => {
		render(AppFooter, { me: me(true), onhotkeys: vi.fn() });

		expect(screen.queryByRole('button', { name: 'Keyboard shortcuts' })).toBeNull();
		expect(screen.getByRole('button', { name: 'About' })).toBeTruthy();
	});
});
