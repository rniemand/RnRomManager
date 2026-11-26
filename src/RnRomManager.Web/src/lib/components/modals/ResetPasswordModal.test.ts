import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { fakePassword } from '#lib/testing/fakePassword.js';
import type { UserInfo } from '#lib/types/index.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));

const { default: ResetPasswordModal } = await import('./ResetPasswordModal.svelte');

const user = { id: 7, username: 'bob' } as UserInfo;

describe('ResetPasswordModal', () => {
	let calls: { url: string; body: unknown }[];

	beforeEach(() => {
		resetClient();
		calls = [];
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (init?.method === 'POST') calls.push({ url, body: JSON.parse(init.body as string) });
			return Promise.resolve(new Response(null, { status: 204 }));
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	function setup() {
		const onfailed = vi.fn();
		let open = true;
		render(ResetPasswordModal, {
			user,
			onfailed,
			get open() {
				return open;
			},
			set open(v) {
				open = v;
			}
		});
		return { isOpen: () => open };
	}

	it('names the user and rejects an empty password', async () => {
		setup();
		expect(document.querySelector('.modal-body p')?.textContent).toContain(
			'Set a new password for bob.'
		);
		await fireEvent.click(screen.getByRole('button', { name: 'Reset password', hidden: true }));
		expect(calls).toHaveLength(0);
		expect(screen.getByLabelText('New password').className).toBe('form-control is-invalid');
	});

	it('posts the new password for the user and closes', async () => {
		const { isOpen } = setup();
		const password = fakePassword('reset');
		await fireEvent.input(screen.getByLabelText('New password'), { target: { value: password } });
		await fireEvent.click(screen.getByRole('button', { name: 'Reset password', hidden: true }));
		await waitFor(() => expect(isOpen()).toBe(false));
		expect(calls).toEqual([
			{ url: '/api/users/7/reset-password', body: { newPassword: password } }
		]);
	});

	it('Cancel closes', async () => {
		const { isOpen } = setup();
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(isOpen()).toBe(false);
	});
});
