import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import FileInput from './FileInput.svelte';

describe('FileInput', () => {
	it('is a file form-control forwarding accept, multiple and onchange, with ref', async () => {
		const onchange = vi.fn();
		let ref: HTMLInputElement | undefined;
		render(FileInput, {
			'aria-label': 'Image',
			accept: 'image/png',
			multiple: true,
			size: 'sm',
			class: 'is-invalid',
			onchange,
			get ref() {
				return ref;
			},
			set ref(el) {
				ref = el;
			}
		});
		const input = screen.getByLabelText('Image') as HTMLInputElement;
		expect(input.type).toBe('file');
		expect(input.className).toBe('form-control form-control-sm is-invalid');
		expect(input.accept).toBe('image/png');
		expect(input.multiple).toBe(true);
		expect(ref).toBe(input);
		await fireEvent.change(input);
		expect(onchange).toHaveBeenCalledOnce();
	});
});
