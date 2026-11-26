import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { fakePassword } from '#lib/testing/fakePassword.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: CreateUserModal } = await import('./CreateUserModal.svelte');

const typeLabels = { admin: 'Admin', 'site-user': 'Site user' };
const user = { id: 2, username: 'bob', roles: [], isDisabled: false, lastLoginAt: null };

describe('CreateUserModal', () => {
	let posted: unknown[];
	let status: number;

	beforeEach(() => {
		resetClient();
		posted = [];
		status = 201;
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (init?.method === 'POST') posted.push(JSON.parse(init.body as string));
			const body = status === 201 ? user : { status, title: 'Boom', code: 'Other' };
			return Promise.resolve(new Response(JSON.stringify(body), { status }));
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	function setup() {
		const oncreated = vi.fn();
		const onfailed = vi.fn();
		let open = true;
		render(CreateUserModal, {
			typeLabels,
			oncreated,
			onfailed,
			get open() {
				return open;
			},
			set open(v) {
				open = v;
			}
		});
		return { oncreated, onfailed, isOpen: () => open };
	}

	it('validates before posting', async () => {
		const { oncreated } = setup();
		await fireEvent.click(screen.getByRole('button', { name: 'Create', hidden: true }));
		expect(posted).toHaveLength(0);
		expect(oncreated).not.toHaveBeenCalled();
		expect(screen.getByLabelText('Username').className).toBe('form-control is-invalid');
	});

	it('Create posts the form and reports the new user', async () => {
		const { oncreated, isOpen } = setup();
		const password = fakePassword('create');
		await fireEvent.input(screen.getByLabelText('Username'), { target: { value: ' bob ' } });
		await fireEvent.input(screen.getByLabelText('Initial password'), {
			target: { value: password }
		});
		await fireEvent.change(screen.getByLabelText('Account type'), { target: { value: 'admin' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Create', hidden: true }));
		await waitFor(() => expect(oncreated).toHaveBeenCalledWith(user));
		expect(posted).toEqual([{ username: 'bob', roles: ['admin'], password }]);
		expect(isOpen()).toBe(false);
	});

	it('another failure goes to onfailed and the modal stays open', async () => {
		status = 500;
		const { onfailed, isOpen } = setup();
		await fireEvent.input(screen.getByLabelText('Username'), { target: { value: 'bob' } });
		await fireEvent.input(screen.getByLabelText('Initial password'), {
			target: { value: fakePassword('create') }
		});
		await fireEvent.click(screen.getByRole('button', { name: 'Create', hidden: true }));
		await waitFor(() => expect(onfailed).toHaveBeenCalledOnce());
		expect(isOpen()).toBe(true);
	});

	it('Cancel closes', async () => {
		const { isOpen } = setup();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
