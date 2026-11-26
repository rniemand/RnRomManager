import type { ImageInfo } from './ImageInfo.js';

/** Image Resolutions R4: one row of `GET /api/reports/image-resolutions`. */
export interface ImageResolutionItem {
	imageId: number;
	gameId: string;
	displayTitle: string;
	system: string;
	systemName: string;
	type: ImageInfo['type'];
	width: number;
	height: number;
	sizeBytes: number;
}
