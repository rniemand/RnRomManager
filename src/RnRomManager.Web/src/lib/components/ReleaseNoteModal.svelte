<script lang="ts">
	import { untrack } from 'svelte';
	import { api } from '../api/client';
	import { isApiError } from '../api/errors';
	import type { RomDetail } from '../api/types';
	import { maxNoteBytes, normaliseNote, noteBytes, renderMarkdown } from '../markdown';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import Loading from './Loading.svelte';

	/** B11: one release's Markdown note — view, edit with a live preview, save or delete. */
	interface Props {
		open: boolean;
		gameId: string;
		rom: RomDetail | null;
		/** After a save or delete: whether the release now has a note. */
		onsaved: (romId: number, hasNote: boolean) => void;
	}

	let { open = $bindable(), gameId, rom, onsaved }: Props = $props();

	type Mode = 'view' | 'edit' | 'preview';

	let mode = $state<Mode>('edit');
	/** The stored note ('' when there's none). */
	let original = $state('');
	let exists = $state(false);
	let text = $state('');
	let loading = $state(false);
	let loadError = $state<unknown>(null);
	let error = $state<unknown>(null);
	let busy = $state(false);
	let confirmingDelete = $state(false);
	let controller: AbortController | undefined;

	const url = $derived(rom ? `/api/games/${encodeURIComponent(gameId)}/roms/${rom.id}/note` : '');
	const bytes = $derived(noteBytes(text));
	const over = $derived(bytes > maxNoteBytes);
	const changed = $derived(normaliseNote(text) !== original);
	const counterId = `note-bytes-${Math.random().toString(36).slice(2)}`;
	const groupName = `note-mode-${Math.random().toString(36).slice(2)}`;

	async function load(target: RomDetail): Promise<void> {
		controller?.abort();
		const current = (controller = new AbortController());
		loadError = null;
		if (!target.hasNote) {
			loading = false;
			return;
		}
		loading = true;
		try {
			const note = normaliseNote(await api.getText(url, { signal: current.signal }));
			if (current.signal.aborted) return;
			original = note;
			text = note;
			exists = true;
			mode = 'view';
		} catch (e) {
			if (current.signal.aborted) return;
			if (isApiError(e) && e.status === 404) {
				// Deleted meanwhile: start a new one.
				onsaved(target.id, false);
			} else {
				loadError = e;
			}
		} finally {
			if (!current.signal.aborted) loading = false;
		}
	}

	$effect(() => {
		if (!open || !rom) {
			controller?.abort();
			return;
		}
		const target = rom;
		mode = 'edit';
		original = '';
		text = '';
		exists = false;
		error = null;
		busy = false;
		confirmingDelete = false;
		// Only open/rom drive this: onsaved flipping rom.hasNote mustn't reset the modal.
		untrack(() => void load(target));
	});

	function fail(e: unknown): void {
		if (!toasts.jobRunning(e)) error = e;
	}

	async function save(): Promise<void> {
		if (!rom || busy || !changed || over) return;
		busy = true;
		error = null;
		try {
			const normalised = normaliseNote(text);
			await api.put(url, { text: normalised });
			const hasNote = normalised.trim() !== '';
			onsaved(rom.id, hasNote);
			toasts.show(hasNote ? 'Note saved.' : 'Note deleted.', 'success');
			open = false;
		} catch (e) {
			fail(e);
		} finally {
			busy = false;
		}
	}

	async function remove(): Promise<void> {
		if (!rom || busy) return;
		busy = true;
		error = null;
		try {
			await api.delete(url);
			onsaved(rom.id, false);
			toasts.show('Note deleted.', 'success');
			open = false;
		} catch (e) {
			confirmingDelete = false;
			fail(e);
		} finally {
			busy = false;
		}
	}

	const modes: [Mode, string][] = [
		['view', 'View'],
		['edit', 'Edit'],
		['preview', 'Preview']
	];
</script>

<ConfirmModal
	bind:open
	title="Note — {rom?.fileName ?? ''}"
	size="lg"
	confirmLabel="Save"
	{busy}
	confirmDisabled={loading || !!loadError || !changed || over}
	onconfirm={save}
>
	{#if loadError}
		<ErrorAlert error={loadError} onretry={() => rom && load(rom)} />
	{:else if loading}
		<Loading label="Loading note…" />
	{:else}
		{#if error}<ErrorAlert {error} ondismiss={() => (error = null)} />{/if}

		<div class="btn-group btn-group-sm mb-3" role="group" aria-label="Note mode">
			{#each modes as [value, label] (value)}
				<input
					type="radio"
					class="btn-check"
					name={groupName}
					id="{groupName}-{value}"
					autocomplete="off"
					{value}
					bind:group={mode}
					disabled={value === 'view' && !exists}
				/>
				<label class="btn btn-outline-secondary" for="{groupName}-{value}">{label}</label>
			{/each}
		</div>

		{#if mode === 'edit'}
			<label class="visually-hidden" for="{groupName}-text">Note (Markdown)</label>
			<textarea
				id="{groupName}-text"
				class="form-control font-monospace"
				class:is-invalid={over}
				rows="12"
				aria-describedby={counterId}
				bind:value={text}></textarea>
			<div id={counterId} class="form-text" class:text-danger={over}>
				{bytes.toLocaleString('en-US')} / {maxNoteBytes.toLocaleString('en-US')} bytes
			</div>
		{:else}
			{@const source = mode === 'view' ? original : text}
			{#if source.trim()}
				<div class="note-markdown border rounded p-3" data-testid="note-rendered">
					<!-- eslint-disable-next-line svelte/no-at-html-tags -- sanitised by DOMPurify in renderMarkdown -->
					{@html renderMarkdown(source)}
				</div>
			{:else}
				<p class="text-body-secondary mb-0">Nothing to preview.</p>
			{/if}
		{/if}

		{#if exists}
			<div class="d-flex align-items-center gap-2 mt-3">
				{#if confirmingDelete}
					<span>Delete this note?</span>
					<button type="button" class="btn btn-sm btn-danger" disabled={busy} onclick={remove}
						>Yes</button
					>
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						disabled={busy}
						onclick={() => (confirmingDelete = false)}>No</button
					>
				{:else}
					<button
						type="button"
						class="btn btn-sm btn-outline-danger"
						disabled={busy}
						onclick={() => (confirmingDelete = true)}>Delete note</button
					>
				{/if}
			</div>
		{/if}
	{/if}
</ConfirmModal>

<style>
	.note-markdown :global(:last-child) {
		margin-bottom: 0;
	}
	.note-markdown :global(img) {
		max-width: 100%;
	}
</style>
