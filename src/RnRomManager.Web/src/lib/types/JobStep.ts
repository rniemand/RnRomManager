/** Large ROM import progress B1: one streamed phase of the current file. */
export interface JobStep {
	/** `Hashing`, `CopyingToLibrary` or `Copying`; another value shows as is. */
	phase: string;
	bytesDone: number;
	bytesTotal: number;
	bytesPerSecond: number | null;
}
