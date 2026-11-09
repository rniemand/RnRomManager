import { api } from './api/client';
import type { CollectionDetail, CollectionListItem, CollectionRef, Page } from './api/types';
import { maxNoteBytes, noteBytes } from './markdown';

/** B2: mirrors `CollectionService.MaxNameLength` (code points, after trimming). */
export const maxNameLength = 100;
/** B2: notes share the release-note cap and byte counting. */
export const maxNotesBytes = maxNoteBytes;
/** B13: ids per add request. */
export const maxGameIds = 500;

/** B2's name rule, as the server words it; null when valid. */
export function nameError(name: string): string | null {
	const trimmed = name.trim();
	if (!trimmed) return 'A name is required.';
	return [...trimmed].length > maxNameLength
		? `The name must be at most ${maxNameLength} characters.`
		: null;
}

/** B2's notes rule (UTF-8 bytes after `\n` normalisation); null when valid. */
export function notesError(notes: string): string | null {
	return noteBytes(notes) > maxNotesBytes
		? `The notes must be at most ${maxNotesBytes.toLocaleString('en-US')} bytes (UTF-8).`
		: null;
}

/** B22: the Collections page's URL query (`?q=&page=`), page 1 and an empty filter omitted. */
export function collectionsSearch(state: { q: string; page: number }): string {
	const params = new URLSearchParams();
	if (state.q) params.set('q', state.q);
	if (state.page > 1) params.set('page', String(state.page));
	const search = params.toString();
	return search ? `?${search}` : '';
}

const base = '/api/collections';
const one = (id: string) => `${base}/${encodeURIComponent(id)}`;

export const collectionsApi = {
	list: (params: { q?: string; page?: number; pageSize?: number } = {}) => {
		const search = new URLSearchParams();
		if (params.q?.trim()) search.set('q', params.q.trim());
		if (params.page && params.page > 1) search.set('page', String(params.page));
		if (params.pageSize) search.set('pageSize', String(params.pageSize));
		const query = search.toString();
		return api.get<Page<CollectionListItem>>(query ? `${base}?${query}` : base);
	},
	get: (id: string) => api.get<CollectionDetail>(one(id)),
	create: (name: string, notes: string | null) => api.post<CollectionDetail>(base, { name, notes }),
	/** Absent fields are unchanged; `notes: null` or `''` clears them (B4). */
	update: (id: string, patch: { name?: string; notes?: string | null }) =>
		api.patch<CollectionDetail>(one(id), patch),
	delete: (id: string) => api.delete<void>(one(id)),
	uploadCover: (id: string, file: File) => {
		const form = new FormData();
		form.append('file', file);
		return api.post<CollectionDetail>(`${one(id)}/cover`, form);
	},
	coverFromUrl: (id: string, url: string) =>
		api.post<CollectionDetail>(`${one(id)}/cover/from-url`, { url }),
	removeCover: (id: string) => api.delete<CollectionDetail>(`${one(id)}/cover`),
	addGames: (id: string, gameIds: string[]) =>
		api.post<{ added: number }>(`${one(id)}/games`, { gameIds }),
	removeGame: (id: string, gameId: string) =>
		api.delete<void>(`${one(id)}/games/${encodeURIComponent(gameId)}`),
	forGame: (gameId: string) =>
		api.get<CollectionRef[]>(`/api/games/${encodeURIComponent(gameId)}/collections`)
};
