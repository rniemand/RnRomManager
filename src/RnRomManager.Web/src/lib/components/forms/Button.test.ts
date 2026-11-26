import { describe, expect, it, vi } from 'vitest';
import { createRawSnippet } from 'svelte';
import { fireEvent, render, screen } from '@testing-library/svelte';
import Button from './Button.svelte';

const text = (t: string) => createRawSnippet(() => ({ render: () => `<span>${t}</span>` }));

describe('Button', () => {
	it('defaults to type="button" and renders its children', () => {
		render(Button, { variant: 'primary', children: text('Save') });
		const button = screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement;
		expect(button.type).toBe('button');
		expect(button.className).toBe('btn btn-primary');
		expect(button.disabled).toBe(false);
	});

	it.each([
		[{ variant: 'danger' }, 'btn btn-danger'],
		[{ variant: 'secondary', outline: true }, 'btn btn-outline-secondary'],
		[
			{ variant: 'secondary', outline: true, size: 'sm', class: 'ms-2' },
			'btn btn-sm btn-outline-secondary ms-2'
		],
		[{ variant: 'link', class: 'btn-sm p-0' }, 'btn btn-link btn-sm p-0'],
		[{ variant: 'primary', size: 'lg' }, 'btn btn-lg btn-primary'],
		[{ class: 'btn-close' }, 'btn-close'],
		[{}, '']
	])('%o → "%s"', (props, expected) => {
		render(Button, { ...props, 'aria-label': 'B' } as never);
		expect(screen.getByRole('button', { name: 'B' }).className).toBe(expected);
	});

	it('an icon precedes the children, separated by a space', () => {
		render(Button, { variant: 'primary', icon: 'download', children: text('Get') });
		const button = screen.getByRole('button', { name: 'Get' });
		expect(button.firstElementChild!.tagName).toBe('I');
		expect(button.firstElementChild!.getAttribute('aria-hidden')).toBe('true');
		expect(button.textContent).toBe(' Get');
	});

	it('busy shows the spinner in place of the icon and disables the button', () => {
		render(Button, { variant: 'primary', icon: 'download', busy: true, children: text('Get') });
		const button = screen.getByRole('button', { name: 'Get' }) as HTMLButtonElement;
		expect(button.querySelector('i')).toBeNull();
		expect(button.firstElementChild!.className).toBe('spinner-border spinner-border-sm');
		expect(button.disabled).toBe(true);
	});

	it('busy without an icon puts the spinner before the children', () => {
		render(Button, { variant: 'primary', busy: true, children: text('Save') });
		const button = screen.getByRole('button', { name: 'Save' });
		expect(button.firstElementChild!.className).toBe('spinner-border spinner-border-sm');
		expect(button.textContent).toBe(' Save');
	});

	it('disabled disables it; type, handlers and other attributes go through; ref binds', async () => {
		const onclick = vi.fn();
		let ref: HTMLButtonElement | HTMLAnchorElement | undefined;
		render(Button, {
			variant: 'primary',
			type: 'submit',
			title: 'Go',
			'aria-label': 'Go',
			onclick,
			get ref() {
				return ref;
			},
			set ref(el) {
				ref = el;
			}
		});
		const button = screen.getByRole('button', { name: 'Go' }) as HTMLButtonElement;
		expect(button.type).toBe('submit');
		expect(button.title).toBe('Go');
		expect(ref).toBe(button);
		await fireEvent.click(button);
		expect(onclick).toHaveBeenCalledOnce();
	});

	it('disabled renders a disabled button', () => {
		render(Button, { variant: 'primary', disabled: true, 'aria-label': 'X' });
		expect((screen.getByRole('button', { name: 'X' }) as HTMLButtonElement).disabled).toBe(true);
	});

	it('href renders an anchor with the btn classes', () => {
		render(Button, {
			variant: 'secondary',
			outline: true,
			size: 'sm',
			href: '/games/1',
			icon: 'library',
			children: text('Open')
		});
		const link = screen.getByRole('link', { name: 'Open' });
		expect(link.getAttribute('href')).toBe('/games/1');
		expect(link.className).toBe('btn btn-sm btn-outline-secondary');
		expect(link.hasAttribute('type')).toBe(false);
	});
});
