import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import { auth } from '#lib/stores/auth.svelte.js';
import { toasts } from '#lib/stores/toasts.svelte.js';
import { navigateTo, testUrl } from '#lib/testing/url.svelte.js';

vi.mock('$app/navigation', () => ({ goto: (path: string) => navigateTo(path) }));
vi.mock('$app/state', () => ({
	page: {
		params: { id: 'g1' },
		get url() {
			return testUrl.current;
		}
	}
}));
vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));
vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/tooltip.js', () => ({ tooltip: () => ({}) }));
const { default: GamePage } = await import('./[id]/+page.svelte');

const game = (title = 'Legend, The', displayTitle = 'The Legend') => ({
	id: 'g1',
	system: 'gb',
	systemName: 'Game Boy',
	systemShortName: 'GB',
	title,
	displayTitle,
	releaseYear: null,
	developers: [],
	publishers: [],
	genres: [],
	description: null,
	hasDefaultBoxArt: false,
	rating: null,
	favourite: false,
	metadata: { createdAt: '', updatedAt: '' },
	roms: [],
	images: []
});

type Answer = { status: number; body?: unknown };
const problem = (
	status: number,
	code: string,
	title: string,
	errors?: Record<string, string[]>
) => ({
	status,
	body: { status, code, title, errors }
});

describe('inline title edit (T1–T8)', () => {
	let calls: string[];
	let bodies: unknown[];
	let answers: Record<string, Answer[]>;

	beforeEach(() => {
		resetClient();
		navigateTo('/games/g1');
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		toasts.items.splice(0);
		calls = [];
		bodies = [];
		answers = { 'GET /api/games/g1': [{ status: 200, body: game() }] };
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			if (url.endsWith('/collections')) return Promise.resolve(new Response('[]', { status: 200 }));
			const key = `${init?.method ?? 'GET'} ${url}`;
			calls.push(key);
			if (key.startsWith('PATCH')) bodies.push(JSON.parse(init!.body as string));
			const answer = answers[key]?.shift() ?? { status: 200, body: game() };
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

	const patches = () => calls.filter((c) => c === 'PATCH /api/games/g1').length;
	const edit = async () => {
		await fireEvent.click(await screen.findByRole('button', { name: 'The Legend' }));
		return screen.getByLabelText('Title') as HTMLInputElement;
	};
	const flush = () => new Promise((r) => setTimeout(r, 0));

	it('a non-Admin sees a plain heading (T1)', async () => {
		auth.set({ id: 2, username: 'user', roles: [], mustChangePassword: false });
		render(GamePage);

		const heading = await screen.findByRole('heading', { level: 1, name: 'The Legend' });
		expect(heading.querySelector('button')).toBeNull();
	});

	it('an Admin heading is a button named by the title; clicking shows the raw title selected (T2, T3)', async () => {
		render(GamePage);

		const button = await screen.findByRole('button', { name: 'The Legend' });
		expect(button.closest('h1')).toBeTruthy();
		expect(button.classList.contains('title-edit')).toBe(true);
		await fireEvent.click(button);

		const input = screen.getByLabelText('Title') as HTMLInputElement;
		expect(input.value).toBe('Legend, The');
		expect(document.activeElement).toBe(input);
		expect([input.selectionStart, input.selectionEnd]).toEqual([0, 'Legend, The'.length]);
		expect(input.getAttribute('maxlength')).toBe('255');
	});

	it('Enter saves { title } once, even with the blur after it; shows the new title and Saved. (T4, T6)', async () => {
		answers['PATCH /api/games/g1'] = [{ status: 200, body: game('Legend II', 'Legend II') }];
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: '  Legend II ' } });
		await fireEvent.submit(input.form!);
		await fireEvent.blur(input);

		expect(await screen.findByRole('button', { name: 'Legend II' })).toBeTruthy();
		expect(bodies).toEqual([{ title: 'Legend II' }]);
		expect(toasts.items.map((t) => t.text)).toContain('Saved.');
		await flush();
		expect(patches()).toBe(1);
	});

	it('blur saves (T4)', async () => {
		answers['PATCH /api/games/g1'] = [{ status: 200, body: game('Legend II', 'Legend II') }];
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: 'Legend II' } });
		await fireEvent.blur(input);

		expect(await screen.findByRole('button', { name: 'Legend II' })).toBeTruthy();
		expect(patches()).toBe(1);
	});

	it('Escape, then blur, sends nothing and restores the heading (T4)', async () => {
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: 'Other' } });
		await fireEvent.keyDown(input, { key: 'Escape' });
		await fireEvent.blur(input);

		expect(await screen.findByRole('button', { name: 'The Legend' })).toBeTruthy();
		await flush();
		expect(patches()).toBe(0);
	});

	it('an unchanged value sends nothing (T4)', async () => {
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: ' Legend, The ' } });
		await fireEvent.blur(input);

		expect(await screen.findByRole('button', { name: 'The Legend' })).toBeTruthy();
		expect(patches()).toBe(0);
	});

	it('an invalid title shows the message and keeps edit mode, also on blur (T5)', async () => {
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: '   ' } });
		await fireEvent.blur(input);

		expect(screen.getByText('Use 1–255 characters.').className).toContain('invalid-feedback');
		expect(input.classList.contains('is-invalid')).toBe(true);
		await fireEvent.input(input, { target: { value: 'Legend II' } });
		await fireEvent.submit(input.form!);
		await vi.waitFor(() => expect(patches()).toBe(1), { timeout: 2000 });
	});

	it('a server ValidationFailed on title is shown inline; edit mode stays (T7)', async () => {
		answers['PATCH /api/games/g1'] = [
			problem(400, 'ValidationFailed', 'Invalid', {
				title: ['Must contain more than dots and spaces.']
			})
		];
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: 'Legend II' } });
		await fireEvent.submit(input.form!);

		expect(await screen.findByText('Must contain more than dots and spaces.')).toBeTruthy();
		expect(screen.getByLabelText('Title')).toBeTruthy();
	});

	it('NameTaken is toasted; edit mode stays with the typed value (T7)', async () => {
		answers['PATCH /api/games/g1'] = [
			problem(409, 'NameTaken', 'Another game already has that name.')
		];
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: 'Legend II' } });
		await fireEvent.submit(input.form!);

		await vi.waitFor(() =>
			expect(toasts.items.map((t) => t.text)).toContain('Another game already has that name.')
		);
		expect((screen.getByLabelText('Title') as HTMLInputElement).value).toBe('Legend II');
		await fireEvent.submit(input.form!);
		await vi.waitFor(() => expect(patches()).toBe(2), { timeout: 2000 });
	});

	it('a running job is toasted and closes edit mode (T7)', async () => {
		answers['PATCH /api/games/g1'] = [problem(409, 'JobRunning', 'JobRunning')];
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: 'Legend II' } });
		await fireEvent.submit(input.form!);

		expect(await screen.findByRole('button', { name: 'The Legend' })).toBeTruthy();
		expect(toasts.items.map((t) => t.text)).toContain(
			'Another job is running. Try again when it has finished.'
		);
	});

	it('a 404 closes edit mode and reloads the game (T7)', async () => {
		answers['PATCH /api/games/g1'] = [problem(404, 'NotFound', 'Not found')];
		render(GamePage);
		const input = await edit();

		await fireEvent.input(input, { target: { value: 'Legend II' } });
		await fireEvent.submit(input.form!);

		await vi.waitFor(() =>
			expect(calls.filter((c) => c === 'GET /api/games/g1').length).toBeGreaterThan(1)
		);
		expect(await screen.findByText('This game changed, please retry.')).toBeTruthy();
	});
});
