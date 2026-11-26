import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { ComponentProps } from 'svelte';
import type { ReviewDetail } from '#lib/types/index.js';
import ReviewAttachTab from './ReviewAttachTab.svelte';

const item = { id: 1, system: 'psx', fileSets: true, addOns: false } as ReviewDetail;

function setup(props: Partial<ComponentProps<typeof ReviewAttachTab>> = {}) {
	const onsubmit = vi.fn((e: SubmitEvent) => e.preventDefault());
	render(ReviewAttachTab, {
		item,
		attachFlags: ['Release', 'Hack', 'Translated', 'Disc'],
		attachTarget: null,
		attachFlag: 'Release',
		attachDisc: null,
		disabled: false,
		onsubmit,
		...props
	});
	return onsubmit;
}

describe('ReviewAttachTab', () => {
	it('offers the flags it is given and shows the disc field only for Disc', async () => {
		setup();

		expect(screen.getByText(/another disc/)).toBeTruthy();
		expect(screen.queryByRole('spinbutton', { name: 'Disc' })).toBeNull();
		await fireEvent.click(screen.getByRole('radio', { name: 'Disc' }));
		expect(screen.getByRole('spinbutton', { name: 'Disc' })).toBeTruthy();
	});

	it('shows the errors it is given and submits through onsubmit', async () => {
		const onsubmit = setup({ attachFlag: 'Disc', attachDiscError: 'Enter a disc number.' });

		expect(screen.getByText('Enter a disc number.')).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Attach' }));
		expect(onsubmit).toHaveBeenCalledOnce();
	});
});
