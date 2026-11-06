import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
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

describe('Logs tab', () => {
	beforeEach(() => {
		FakeEventSource.instances = [];
		vi.stubGlobal('EventSource', FakeEventSource);
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	it('opens the stream, appends lines with a level badge and has the log download', async () => {
		render(LogsPage);
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
		render(LogsPage);
		source().send(entry('debug line', 'Debug'), entry('info line'), entry('warn line', 'Warn'));

		await vi.waitFor(() => expect(lines()).toHaveLength(2));
		await fireEvent.change(screen.getByLabelText('Minimum level'), { target: { value: 'Warn' } });
		expect(lines().map((l) => l.textContent)).toEqual([expect.stringContaining('warn line')]);
		await fireEvent.change(screen.getByLabelText('Minimum level'), { target: { value: 'Trace' } });
		expect(lines()).toHaveLength(3);
	});

	it('pause buffers events and resume appends them', async () => {
		render(LogsPage);
		source().send(entry('before'));
		await fireEvent.click(screen.getByRole('button', { name: 'Pause' }));
		source().send(entry('during 1'), entry('during 2'));

		await vi.waitFor(() => expect(lines()).toHaveLength(1));
		await fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
		expect(lines().map((l) => l.textContent?.includes('during'))).toEqual([false, true, true]);
	});

	it('keeps at most 2,000 lines', async () => {
		render(LogsPage);
		source().send(...Array.from({ length: 2005 }, (_, i) => entry(`m${i}`)));

		await vi.waitFor(() => expect(lines()).toHaveLength(2000));
		expect(lines()[0].textContent).toContain('m5 ');
	});

	it('shows Reconnecting while the browser retries, and an ErrorAlert with Retry only when closed', async () => {
		render(LogsPage);
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
});
