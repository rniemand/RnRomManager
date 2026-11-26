import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import NumberInput from './NumberInput.svelte';

describe('NumberInput', () => {
	it('is a number form-control with min, max and step', () => {
		render(NumberInput, { 'aria-label': 'Count', min: 1, max: 9, step: 2, class: 'w-auto' });
		const input = screen.getByRole('spinbutton', { name: 'Count' }) as HTMLInputElement;
		expect(input.className).toBe('form-control w-auto');
		expect([input.min, input.max, input.step]).toEqual(['1', '9', '2']);
	});

	it('binds a number, binds ref and forwards handlers', async () => {
		let value: number | null | undefined = 3;
		let ref: HTMLInputElement | undefined;
		const onchange = vi.fn();
		render(NumberInput, {
			'aria-label': 'Count',
			size: 'sm',
			onchange,
			get value() {
				return value;
			},
			set value(v) {
				value = v;
			},
			get ref() {
				return ref;
			},
			set ref(el) {
				ref = el;
			}
		});
		const input = screen.getByRole('spinbutton', { name: 'Count' }) as HTMLInputElement;
		expect(input.className).toBe('form-control form-control-sm');
		expect(input.value).toBe('3');
		expect(ref).toBe(input);
		await fireEvent.input(input, { target: { value: '7' } });
		expect(value).toBe(7);
		await fireEvent.change(input);
		expect(onchange).toHaveBeenCalledOnce();
	});
});
