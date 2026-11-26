/** L2: `GET /api/activity`, what holds the job lock; 204 when free. */
export interface Activity {
	/** e.g. "Saving changes", or the job type's name for a job. */
	activity: string;
	/** The `JobType` name when a job holds the lock. */
	jobType: string | null;
}
