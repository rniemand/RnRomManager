import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import RadioButtons from './RadioButtons.svelte';

const options = [
	{ value: 'url', label: 'URL' },
	{ value: 'file', label: 'File', id: 'custom-file' }
];

describe('RadioButtons', () => {
	it('renders a btn-group of btn-check radios with outline labels, variant before size', () => {
		const { container } = render(RadioButtons, {
			name: 'source',
			options,
			value: 'url',
			size: 'sm',
			class: 'mb-3',
			role: 'radiogroup',
			'aria-label': 'Image source'
		});
		const group = screen.getByRole('radiogroup', { name: 'Image source' });
		expect(group).toBe(container.firstElementChild);
		expect(group.className).toBe('btn-group mb-3');
		const url = screen.getByRole('radio', { name: 'URL' }) as HTMLInputElement;
		const file = screen.getByRole('radio', { name: 'File' }) as HTMLInputElement;
		expect(url.className).toBe('btn-check');
		expect([url.id, file.id]).toEqual(['source-url', 'custom-file']);
		expect([url.name, file.name]).toEqual(['source', 'source']);
		expect([url.checked, file.checked]).toEqual([true, false]);
		expect(group.querySelector('label')!.className).toBe('btn btn-outline-secondary btn-sm');
	});

	it('binds value and reports changes', async () => {
		let value = 'url';
		const onchange = vi.fn();
		render(RadioButtons, {
			name: 'source',
			options,
			onchange,
			get value() {
				return value;
			},
			set value(v) {
				value = v ?? '';
			}
		});
		await fireEvent.click(screen.getByRole('radio', { name: 'File' }));
		expect(value).toBe('file');
		expect(onchange).toHaveBeenCalledWith('file');
	});

	it('disabled disables every radio', () => {
		render(RadioButtons, { name: 'source', options, disabled: true });
		const radios = screen.getAllByRole('radio') as HTMLInputElement[];
		expect(radios.every((r) => r.disabled)).toBe(true);
	});
});
