import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import { api } from '#lib/api/client.js';
import { ApiError } from '#lib/api/errors.js';
import type { LogEntry } from '#lib/api/types.js';
import { auth } from '#lib/stores/auth.svelte.js';

const { default: LogsPage } = await import('./logs/+page.svelte');

/** Records every EventSource the page opens and lets a test push events and state changes. */
class FakeEventSource {
	static instances: FakeEventSource[] = [];
	readyState = 0;
	closed = false;
	onopen: (() => void) | null = null;
	onmessage: ((event: MessageEvent<string>) => void) | null = null;
	onerror: (() => void) | null = null;

	constructor(public url: string) {
		FakeEventSource.instances.push(this);
	}

	open(): void {
		this.readyState = 1;
		this.onopen?.();
	}

	send(...entries: LogEntry[]): void {
		for (const entry of entries)
			this.onmessage?.(new MessageEvent('message', { data: JSON.stringify(entry) }));
	}

	fail(readyState: number): void {
		this.readyState = readyState;
		this.onerror?.();
	}

	close(): void {
		this.closed = true;
		this.readyState = 2;
	}
}

let nextId = 1;
const entry = (message: string, level: LogEntry['level'] = 'Info'): LogEntry => ({
	id: nextId++,
	time: '2026-11-06T12:34:56.789Z',
	level,
	logger: 'RnRomManager.Test',
	message,
	exception: null
});

const lines = () => [...screen.getByRole('log').querySelectorAll(':scope > div')];
const source = () => FakeEventSource.instances.at(-1)!;
/** Each line's message (it follows the logger name). */
const text = () => lines().map((l) => /RnRomManager\.Test\s+(.*?)\s*$/.exec(l.textContent!)![1]);

/** Renders the tab and waits for the stream, which opens after the recent-logs fetch. */
async function mount(): Promise<void> {
	render(LogsPage);
	await vi.waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
}

describe('Logs tab', () => {
	let recent: ReturnType<typeof vi.spyOn>;

	beforeEach(() => {
		FakeEventSource.instances = [];
		vi.stubGlobal('EventSource', FakeEventSource);
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		recent = vi.spyOn(api, 'get').mockResolvedValue([]);
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		vi.restoreAllMocks();
		auth.set(null);
	});

	it('opens the stream, appends lines with a level badge and has the log download', async () => {
		await mount();
		expect(recent).toHaveBeenCalledWith('/api/admin/logs/recent');
		expect(source().url).toBe('/api/admin/logs/stream');
		expect(screen.getByRole('link', { name: /Download logs/ }).getAttribute('href')).toBe(
			'/api/admin/logs'
		);

		source().open();
		source().send(entry('first'), entry('broke', 'Error'));

		await vi.waitFor(() => expect(lines()).toHaveLength(2));
		expect(lines()[1].textContent).toContain('broke');
		expect(lines()[1].querySelector('.badge')?.className).toContain('text-bg-danger');
		expect(lines()[0].textContent).toContain('12:34:56.789');
	});

	it('filters by minimum level client-side (default Info)', async () => {
		await mount();
		source().send(entry('debug line', 'Debug'), entry('info line'), entry('warn line', 'Warn'));

		await vi.waitFor(() => expect(lines()).toHaveLength(2));
		await fireEvent.change(screen.getByLabelText('Minimum level'), { target: { value: 'Warn' } });
		expect(lines().map((l) => l.textContent)).toEqual([expect.stringContaining('warn line')]);
		await fireEvent.change(screen.getByLabelText('Minimum level'), { target: { value: 'Trace' } });
		expect(lines()).toHaveLength(3);
	});

	it('pause buffers events and resume appends them', async () => {
		await mount();
		source().send(entry('before'));
		await fireEvent.click(screen.getByRole('button', { name: 'Pause' }));
		source().send(entry('during 1'), entry('during 2'));

		await vi.waitFor(() => expect(lines()).toHaveLength(1));
		await fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
		expect(lines().map((l) => l.textContent?.includes('during'))).toEqual([false, true, true]);
	});

	// Rendering 2,000 rows in jsdom takes several seconds on a slow CI runner.
	it('keeps at most 2,000 lines', { timeout: 30_000 }, async () => {
		await mount();
		source().send(...Array.from({ length: 2005 }, (_, i) => entry(`m${i}`)));

		await vi.waitFor(() => expect(lines()).toHaveLength(2000), { timeout: 25_000 });
		expect(lines()[0].textContent).toContain('m5 ');
	});

	it('shows Reconnecting while the browser retries, and an ErrorAlert with Retry only when closed', async () => {
		await mount();
		source().fail(0); // CONNECTING: the browser retries by itself

		expect(await screen.findByText('Reconnecting…')).toBeTruthy();
		expect(screen.queryByRole('alert')).toBeNull();

		source().open();
		await vi.waitFor(() => expect(screen.queryByText('Reconnecting…')).toBeNull());

		source().fail(2); // CLOSED, e.g. 403
		expect(await screen.findByRole('alert')).toBeTruthy();
		const first = source();
		await fireEvent.click(screen.getByRole('button', { name: /Retry/ }));
		expect(FakeEventSource.instances).toHaveLength(2);
		expect(first.closed).toBe(true);
		expect(screen.queryByRole('alert')).toBeNull();
	});

	it('shows the fetched entries before the stream sends anything, then merges by id', async () => {
		const older = [entry('old 1'), entry('old 2')];
		const fetched = [entry('recent 1'), entry('recent 2')];
		recent.mockResolvedValue(fetched);
		await mount();

		expect(text()).toEqual(['recent 1', 'recent 2']);
		const newer = entry('new');
		source().send(...older, ...fetched, newer);

		await vi.waitFor(() => expect(lines()).toHaveLength(5));
		expect(text()).toEqual(['old 1', 'old 2', 'recent 1', 'recent 2', 'new']);
	});

	it('a failed fetch shows Retry, the stream still opens, and the refetch merges without duplicates', async () => {
		recent.mockRejectedValue(new ApiError(500, { title: 'Server error', status: 500 }));
		await mount();

		expect(await screen.findByRole('alert')).toBeTruthy();
		const streamed = entry('streamed');
		source().send(streamed);
		const before = entry('before');
		before.id = streamed.id - 10;
		recent.mockResolvedValue([before, streamed]);
		await fireEvent.click(screen.getByRole('button', { name: /Retry/ }));

		await vi.waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
		expect(text()).toEqual(['before', 'streamed']);
		expect(FakeEventSource.instances).toHaveLength(1);
	});

	it('tells an empty log from one the filter hides', async () => {
		await mount();
		expect(screen.getByText('No log events yet.')).toBeTruthy();

		source().send(entry('debug only', 'Debug'));
		expect(await screen.findByText('No log events at Info or above.')).toBeTruthy();
	});
});
