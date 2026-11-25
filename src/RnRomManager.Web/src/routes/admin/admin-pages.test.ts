import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { fakePassword } from '#lib/testing/fakePassword.js';
import { createRawSnippet } from 'svelte';
import { expectOnePageTitle } from '#lib/testing/pageTitle.js';

// Bootstrap's Modal and Tooltip need layout/transitions jsdom lacks.
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	},
	Tooltip: class {
		setContent() {}
		dispose() {}
	}
}));

const { default: UsersPage } = await import('./users/+page.svelte');
const { default: SettingsPage } = await import('./settings/+page.svelte');
const { default: DatsPage } = await import('./dats/+page.svelte');
const { default: AdminLayout } = await import('./+layout.svelte');

const admin = {
	id: 1,
	username: 'admin',
	roles: ['admin'],
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
			roles: ['admin'],
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

	const boss = { id: 2, username: 'boss', roles: ['admin'], isDisabled: false, lastLoginAt: null };
	const visitor = { id: 3, username: 'visitor', roles: [], isDisabled: false, lastLoginAt: null };
	const typeSelect = (username: string) =>
		screen.getByLabelText(`Account type of ${username}`) as HTMLSelectElement;

	it('the users table uses the standard style with a Type column', async () => {
		answers['GET /api/users'] = { status: 200, body: [admin, visitor] };
		render(UsersPage);
		await screen.findByRole('cell', { name: 'admin' });

		const table = document.querySelector('table')!;
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(table.closest('.table-responsive')?.parentElement?.classList.contains('card')).toBe(
			true
		);
		expect(screen.getByRole('columnheader', { name: 'Type' })).toBeTruthy();
		expect(typeSelect('admin').value).toBe('admin');
		expect(typeSelect('visitor').value).toBe('site-user');
		expect([...typeSelect('visitor').options].map((o) => o.text)).toEqual(['Admin', 'Site user']);
	});

	it("the signed-in admin's own row can't change its type or be disabled", async () => {
		answers['GET /api/users'] = { status: 200, body: [admin, visitor] };
		render(UsersPage);
		await screen.findByRole('cell', { name: 'admin' });

		expect(typeSelect('admin').disabled).toBe(true);
		expect(
			(screen.getByRole('button', { name: 'Disable admin' }) as HTMLButtonElement).disabled
		).toBe(true);
		expect(typeSelect('visitor').disabled).toBe(false);
		expect(
			(screen.getByRole('button', { name: 'Disable visitor' }) as HTMLButtonElement).disabled
		).toBe(false);
	});

	it('changing a row type PATCHes its roles', async () => {
		answers['GET /api/users'] = { status: 200, body: [admin, visitor] };
		answers['PATCH /api/users/3'] = { status: 200, body: { ...visitor, roles: ['admin'] } };
		render(UsersPage);
		await screen.findByRole('cell', { name: 'visitor' });

		await fireEvent.change(typeSelect('visitor'), { target: { value: 'admin' } });

		expect(calls).toContainEqual({ key: 'PATCH /api/users/3', body: { roles: ['admin'] } });
		await vi.waitFor(() => expect(typeSelect('visitor').value).toBe('admin'));
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
			body: { id: 2, username: 'bob', roles: ['admin'], isDisabled: false, lastLoginAt: null }
		};
		render(UsersPage);
		await openCreate();

		await type('Username', 'bob');
		await type('Initial password', password);
		await fireEvent.change(screen.getByLabelText('Account type'), { target: { value: 'admin' } });
		await submitCreate();

		await vi.waitFor(() => expect(calls.filter((c) => c.key === 'GET /api/users')).toHaveLength(2));
		expect(calls).toContainEqual({
			key: 'POST /api/users',
			body: { username: 'bob', roles: ['admin'], password }
		});
		expect(toasts.items.map((t) => t.text)[0]).toMatch(/^bob created/);
	});

	it('the create form starts on Site user, which sends no roles', async () => {
		const password = fakePassword('new');
		answers['POST /api/users'] = { status: 201, body: { ...visitor, username: 'carol' } };
		render(UsersPage);
		await openCreate();

		expect((screen.getByLabelText('Account type') as HTMLSelectElement).value).toBe('site-user');
		await type('Username', 'carol');
		await type('Initial password', password);
		await submitCreate();

		await vi.waitFor(() =>
			expect(calls).toContainEqual({
				key: 'POST /api/users',
				body: { username: 'carol', roles: [], password }
			})
		);
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
				errors: { roles: ['Roles must be any of: admin.'] }
			}
		};
		await submitCreate();
		expect(await screen.findByText('Roles must be any of: admin.')).toBeTruthy();
		expect(calls.filter((c) => c.key === 'GET /api/users')).toHaveLength(1);
	});

	it('a SelfChange answer is a warning toast with its title, and the list reloads', async () => {
		const title = "You can't disable or demote your own account";
		answers['GET /api/users'] = { status: 200, body: [admin, boss] };
		answers['PATCH /api/users/2'] = { status: 409, body: { code: 'SelfChange', title } };
		render(UsersPage);
		await screen.findByRole('cell', { name: 'boss' });

		await fireEvent.click(screen.getByRole('button', { name: 'Disable boss' }));

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => [t.text, t.kind])).toContainEqual([title, 'warning'])
		);
		expect(calls.filter((c) => c.key === 'GET /api/users')).toHaveLength(2);
	});

	it('demoting the last admin shows the LastAdmin toast and reloads', async () => {
		answers['GET /api/users'] = { status: 200, body: [admin, boss] };
		answers['PATCH /api/users/2'] = { status: 409, body: { code: 'LastAdmin', title: 'x' } };
		render(UsersPage);
		await screen.findByRole('cell', { name: 'boss' });

		await fireEvent.change(typeSelect('boss'), { target: { value: 'site-user' } });
		expect(calls).toContainEqual({ key: 'PATCH /api/users/2', body: { roles: [] } });

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

	it('settings are shown flattened, without the log download (moved to Logs)', async () => {
		answers['GET /api/admin/settings'] = {
			status: 200,
			body: { connectionString: 'Server=db', paths: { roms: '/app/roms' } }
		};
		render(SettingsPage);

		expect(await screen.findByText('paths.roms')).toBeTruthy();
		expect(screen.queryByRole('link', { name: /Download logs/ })).toBeNull();
	});

	const expectT1 = () => {
		const table = document.querySelector('table')!;
		expect(table.className).toBe('table table-sm table-striped table-hover align-middle mb-0');
		expect(table.closest('.table-responsive')?.parentElement?.classList.contains('card')).toBe(
			true
		);
		expect(table.querySelector('thead tr')?.className).toBe(
			'small text-uppercase text-body-secondary'
		);
	};

	it('the settings table uses the standard style', async () => {
		answers['GET /api/admin/settings'] = { status: 200, body: { a: 1 } };
		render(SettingsPage);
		await screen.findByText('a');
		expectT1();
	});

	it('the DATs table uses the standard style', async () => {
		answers['GET /api/admin/dats'] = {
			status: 200,
			body: [{ datName: 'gb.dat', version: '1', downloadedAt: '2026-11-06T10:00:00Z' }]
		};
		render(DatsPage);
		await screen.findByText('gb.dat');
		expectT1();
	});

	it('the Admin layout has a ROM Zip Config tab after Settings (ROM Zip Config B15)', () => {
		render(AdminLayout, {
			children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
		});
		const tabs = screen.getAllByRole('link').map((l) => l.textContent?.trim());
		expect(tabs.indexOf('ROM Zip Config')).toBe(tabs.indexOf('Settings') + 1);
		expect(screen.getByRole('link', { name: 'ROM Zip Config' }).getAttribute('href')).toBe(
			'/admin/rom-storage'
		);
	});

	it('each Admin tab has its icon (release editing B33)', () => {
		render(AdminLayout, {
			children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
		});
		const links = screen.getAllByRole('link');
		expect(
			links.map((a) => [a.textContent?.trim(), a.querySelector('i')?.className.trim()])
		).toEqual([
			['Users', 'bi bi-people'],
			['DATs', 'bi bi-database'],
			['Settings', 'bi bi-gear'],
			['ROM Zip Config', 'bi bi-file-zip'],
			['BIOS info', 'bi bi-cpu'],
			['Backups', 'bi bi-archive'],
			['Logs', 'bi bi-journal-text'],
			['Auditing', 'bi bi-clock-history']
		]);
		expect(
			links.every((a) => a.className.includes('nav-link d-flex align-items-center gap-1'))
		).toBe(true);
	});

	it('the Admin layout has one page-title h1 (B1)', () => {
		render(AdminLayout, {
			children: createRawSnippet(() => ({ render: () => '<div>Content</div>' }))
		});
		expectOnePageTitle('Admin');
	});
});
