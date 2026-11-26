import { flushSync } from 'svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';

type Call = {
	url: string;
	signal: AbortSignal;
	resolve: (v: unknown) => void;
	reject: (e: unknown) => void;
};
const calls = vi.hoisted(() => [] as Call[]);
vi.mock('#lib/api/client.js', () => ({
	api: {
		get: (url: string, { signal }: { signal: AbortSignal }) =>
			new Promise((resolve, reject) => calls.push({ url, signal, resolve, reject }))
	}
}));

const { useAbortableGet } = await import('./loadPage.svelte');

const settle = () => new Promise((r) => setTimeout(r));

describe('useAbortableGet', () => {
	let cleanup: (() => void) | undefined;
	afterEach(() => {
		cleanup?.();
		calls.length = 0;
	});

	function setup(initial: string | null, onPastEnd?: (r: unknown) => void) {
		let url = $state(initial);
		let loader!: ReturnType<typeof useAbortableGet<unknown>>;
		cleanup = $effect.root(() => {
			loader = useAbortableGet<unknown>(() => url, onPastEnd);
		});
		flushSync();
		return {
			loader,
			setUrl: (next: string | null) => {
				url = next;
				flushSync();
			}
		};
	}

	it('loads the URL and exposes the result', async () => {
		const { loader } = setup('/a');
		expect(calls.map((c) => c.url)).toEqual(['/a']);
		expect(loader.result).toBeNull();
		calls[0].resolve({ items: [1], total: 1 });
		await settle();
		expect(loader.result).toEqual({ items: [1], total: 1 });
		expect(loader.error).toBeNull();
	});

	it('a new URL aborts the old request and hides the old result', async () => {
		const { loader, setUrl } = setup('/a');
		calls[0].resolve('A');
		await settle();
		setUrl('/b');
		expect(loader.result).toBeNull();
		setUrl('/c');
		expect(calls[1].signal.aborted).toBe(true);
		calls[1].resolve('B');
		calls[2].resolve('C');
		await settle();
		expect(loader.result).toBe('C');
	});

	it('reports an error unless aborted, and reload clears it and loads again', async () => {
		const { loader } = setup('/a');
		calls[0].reject(new Error('boom'));
		await settle();
		expect((loader.error as Error).message).toBe('boom');
		loader.reload();
		flushSync();
		expect(loader.error).toBeNull();
		expect(calls).toHaveLength(2);
		calls[1].resolve('again');
		await settle();
		expect(loader.result).toBe('again');
	});

	it('loads nothing for a null URL', () => {
		setup(null);
		expect(calls).toHaveLength(0);
	});

	it('a page past the last goes to onPastEnd, not result', async () => {
		const onPastEnd = vi.fn();
		const { loader } = setup('/a', onPastEnd);
		calls[0].resolve({ items: [], total: 5 });
		await settle();
		expect(onPastEnd).toHaveBeenCalledWith({ items: [], total: 5 });
		expect(loader.result).toBeNull();
	});

	it('an empty report is a result', async () => {
		const onPastEnd = vi.fn();
		const { loader } = setup('/a', onPastEnd);
		calls[0].resolve({ items: [], total: 0 });
		await settle();
		expect(onPastEnd).not.toHaveBeenCalled();
		expect(loader.result).toEqual({ items: [], total: 0 });
	});
});
