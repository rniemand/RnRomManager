import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/svelte';
import ToastHost from './ToastHost.svelte';
import { toasts, toastDurationMs } from '../stores/toasts.svelte';

describe('ToastHost pause on hover and focus (B2)', () => {
	beforeEach(() => {
		vi.useFakeTimers();
		toasts.items = [];
	});
	afterEach(() => vi.useRealTimers());

	function showLinked() {
		toasts.show('linked', 'success', { href: '/games/g1', text: 'Open game' });
		render(ToastHost);
		return {
			toast: screen.getByRole('status'),
			link: screen.getByRole('link', { name: 'Open game' }),
			close: screen.getByRole('button', { name: 'Close' })
		};
	}

	const texts = () => toasts.items.map((t) => t.text);

	it('mouseenter pauses and mouseleave gives a fresh 5 s', async () => {
		const { toast } = showLinked();
		await fireEvent.mouseEnter(toast);
		vi.advanceTimersByTime(toastDurationMs * 2);
		expect(texts()).toEqual(['linked']);

		await fireEvent.mouseLeave(toast);
		vi.advanceTimersByTime(toastDurationMs - 1);
		expect(texts()).toEqual(['linked']);
		vi.advanceTimersByTime(1);
		expect(texts()).toEqual([]);
	});

	it('focusin pauses and focusout out of the toast resumes', async () => {
		const { link } = showLinked();
		await fireEvent.focusIn(link);
		vi.advanceTimersByTime(toastDurationMs * 2);
		expect(texts()).toEqual(['linked']);

		await fireEvent.focusOut(link, { relatedTarget: document.body });
		vi.advanceTimersByTime(toastDurationMs);
		expect(texts()).toEqual([]);
	});

	it('mouseleave while focus is inside does not resume', async () => {
		const { toast, link } = showLinked();
		await fireEvent.mouseEnter(toast);
		await fireEvent.focusIn(link);
		await fireEvent.mouseLeave(toast);
		vi.advanceTimersByTime(toastDurationMs * 2);
		expect(texts()).toEqual(['linked']);
	});

	it('focusout while hovered does not resume', async () => {
		const { toast, link } = showLinked();
		await fireEvent.mouseEnter(toast);
		await fireEvent.focusIn(link);
		await fireEvent.focusOut(link, { relatedTarget: document.body });
		vi.advanceTimersByTime(toastDurationMs * 2);
		expect(texts()).toEqual(['linked']);
	});

	it('focus moving from the link to the close button does not resume', async () => {
		const { link, close } = showLinked();
		await fireEvent.focusIn(link);
		await fireEvent.focusOut(link, { relatedTarget: close });
		vi.advanceTimersByTime(toastDurationMs * 2);
		expect(texts()).toEqual(['linked']);
	});

	it('pausing one toast leaves the others on their timers', async () => {
		toasts.show('other');
		toasts.show('linked', 'success', { href: '/games/g1', text: 'Open game' });
		render(ToastHost);
		await fireEvent.mouseEnter(screen.getAllByRole('status')[1]);
		vi.advanceTimersByTime(toastDurationMs);
		expect(texts()).toEqual(['linked']);
	});
});
