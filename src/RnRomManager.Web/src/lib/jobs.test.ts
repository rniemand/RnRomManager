import { describe, expect, it } from 'vitest';
import { formatSummary, outcomeClass, percent, statusClass } from './jobs';

describe('job display helpers', () => {
	it('summarises non-zero counts', () => {
		expect(formatSummary({ Ingested: 3, Duplicate: 0, Review: 1 })).toBe('Ingested 3 · Review 1');
		expect(formatSummary(null)).toBe('');
	});

	it('progress percent is bounded and 0 without a total', () => {
		expect(percent(1, 3)).toBe(33);
		expect(percent(5, 3)).toBe(100);
		expect(percent(0, 0)).toBe(0);
	});

	it('unknown statuses and outcomes get a neutral badge', () => {
		expect(statusClass('Failed')).toBe('text-bg-danger');
		expect(statusClass('Weird')).toBe('text-bg-secondary');
		expect(outcomeClass('Error')).toBe('text-bg-danger');
		expect(outcomeClass('Weird')).toBe('text-bg-secondary');
	});
});
