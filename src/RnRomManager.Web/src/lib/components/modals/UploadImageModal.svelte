<script lang="ts">
	import { untrack } from 'svelte';
	import BaseModal from './BaseModal.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import FileInput from '#lib/components/forms/FileInput.svelte';
	import FormField from '#lib/components/forms/FormField.svelte';
	import RadioButtons from '#lib/components/forms/RadioButtons.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import type { GameDetail } from '#lib/types/index.js';

	interface Props {
		/** Opening starts on URL, Box art, with no errors. */
		open: boolean;
		gameId: string;
		/** After an upload: the fresh detail. */
		onchanged: (detail: GameDetail) => void;
		/** Any failure the dialog doesn't show itself. */
		onfailed: (e: unknown) => void;
	}

	let { open = $bindable(), gameId, onchanged, onfailed }: Props = $props();

	/** Same limit as the API (I1a); checked here so a big file isn't sent at all. */
	const maxUploadBytes = 8 * 1024 * 1024;
	const base = $derived(`/api/games/${encodeURIComponent(gameId)}/images`);

	let busy = $state(false);
	let fileInput = $state<HTMLInputElement>();
	let uploadType = $state<'BoxArt' | 'Snap'>('BoxArt');
	let uploadError = $state<string | undefined>();
	let typeError = $state<string | undefined>();
	/** U6: upload a file, or have the server download a URL. */
	let uploadMode = $state<'file' | 'url'>('url');
	let imageUrl = $state('');
	let urlInput = $state<HTMLInputElement>();
	let urlError = $state<string | undefined>();
	/** The server couldn't use the URL: offer the file upload instead. */
	let urlFallback = $state(false);
	/** I3: a file is being dragged over the modal body. */
	let dragging = $state(false);
	/** I3: several files were dropped; the first is used. */
	let dropNote = $state<string | undefined>();

	$effect(() => {
		if (!open) return;
		untrack(() => {
			uploadError = typeError = urlError = undefined;
			urlFallback = false;
			uploadMode = 'url';
			dropNote = undefined;
			dragging = false;
			imageUrl = '';
			uploadType = 'BoxArt';
			if (fileInput) fileInput.value = '';
		});
	});

	function useFileInstead(): void {
		uploadMode = 'file';
		urlError = undefined;
		urlFallback = false;
	}

	/** I3: only drags that carry files are taken (text and links are left to the browser). */
	const hasFiles = (e: DragEvent) => !!e.dataTransfer?.types.includes('Files');

	function onDragOver(e: DragEvent): void {
		if (!hasFiles(e)) return;
		e.preventDefault(); // allow the drop, so the browser never navigates to the file
		dragging = true;
	}

	function onDragLeave(e: DragEvent): void {
		const target = e.currentTarget as HTMLElement;
		if (!target.contains(e.relatedTarget as Node | null)) dragging = false;
	}

	function onDrop(e: DragEvent): void {
		if (!hasFiles(e)) return;
		e.preventDefault();
		dragging = false;
		const files = e.dataTransfer!.files;
		if (!files.length || !fileInput) return;
		const transfer = new DataTransfer();
		transfer.items.add(files[0]);
		fileInput.files = transfer.files; // as if picked; Upload runs the usual checks
		uploadMode = 'file';
		uploadError = undefined;
		dropNote =
			files.length > 1
				? `Only one image can be uploaded at a time; using ${files[0].name}.`
				: undefined;
	}

	/** B7: Enter in the URL input submits as Upload does. */
	function onUrlKeydown(e: KeyboardEvent): void {
		if (e.key !== 'Enter') return;
		e.preventDefault();
		if (!busy) void upload();
	}

	async function upload(): Promise<void> {
		uploadError = typeError = urlError = undefined;
		urlFallback = false;
		if (uploadMode === 'url') return uploadFromUrl();
		const file = fileInput?.files?.[0];
		if (!file) {
			uploadError = 'Choose an image file.';
			return;
		}
		if (file.size > maxUploadBytes) {
			uploadError = 'The image must be at most 8 MiB.';
			return;
		}
		const form = new FormData();
		form.append('file', file);
		form.append('type', uploadType);
		busy = true;
		try {
			onchanged(await api.post<GameDetail>(base, form));
			toasts.show(`${file.name} uploaded.`, 'success');
			if (fileInput) fileInput.value = '';
			uploadType = 'BoxArt';
			open = false;
		} catch (e) {
			if (isApiError(e, 'ValidationFailed')) {
				uploadError = e.errors.file?.[0];
				typeError = e.errors.type?.[0];
			} else if (isApiError(e) && e.status === 413)
				uploadError = 'The image must be at most 8 MiB.';
			else onfailed(e);
		} finally {
			busy = false;
		}
	}

	async function uploadFromUrl(): Promise<void> {
		const url = imageUrl;
		if (!url.trim()) {
			urlError = 'Enter an http or https image URL.';
			return;
		}
		busy = true;
		try {
			onchanged(await api.post<GameDetail>(`${base}/from-url`, { url, type: uploadType }));
			toasts.show(`Image added from ${new URL(url).hostname}.`, 'success');
			open = false;
		} catch (e) {
			if (isApiError(e, 'ImageDownloadFailed') || isApiError(e, 'ImageUrlBlocked')) {
				urlError = e.title;
				urlFallback = true;
			} else if (isApiError(e, 'ValidationFailed')) {
				urlError = e.errors.url?.[0];
				typeError = e.errors.type?.[0];
				urlFallback = urlError !== undefined;
			} else onfailed(e);
		} finally {
			busy = false;
		}
	}
</script>

<BaseModal
	bind:open
	title="Upload Image"
	confirmLabel="Upload"
	{busy}
	onconfirm={upload}
	onshown={() => urlInput?.focus()}
>
	<!-- I3: the whole body takes a dropped image file. -->
	<div
		class="position-relative"
		role="group"
		aria-label="Image to upload"
		ondragenter={onDragOver}
		ondragover={onDragOver}
		ondragleave={onDragLeave}
		ondrop={onDrop}
	>
		{#if dragging}
			<div
				class="drop-overlay position-absolute top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center border border-2 border-primary rounded bg-body"
			>
				Drop image to upload
			</div>
		{/if}
		<RadioButtons
			class="mb-3"
			role="group"
			aria-label="Image source"
			name="image-source"
			size="sm"
			options={[
				{ value: 'url', label: 'URL' },
				{ value: 'file', label: 'File' }
			]}
			bind:value={uploadMode}
		/>
		{#if uploadMode === 'url'}
			<div class="mb-3">
				<FormField label="Image URL" for="image-url">
					<TextInput
						id="image-url"
						class={urlError ? 'is-invalid' : undefined}
						type="url"
						bind:ref={urlInput}
						bind:value={imageUrl}
						onkeydown={onUrlKeydown}
						aria-describedby="image-url-help"
					/>
					{#if urlError}
						<div class="invalid-feedback">
							{urlError}
							{#if urlFallback}
								Download the image and upload the file instead.
								<Button variant="link" class="btn-sm p-0 align-baseline" onclick={useFileInstead}
									>Upload a file instead</Button
								>
							{/if}
						</div>
					{/if}
					<div id="image-url-help" class="form-text">
						The server downloads the image. PNG, JPEG or WebP (saved as PNG/JPEG), at most 8 MiB.
					</div>
				</FormField>
			</div>
		{/if}
		<div class="mb-3" hidden={uploadMode !== 'file'}>
			<FormField label="Upload an image" for="image-file">
				<FileInput
					bind:ref={fileInput}
					id="image-file"
					class={uploadError ? 'is-invalid' : undefined}
					accept="image/png,image/jpeg,image/webp"
					aria-describedby="image-file-help"
					onchange={() => (dropNote = undefined)}
				/>
				{#if uploadError}<div class="invalid-feedback">{uploadError}</div>{/if}
				{#if dropNote}<div class="form-text" role="status">{dropNote}</div>{/if}
				<div id="image-file-help" class="form-text">
					PNG, JPEG or WebP (saved as PNG/JPEG), at most <FileSize bytes={maxUploadBytes} />, or
					drop it here
				</div>
			</FormField>
		</div>
		<RadioButtons
			role="group"
			aria-label="Image type"
			name="image-type"
			size="sm"
			options={[
				{ value: 'BoxArt', label: 'Box art', id: 'image-type-boxart' },
				{ value: 'Snap', label: 'Screenshot', id: 'image-type-snap' }
			]}
			bind:value={uploadType}
		/>
		{#if typeError}<div class="invalid-feedback d-block">{typeError}</div>{/if}
	</div>
</BaseModal>

<style>
	.drop-overlay {
		border-style: dashed !important;
	}
</style>
