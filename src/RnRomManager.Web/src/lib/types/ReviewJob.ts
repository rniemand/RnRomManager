/** A Review resolution started from an item page (B7). `done` is the success text shown on the item's page. */
export interface ReviewJob {
	jobId: string;
	reviewId: number;
	fileName: string;
	done: string;
	/** Review QoL B10: started by Use and Next. */
	next?: true;
}
