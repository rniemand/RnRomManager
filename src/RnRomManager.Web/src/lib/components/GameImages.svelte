<script lang="ts">
	import { api } from '../api/client';
	import { isApiError } from '../api/errors';
	import { gameSearchQuery, googleImagesUrl } from '../googleSearch';
	import { imageTypeLabels } from '../imageResolutions';
	import type { GameDetail, ImageInfo, ImageSuggestion, ImageSuggestions } from '../api/types';
	import { auth } from '../stores/auth.svelte';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';
	import Loading from './Loading.svelte';

	interface Props {
		game: GameDetail;
		/** After an upload, delete or cover change: the fresh detail (image ids change on every write). */
		onchanged: (detail: GameDetail) => void;
	}

	let { game, onchanged }: Props = $props();

	/** Same limit as the API (I1a); checked here so a big file isn't sent at all. */
	const maxUploadBytes = 8 * 1024 * 1024;

	let viewing = $state<ImageInfo | null>(null);
	let viewOpen = $state(false);
	let deleting = $state<ImageInfo | null>(null);
	let deleteOpen = $state(false);
	let busy = $state(false);

	let uploadOpen = $state(false);
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

	const base = $derived(`/api/games/${encodeURIComponent(game.id)}/images`);

	// IGDB suggestions S12, S13: the Suggest Images dialog.
	let suggestOpen = $state(false);
	let suggestType = $state<'BoxArt' | 'Snap'>('BoxArt');
	let suggestions = $state<ImageSuggestion[] | null>(null);
	let suggestError = $state<unknown>(null);
	let suggestNotConfigured = $state(false);
	/** The tile whose add is running. */
	let adding = $state<number | null>(null);
	/** Bumped by each load and each close: a response from an older one is ignored. */
	let loadSeq = 0;
	let loadAbort: AbortController | null = null;
	const suggestLabel = $derived(suggestType === 'BoxArt' ? 'box art' : 'screenshot');

	function openSuggest(): void {
		suggestType = 'BoxArt';
		suggestOpen = true;
		void loadSuggestions();
	}

	async function loadSuggestions(): Promise<void> {
		loadAbort?.abort();
		const controller = new AbortController();
		loadAbort = controller;
		const seq = ++loadSeq;
		suggestions = null;
		suggestError = null;
		suggestNotConfigured = false;
		try {
			const result = await api.get<ImageSuggestions>(
				`/api/games/${encodeURIComponent(game.id)}/image-suggestions?type=${suggestType}`,
				{ signal: controller.signal }
			);
			if (seq === loadSeq) suggestions = result.suggestions;
		} catch (e) {
			if (seq !== loadSeq) return;
			if (isApiError(e, 'IgdbNotConfigured')) suggestNotConfigured = true;
			else suggestError = e;
		}
	}

	// S12: closing the dialog aborts its load and makes any late answer stale.
	$effect(() => {
		if (suggestOpen) return;
		loadAbort?.abort();
		loadAbort = null;
		loadSeq++;
	});

	async function addSuggestion(suggestion: ImageSuggestion, index: number): Promise<void> {
		const seq = loadSeq;
		adding = index;
		try {
			onchanged(
				await api.post<GameDetail>(`${base}/from-url`, {
					url: suggestion.imageUrl,
					type: suggestType
				})
			);
			toasts.show('Image added from IGDB.', 'success');
			// S1: closes only the same opening and type, never a dialog reopened meanwhile.
			if (seq === loadSeq) suggestOpen = false;
		} catch (e) {
			if (isApiError(e, 'ImageDownloadFailed') || isApiError(e, 'ImageUrlBlocked'))
				toasts.show(e.title, 'danger');
			else if (isApiError(e, 'ValidationFailed'))
				toasts.show(e.errors.url?.[0] ?? e.errors.type?.[0] ?? e.title, 'danger');
			else failed(e);
		} finally {
			adding = null;
		}
	}

	function view(image: ImageInfo): void {
		viewing = image;
		viewOpen = true;
	}

	function failed(e: unknown): void {
		if (toasts.jobRunning(e)) return;
		const messages: Record<string, string> = {
			DuplicateImage: 'The game already has this image.',
			TooManyImages: 'A game can have at most 20 images.',
			DefaultImage: 'The placeholder is managed automatically.'
		};
		toasts.show(
			(isApiError(e) && e.code && messages[e.code]) ||
				(isApiError(e) ? e.title : 'Something went wrong.'),
			'danger'
		);
	}

	async function run(call: () => Promise<GameDetail>, done: string): Promise<boolean> {
		busy = true;
		try {
			onchanged(await call());
			toasts.show(done, 'success');
			return true;
		} catch (e) {
			failed(e);
			return false;
		} finally {
			busy = false;
		}
	}

	function openUpload(): void {
		uploadError = typeError = urlError = undefined;
		urlFallback = false;
		uploadMode = 'url';
		dropNote = undefined;
		dragging = false;
		imageUrl = '';
		uploadType = 'BoxArt';
		if (fileInput) fileInput.value = '';
		uploadOpen = true;
	}

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
			uploadOpen = false;
		} catch (e) {
			if (isApiError(e, 'ValidationFailed')) {
				uploadError = e.errors.file?.[0];
				typeError = e.errors.type?.[0];
			} else if (isApiError(e) && e.status === 413)
				uploadError = 'The image must be at most 8 MiB.';
			else failed(e);
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
			uploadOpen = false;
		} catch (e) {
			if (isApiError(e, 'ImageDownloadFailed') || isApiError(e, 'ImageUrlBlocked')) {
				urlError = e.title;
				urlFallback = true;
			} else if (isApiError(e, 'ValidationFailed')) {
				urlError = e.errors.url?.[0];
				typeError = e.errors.type?.[0];
				urlFallback = urlError !== undefined;
			} else failed(e);
		} finally {
			busy = false;
		}
	}

	function setCover(image: ImageInfo): Promise<boolean> {
		return run(
			() => api.post<GameDetail>(`${base}/${image.id}/cover`),
			`${image.fileName} is now the cover.`
		);
	}

	async function confirmDelete(): Promise<void> {
		if (!deleting) return;
		const image = deleting;
		if (
			await run(() => api.delete<GameDetail>(`${base}/${image.id}`), `${image.fileName} deleted.`)
		)
			deleteOpen = false;
	}
</script>

<section class="mb-4" aria-labelledby="images-heading">
	<!-- I1: the tab names the panel; the heading stays for the outline only. -->
	<h2 class="visually-hidden" id="images-heading">Images</h2>
	<div class="d-flex flex-wrap align-items-center gap-2 mb-2">
		{#if auth.isAdmin}
			<!-- IGDB suggestions S11: before Google Images, and the start of the right-aligned group. -->
			<button type="button" class="btn btn-outline-secondary btn-sm ms-auto" onclick={openSuggest}>
				<Icon name="suggest-images" /> Suggest Images
			</button>
			<!-- B8: an external search, not an app page. -->
			<!-- eslint-disable svelte/no-navigation-without-resolve -->
			<a
				class="btn btn-outline-secondary btn-sm"
				href={googleImagesUrl(gameSearchQuery(game))}
				target="_blank"
				rel="noopener noreferrer"><Icon name="google" /> Google Images</a
			>
			<!-- eslint-enable svelte/no-navigation-without-resolve -->
			<button type="button" class="btn btn-outline-primary btn-sm" onclick={openUpload}>
				<Icon name="upload" /> Upload Image
			</button>
		{/if}
	</div>
	<div class="row row-cols-2 row-cols-sm-3 row-cols-lg-5 g-3 mb-3">
		{#each game.images as image (image.id)}
			<div class="col">
				<!-- B10: the tile lifts on hover or keyboard focus. -->
				<div class="card h-100 image-tile">
					<!-- B10: the size badge sits over the thumbnail, outside its button. -->
					<div class="position-relative">
						<button
							type="button"
							class="btn p-0 border-0 w-100"
							aria-label="View {image.fileName} full size"
							onclick={() => view(image)}
						>
							<img
								class="card-img-top object-fit-contain bg-body-tertiary"
								style="height: 8rem"
								src={image.url}
								alt="{game.displayTitle} {imageTypeLabels[image.type].toLowerCase()}"
								loading="lazy"
							/>
						</button>
						<span class="badge text-bg-light border position-absolute top-0 end-0 m-1"
							><FileSize bytes={image.sizeBytes} /></span
						>
					</div>
					<div class="card-body p-2 small">
						<div class="d-flex flex-wrap gap-1 mb-1">
							<span class="badge text-bg-secondary">{imageTypeLabels[image.type]}</span>
							{#if image.isCover}<span class="badge text-bg-primary">Cover</span>{/if}
							{#if image.isDefault}<span class="badge text-bg-light border">Placeholder</span>{/if}
						</div>
						<div class="text-body-secondary text-nowrap">{image.width}×{image.height}</div>
						<!-- B10: Download for every user, then the Admin's Use as cover and Delete. -->
						<div class="d-flex flex-wrap gap-1 mt-2">
							<!-- eslint-disable svelte/no-navigation-without-resolve -- an API download, not a page -->
							<a
								class="btn btn-sm btn-outline-secondary"
								href="/api/games/{encodeURIComponent(game.id)}/images/{image.id}?download=true"
								download
								aria-label="Download {image.fileName}"><Icon name="download" /></a
							>
							<!-- eslint-enable svelte/no-navigation-without-resolve -->
							{#if auth.isAdmin}
								{#if !image.isCover}
									<button
										type="button"
										class="btn btn-sm btn-outline-primary"
										aria-label="Use as cover: {image.fileName}"
										disabled={busy}
										onclick={() => setCover(image)}
									>
										Use as cover
									</button>
								{/if}
								{#if !image.isDefault}
									<button
										type="button"
										class="btn btn-sm btn-outline-danger"
										aria-label="Delete {image.fileName}"
										disabled={busy}
										onclick={() => {
											deleting = image;
											deleteOpen = true;
										}}
									>
										<Icon name="delete" />
									</button>
								{/if}
							{/if}
						</div>
					</div>
				</div>
			</div>
		{/each}
	</div>
</section>

{#if auth.isAdmin}
	<ConfirmModal
		bind:open={uploadOpen}
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
					style="border-style: dashed !important"
				>
					Drop image to upload
				</div>
			{/if}
			<div class="btn-group mb-3" role="group" aria-label="Image source">
				<input
					type="radio"
					class="btn-check"
					name="image-source"
					id="image-source-url"
					value="url"
					autocomplete="off"
					bind:group={uploadMode}
				/>
				<label class="btn btn-outline-secondary btn-sm" for="image-source-url">URL</label>
				<input
					type="radio"
					class="btn-check"
					name="image-source"
					id="image-source-file"
					value="file"
					autocomplete="off"
					bind:group={uploadMode}
				/>
				<label class="btn btn-outline-secondary btn-sm" for="image-source-file">File</label>
			</div>
			{#if uploadMode === 'url'}
				<div class="mb-3">
					<label class="form-label" for="image-url">Image URL</label>
					<input
						id="image-url"
						class="form-control"
						class:is-invalid={urlError}
						type="url"
						bind:this={urlInput}
						bind:value={imageUrl}
						onkeydown={onUrlKeydown}
						aria-describedby="image-url-help"
					/>
					{#if urlError}
						<div class="invalid-feedback">
							{urlError}
							{#if urlFallback}
								Download the image and upload the file instead.
								<button
									type="button"
									class="btn btn-link btn-sm p-0 align-baseline"
									onclick={useFileInstead}>Upload a file instead</button
								>
							{/if}
						</div>
					{/if}
					<div id="image-url-help" class="form-text">
						The server downloads the image. PNG, JPEG or WebP (saved as PNG/JPEG), at most 8 MiB.
					</div>
				</div>
			{/if}
			<div class="mb-3" hidden={uploadMode !== 'file'}>
				<label class="form-label" for="image-file">Upload an image</label>
				<input
					bind:this={fileInput}
					id="image-file"
					class="form-control"
					class:is-invalid={uploadError}
					type="file"
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
			</div>
			<div class="btn-group" role="group" aria-label="Image type">
				<input
					type="radio"
					class="btn-check"
					name="image-type"
					id="image-type-boxart"
					value="BoxArt"
					autocomplete="off"
					bind:group={uploadType}
				/>
				<label class="btn btn-outline-secondary btn-sm" for="image-type-boxart">Box art</label>
				<input
					type="radio"
					class="btn-check"
					name="image-type"
					id="image-type-snap"
					value="Snap"
					autocomplete="off"
					bind:group={uploadType}
				/>
				<label class="btn btn-outline-secondary btn-sm" for="image-type-snap">Screenshot</label>
			</div>
			{#if typeError}<div class="invalid-feedback d-block">{typeError}</div>{/if}
		</div>
	</ConfirmModal>
{/if}

{#if auth.isAdmin}
	<!-- IGDB suggestions S12: info mode (Close only); Close stays enabled while an add runs (S13). -->
	<ConfirmModal bind:open={suggestOpen} title="Suggest Images" size="lg">
		<!-- Only while open, so its controls never sit next to the Upload Image dialog's in the page. -->
		{#if suggestOpen}
			<div class="btn-group mb-3" role="group" aria-label="Image type">
				<input
					type="radio"
					class="btn-check"
					name="suggest-type"
					id="suggest-type-boxart"
					value="BoxArt"
					autocomplete="off"
					disabled={adding !== null}
					bind:group={suggestType}
					onchange={loadSuggestions}
				/>
				<label class="btn btn-outline-secondary btn-sm" for="suggest-type-boxart">Box art</label>
				<input
					type="radio"
					class="btn-check"
					name="suggest-type"
					id="suggest-type-snap"
					value="Snap"
					autocomplete="off"
					disabled={adding !== null}
					bind:group={suggestType}
					onchange={loadSuggestions}
				/>
				<label class="btn btn-outline-secondary btn-sm" for="suggest-type-snap">Screenshot</label>
			</div>
			<div aria-live="polite">
				{#if suggestNotConfigured}
					<div class="alert alert-info mb-0">
						Image suggestions need an IGDB Client ID and secret. Set <code class="text-reset"
							>Sources:IgdbClientId</code
						>
						and <code class="text-reset">Sources:IgdbClientSecret</code> and restart.
					</div>
				{:else if suggestError}
					<ErrorAlert error={suggestError} onretry={loadSuggestions} />
				{:else if !suggestions}
					<Loading />
				{:else if suggestions.length === 0}
					<p class="mb-0">
						{suggestType === 'BoxArt'
							? 'IGDB has no box art for this game.'
							: 'IGDB has no screenshots for this game.'}
					</p>
				{:else}
					<div class="row row-cols-2 row-cols-md-5 g-2">
						{#each suggestions as suggestion, i (suggestion.imageUrl)}
							<div class="col">
								<button
									type="button"
									class="btn p-0 border card h-100 w-100 position-relative overflow-hidden"
									aria-label="Add {suggestion.gameName} {suggestLabel} {i + 1}"
									disabled={adding !== null}
									onclick={() => addSuggestion(suggestion, i)}
								>
									<img
										class="card-img-top object-fit-contain bg-body-tertiary"
										style="height: 8rem"
										src={suggestion.previewUrl}
										alt=""
										loading="lazy"
									/>
									{#if adding === i}
										<span
											class="spinner-border spinner-border-sm position-absolute top-0 start-0 m-1"
											aria-hidden="true"
										></span>
									{/if}
									<span class="d-block w-100 p-1 small text-start">
										<span class="d-block text-truncate" title={suggestion.gameName}
											>{suggestion.gameName}</span
										>
										{#if suggestion.width && suggestion.height}
											<span class="d-block text-body-secondary"
												>{suggestion.width}×{suggestion.height}</span
											>
										{/if}
									</span>
								</button>
							</div>
						{/each}
					</div>
				{/if}
			</div>
			<div class="form-text">Suggestions from IGDB.com. Click one to add it to the game.</div>
		{/if}
	</ConfirmModal>
{/if}

<ConfirmModal bind:open={viewOpen} title={viewing?.fileName ?? 'Image'} size="lg">
	{#if viewing}
		<img
			class="img-fluid d-block mx-auto"
			src={viewing.url}
			width={viewing.width}
			height={viewing.height}
			alt="{game.displayTitle} {imageTypeLabels[viewing.type].toLowerCase()}, full size"
		/>
	{/if}
</ConfirmModal>

<ConfirmModal
	bind:open={deleteOpen}
	title="Delete image"
	confirmLabel="Delete image"
	confirmClass="btn-danger"
	{busy}
	onconfirm={confirmDelete}
>
	<p class="mb-0">
		Delete <code>{deleting?.fileName}</code> from {game.displayTitle}? This can't be undone.
	</p>
</ConfirmModal>

<style>
	.image-tile {
		transition:
			transform 0.15s ease,
			box-shadow 0.15s ease;
	}
	.image-tile:hover,
	.image-tile:focus-within {
		transform: translateY(-2px);
		box-shadow: var(--bs-box-shadow);
	}
	@media (prefers-reduced-motion: reduce) {
		.image-tile {
			transition: none;
		}
		.image-tile:hover,
		.image-tile:focus-within {
			transform: none;
		}
	}
</style>
