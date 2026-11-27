import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';

const tooltips = vi.hoisted(() => [] as string[]);
vi.mock('#lib/tooltip.js', () => ({
	tooltip: (_node: HTMLElement, text: string) => {
		tooltips.push(text);
	}
}));

import IconButton from './IconButton.svelte';

describe('IconButton', () => {
	it('renders a small link button with the label, tooltip and default tone (I1)', async () => {
		tooltips.length = 0;
		const onclick = vi.fn();
		render(IconButton, { icon: 'edit', label: 'Quick edit', tooltip: 'Edit Metadata', onclick });
		const button = screen.getByRole('button', { name: 'Quick edit' });
		expect(button.className).toBe('btn btn-sm btn-link text-body-secondary p-0');
		expect(button.querySelector('i.bi')).not.toBeNull();
		expect(tooltips).toEqual(['Edit Metadata']);
		await fireEvent.click(button);
		expect(onclick).toHaveBeenCalledOnce();
	});

	it('defaults the tooltip to the label and maps the tone and extra class', () => {
		tooltips.length = 0;
		render(IconButton, { icon: 'delete', label: 'Delete x', tone: 'danger', class: 'ms-auto' });
		const button = screen.getByRole('button', { name: 'Delete x' });
		expect(button.className).toBe('btn btn-sm btn-link text-danger-emphasis p-0 ms-auto');
		expect(tooltips).toEqual(['Delete x']);
	});

	it('passes disabled and extra attributes to the button', () => {
		render(IconButton, {
			icon: 'play',
			label: 'Play',
			tone: 'success',
			disabled: true,
			'aria-expanded': false
		});
		const button = screen.getByRole<HTMLButtonElement>('button', { name: 'Play' });
		expect(button.disabled).toBe(true);
		expect(button.getAttribute('aria-expanded')).toBe('false');
		expect(button.classList.contains('text-success-emphasis')).toBe(true);
	});

	it('renders an <a download> link with href', () => {
		render(IconButton, {
			icon: 'download',
			label: 'Download a.zip',
			tone: 'primary',
			href: '/files/a.zip',
			download: ''
		});
		const link = screen.getByRole('link', { name: 'Download a.zip' });
		expect(link.getAttribute('href')).toBe('/files/a.zip');
		expect(link.hasAttribute('download')).toBe(true);
		expect(link.className).toBe('btn btn-sm btn-link text-primary-emphasis p-0');
	});
});
