import { describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/svelte';
import { flushSync } from 'svelte';
import type { RomDetail } from '../api/types';

vi.mock('../bootstrap', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('../modal', () => ({ releaseModal: () => {} }));

const { default: EmulatorModal } = await import('./EmulatorModal.svelte');

const rom = { id: 13, fileName: 'Orbit Blocks (World) (Rev 1).gb' } as RomDetail;

describe('EmulatorModal', () => {
	it('frames the play page for the release', () => {
		const { container } = render(EmulatorModal, {
			open: true,
			gameId: 'g-1',
			core: 'gambatte',
			rom
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
			rom
		});
		expect(container.querySelector('iframe')).not.toBeNull();

		(container.querySelector('.btn-close') as HTMLButtonElement).click();
		flushSync();
		expect(container.querySelector('iframe')).toBeNull();

		await rerender({ open: true });
		expect(container.querySelector('iframe')).not.toBeNull();
	});

	it('renders no iframe without a core', () => {
		const { container } = render(EmulatorModal, { open: true, gameId: 'g-1', core: null, rom });
		expect(container.querySelector('iframe')).toBeNull();
	});
});
