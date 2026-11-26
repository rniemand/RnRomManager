import { describe, expect, it } from 'vitest';
import { createRawSnippet } from 'svelte';
import { render, screen } from '@testing-library/svelte';
import FormField from './FormField.svelte';

const control = createRawSnippet(() => ({ render: () => '<input id="name" />' }));

describe('FormField', () => {
	it('renders a form-label for the control, then the control', () => {
		const { container } = render(FormField, { label: 'Name', for: 'name', children: control });
		const label = container.querySelector('label')!;
		expect(label.className).toBe('form-label');
		expect(label.nextElementSibling).toBe(screen.getByLabelText('Name'));
	});

	it('appends labelClass', () => {
		const { container } = render(FormField, {
			label: 'Name',
			for: 'name',
			labelClass: 'small mb-1',
			children: control
		});
		expect(container.querySelector('label')!.className).toBe('form-label small mb-1');
	});

	it('hiddenLabel keeps the label for screen readers only', () => {
		const { container } = render(FormField, {
			label: 'Name',
			for: 'name',
			hiddenLabel: true,
			children: control
		});
		expect(container.querySelector('label')!.className).toBe('visually-hidden');
		expect(screen.getByLabelText('Name').id).toBe('name');
	});
});
