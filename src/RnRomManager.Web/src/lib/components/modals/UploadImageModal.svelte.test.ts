import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import { flushSync } from 'svelte';

vi.mock('#lib/bootstrap.js', () => ({
	Modal: class {
		show() {}
		hide() {}
		dispose() {}
	}
}));
vi.mock('#lib/modal.js', () => ({ releaseModal: () => {} }));
const post = vi.hoisted(() => vi.fn());
vi.mock('#lib/api/client.js', () => ({ api: { post } }));

const { default: UploadImageModal } = await import('./UploadImageModal.svelte');

function setup(initialType?: 'BoxArt' | 'Snap') {
	const state = $state({ open: false });
	const onchanged = vi.fn();
	const onfailed = vi.fn();
	render(UploadImageModal, {
		gameId: 'g 1',
		initialType,
		onchanged,
		onfailed,
		get open() {
			return state.open;
		},
		set open(v) {
			state.open = v;
		}
	});
	state.open = true;
	flushSync();
	return { state, onchanged, onfailed };
}

describe('UploadImageModal', () => {
	it('initialType Snap opens on Screenshot, each time it opens (IM4)', () => {
		const { state } = setup('Snap');
		const shot = () =>
			screen.getByRole('radio', { name: 'Screenshot', hidden: true }) as HTMLInputElement;
		expect(shot().checked).toBe(true);
		(screen.getByRole('radio', { name: 'Box art', hidden: true }) as HTMLInputElement).click();
		flushSync();
		state.open = false;
		flushSync();
		state.open = true;
		flushSync();
		expect(shot().checked).toBe(true);
	});

	it('opens on URL and Box art; an empty URL is an error and nothing is sent', async () => {
		setup();
		expect(
			(screen.getByRole('radio', { name: 'URL', hidden: true }) as HTMLInputElement).checked
		).toBe(true);
		expect(
			(screen.getByRole('radio', { name: 'Box art', hidden: true }) as HTMLInputElement).checked
		).toBe(true);
		await fireEvent.click(screen.getByRole('button', { name: 'Upload', hidden: true }));
		expect(post).not.toHaveBeenCalled();
		expect(screen.getByLabelText('Image URL').className).toBe('form-control is-invalid');
	});

	it('Upload sends the URL and type, reports the detail and closes', async () => {
		post.mockResolvedValueOnce({ id: 'g 1' });
		const { state, onchanged } = setup();
		await fireEvent.input(screen.getByLabelText('Image URL'), {
			target: { value: 'https://example.com/a.png' }
		});
		await fireEvent.click(screen.getByRole('radio', { name: 'Screenshot', hidden: true }));
		await fireEvent.click(screen.getByRole('button', { name: 'Upload', hidden: true }));
		await waitFor(() => expect(onchanged).toHaveBeenCalledWith({ id: 'g 1' }));
		expect(post).toHaveBeenCalledWith('/api/games/g%201/images/from-url', {
			url: 'https://example.com/a.png',
			type: 'Snap'
		});
		expect(state.open).toBe(false);
	});

	it('File without a file is an error; Cancel closes', async () => {
		post.mockClear();
		const { state } = setup();
		await fireEvent.click(screen.getByRole('radio', { name: 'File', hidden: true }));
		await fireEvent.click(screen.getByRole('button', { name: 'Upload', hidden: true }));
		expect(post).not.toHaveBeenCalled();
		expect(document.querySelector('.invalid-feedback')?.textContent).toBe('Choose an image file.');
		await fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: true }));
		expect(state.open).toBe(false);
	});
});
