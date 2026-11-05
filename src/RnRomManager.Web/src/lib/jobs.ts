/** Display helpers for jobs (spec §3.1 statuses, §7.2 outcomes). */
import type { JobStatus } from './api/types';

export const outcomes = [
	'Ingested',
	'Repaired',
	'Duplicate',
	'Review',
	'Failed',
	'Error',
	'Skipped'
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
	Duplicate: 'text-bg-secondary',
	Review: 'text-bg-info',
	Failed: 'text-bg-warning',
	Error: 'text-bg-danger',
	Skipped: 'text-bg-light'
};

export function statusClass(status: string): string {
	return statusClasses[status as JobStatus] ?? 'text-bg-secondary';
}

export function outcomeClass(outcome: string): string {
	return outcomeClasses[outcome] ?? 'text-bg-secondary';
}

/** `Ingested 3 · Review 1`; zero counts left out. */
export function formatSummary(summary: Record<string, number> | null): string {
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
