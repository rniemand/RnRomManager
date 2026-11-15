import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { resetClient } from '../api/client';
import type { BundleEntry, BundleQueueSummary } from '../api/types';
import { bundle } from '../stores/bundle.svelte';
import { toasts } from '../stores/toasts.svelte';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));

const { default: SavedBundleQueues } = await import('./SavedBundleQueues.svelte');

const gameId = '8c0f3b55-1a2b-4c3d-8e9f-0a1b2c3d4e5f';
const entry = (n: number): BundleEntry => ({
	gameId,
	releaseSha1: n.toString(16).padStart(40, '0'),
	member: null,
	gameTitle: `Game ${n}`,
	fileName: `game ${n}.gb`
});
const summary = (id: string, name: string, revision = 1, itemCount = 1): BundleQueueSummary => ({
	id,
	name,
	revision,
	itemCount
});

type Answer = { status: number; body?: unknown };

describe('SavedBundleQueues (B2, B4)', () => {
	let calls: { key: string; body?: unknown }[];
	let answers: Record<string, Answer[]>;
	let store: Map<string, string>;

	beforeEach(() => {
		resetClient();
		toasts.items.length = 0;
		calls = [];
		answers = {};
		store = new Map();
		vi.stubGlobal('localStorage', {
			getItem: (key: string) => store.get(key) ?? null,
			setItem: (key: string, value: string) => void store.set(key, value),
			removeItem: (key: string) => void store.delete(key)
		});
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			const key = `${init?.method ?? 'GET'} ${url}`;
			if (url !== '/api/auth/antiforgery')
				calls.push({ key, body: init?.body ? JSON.parse(String(init.body)) : undefined });
			const answer = answers[key]?.shift() ?? { status: 204 };
			return Promise.resolve(
				new Response(answer.body === undefined ? null : JSON.stringify(answer.body), {
					status: answer.status,
					headers: { 'Content-Type': 'application/json' }
				})
			);
		});
		bundle.follow({ id: 5, username: 'u', roles: [], mustChangePassword: false });
	});
	afterEach(() => {
		bundle.follow(null);
		vi.unstubAllGlobals();
	});

	const list = (...queues: BundleQueueSummary[]) => ({ status: 200, body: queues });
	const answer = (key: string, ...items: Answer[]) =>
		(answers[key] = [...(answers[key] ?? []), ...items]);
	const conflict = (code: string) => ({ status: 409, body: { status: 409, code, title: code } });
	const modal = () => document.querySelector<HTMLElement>('.modal')!;
	const confirmButton = (name: string) =>
		within(modal()).getByRole('button', { name, hidden: true });
	const callKeys = () => calls.map((c) => c.key);

	it('lists the saved queues, or says there are none', async () => {
		answer('GET /api/bundle-queues', list(summary('q1', 'Weekend', 2, 12)));
		render(SavedBundleQueues);

		const row = (await screen.findByText('Weekend')).closest('tr')!;
		expect(within(row).getByText('12')).toBeTruthy();
		expect(within(row).getByRole('button', { name: 'Load Weekend into the cart' })).toBeTruthy();
	});

	it('shows an empty state with no queues', async () => {
		answer('GET /api/bundle-queues', list());
		render(SavedBundleQueues);

		expect(await screen.findByText('No saved queues yet.')).toBeTruthy();
	});

	it('saves the cart as a new queue with a trimmed name', async () => {
		bundle.add(entry(1));
		bundle.add(entry(2));
		answer('GET /api/bundle-queues', list(), list(summary('q1', 'Weekend', 1, 2)));
		answer('POST /api/bundle-queues', {
			status: 201,
			body: { id: 'q1', name: 'Weekend', revision: 1, entries: [] }
		});
		render(SavedBundleQueues);
		await screen.findByText('No saved queues yet.');

		await fireEvent.input(screen.getByLabelText('Queue name'), {
			target: { value: '  Weekend  ' }
		});
		await fireEvent.click(screen.getByRole('button', { name: /Save cart as queue/ }));

		await screen.findByText('Weekend');
		const post = calls.find((c) => c.key === 'POST /api/bundle-queues')!;
		expect(post.body).toEqual({ name: 'Weekend', entries: [entry(1), entry(2)] });
		expect(toasts.items.at(-1)?.text).toBe('Saved "Weekend".');
	});

	it('validates the name and needs files before saving', async () => {
		answer('GET /api/bundle-queues', list());
		render(SavedBundleQueues);
		await screen.findByText('No saved queues yet.');

		expect(
			screen.getByRole('button', { name: /Save cart as queue/ }).hasAttribute('disabled')
		).toBe(true);
		bundle.add(entry(1));
		await fireEvent.input(screen.getByLabelText('Queue name'), {
			target: { value: 'x'.repeat(101) }
		});

		expect(await screen.findByText('The name must be at most 100 characters.')).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: /Save cart as queue/ }));
		expect(callKeys()).not.toContain('POST /api/bundle-queues');
	});

	it('asks before saving over a queue with the same name, then replaces it at its revision', async () => {
		bundle.add(entry(1));
		answer(
			'GET /api/bundle-queues',
			list(summary('q1', 'Weekend', 3)),
			list(summary('q1', 'weekend', 4))
		);
		answer('PUT /api/bundle-queues/q1', {
			status: 200,
			body: { id: 'q1', name: 'weekend', revision: 4, entries: [] }
		});
		render(SavedBundleQueues);
		await screen.findByText('Weekend');

		await fireEvent.input(screen.getByLabelText('Queue name'), { target: { value: 'weekend' } });
		await fireEvent.click(screen.getByRole('button', { name: /Save cart as queue/ }));

		expect(modal().textContent).toContain('Save over this queue?');
		expect(callKeys()).not.toContain('PUT /api/bundle-queues/q1');
		await fireEvent.click(confirmButton('Save over'));

		await waitFor(() => expect(toasts.items.at(-1)?.text).toBe('Saved over "weekend".'));
		expect(calls.find((c) => c.key === 'PUT /api/bundle-queues/q1')!.body).toEqual({
			name: 'weekend',
			entries: [entry(1)],
			revision: 3
		});
		expect(callKeys()).not.toContain('POST /api/bundle-queues');
	});

	it('a different accent is a different name', async () => {
		bundle.add(entry(1));
		answer(
			'GET /api/bundle-queues',
			list(summary('q1', 'Pokemon')),
			list(summary('q1', 'Pokemon'), summary('q2', 'Pokémon'))
		);
		answer('POST /api/bundle-queues', {
			status: 201,
			body: { id: 'q2', name: 'Pokémon', revision: 1, entries: [] }
		});
		render(SavedBundleQueues);
		await screen.findByText('Pokemon');

		await fireEvent.input(screen.getByLabelText('Queue name'), { target: { value: 'Pokémon' } });
		await fireEvent.click(screen.getByRole('button', { name: /Save cart as queue/ }));

		await waitFor(() => expect(callKeys()).toContain('POST /api/bundle-queues'));
	});

	it('a stale overwrite says so and reloads the list', async () => {
		bundle.add(entry(1));
		answer(
			'GET /api/bundle-queues',
			list(summary('q1', 'Weekend', 1)),
			list(summary('q1', 'Weekend', 2))
		);
		answer('PUT /api/bundle-queues/q1', conflict('BundleQueueChanged'));
		render(SavedBundleQueues);
		await screen.findByText('Weekend');

		await fireEvent.input(screen.getByLabelText('Queue name'), { target: { value: 'Weekend' } });
		await fireEvent.click(screen.getByRole('button', { name: /Save cart as queue/ }));
		await fireEvent.click(confirmButton('Save over'));

		expect((await screen.findByRole('alert')).textContent).toContain(
			'changed in another tab or browser'
		);
		expect(callKeys().filter((k) => k === 'GET /api/bundle-queues')).toHaveLength(2);
	});

	it('a name taken since the list loaded offers the overwrite', async () => {
		bundle.add(entry(1));
		answer('GET /api/bundle-queues', list(), list(summary('q9', 'Weekend', 7)));
		answer('POST /api/bundle-queues', conflict('BundleQueueNameTaken'));
		render(SavedBundleQueues);
		await screen.findByText('No saved queues yet.');

		await fireEvent.input(screen.getByLabelText('Queue name'), { target: { value: 'Weekend' } });
		await fireEvent.click(screen.getByRole('button', { name: /Save cart as queue/ }));

		await waitFor(() => expect(modal().textContent).toContain('"Weekend" already exists'));
	});

	it('the account cap is reported', async () => {
		bundle.add(entry(1));
		answer('GET /api/bundle-queues', list());
		answer('POST /api/bundle-queues', conflict('BundleQueueLimit'));
		render(SavedBundleQueues);
		await screen.findByText('No saved queues yet.');

		await fireEvent.input(screen.getByLabelText('Queue name'), { target: { value: 'One more' } });
		await fireEvent.click(screen.getByRole('button', { name: /Save cart as queue/ }));

		expect((await screen.findByRole('alert')).textContent).toContain('at most 100 queues');
	});

	it('loads into an empty cart at once', async () => {
		answer('GET /api/bundle-queues', list(summary('q1', 'Weekend')));
		answer('GET /api/bundle-queues/q1', {
			status: 200,
			body: { id: 'q1', name: 'Weekend', revision: 1, entries: [entry(3)] }
		});
		render(SavedBundleQueues);

		await fireEvent.click(
			await screen.findByRole('button', { name: 'Load Weekend into the cart' })
		);

		await waitFor(() => expect(bundle.entries).toEqual([entry(3)]));
		expect(modal().textContent).not.toContain('Replace the cart?');
	});

	it('asks before loading over a cart with other files, and keeps it on cancel', async () => {
		bundle.add(entry(1));
		answer('GET /api/bundle-queues', list(summary('q1', 'Weekend')));
		const queue = {
			status: 200,
			body: { id: 'q1', name: 'Weekend', revision: 1, entries: [entry(3), entry(4)] }
		};
		answer('GET /api/bundle-queues/q1', queue, queue);
		render(SavedBundleQueues);

		await fireEvent.click(
			await screen.findByRole('button', { name: 'Load Weekend into the cart' })
		);
		await waitFor(() =>
			expect(modal().textContent).toContain('replaces the 1 files in the cart with its 2')
		);
		await fireEvent.click(within(modal()).getByRole('button', { name: 'Cancel', hidden: true }));
		expect(bundle.entries).toEqual([entry(1)]);

		await fireEvent.click(screen.getByRole('button', { name: 'Load Weekend into the cart' }));
		await waitFor(() => expect(modal().textContent).toContain('Replace the cart?'));
		await fireEvent.click(confirmButton('Replace cart'));
		expect(bundle.entries).toEqual([entry(3), entry(4)]);
	});

	it('loads without asking when the cart already holds exactly the queue', async () => {
		bundle.add(entry(3));
		answer('GET /api/bundle-queues', list(summary('q1', 'Weekend')));
		answer('GET /api/bundle-queues/q1', {
			status: 200,
			body: { id: 'q1', name: 'Weekend', revision: 1, entries: [entry(3)] }
		});
		render(SavedBundleQueues);

		await fireEvent.click(
			await screen.findByRole('button', { name: 'Load Weekend into the cart' })
		);

		await waitFor(() => expect(toasts.items.at(-1)?.text).toBe('Loaded "Weekend" into the cart.'));
	});

	it('deletes after confirmation at the revision, leaving the cart alone', async () => {
		bundle.add(entry(1));
		answer('GET /api/bundle-queues', list(summary('q1', 'Weekend', 6)), list());
		render(SavedBundleQueues);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Weekend' }));
		expect(modal().textContent).toContain('The working cart is not changed.');
		await fireEvent.click(confirmButton('Delete'));

		await screen.findByText('No saved queues yet.');
		expect(callKeys()).toContain('DELETE /api/bundle-queues/q1?revision=6');
		expect(bundle.entries).toEqual([entry(1)]);
	});

	it('a stale delete says so and reloads', async () => {
		answer(
			'GET /api/bundle-queues',
			list(summary('q1', 'Weekend', 1)),
			list(summary('q1', 'Weekend', 2))
		);
		answer('DELETE /api/bundle-queues/q1?revision=1', conflict('BundleQueueChanged'));
		render(SavedBundleQueues);

		await fireEvent.click(await screen.findByRole('button', { name: 'Delete Weekend' }));
		await fireEvent.click(confirmButton('Delete'));

		expect((await screen.findByRole('alert')).textContent).toContain(
			'changed in another tab or browser'
		);
		expect(callKeys().filter((k) => k === 'GET /api/bundle-queues')).toHaveLength(2);
	});

	it('drops a list answer that arrives after an account switch', async () => {
		let release!: (r: Response) => void;
		vi.stubGlobal('fetch', (url: string) =>
			url === '/api/bundle-queues'
				? new Promise<Response>((done) => (release = done))
				: Promise.resolve(new Response(null, { status: 204 }))
		);
		render(SavedBundleQueues);
		await waitFor(() => expect(release).toBeTypeOf('function'));
		const first = release;

		bundle.follow({ id: 6, username: 'other', roles: [], mustChangePassword: false });
		await waitFor(() => expect(release).not.toBe(first));
		first(new Response(JSON.stringify([summary('q1', 'Not yours')]), { status: 200 }));
		release(new Response('[]', { status: 200 }));

		expect(await screen.findByText('No saved queues yet.')).toBeTruthy();
		expect(screen.queryByText('Not yours')).toBeNull();
	});
});
