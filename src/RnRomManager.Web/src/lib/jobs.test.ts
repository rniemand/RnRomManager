import { describe, expect, it } from 'vitest';
import {
	formatSummary,
	latestJobs,
	outcomeClass,
	percent,
	severityClass,
	statusClass
} from './jobs';

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

describe('integrity helpers', () => {
	it('merges histories newest first', () => {
		const a = [{ startedAt: '2026-11-03T00:00:00Z' }, { startedAt: '2026-11-01T00:00:00Z' }];
		const b = [{ startedAt: '2026-11-02T00:00:00Z' }];
		expect(latestJobs([a, b], 2).map((j) => j.startedAt)).toEqual([
			'2026-11-03T00:00:00Z',
			'2026-11-02T00:00:00Z'
		]);
	});

	it('severity badges', () => {
		expect(severityClass('Warn')).toBe('text-bg-warning');
		expect(severityClass('x')).toBe('text-bg-secondary');
	});
});
