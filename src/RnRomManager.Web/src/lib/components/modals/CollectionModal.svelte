<script lang="ts">
	import RadioButtons from '#lib/components/forms/RadioButtons.svelte';
	import Textarea from '#lib/components/forms/Textarea.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import FileInput from '#lib/components/forms/FileInput.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import { untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { isApiError } from '#lib/api/errors.js';
	import type { CollectionDetail } from '#lib/types/index.js';
	import {
		collectionsApi,
		maxNameLength,
		maxNotesBytes,
		nameError,
		notesError
	} from '#lib/collections.js';
	import { normaliseNote, noteBytes, renderMarkdown } from '#lib/markdown.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import BaseModal from './BaseModal.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Icon from '#lib/icons/Icon.svelte';

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

<BaseModal
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
		<TextInput
			class={nameMessage ? 'is-invalid' : undefined}
			id="collection-name-{uid}"
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
			<RadioButtons
				class="btn-group-sm"
				role="group"
				aria-label="Notes mode"
				name="collection-notes-mode-{uid}"
				options={modes.map(([value, label]) => ({
					value,
					label,
					id: `collection-notes-${value}-${uid}`,
					disabled: value === 'view' && !collection?.notes
				}))}
				bind:value={mode}
			/>
		</div>
		{#if mode === 'edit'}
			<Textarea
				class={['font-monospace', notesMessage && 'is-invalid'].filter(Boolean).join(' ')}
				rows={8}
				aria-labelledby="collection-notes-label-{uid}"
				aria-describedby="collection-notes-bytes-{uid}"
				bind:value={notes}
				oninput={() => (notesServerError = undefined)}
			/>
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
				<RadioButtons
					class="btn-group-sm mb-2"
					role="group"
					aria-label="Cover source"
					name="cover-source-{uid}"
					options={[
						{ value: 'file', label: 'File', id: `cover-file-${uid}` },
						{ value: 'url', label: 'URL', id: `cover-url-${uid}` }
					]}
					bind:value={coverSource}
				/>
				{#if coverSource === 'file'}
					<label class="visually-hidden" for="cover-input-file-{uid}">Cover image file</label>
					<FileInput
						size="sm"
						class={coverError ? 'is-invalid' : undefined}
						id="cover-input-file-{uid}"
						accept="image/png,image/jpeg,image/webp"
						bind:ref={fileInput}
						onchange={() => (coverError = undefined)}
					/>
				{:else}
					<label class="visually-hidden" for="cover-input-url-{uid}">Cover image URL</label>
					<TextInput
						size="sm"
						class={coverError ? 'is-invalid' : undefined}
						id="cover-input-url-{uid}"
						type="url"
						placeholder="https://…"
						maxlength={1024}
						bind:value={coverUrlInput}
						oninput={() => (coverError = undefined)}
					/>
				{/if}
				{#if coverError}<div class="invalid-feedback d-block" role="alert">{coverError}</div>{/if}
				{#if editing}
					<div class="d-flex gap-2 mt-2">
						<Button
							variant="primary"
							outline
							size="sm"
							icon="upload"
							disabled={busy}
							onclick={() => applyCover('set')}>Set cover</Button
						>
						{#if coverPreview}
							<Button
								variant="danger"
								outline
								size="sm"
								icon="delete"
								disabled={busy}
								onclick={() => applyCover('remove')}>Remove cover</Button
							>
						{/if}
					</div>
				{:else}
					<div class="form-text">Optional; PNG, JPEG or WebP, at most 8 MiB.</div>
				{/if}
			</div>
		</div>
	</fieldset>
</BaseModal>

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
