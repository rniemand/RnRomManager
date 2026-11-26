import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import { flushSync } from 'svelte';
import type { GameDetail, SaveItem } from '#lib/types/index.js';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));
const savesApi = vi.hoisted(() => ({
	add: vi.fn(() => Promise.resolve()),
	update: vi.fn(() => Promise.resolve()),
	replace: vi.fn(() => Promise.resolve()),
	remove: vi.fn(() => Promise.resolve())
}));
vi.mock('#lib/saves.js', async (original) => ({
	...(await original<typeof import('#lib/saves.js')>()),
	savesApi
}));

const { default: SaveFileModal } = await import('./SaveFileModal.svelte');

const game = { id: 'g1', roms: [{ id: 5, fileName: 'a.gb' }] } as unknown as GameDetail;
const save = { id: 9, name: 'Boss', description: 'before the boss' } as unknown as SaveItem;

function setup(mode: 'add' | 'edit' | 'replace' | 'delete', item: SaveItem | null) {
	const state = $state({ open: false });
	const onsaved = vi.fn();
	render(SaveFileModal, {
		mode,
		game,
		save: item,
		onsaved,
		get open() {
			return state.open;
		},
		set open(v) {
			state.open = v;
		}
	});
	state.open = true;
	flushSync();
	return { state, onsaved };
}

describe('SaveFileModal', () => {
	it('delete names the save and removes it', async () => {
		const { state, onsaved } = setup('delete', save);
		expect(document.querySelector('.modal-body p strong')?.textContent).toBe('Boss');
		const del = screen.getByRole('button', { name: 'Delete save', hidden: true });
		expect(del.classList.contains('btn-danger')).toBe(true);
		await fireEvent.click(del);
		await waitFor(() => expect(onsaved).toHaveBeenCalledOnce());
		expect(savesApi.remove).toHaveBeenCalledWith('g1', 9);
		expect(state.open).toBe(false);
	});

	it('edit starts from the save and sends the new name', async () => {
		const { onsaved } = setup('edit', save);
		const name = screen.getByLabelText('Name') as HTMLInputElement;
		expect(name.value).toBe('Boss');
		expect((screen.getByLabelText('Description') as HTMLTextAreaElement).value).toBe(
			'before the boss'
		);
		await fireEvent.input(name, { target: { value: 'Final boss' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Save', hidden: true }));
		await waitFor(() => expect(onsaved).toHaveBeenCalledOnce());
		expect(savesApi.update).toHaveBeenCalledWith('g1', 9, {
			name: 'Final boss',
			description: 'before the boss'
		});
	});

	it('add shows the release, kind radios and file, and refuses without a file; Cancel closes', async () => {
		const { state, onsaved } = setup('add', null);
		expect((screen.getByLabelText('Release') as HTMLSelectElement).value).toBe('5');
		const radios = screen.getAllByRole('radio', { hidden: true }) as HTMLInputElement[];
		expect(radios.map((r) => r.checked)).toEqual([true, false]);
		expect(radios[0].closest('div')!.className).toBe('form-check form-check-inline');
		await fireEvent.input(screen.getByLabelText('Name'), { target: { value: 'New' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Add save', hidden: true }));
		expect(savesApi.add).not.toHaveBeenCalled();
		expect(onsaved).not.toHaveBeenCalled();
		expect(screen.getByLabelText('File').className).toBe('form-control is-invalid');
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(state.open).toBe(false);
	});
});
