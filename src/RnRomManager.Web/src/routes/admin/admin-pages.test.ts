import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { fakePassword } from '#lib/testing/fakePassword.js';

// Bootstrap's Modal needs layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: UsersPage } = await import('./users/+page.svelte');
const { default: SettingsPage } = await import('./settings/+page.svelte');

const admin = { id: 1, username: 'admin', role: 'Admin', isDisabled: false, lastLoginAt: null };

type Answer = { status: number; body?: unknown };

describe('admin pages', () => {
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		auth.set({ id: 1, username: 'admin', role: 'Admin', mustChangePassword: false });
		calls = [];
		answers = { 'GET /api/users': { status: 200, body: [admin] } };
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push({ key, body: init?.body ? JSON.parse(init.body as string) : undefined });
			const answer = answers[key] ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status
				})
			);
		});
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	const type = (label: string, value: string) =>
		fireEvent.input(screen.getByLabelText(label), { target: { value } });

	it('create user validates username and password before sending', async () => {
		render(UsersPage);
		await screen.findByRole('cell', { name: 'admin' });

		await type('Username', 'a b');
		await type('Initial password', 'short');
		await fireEvent.click(screen.getByRole('button', { name: 'Create' }));

		expect(screen.getByText(/Use 3–64 characters/)).toBeTruthy();
		expect(screen.getByText('Use 12–128 characters.')).toBeTruthy();
		expect(calls.some((c) => c.key === 'POST /api/users')).toBe(false);
	});

	it('creates a user and shows UsernameTaken on the field', async () => {
		const password = fakePassword('new');
		answers['POST /api/users'] = { status: 409, body: { code: 'UsernameTaken', title: 'x' } };
		render(UsersPage);
		await screen.findByRole('cell', { name: 'admin' });

		await type('Username', 'bob');
		await type('Initial password', password);
		await fireEvent.click(screen.getByRole('button', { name: 'Create' }));

		expect(await screen.findByText('That username is taken.')).toBeTruthy();
		expect(calls).toContainEqual({
			key: 'POST /api/users',
			body: { username: 'bob', role: 'User', password }
		});
	});

	it('demoting the last admin shows the LastAdmin toast and reloads', async () => {
		answers['PATCH /api/users/1'] = { status: 409, body: { code: 'LastAdmin', title: 'x' } };
		render(UsersPage);

		await fireEvent.change(await screen.findByLabelText('Role of admin'), {
			target: { value: 'User' }
		});

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toContain(
				"The last enabled Admin can't be disabled or demoted."
			)
		);
		expect(calls.filter((c) => c.key === 'GET /api/users')).toHaveLength(2);
	});

	it('reset password checks the policy, then posts newPassword', async () => {
		const password = fakePassword('reset');
		render(UsersPage);

		await fireEvent.click(await screen.findByRole('button', { name: /Reset password of admin/ }));
		await type('New password', 'admin');
		await fireEvent.click(
			screen.getAllByRole('button', { name: 'Reset password', hidden: true })[0]
		);
		expect(screen.getByText('Use 12–128 characters.')).toBeTruthy();

		await type('New password', password);
		await fireEvent.click(
			screen.getAllByRole('button', { name: 'Reset password', hidden: true })[0]
		);

		await vi.waitFor(() =>
			expect(calls).toContainEqual({
				key: 'POST /api/users/1/reset-password',
				body: { newPassword: password }
			})
		);
	});

	it('settings are shown flattened, with a log download link', async () => {
		answers['GET /api/admin/settings'] = {
			status: 200,
			body: { connectionString: 'Server=db', paths: { roms: '/app/roms' } }
		};
		render(SettingsPage);

		expect(await screen.findByText('paths.roms')).toBeTruthy();
		expect(screen.getByRole('link', { name: /Download logs/ }).getAttribute('href')).toBe(
			'/api/admin/logs'
		);
	});
});
