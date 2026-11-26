import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { debounced } from './debounce';

describe('debounced', () => {
	beforeEach(() => vi.useFakeTimers());
	afterEach(() => vi.useRealTimers());

	it('calls once, after the delay, with the last arguments', () => {
		const fn = vi.fn();
		const call = debounced(fn, 300);
		call('a');
		vi.advanceTimersByTime(200);
		call('b');
		vi.advanceTimersByTime(299);
		expect(fn).not.toHaveBeenCalled();
		vi.advanceTimersByTime(1);
		expect(fn).toHaveBeenCalledOnce();
		expect(fn).toHaveBeenCalledWith('b');
	});

	it('cancel drops the pending call', () => {
		const fn = vi.fn();
		const call = debounced(fn, 250);
		call();
		call.cancel();
		vi.advanceTimersByTime(1000);
		expect(fn).not.toHaveBeenCalled();
		call();
		vi.advanceTimersByTime(250);
		expect(fn).toHaveBeenCalledOnce();
	});
});
