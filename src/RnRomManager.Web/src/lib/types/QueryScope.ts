/**
 * B24: a collection's page uses the same filters without letter and region, defaulting to added-to-collection
 * newest first; its API calls carry `collection`.
 */
export interface QueryScope {
	collection: string;
}
