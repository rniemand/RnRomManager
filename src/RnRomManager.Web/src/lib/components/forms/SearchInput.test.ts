import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import SearchInput from './SearchInput.svelte';

describe('SearchInput', () => {
	it('is a search-type form-control with size and class', () => {
		render(SearchInput, { 'aria-label': 'Find', size: 'sm', class: 'flex-grow-1' });
		const input = screen.getByRole('searchbox', { name: 'Find' });
		expect(input.className).toBe('form-control form-control-sm flex-grow-1');
	});

	it('binds value and ref, and forwards handlers', async () => {
		let value = '';
		let ref: HTMLInputElement | undefined;
		const oninput = vi.fn();
		render(SearchInput, {
			'aria-label': 'Find',
			oninput,
			get value() {
				return value;
			},
			set value(v) {
				value = v ?? '';
			},
			get ref() {
				return ref;
			},
			set ref(el) {
				ref = el;
			}
		});
		const input = screen.getByRole('searchbox', { name: 'Find' });
		expect(ref).toBe(input);
		await fireEvent.input(input, { target: { value: 'mario' } });
		expect(value).toBe('mario');
		expect(oninput).toHaveBeenCalledOnce();
	});
});
