import { api } from './api/client';
import type { SaveItem, SaveKind } from './api/types';

/** B8: the server's limits, mirrored for the client checks. */
export const maxSaveBytes = 256 * 1024 * 1024;
export const maxNameLength = 100;
export const maxDescriptionLength = 1000;

export const kindLabels: Record<SaveKind, string> = { state: 'State', battery: 'Battery' };

// .NET char.IsControl: C0, DEL and C1.
// eslint-disable-next-line no-control-regex
const control = /[\u0000-\u001f\u007f-\u009f]/;
// eslint-disable-next-line no-control-regex
const controlButLineBreak = /[\u0000-\u0009\u000b-\u001f\u007f-\u009f]/;

/** B8: trimmed, 1–100 characters, no control characters. */
export function nameError(name: string): string | null {
	const trimmed = name.trim();
	if (trimmed.length === 0 || trimmed.length > maxNameLength)
		return `Must be 1–${maxNameLength} characters.`;
	return control.test(trimmed) ? 'Must not contain control characters.' : null;
}

/** B8: at most 1,000 characters, no control characters but line breaks. */
export function descriptionError(description: string): string | null {
	if (description.length > maxDescriptionLength)
		return `Must be at most ${maxDescriptionLength} characters.`;
	return controlButLineBreak.test(description)
		? 'Must not contain control characters other than line breaks.'
		: null;
}

/** B8: one file of 1 byte – 256 MiB. */
export function fileError(file: File | null | undefined): string | null {
	if (!file) return 'Choose a save file.';
	if (file.size === 0) return 'The save file is empty.';
	return file.size > maxSaveBytes ? 'The save is larger than 256 MiB.' : null;
}

const base = (gameId: string) => `/api/games/${encodeURIComponent(gameId)}/saves`;

/** B7–B12 through the shared client. Multipart bodies send the text fields before the file, as B8 requires. */
export const savesApi = {
	list: (gameId: string, signal?: AbortSignal) =>
		api.get<{ items: SaveItem[] }>(base(gameId), { signal }).then((r) => r.items),

	add(
		gameId: string,
		save: {
			romId: number;
			kind: SaveKind;
			name: string;
			description: string;
			file: Blob;
			fileName: string;
		}
	): Promise<SaveItem> {
		const form = new FormData();
		form.append('romId', String(save.romId));
		form.append('kind', save.kind);
		form.append('name', save.name.trim());
		if (save.description) form.append('description', save.description);
		form.append('file', save.file, save.fileName);
		return api.post<SaveItem>(base(gameId), form);
	},

	update: (gameId: string, saveId: string, changes: { name?: string; description?: string }) =>
		api.patch<SaveItem>(`${base(gameId)}/${saveId}`, changes),

	replace(gameId: string, saveId: string, file: File): Promise<SaveItem> {
		const form = new FormData();
		form.append('file', file, file.name);
		return api.put<SaveItem>(`${base(gameId)}/${saveId}/file`, form);
	},

	remove: (gameId: string, saveId: string) => api.delete<void>(`${base(gameId)}/${saveId}`),

	downloadUrl: (gameId: string, saveId: string) => `${base(gameId)}/${saveId}/download`
};
