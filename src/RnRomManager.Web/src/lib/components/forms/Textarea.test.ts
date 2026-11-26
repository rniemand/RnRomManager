import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import Textarea from './Textarea.svelte';

describe('Textarea', () => {
	it('is a form-control with rows and the class prop', () => {
		render(Textarea, { 'aria-label': 'Note', rows: 4, class: 'font-monospace' });
		const area = screen.getByRole('textbox', { name: 'Note' }) as HTMLTextAreaElement;
		expect(area.className).toBe('form-control font-monospace');
		expect(area.rows).toBe(4);
	});

	it('binds value and ref, and forwards handlers', async () => {
		let value = 'x';
		let ref: HTMLTextAreaElement | undefined;
		const onblur = vi.fn();
		render(Textarea, {
			'aria-label': 'Note',
			onblur,
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
		const area = screen.getByRole('textbox', { name: 'Note' }) as HTMLTextAreaElement;
		expect(area.value).toBe('x');
		expect(ref).toBe(area);
		await fireEvent.input(area, { target: { value: 'hello' } });
		expect(value).toBe('hello');
		await fireEvent.blur(area);
		expect(onblur).toHaveBeenCalledOnce();
	});
});
