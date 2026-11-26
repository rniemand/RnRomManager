import { describe, expect, it, vi } from 'vitest';
import { createRawSnippet } from 'svelte';
import { fireEvent, render, screen } from '@testing-library/svelte';
import Checkbox from './Checkbox.svelte';

describe('Checkbox', () => {
	it('with a label: a form-check wrapper, the input, then its label', () => {
		const children = createRawSnippet(() => ({
			render: () => '<div class="invalid-feedback">Bad</div>'
		}));
		const { container } = render(Checkbox, {
			id: 'agree',
			label: 'Agree',
			wrapperClass: 'mb-3',
			class: 'is-invalid',
			children
		});
		const input = screen.getByRole('checkbox', { name: 'Agree' });
		const wrapper = container.firstElementChild!;
		expect(wrapper.className).toBe('form-check mb-3');
		expect(input.className).toBe('form-check-input is-invalid');
		expect(wrapper.querySelector('label')!.className).toBe('form-check-label');
		expect(wrapper.lastElementChild!.className).toBe('invalid-feedback');
	});

	it('inline adds form-check-inline', () => {
		const { container } = render(Checkbox, { id: 'x', label: 'X', inline: true });
		expect(container.firstElementChild!.className).toBe('form-check form-check-inline');
	});

	it('without a label: only the bare input', () => {
		const { container } = render(Checkbox, { 'aria-label': 'Select row', class: 'm-0' });
		const input = screen.getByRole('checkbox', { name: 'Select row' });
		expect(container.firstElementChild).toBe(input);
		expect(input.className).toBe('form-check-input m-0');
	});

	it('binds checked, indeterminate and ref, and forwards onchange', async () => {
		let checked = false;
		let ref: HTMLInputElement | undefined;
		const onchange = vi.fn();
		render(Checkbox, {
			'aria-label': 'All',
			indeterminate: true,
			onchange,
			get checked() {
				return checked;
			},
			set checked(v) {
				checked = v;
			},
			get ref() {
				return ref;
			},
			set ref(el) {
				ref = el;
			}
		});
		const input = screen.getByRole('checkbox', { name: 'All' }) as HTMLInputElement;
		expect(ref).toBe(input);
		expect(input.indeterminate).toBe(true);
		await fireEvent.click(input);
		expect(checked).toBe(true);
		expect(onchange).toHaveBeenCalledOnce();
	});

	it('type="radio" reproduces bind:group', async () => {
		let group: unknown = 'a';
		const onchange = vi.fn();
		const props = (value: string) => ({
			type: 'radio' as const,
			name: 'g',
			id: `g-${value}`,
			label: value.toUpperCase(),
			value,
			onchange,
			get group() {
				return group;
			},
			set group(v) {
				group = v;
			}
		});
		render(Checkbox, props('a'));
		render(Checkbox, props('b'));
		const a = screen.getByRole('radio', { name: 'A' }) as HTMLInputElement;
		const b = screen.getByRole('radio', { name: 'B' }) as HTMLInputElement;
		expect([a.checked, b.checked]).toEqual([true, false]);
		expect(b.className).toBe('form-check-input');
		await fireEvent.click(b);
		expect(group).toBe('b');
		expect(onchange).toHaveBeenCalledOnce();
	});
});
