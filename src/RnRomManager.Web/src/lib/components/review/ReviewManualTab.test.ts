import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import type { ComponentProps } from 'svelte';
import type { ReviewDetail } from '#lib/types/index.js';
import { emptyManualForm } from '#lib/reviewForm.js';
import ReviewManualTab from './ReviewManualTab.svelte';

function setup(props: Partial<ComponentProps<typeof ReviewManualTab>> = {}) {
	const onsubmit = vi.fn((e: SubmitEvent | null) => e?.preventDefault());
	render(ReviewManualTab, {
		item: { fileSets: false, hasDat: true } as ReviewDetail,
		manual: emptyManualForm(),
		manualErrors: {},
		disabled: false,
		next: 5,
		onsubmit,
		...props
	});
	return onsubmit;
}

describe('ReviewManualTab', () => {
	it('previews the file name for a title and submits', async () => {
		const onsubmit = setup({ manual: { ...emptyManualForm(), title: 'Tetris' } });

		expect(screen.getByText(/^File name: Tetris/)).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Add manually' }));
		expect(onsubmit).toHaveBeenCalledOnce();
		expect(screen.queryByRole('button', { name: 'Add and Next' })).toBeNull();
	});

	it('shows field errors, the disc field on disc systems and Add and Next without a DAT', async () => {
		const onsubmit = setup({
			item: { fileSets: true, hasDat: false } as ReviewDetail,
			manualErrors: { title: 'Enter a title.' }
		});

		expect(screen.getByText('Enter a title.')).toBeTruthy();
		expect(screen.getByLabelText('Disc')).toBeTruthy();
		await fireEvent.click(screen.getByRole('button', { name: 'Add and Next' }));
		expect(onsubmit).toHaveBeenCalledWith(null, true);
	});
});
