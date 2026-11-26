import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import Switch from './Switch.svelte';

describe('Switch', () => {
	it('a form-check form-switch wrapper with a role="switch" input and its label', () => {
		const { container } = render(Switch, { id: 'hack', label: 'Hack', wrapperClass: 'mb-3' });
		const input = screen.getByRole('switch', { name: 'Hack' });
		expect(container.firstElementChild!.className).toBe('form-check form-switch mb-3');
		expect(input.className).toBe('form-check-input');
		expect((input as HTMLInputElement).type).toBe('checkbox');
	});

	it('binds checked and ref, and forwards onchange', async () => {
		let checked = true;
		let ref: HTMLInputElement | undefined;
		const onchange = vi.fn();
		render(Switch, {
			id: 'on',
			label: 'On',
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
		const input = screen.getByRole('switch', { name: 'On' }) as HTMLInputElement;
		expect(input.checked).toBe(true);
		expect(ref).toBe(input);
		await fireEvent.click(input);
		expect(checked).toBe(false);
		expect(onchange).toHaveBeenCalledOnce();
	});
});
