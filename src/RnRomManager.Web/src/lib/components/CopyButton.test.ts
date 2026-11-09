import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import CopyButton from './CopyButton.svelte';

describe('CopyButton', () => {
	it('defaults to the icon-only link look', () => {
		render(CopyButton, { value: 'x', label: 'SHA1' });
		const button = screen.getByRole('button', { name: 'Copy SHA1' });
		expect(button.className).toBe('btn btn-sm btn-link p-0 align-baseline');
		expect(button.textContent?.trim()).toBe('');
	});

	it('variant="button" is an outlined small button reading Copy (J1)', () => {
		render(CopyButton, { value: 'x', label: 'info.json', variant: 'button' });
		const button = screen.getByRole('button', { name: 'Copy info.json' });
		expect(button.className).toBe('btn btn-sm btn-outline-secondary');
		expect(button.querySelector('.bi-clipboard')).not.toBeNull();
		expect(button.textContent?.trim()).toBe('Copy');
	});
});
