import { describe, expect, it, vi } from 'vitest';
import { createRawSnippet } from 'svelte';
import { fireEvent, render, screen } from '@testing-library/svelte';
import Select from './Select.svelte';

const options = [
	{ value: 'a', label: 'Alpha' },
	{ value: 'b', label: 'Beta', disabled: true },
	{ value: 'c', label: 'Gamma' }
];

describe('Select', () => {
	it('renders form-select, size, then the class prop, and its options', () => {
		render(Select, { 'aria-label': 'Pick', options, size: 'sm', class: 'w-auto', value: 'a' });
		const select = screen.getByRole('combobox', { name: 'Pick' }) as HTMLSelectElement;
		expect(select.className).toBe('form-select form-select-sm w-auto');
		expect([...select.options].map((o) => [o.value, o.text, o.disabled])).toEqual([
			['a', 'Alpha', false],
			['b', 'Beta', true],
			['c', 'Gamma', false]
		]);
	});

	it('renders children options when no options are given', () => {
		const children = createRawSnippet(() => ({
			render: () => '<option value="x">Ex</option>'
		}));
		render(Select, { 'aria-label': 'Pick', children });
		const select = screen.getByRole('combobox', { name: 'Pick' }) as HTMLSelectElement;
		expect(select.className).toBe('form-select');
		expect(select.options[0].text).toBe('Ex');
	});

	it('binds value and ref, and forwards handlers', async () => {
		let value: unknown = 'a';
		let ref: HTMLSelectElement | undefined;
		const onchange = vi.fn();
		render(Select, {
			'aria-label': 'Pick',
			id: 'pick',
			options,
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
		const select = screen.getByRole('combobox', { name: 'Pick' }) as HTMLSelectElement;
		expect(select.id).toBe('pick');
		expect(select.value).toBe('a');
		expect(ref).toBe(select);
		select.value = 'c';
		await fireEvent.change(select);
		expect(value).toBe('c');
		expect(onchange).toHaveBeenCalledOnce();
	});
});
