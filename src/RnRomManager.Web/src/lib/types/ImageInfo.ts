export interface ImageInfo {
	/** Changes whenever the game's ZIP is rewritten: use the ids from the latest detail. */
	id: number;
	type: 'BoxArt' | 'Snap' | 'TitleScreen' | 'Other';
	fileName: string;
	source: 'libretro-thumbnails' | 'default' | 'upload';
	/** The system placeholder box art. */
	isDefault: boolean;
	/** The image shown for the game (exactly one when it has images). */
	isCover: boolean;
	sizeBytes: number;
	width: number;
	height: number;
	url: string;
}
