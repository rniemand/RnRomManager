import type { ProblemDetails } from '#lib/types/index.js';

/** A failed API call. `code` is the §23.1 code (`ValidationFailed`, `JobRunning`, …) or `HttpError`/`NetworkError`. */
export class ApiError extends Error {
	readonly status: number;
	readonly code: string;
	readonly title: string;
	readonly detail?: string;
	readonly traceId?: string;
	readonly errors: Record<string, string[]>;
	/** RP8: the sources already merged when a group merge stopped; empty otherwise. */
	readonly merged: string[];

	constructor(status: number, problem: ProblemDetails = {}) {
		const title = problem.title ?? (status === 0 ? 'Network error' : `Request failed (${status})`);
		super(title);
		this.name = 'ApiError';
		this.status = status;
		this.code = problem.code ?? (status === 0 ? 'NetworkError' : 'HttpError');
		this.title = title;
		this.detail = problem.detail;
		this.traceId = problem.traceId;
		this.errors = problem.errors ?? {};
		this.merged = problem.merged ?? [];
	}
}

/** True when `error` is an {@link ApiError} with this code. */
export function isApiError(error: unknown, code?: string): error is ApiError {
	return error instanceof ApiError && (code === undefined || error.code === code);
}
