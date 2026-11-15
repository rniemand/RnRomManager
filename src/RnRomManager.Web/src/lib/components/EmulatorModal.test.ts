import { afterEach, describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/svelte';
import { flushSync } from 'svelte';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));

const { default: EmulatorModal } = await import('./EmulatorModal.svelte');

// C2: only id and file name, as a Library card passes them.
const rom = { id: 13, fileName: 'Orbit Blocks (World) (Rev 1).gb' };

describe('EmulatorModal', () => {
	it('frames the play page for the release', () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false
		});

		const iframe = container.querySelector('iframe')!;
		expect(iframe.getAttribute('src')).toBe(
			'/emulator/play.html?game=g-1&rom=13&core=gambatte&name=Orbit%20Blocks%20(World)%20(Rev%201).gb'
		);
		expect(iframe.title).toBe('Emulator: Orbit Blocks (World) (Rev 1).gb');
		expect(iframe.getAttribute('allow')).toBe('fullscreen; gamepad; autoplay');
		expect(container.querySelector('.modal-dialog')!.classList).toContain('modal-xl');
	});

	it('removes the iframe when closed', async () => {
		const { container, rerender } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom,
			disc: false
		});
		expect(container.querySelector('iframe')).not.toBeNull();

		(container.querySelector('.btn-close') as HTMLButtonElement).click();
		flushSync();
		expect(container.querySelector('iframe')).toBeNull();

		await rerender({ open: true });
		expect(container.querySelector('iframe')).not.toBeNull();
	});

	it('renders no iframe without a core', () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: null,
			rom,
			disc: false
		});
		expect(container.querySelector('iframe')).toBeNull();
	});

	it('adds disc=1 for a disc release (Sega CD B6)', () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-2',
			core: 'genesis_plus_gx',
			rom: { id: 14, fileName: 'Disc Comet (USA).cue' },
			disc: true
		});

		expect(container.querySelector('iframe')!.getAttribute('src')).toMatch(/&disc=1$/);
	});
});

describe('EmulatorModal 3DO saves (3DO B11)', () => {
	afterEach(() => vi.useRealTimers());

	const close = { type: 'rnrm-emulator-close' };

	function openFor(core: string) {
		vi.useFakeTimers();
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-3',
			core,
			rom: { id: 15, fileName: 'Opal Rally (USA).cue' },
			disc: true
		});
		const iframe = container.querySelector('iframe')!;
		const posted = vi.spyOn(iframe.contentWindow!, 'postMessage').mockImplementation(() => {});
		return { container, iframe, posted };
	}

	function reply(source: Window | null, data: unknown = { type: 'rnrm-emulator-closed' }) {
		window.dispatchEvent(new MessageEvent('message', { data, origin: location.origin, source }));
	}

	async function closeButton(container: HTMLElement) {
		(container.querySelector('.btn-close') as HTMLButtonElement).click();
		flushSync();
	}

	it('asks the player to save and closes on its reply', async () => {
		const { container, iframe, posted } = openFor('opera');

		await closeButton(container);
		expect(posted).toHaveBeenCalledWith(close, location.origin);
		expect(container.querySelector('iframe')).not.toBeNull();

		reply(window, { type: 'rnrm-emulator-closed' });
		await vi.advanceTimersByTimeAsync(0);
		flushSync();
		expect(container.querySelector('iframe')).not.toBeNull();

		reply(iframe.contentWindow);
		await vi.advanceTimersByTimeAsync(0);
		flushSync();
		expect(container.querySelector('iframe')).toBeNull();
	});

	it('closes after 3 s without a reply', async () => {
		const { container } = openFor('opera');

		await closeButton(container);
		await vi.advanceTimersByTimeAsync(2999);
		flushSync();
		expect(container.querySelector('iframe')).not.toBeNull();
		await vi.advanceTimersByTimeAsync(1);
		flushSync();
		expect(container.querySelector('iframe')).toBeNull();
	});

	it('closes at once and posts nothing for another core', async () => {
		const { container, posted } = openFor('snes9x');

		await closeButton(container);

		expect(posted).not.toHaveBeenCalled();
		expect(container.querySelector('iframe')).toBeNull();
	});
});
