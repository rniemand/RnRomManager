import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import TextInput from './TextInput.svelte';

describe('TextInput', () => {
	it('renders a text form-control', () => {
		render(TextInput, { 'aria-label': 'Name' });
		const input = screen.getByLabelText('Name') as HTMLInputElement;
		expect(input.className).toBe('form-control');
		expect(input.type).toBe('text');
	});

	it('size, then the class prop', () => {
		render(TextInput, { 'aria-label': 'Name', size: 'sm', class: 'flex-grow-1', type: 'email' });
		const input = screen.getByLabelText('Name') as HTMLInputElement;
		expect(input.className).toBe('form-control form-control-sm flex-grow-1');
		expect(input.type).toBe('email');
	});

	it('binds value both ways', async () => {
		let value = 'a';
		render(TextInput, {
			'aria-label': 'Name',
			get value() {
				return value;
			},
			set value(v) {
				value = v ?? '';
			}
		});
		const input = screen.getByLabelText('Name') as HTMLInputElement;
		expect(input.value).toBe('a');
		await fireEvent.input(input, { target: { value: 'abc' } });
		expect(value).toBe('abc');
	});

	it('forwards other attributes and handlers, and binds ref', async () => {
		const onkeydown = vi.fn();
		let ref: HTMLInputElement | undefined;
		render(TextInput, {
			id: 'q',
			maxlength: 5,
			'aria-label': 'Name',
			onkeydown,
			get ref() {
				return ref;
			},
			set ref(el) {
				ref = el;
			}
		});
		const input = screen.getByLabelText('Name') as HTMLInputElement;
		expect(input.id).toBe('q');
		expect(input.maxLength).toBe(5);
		expect(ref).toBe(input);
		await fireEvent.keyDown(input, { key: 'Enter' });
		expect(onkeydown).toHaveBeenCalledOnce();
	});
});
