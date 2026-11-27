import { describe, expect, it } from 'vitest';
import { createRawSnippet } from 'svelte';
import { fireEvent, render, screen } from '@testing-library/svelte';
import AccordionItem from './AccordionItem.svelte';

const header = createRawSnippet(() => ({ render: () => '<span>Pending files (3)</span>' }));
const children = createRawSnippet(() => ({ render: () => '<input aria-label="Draft" />' }));

function renderItem(open?: boolean) {
	return render(AccordionItem, {
		id: 'pending-panel',
		headingId: 'pending-heading',
		open,
		header,
		children
	});
}

describe('AccordionItem', () => {
	it('renders Bootstrap accordion markup in its own .accordion wrapper', () => {
		const { container } = renderItem(true);
		const wrapper = container.querySelector('.accordion')!;
		expect(
			wrapper.querySelector(':scope > .accordion-item > h2.accordion-header#pending-heading')
		).not.toBeNull();
		const body = container.querySelector('#pending-panel')!;
		expect(body.className).toContain('accordion-collapse collapse show');
		expect(body.getAttribute('aria-labelledby')).toBe('pending-heading');
		expect(screen.getByRole('heading', { level: 2, name: 'Pending files (3)' })).toBeTruthy();
	});

	it('headingLevel 3 renders an h3 header (S2a)', () => {
		const { container } = render(AccordionItem, {
			id: 'saves-rom-1',
			headingId: 'saves-rom-1-heading',
			headingLevel: 3,
			header,
			children
		});
		expect(container.querySelector('h3.accordion-header#saves-rom-1-heading')).not.toBeNull();
		expect(container.querySelector('h2')).toBeNull();
	});

	it('is closed by default: collapsed button, aria-expanded false, no show', () => {
		const { container } = renderItem();
		const button = screen.getByRole('button', { name: 'Pending files (3)' });
		expect(button.classList.contains('collapsed')).toBe(true);
		expect(button.getAttribute('aria-expanded')).toBe('false');
		expect(button.getAttribute('aria-controls')).toBe('pending-panel');
		expect(container.querySelector('#pending-panel')!.classList.contains('show')).toBe(false);
	});

	it('toggles on click and keeps the closed body mounted', async () => {
		const { container } = renderItem(true);
		const button = screen.getByRole('button', { name: 'Pending files (3)' });
		const input = screen.getByLabelText<HTMLInputElement>('Draft');
		input.value = 'kept';

		await fireEvent.click(button);
		expect(button.getAttribute('aria-expanded')).toBe('false');
		expect(button.classList.contains('collapsed')).toBe(true);
		expect(container.querySelector('#pending-panel')!.classList.contains('show')).toBe(false);
		expect(container.contains(input)).toBe(true);

		await fireEvent.click(button);
		expect(button.getAttribute('aria-expanded')).toBe('true');
		expect(screen.getByLabelText<HTMLInputElement>('Draft').value).toBe('kept');
	});
});
