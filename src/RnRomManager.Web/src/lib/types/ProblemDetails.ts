/** RFC 7807 ProblemDetails with the app's extras (spec §23, §23.1). */
export interface ProblemDetails {
	status?: number;
	title?: string;
	/** The longer explanation some problems carry (e.g. B27's `BackupTooNew`, `RestoreFailed`). */
	detail?: string;
	code?: string;
	traceId?: string;
	errors?: Record<string, string[]>;
	/** RP8 group merge: the source games merged before the failure. */
	merged?: string[];
}
