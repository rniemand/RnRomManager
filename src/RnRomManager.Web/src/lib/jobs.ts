/** Display helpers for jobs (spec §3.1 statuses, §7.2 outcomes). */
import type { CurrentJob, JobStatus, JobStep } from './api/types';
import { formatSize } from './format';

export const outcomes = [
	'Ingested',
	'Repaired',
	'Repacked',
	'Moved',
	'Duplicate',
	'Review',
	'Failed',
	'Error',
	'Skipped',
	'Pulled',
	'NoCover',
	'ImageLimit',
	'Updated',
	'NoRating',
	'NotFound'
];

const statusClasses: Record<JobStatus, string> = {
	Running: 'text-bg-primary',
	Completed: 'text-bg-success',
	Cancelled: 'text-bg-secondary',
	Failed: 'text-bg-danger',
	Aborted: 'text-bg-warning'
};

const outcomeClasses: Record<string, string> = {
	Ingested: 'text-bg-success',
	Repaired: 'text-bg-success',
	Repacked: 'text-bg-success',
	Moved: 'text-bg-success',
	Duplicate: 'text-bg-secondary',
	Review: 'text-bg-info',
	Failed: 'text-bg-warning',
	Error: 'text-bg-danger',
	Skipped: 'text-bg-light',
	Pulled: 'text-bg-success',
	NoCover: 'text-bg-secondary',
	ImageLimit: 'text-bg-warning',
	Updated: 'text-bg-success',
	NoRating: 'text-bg-secondary',
	NotFound: 'text-bg-warning'
};

export function statusClass(status: string): string {
	return statusClasses[status as JobStatus] ?? 'text-bg-secondary';
}

export function outcomeClass(outcome: string): string {
	return outcomeClasses[outcome] ?? 'text-bg-secondary';
}

const jobLabels: Record<CurrentJob['type'], string> = {
	Ingest: 'Ingest',
	Integrity: 'Integrity Check',
	DatRefresh: 'DAT Refresh',
	MediaRetry: 'Media Retry',
	ReviewResolve: 'Review',
	StorageMigration: 'Moving ROMs',
	ZipRepack: 'Repacking ZIPs',
	SaveFolders: 'Moving Saves to User Folders',
	PullCovers: 'Pull Cover Images',
	IgdbSync: 'Refresh IGDB Ratings'
};

/** ROM Zip Config B17: a job type's short label (footer, Jobs page); an unknown type shows as is. */
export function jobLabel(type: string): string {
	return jobLabels[type as CurrentJob['type']] ?? type;
}

/** B4: a Review resolution runs to its end, so it has no Cancel button. */
export function isCancellable(type: string): boolean {
	return type !== 'ReviewResolve';
}

/** A summary value for a badge (B6): strings and numbers as they are, anything else (`null`, objects) as JSON. */
export function formatSummaryValue(value: unknown): string {
	return typeof value === 'string' || typeof value === 'number'
		? String(value)
		: JSON.stringify(value);
}

/** A summary entry shown as a two-column table rather than a badge. */
export interface SummaryTable {
	key: string;
	title: string;
	columns: [string, string];
	rows: [string, string][];
}

const summaryTableLabels: Record<string, { title: string; columns: [string, string] }> = {
	versions: { title: 'DAT versions', columns: ['DAT', 'Version'] }
};

/** A non-empty plain object whose values are all strings or numbers (e.g. DatRefresh's `versions`). */
function isFlatMap(value: unknown): value is Record<string, string | number> {
	if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
	const values = Object.values(value);
	return values.length > 0 && values.every((v) => typeof v === 'string' || typeof v === 'number');
}

/**
 * Splits a job summary for display: flat name → value maps become tables, everything else a badge (B6). Nested
 * values (a ReviewResolve job's `errors`) stay badges with JSON.
 */
export function splitSummary(summary: Record<string, unknown> | null): {
	badges: [string, unknown][];
	tables: SummaryTable[];
} {
	const badges: [string, unknown][] = [];
	const tables: SummaryTable[] = [];
	for (const [key, value] of Object.entries(summary ?? {})) {
		if (isFlatMap(value)) {
			const label = summaryTableLabels[key] ?? { title: key, columns: ['Name', 'Value'] };
			tables.push({
				key,
				...label,
				rows: Object.entries(value).map(([name, v]) => [name, String(v)])
			});
		} else badges.push([key, value]);
	}
	return { badges, tables };
}

/** `Ingested 3 · Review 1`; zero counts left out. */
export function formatSummary(summary: Record<string, unknown> | null): string {
	if (!summary) return '';
	return Object.entries(summary)
		.filter(([, count]) => count)
		.map(([key, count]) => `${key} ${count}`)
		.join(' · ');
}

/** 0–100 for the progress bar; 0 while the total is unknown. */
export function percent(current: number, total: number): number {
	return total > 0 ? Math.min(100, Math.round((current / total) * 100)) : 0;
}

const stepLabels: Record<string, string> = {
	Hashing: 'Hashing',
	CopyingToLibrary: 'Copying to library',
	Copying: 'Copying'
};

/** 0–100 for the current file's bar; 0 without a total. */
export function stepPercent(step: JobStep): number {
	return percent(step.bytesDone, step.bytesTotal);
}

/** `about 50 s left` / `about 3 min left`; `''` when unknown or nothing is left. */
function timeLeft(step: JobStep): string {
	if (!step.bytesPerSecond || step.bytesTotal <= 0) return '';
	const seconds = Math.ceil((step.bytesTotal - step.bytesDone) / step.bytesPerSecond);
	if (seconds <= 0) return '';
	return seconds < 60 ? `about ${seconds} s left` : `about ${Math.ceil(seconds / 60)} min left`;
}

/** `Hashing · 3.2 GiB / 8.0 GiB · 95.0 MiB/s · about 50 s left` (large ROM import progress B7). */
export function formatStep(step: JobStep): string {
	const parts = [
		stepLabels[step.phase] ?? step.phase,
		`${formatSize(step.bytesDone)} / ${formatSize(step.bytesTotal)}`
	];
	if (step.bytesPerSecond) parts.push(`${formatSize(step.bytesPerSecond)}/s`);
	const left = timeLeft(step);
	if (left) parts.push(left);
	return parts.join(' · ');
}

/** §12.2 issue types, for the filter. */
export const issueTypes = [
	'ZipUnreadable',
	'InfoJsonMissing',
	'InfoJsonInvalid',
	'InfoJsonNewerSchema',
	'EntryMissing',
	'EntrySizeMismatch',
	'EntryUnlisted',
	'HashMismatch',
	'MissingBoxArt',
	'DefaultBoxArt',
	'WrongLocation',
	'LegacyEmbeddedStorage',
	'OrphanFile',
	'DuplicateGameId',
	'DuplicateSha1',
	'DbRowInserted',
	'DbRowUpdated',
	'DbRowDeleted'
];

export const issueStatuses = ['Open', 'AutoRepaired', 'Resolved'];

const severityClasses: Record<string, string> = {
	Error: 'text-bg-danger',
	Warn: 'text-bg-warning',
	Info: 'text-bg-info'
};

export function severityClass(severity: string): string {
	return severityClasses[severity] ?? 'text-bg-secondary';
}

/** Newest first, at most `limit` (merges the Integrity and MediaRetry histories). */
export function latestJobs<T extends { startedAt: string }>(lists: T[][], limit: number): T[] {
	return lists
		.flat()
		.sort((a, b) => b.startedAt.localeCompare(a.startedAt))
		.slice(0, limit);
}
