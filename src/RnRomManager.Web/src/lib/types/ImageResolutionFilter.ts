import type { ImageInfo } from './ImageInfo.js';

/** Image Resolutions R9: the report's filter and page, kept in the URL query and remembered in `localStorage`. */
export interface ImageResolutionFilter {
	system: string;
	type: ImageInfo['type'] | '';
	maxWidth: number | null;
	maxHeight: number | null;
	page: number;
}
