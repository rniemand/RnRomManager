<script lang="ts">
	import { untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { isApiError } from '../api/errors';
	import type { CollectionDetail } from '../api/types';
	import {
		collectionsApi,
		maxNameLength,
		maxNotesBytes,
		nameError,
		notesError
	} from '../collections';
	import { normaliseNote, noteBytes, renderMarkdown } from '../markdown';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import Icon from './Icon.svelte';

	/**
	 * B23: create a collection (name, notes, optional cover uploaded right after the create, then go to it) or edit one
	 * (Save PATCHes name + notes; cover actions apply at once).
	 */
	interface Props {
		open: boolean;
		/** null = create. */
		collection: CollectionDetail | null;
		/** Edit only: the collection after a save or a cover change. */
		onchanged?: (collection: CollectionDetail) => void;
	}

	let { open = $bindable(), collection, onchanged }: Props = $props();

	type Mode = 'view' | 'edit' | 'preview';
	type CoverSource = 'file' | 'url';
	const maxUploadBytes = 8 * 1024 * 1024;

	let name = $state('');
	let notes = $state('');
	let mode = $state<Mode>('edit');
	let coverSource = $state<CoverSource>('file');
	let coverUrlInput = $state('');
	let fileInput = $state<HTMLInputElement>();
	let coverPreview = $state<string | null>(null);
	let busy = $state(false);
	let error = $state<unknown>(null);
	let nameServerError = $state<string | undefined>();
	let notesServerError = $state<string | undefined>();
	let coverError = $state<string | undefined>();
	let touched = $state(false);

	const editing = $derived(collection !== null);
	const bytes = $derived(noteBytes(notes));
	const nameMessage = $derived(
		nameServerError ?? (touched ? (nameError(name) ?? undefined) : undefined)
	);
	const notesMessage = $derived(notesServerError ?? notesError(notes) ?? undefined);
	const uid = Math.random().toString(36).slice(2);

	// Only opening resets the form: a cover change updating `collection` mustn't wipe unsaved name/notes edits.
	$effect(() => {
		if (open) untrack(reset);
	});

	function reset(): void {
		name = collection?.name ?? '';
		notes = collection?.notes ?? '';
		mode = collection?.notes ? 'view' : 'edit';
		coverSource = 'file';
		coverUrlInput = '';
		coverPreview = collection?.coverUrl ?? null;
		busy = false;
		error = null;
		nameServerError = notesServerError = coverError = undefined;
		touched = false;
	}

	function chosenFile(): File | undefined {
		return fileInput?.files?.[0];
	}

	/** Client checks before any cover request; returns the error to show. */
	function coverInputError(): string | undefined {
		if (coverSource === 'url')
			return coverUrlInput.trim() ? undefined : 'Enter an http or https image URL.';
		const file = chosenFile();
		if (!file) return 'Choose an image file.';
		return file.size > maxUploadBytes ? 'The image must be at most 8 MiB.' : undefined;
	}

	function hasCoverInput(): boolean {
		return coverSource === 'url' ? coverUrlInput.trim() !== '' : chosenFile() !== undefined;
	}

	function sendCover(id: string): Promise<CollectionDetail> {
		return coverSource === 'url'
			? collectionsApi.coverFromUrl(id, coverUrlInput.trim())
			: collectionsApi.uploadCover(id, chosenFile()!);
	}

	function coverMessage(e: unknown): string {
		if (isApiError(e, 'ValidationFailed'))
			return e.errors.file?.[0] ?? e.errors.url?.[0] ?? e.title;
		if (isApiError(e) && e.status === 413) return 'The image must be at most 8 MiB.';
		return isApiError(e) ? e.title : 'The cover could not be saved.';
	}

	function showSaveError(e: unknown): void {
		if (isApiError(e, 'CollectionNameTaken')) nameServerError = e.title;
		else if (isApiError(e, 'ValidationFailed') && (e.errors.name || e.errors.notes)) {
			nameServerError = e.errors.name?.[0];
			notesServerError = e.errors.notes?.[0];
		} else error = e;
	}

	async function save(): Promise<void> {
		touched = true;
		nameServerError = notesServerError = undefined;
		if (busy || nameError(name) || notesError(notes)) return;
		const text = normaliseNote(notes);
		const body = { name: name.trim(), notes: text.trim() ? text : null };
		busy = true;
		error = null;
		try {
			if (collection) {
				onchanged?.(await collectionsApi.update(collection.id, body));
				toasts.show('Collection saved.', 'success');
				open = false;
				return;
			}
			if (hasCoverInput() && (coverError = coverInputError())) return;
			const created = await collectionsApi.create(body.name, body.notes);
			if (hasCoverInput()) {
				try {
					await sendCover(created.id);
				} catch (e) {
					toasts.show(
						`${created.name} was created, but its cover wasn't saved: ${coverMessage(e)}`,
						'danger'
					);
				}
			}
			open = false;
			await goto(resolve('/collections/[id]', { id: created.id }));
		} catch (e) {
			showSaveError(e);
		} finally {
			busy = false;
		}
	}

	/** Edit only: B23 cover actions apply immediately. */
	async function applyCover(action: 'set' | 'remove'): Promise<void> {
		if (!collection || busy) return;
		coverError = action === 'set' ? coverInputError() : undefined;
		if (coverError) return;
		busy = true;
		try {
			const updated =
				action === 'set'
					? await sendCover(collection.id)
					: await collectionsApi.removeCover(collection.id);
			coverPreview = updated.coverUrl;
			coverUrlInput = '';
			if (fileInput) fileInput.value = '';
			onchanged?.(updated);
		} catch (e) {
			coverError = coverMessage(e);
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
	title={editing ? `Edit ${collection?.name ?? ''}` : 'New collection'}
	size="lg"
	confirmLabel={editing ? 'Save' : 'Create'}
	{busy}
	onconfirm={save}
>
	{#if error}<ErrorAlert {error} ondismiss={() => (error = null)} />{/if}

	<div class="mb-3">
		<label class="form-label" for="collection-name-{uid}">Name</label>
		<input
			id="collection-name-{uid}"
			class="form-control"
			class:is-invalid={nameMessage}
			maxlength={maxNameLength * 2}
			aria-describedby={nameMessage ? `collection-name-error-${uid}` : undefined}
			bind:value={name}
			oninput={() => (nameServerError = undefined)}
			onblur={() => (touched = true)}
		/>
		{#if nameMessage}
			<div id="collection-name-error-{uid}" class="invalid-feedback" role="alert">
				{nameMessage}
			</div>
		{/if}
	</div>

	<div class="mb-3">
		<div class="d-flex align-items-center justify-content-between mb-2">
			<span class="form-label mb-0" id="collection-notes-label-{uid}">Notes (Markdown)</span>
			<div class="btn-group btn-group-sm" role="group" aria-label="Notes mode">
				{#each modes as [value, label] (value)}
					<input
						type="radio"
						class="btn-check"
						name="collection-notes-mode-{uid}"
						id="collection-notes-{value}-{uid}"
						autocomplete="off"
						{value}
						bind:group={mode}
						disabled={value === 'view' && !collection?.notes}
					/>
					<label class="btn btn-outline-secondary" for="collection-notes-{value}-{uid}"
						>{label}</label
					>
				{/each}
			</div>
		</div>
		{#if mode === 'edit'}
			<textarea
				class="form-control font-monospace"
				class:is-invalid={notesMessage}
				rows="8"
				aria-labelledby="collection-notes-label-{uid}"
				aria-describedby="collection-notes-bytes-{uid}"
				bind:value={notes}
				oninput={() => (notesServerError = undefined)}></textarea>
			<div
				id="collection-notes-bytes-{uid}"
				class="form-text"
				class:text-danger={notesMessage}
				role={notesMessage ? 'alert' : undefined}
			>
				{bytes.toLocaleString('en-US')} / {maxNotesBytes.toLocaleString('en-US')} bytes{#if notesMessage}
					— {notesMessage}{/if}
			</div>
		{:else}
			{@const source = mode === 'view' ? (collection?.notes ?? '') : notes}
			{#if source.trim()}
				<div class="collection-notes border rounded p-3" data-testid="notes-rendered">
					<!-- eslint-disable-next-line svelte/no-at-html-tags -- sanitised by DOMPurify in renderMarkdown -->
					{@html renderMarkdown(source)}
				</div>
			{:else}
				<p class="text-body-secondary mb-0">Nothing to preview.</p>
			{/if}
		{/if}
	</div>

	<fieldset>
		<legend class="form-label fs-6">Cover</legend>
		<div class="d-flex gap-3 align-items-start">
			<div class="cover-preview ratio ratio-1x1 bg-body-secondary rounded flex-shrink-0">
				{#if coverPreview}
					<img class="object-fit-cover rounded" src={coverPreview} alt="Current cover" />
				{:else}
					<div class="d-flex align-items-center justify-content-center">
						<Icon name="collections" class="fs-2" />
					</div>
				{/if}
			</div>
			<div class="flex-grow-1">
				<div class="btn-group btn-group-sm mb-2" role="group" aria-label="Cover source">
					<input
						type="radio"
						class="btn-check"
						id="cover-file-{uid}"
						value="file"
						bind:group={coverSource}
					/>
					<label class="btn btn-outline-secondary" for="cover-file-{uid}">File</label>
					<input
						type="radio"
						class="btn-check"
						id="cover-url-{uid}"
						value="url"
						bind:group={coverSource}
					/>
					<label class="btn btn-outline-secondary" for="cover-url-{uid}">URL</label>
				</div>
				{#if coverSource === 'file'}
					<label class="visually-hidden" for="cover-input-file-{uid}">Cover image file</label>
					<input
						id="cover-input-file-{uid}"
						type="file"
						class="form-control form-control-sm"
						class:is-invalid={coverError}
						accept="image/png,image/jpeg"
						bind:this={fileInput}
						onchange={() => (coverError = undefined)}
					/>
				{:else}
					<label class="visually-hidden" for="cover-input-url-{uid}">Cover image URL</label>
					<input
						id="cover-input-url-{uid}"
						type="url"
						class="form-control form-control-sm"
						class:is-invalid={coverError}
						placeholder="https://…"
						maxlength="1024"
						bind:value={coverUrlInput}
						oninput={() => (coverError = undefined)}
					/>
				{/if}
				{#if coverError}<div class="invalid-feedback d-block" role="alert">{coverError}</div>{/if}
				{#if editing}
					<div class="d-flex gap-2 mt-2">
						<button
							type="button"
							class="btn btn-sm btn-outline-primary"
							disabled={busy}
							onclick={() => applyCover('set')}><Icon name="upload" /> Set cover</button
						>
						{#if coverPreview}
							<button
								type="button"
								class="btn btn-sm btn-outline-danger"
								disabled={busy}
								onclick={() => applyCover('remove')}><Icon name="delete" /> Remove cover</button
							>
						{/if}
					</div>
				{:else}
					<div class="form-text">Optional; PNG or JPEG, at most 8 MiB.</div>
				{/if}
			</div>
		</div>
	</fieldset>
</ConfirmModal>

<style>
	.cover-preview {
		width: 6rem;
	}
	.collection-notes :global(:last-child) {
		margin-bottom: 0;
	}
	.collection-notes :global(img) {
		max-width: 100%;
	}
</style>
