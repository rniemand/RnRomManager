import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';

const goto = vi.fn();
const url = { current: new URL('http://localhost/login') };

vi.mock('$app/navigation', () => ({ goto: (...args: unknown[]) => goto(...args) }));
vi.mock('$app/state', () => ({
	page: {
		get url() {
			return url.current;
		}
	}
}));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

const { default: LoginPage } = await import('./login/+page.svelte');
const { default: ChangePasswordPage } = await import('./change-password/+page.svelte');

type Answer = { status: number; body?: unknown };

function stubApi(answers: Record<string, Answer>) {
	const requests: { url: string; body: unknown }[] = [];
	vi.stubGlobal('fetch', (input: string, init?: RequestInit) => {
		requests.push({ url: input, body: init?.body ? JSON.parse(init.body as string) : undefined });
		const answer = answers[input] ?? { status: 204 };
		return Promise.resolve(
			new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
				status: answer.status
			})
		);
	});
	return requests;
}

const type = (label: string, value: string) =>
	fireEvent.input(screen.getByLabelText(label), { target: { value } });

describe('login page', () => {
	beforeEach(() => {
		resetClient();
		goto.mockClear();
		auth.set(null);
	});
	afterEach(() => vi.unstubAllGlobals());

	it('shows one generic message for bad credentials', async () => {
		stubApi({
			'/api/auth/login': {
				status: 401,
				body: { code: 'InvalidCredentials', title: 'Invalid username or password' }
			}
		});
		render(LoginPage);

		await type('Username', 'boss');
		await type('Password', 'wrong-password');
		await fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

		expect(await screen.findByRole('alert')).toHaveProperty(
			'textContent',
			'Invalid username or password.'
		);
		expect((screen.getByLabelText('Password') as HTMLInputElement).value).toBe('');
		expect(goto).not.toHaveBeenCalled();
	});

	it('signs in and follows a safe returnUrl', async () => {
		url.current = new URL('http://localhost/login?returnUrl=%2Fgames%2F1');
		const requests = stubApi({
			'/api/auth/login': {
				status: 200,
				body: { id: 1, username: 'boss', role: 'Admin', mustChangePassword: false }
			}
		});
		render(LoginPage);

		await type('Username', 'boss');
		await type('Password', 'correct-horse-battery');
		await fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/games/1', { replaceState: true }));
		expect(requests.find((r) => r.url === '/api/auth/login')?.body).toEqual({
			username: 'boss',
			password: 'correct-horse-battery'
		});
		expect(auth.me?.username).toBe('boss');
	});

	it('sends a user who must change their password to change-password', async () => {
		url.current = new URL('http://localhost/login?returnUrl=https%3A%2F%2Fevil.example');
		stubApi({
			'/api/auth/login': {
				status: 200,
				body: { id: 1, username: 'admin', role: 'Admin', mustChangePassword: true }
			}
		});
		render(LoginPage);

		await type('Username', 'admin');
		await type('Password', 'initial-password-1');
		await fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

		await vi.waitFor(() =>
			expect(goto).toHaveBeenCalledWith('/change-password', { replaceState: true })
		);
	});
});

describe('change password page', () => {
	beforeEach(() => {
		resetClient();
		goto.mockClear();
		auth.set({ id: 1, username: 'boss', role: 'Admin', mustChangePassword: true });
	});
	afterEach(() => vi.unstubAllGlobals());

	it('shows the policy hint, and that the change is required', () => {
		render(ChangePasswordPage);

		expect(screen.getByText('12–128 characters, and not the same as your username.')).toBeTruthy();
		expect(screen.getByText('Choose a new password before you continue.')).toBeTruthy();
	});

	it('validates before sending: too short and mismatched confirmation', async () => {
		const requests = stubApi({});
		render(ChangePasswordPage);

		await type('Current password', 'old-password-1');
		await type('New password', 'short');
		await type('Confirm new password', 'different');
		await fireEvent.click(screen.getByRole('button', { name: 'Change password' }));

		expect(screen.getByText('Use 12–128 characters.')).toBeTruthy();
		expect(screen.getByText("The passwords don't match.")).toBeTruthy();
		expect(screen.getByLabelText('Confirm new password').classList.contains('is-invalid')).toBe(
			true
		);
		expect(requests.some((r) => r.url === '/api/auth/change-password')).toBe(false);
	});

	it('shows the server error for a wrong current password', async () => {
		stubApi({
			'/api/auth/change-password': {
				status: 400,
				body: {
					code: 'ValidationFailed',
					title: 'Invalid',
					errors: { currentPassword: ['Current password is incorrect.'] }
				}
			}
		});
		render(ChangePasswordPage);

		await type('Current password', 'not-my-password');
		await type('New password', 'a-new-password');
		await type('Confirm new password', 'a-new-password');
		await fireEvent.click(screen.getByRole('button', { name: 'Change password' }));

		expect(await screen.findByText('Current password is incorrect.')).toBeTruthy();
		expect(goto).not.toHaveBeenCalled();
	});

	it('changes the password, reloads me and goes to the Library', async () => {
		const requests = stubApi({
			'/api/auth/me': {
				status: 200,
				body: { id: 1, username: 'boss', role: 'Admin', mustChangePassword: false }
			}
		});
		render(ChangePasswordPage);

		await type('Current password', 'old-password-1');
		await type('New password', 'a-new-password');
		await type('Confirm new password', 'a-new-password');
		await fireEvent.click(screen.getByRole('button', { name: 'Change password' }));

		await vi.waitFor(() => expect(goto).toHaveBeenCalledWith('/', { replaceState: true }));
		expect(requests.find((r) => r.url === '/api/auth/change-password')?.body).toEqual({
			currentPassword: 'old-password-1',
			newPassword: 'a-new-password'
		});
		expect(auth.me?.mustChangePassword).toBe(false);
	});
});
