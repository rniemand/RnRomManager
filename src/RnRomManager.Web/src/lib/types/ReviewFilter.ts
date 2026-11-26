/** RV4: the review queue's filter and page, kept in the URL query and remembered in `localStorage`. */
export interface ReviewFilter {
	q: string;
	system: string;
	reason: string;
	page: number;
}
