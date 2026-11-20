import { describe, expect, it } from 'vitest';
import {
	formatStep,
	formatSummary,
	isCancellable,
	jobLabel,
	latestJobs,
	outcomeClass,
	percent,
	severityClass,
	splitSummary,
	statusClass,
	stepPercent
} from './jobs';

describe('job display helpers', () => {
	it('labels every job type (ROM Zip Config B17); an unknown type shows as is', () => {
		expect(
			(
				[
					'Ingest',
					'Integrity',
					'DatRefresh',
					'MediaRetry',
					'ReviewResolve',
					'StorageMigration',
					'ZipRepack',
					'SaveFolders'
				] as const
			).map(jobLabel)
		).toEqual([
			'Ingest',
			'Integrity check',
			'DAT refresh',
			'Media retry',
			'Review',
			'Moving ROMs',
			'Repacking ZIPs',
			'Moving saves to user folders'
		]);
		expect(jobLabel('Other')).toBe('Other');
	});

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
		expect(outcomeClass('Repacked')).toBe('text-bg-success');
		expect(outcomeClass('Moved')).toBe('text-bg-success');
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

describe('current file step (large ROM import progress B7)', () => {
	const one = (n: number) =>
		new Intl.NumberFormat(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(
			n
		);
	const GiB = 1024 ** 3;
	const MiB = 1024 ** 2;
	const step = (over: Partial<Parameters<typeof formatStep>[0]> = {}) => ({
		phase: 'Hashing',
		bytesDone: 2 * GiB,
		bytesTotal: 8 * GiB,
		bytesPerSecond: 100 * MiB,
		...over
	});

	it.each([
		['Hashing', 'Hashing'],
		['CopyingToLibrary', 'Copying to library'],
		['Copying', 'Copying'],
		['Verifying', 'Verifying']
	])('labels %s as %s', (phase, label) =>
		expect(formatStep(step({ phase, bytesPerSecond: null }))).toBe(
			`${label} · ${one(2)} GiB / ${one(8)} GiB`
		)
	);

	it('adds speed and minutes left (rounded up)', () => {
		expect(formatStep(step())).toBe(
			`Hashing · ${one(2)} GiB / ${one(8)} GiB · ${one(100)} MiB/s · about 2 min left`
		);
	});

	it('shows seconds under a minute', () => {
		expect(formatStep(step({ bytesDone: 8 * GiB - 50 * 100 * MiB }))).toContain('about 50 s left');
	});

	it('leaves time out when nothing is left or the total is 0', () => {
		expect(formatStep(step({ bytesDone: 8 * GiB }))).not.toContain('left');
		expect(formatStep(step({ bytesDone: 0, bytesTotal: 0 }))).toBe(
			`Hashing · 0 B / 0 B · ${one(100)} MiB/s`
		);
	});

	it('percent is 0 without a total and capped at 100', () => {
		expect(stepPercent(step())).toBe(25);
		expect(stepPercent(step({ bytesTotal: 0 }))).toBe(0);
		expect(stepPercent(step({ bytesDone: 9 * GiB }))).toBe(100);
	});
});

describe('isCancellable', () => {
	it('is false only for ReviewResolve (B4)', () => {
		expect(
			['Ingest', 'Integrity', 'DatRefresh', 'MediaRetry', 'ReviewResolve'].map(isCancellable)
		).toEqual([true, true, true, true, false]);
	});

	it('splits a summary: flat name maps become tables, everything else a badge', () => {
		const { badges, tables } = splitSummary({
			files: 2,
			versions: { 'Nintendo - Game Boy': '2026.11.14', 'Sega - 32X': '2026.10.07' },
			errors: { title: ['Bad.'] },
			gameId: null,
			empty: {}
		});
		expect(badges).toEqual([
			['files', 2],
			['errors', { title: ['Bad.'] }],
			['gameId', null],
			['empty', {}]
		]);
		expect(tables).toEqual([
			{
				key: 'versions',
				title: 'DAT versions',
				columns: ['DAT', 'Version'],
				rows: [
					['Nintendo - Game Boy', '2026.11.14'],
					['Sega - 32X', '2026.10.07']
				]
			}
		]);
		expect(splitSummary(null)).toEqual({ badges: [], tables: [] });
	});
});
