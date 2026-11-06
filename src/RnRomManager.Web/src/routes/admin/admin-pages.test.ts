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

const admin = {
	id: 1,
	username: 'admin',
	roles: ['admin', 'rom-manager'],
	isDisabled: false,
	lastLoginAt: null
};

type Answer = { status: number; body?: unknown };

describe('admin pages', () => {
	let calls: { key: string; body: unknown }[];
	let answers: Record<string, Answer>;

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		auth.set({
			id: 1,
			username: 'admin',
			roles: ['admin', 'rom-manager'],
			mustChangePassword: false
		});
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

	const openCreate = async () => {
		await screen.findByRole('cell', { name: 'admin' });
		await fireEvent.click(screen.getByRole('button', { name: /Create user/ }));
	};
	const submitCreate = () =>
		fireEvent.click(screen.getByRole('button', { name: 'Create', hidden: true }));

	it('the users table uses the standard style with role badges', async () => {
		render(UsersPage);
		await screen.findByRole('cell', { name: 'admin' });

		const table = document.querySelector('table')!;
		expect(table.className).toBe('table table-sm table-hover align-middle mb-0');
		expect(table.closest('.table-responsive')?.parentElement?.classList.contains('card')).toBe(
			true
		);
		const badges = [...document.querySelectorAll('td label.badge')].map((b) => b.className);
		expect(badges.every((c) => c.includes('text-bg-primary'))).toBe(true);
		expect(badges).toHaveLength(2);
	});

	it('create user validates username and password before sending', async () => {
		render(UsersPage);
		await openCreate();

		await type('Username', 'a b');
		await type('Initial password', 'short');
		await submitCreate();

		expect(screen.getByText(/Use 3–64 characters/)).toBeTruthy();
		expect(screen.getByText('Use 12–128 characters.')).toBeTruthy();
		expect(calls.some((c) => c.key === 'POST /api/users')).toBe(false);
	});

	it('creates a user from the modal and reloads the table', async () => {
		const password = fakePassword('new');
		answers['POST /api/users'] = {
			status: 201,
			body: { id: 2, username: 'bob', roles: ['rom-manager'], isDisabled: false, lastLoginAt: null }
		};
		render(UsersPage);
		await openCreate();

		await type('Username', 'bob');
		await type('Initial password', password);
		await fireEvent.click(screen.getByRole('checkbox', { name: 'rom-manager', hidden: true }));
		await submitCreate();

		await vi.waitFor(() => expect(calls.filter((c) => c.key === 'GET /api/users')).toHaveLength(2));
		expect(calls).toContainEqual({
			key: 'POST /api/users',
			body: { username: 'bob', roles: ['rom-manager'], password }
		});
		expect(toasts.items.map((t) => t.text)[0]).toMatch(/^bob created/);
	});

	it('shows UsernameTaken and API field errors inline', async () => {
		const password = fakePassword('new');
		answers['POST /api/users'] = { status: 409, body: { code: 'UsernameTaken', title: 'x' } };
		render(UsersPage);
		await openCreate();

		await type('Username', 'bob');
		await type('Initial password', password);
		await submitCreate();
		expect(await screen.findByText('That username is taken.')).toBeTruthy();

		answers['POST /api/users'] = {
			status: 400,
			body: {
				code: 'ValidationFailed',
				title: 'x',
				errors: { roles: ['Roles must be any of: admin, rom-manager.'] }
			}
		};
		await submitCreate();
		expect(await screen.findByText('Roles must be any of: admin, rom-manager.')).toBeTruthy();
		expect(calls.filter((c) => c.key === 'GET /api/users')).toHaveLength(1);
	});

	it('a role checkbox PATCHes the full roles array', async () => {
		answers['PATCH /api/users/1'] = { status: 200, body: { ...admin, roles: ['admin'] } };
		render(UsersPage);

		await fireEvent.click(await screen.findByLabelText('rom-manager role of admin'));

		expect(calls).toContainEqual({ key: 'PATCH /api/users/1', body: { roles: ['admin'] } });
	});

	it('demoting the last admin shows the LastAdmin toast and reloads', async () => {
		answers['PATCH /api/users/1'] = { status: 409, body: { code: 'LastAdmin', title: 'x' } };
		render(UsersPage);

		await fireEvent.click(await screen.findByLabelText('admin role of admin'));
		expect(calls).toContainEqual({ key: 'PATCH /api/users/1', body: { roles: ['rom-manager'] } });

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
