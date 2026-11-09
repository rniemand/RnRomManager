import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetClient } from './api/client';
import type { IngestDeleteEntry } from './api/types';
import { deleteIngestFiles, deleteTitle, isArchive } from './ingestDelete';
import { toasts } from './stores/toasts.svelte';

const entry = (name: string, system = 'gb'): IngestDeleteEntry => ({
	system,
	folder: 'pending',
	name
});

describe('ingestDelete', () => {
	let bodies: { files: IngestDeleteEntry[] }[];
	let answers: { status: number; body?: unknown }[];

	beforeEach(() => {
		resetClient();
		toasts.items = [];
		bodies = [];
		answers = [];
		vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
			// The client's antiforgery GET before the first mutation.
			if (url !== '/api/ingest/delete') return Promise.resolve(new Response(null, { status: 204 }));
			const body = JSON.parse(init!.body as string) as { files: IngestDeleteEntry[] };
			bodies.push(body);
			const answer = answers.shift() ?? {
				status: 200,
				body: { deleted: body.files.length, failed: [] }
			};
			return Promise.resolve(new Response(JSON.stringify(answer.body), { status: answer.status }));
		});
	});
	afterEach(() => vi.unstubAllGlobals());

	const texts = () => toasts.items.map((t) => [t.kind, t.text]);

	it('isArchive matches .zip and .7z in any case only', () => {
		expect(['a.zip', 'b.7Z', 'c.ZIP', 'd.gb', 'zip'].map(isArchive)).toEqual([
			true,
			true,
			true,
			false,
			false
		]);
	});

	it('titles one file by its bare name and several by count', () => {
		expect(deleteTitle([entry('a.gb', 'gbc')])).toBe('Delete a.gb');
		expect(deleteTitle([entry('a.gb'), entry('b.gb')])).toBe('Delete 2 files');
	});

	it('one deleted file toasts its name', async () => {
		await deleteIngestFiles([entry('a.gb')]);
		expect(bodies).toEqual([{ files: [entry('a.gb')] }]);
		expect(texts()).toEqual([['success', 'Deleted a.gb.']]);
	});

	it('501 entries go out as 500 + 1 and the toast sums them', async () => {
		const many = Array.from({ length: 501 }, (_, i) => entry(`${i}.gb`));
		await deleteIngestFiles(many);
		expect(bodies.map((b) => b.files.length)).toEqual([500, 1]);
		expect(bodies[1].files[0].name).toBe('500.gb');
		expect(texts()).toEqual([['success', 'Deleted 501 files.']]);
	});

	it('a partial failure shows both toasts with system/name', async () => {
		answers.push({ status: 200, body: { deleted: 1, failed: [entry('b.gb', 'nes')] } });
		await deleteIngestFiles([entry('a.gb'), entry('b.gb', 'nes')]);
		expect(texts()).toEqual([
			['success', 'Deleted 1 files.'],
			['danger', "Couldn't delete: nes/b.gb"]
		]);
	});

	it('JobRunning shows the standard toast and stops before the next batch', async () => {
		answers.push({ status: 409, body: { code: 'JobRunning', title: 'Busy' } });
		await deleteIngestFiles(Array.from({ length: 501 }, (_, i) => entry(`${i}.gb`)));
		expect(bodies).toHaveLength(1);
		expect(texts()).toEqual([
			['warning', 'Another job is running. Try again when it has finished.']
		]);
	});

	it('an error after a successful batch keeps that batch in the toast and shows the title', async () => {
		answers.push(
			{ status: 200, body: { deleted: 500, failed: [] } },
			{ status: 400, body: { code: 'ValidationFailed', title: 'Bad request' } }
		);
		await deleteIngestFiles(Array.from({ length: 501 }, (_, i) => entry(`${i}.gb`)));
		expect(texts()).toEqual([
			['success', 'Deleted 500 files.'],
			['danger', 'Bad request']
		]);
	});
});
