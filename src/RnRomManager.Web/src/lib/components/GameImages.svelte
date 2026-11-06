<script lang="ts">
	import { api } from '../api/client';
	import { isApiError } from '../api/errors';
	import type { GameDetail, ImageInfo } from '../api/types';
	import { auth } from '../stores/auth.svelte';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';

	interface Props {
		game: GameDetail;
		/** After an upload, delete or cover change: the fresh detail (image ids change on every write). */
		onchanged: (detail: GameDetail) => void;
	}

	let { game, onchanged }: Props = $props();

	/** Same limit as the API (I1a); checked here so a big file isn't sent at all. */
	const maxUploadBytes = 8 * 1024 * 1024;

	const typeLabels: Record<ImageInfo['type'], string> = {
		BoxArt: 'Box art',
		Snap: 'Screenshot',
		TitleScreen: 'Title screen',
		Other: 'Other'
	};

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

	const base = $derived(`/api/games/${encodeURIComponent(game.id)}/images`);

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
		uploadError = typeError = undefined;
		uploadOpen = true;
	}

	async function upload(): Promise<void> {
		uploadError = typeError = undefined;
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
	<h2 class="h5" id="images-heading">Images</h2>
	<div class="row row-cols-2 row-cols-sm-3 row-cols-lg-5 g-3 mb-3">
		{#each game.images as image (image.id)}
			<div class="col">
				<div class="card h-100">
					<button
						type="button"
						class="btn p-0 border-0"
						aria-label="View {image.fileName} full size"
						onclick={() => view(image)}
					>
						<img
							class="card-img-top object-fit-contain bg-body-tertiary"
							style="height: 8rem"
							src={image.url}
							alt="{game.displayTitle} {typeLabels[image.type].toLowerCase()}"
							loading="lazy"
						/>
					</button>
					<div class="card-body p-2 small">
						<div class="d-flex flex-wrap gap-1 mb-1">
							<span class="badge text-bg-secondary">{typeLabels[image.type]}</span>
							{#if image.isCover}<span class="badge text-bg-primary">Cover</span>{/if}
							{#if image.isDefault}<span class="badge text-bg-light border">Placeholder</span>{/if}
						</div>
						<div class="text-body-secondary text-nowrap">
							<FileSize bytes={image.sizeBytes} /> · {image.width}×{image.height}
						</div>
						{#if auth.canEditLibrary}
							<div class="d-flex flex-wrap gap-1 mt-2">
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
							</div>
						{/if}
					</div>
				</div>
			</div>
		{/each}
	</div>

	{#if auth.canEditLibrary}
		<button type="button" class="btn btn-outline-primary btn-sm" onclick={openUpload}>
			<Icon name="upload" /> Upload image
		</button>
	{/if}
</section>

{#if auth.canEditLibrary}
	<ConfirmModal
		bind:open={uploadOpen}
		title="Upload image"
		confirmLabel="Upload"
		{busy}
		onconfirm={upload}
	>
		<div class="mb-3">
			<label class="form-label" for="image-file">Upload an image</label>
			<input
				bind:this={fileInput}
				id="image-file"
				class="form-control"
				class:is-invalid={uploadError}
				type="file"
				accept="image/png,image/jpeg"
				aria-describedby="image-file-help"
			/>
			{#if uploadError}<div class="invalid-feedback">{uploadError}</div>{/if}
			<div id="image-file-help" class="form-text">
				PNG or JPEG, at most <FileSize bytes={maxUploadBytes} />
			</div>
		</div>
		<div>
			<label class="form-label" for="image-type">Type</label>
			<select
				id="image-type"
				class="form-select"
				class:is-invalid={typeError}
				bind:value={uploadType}
			>
				<option value="BoxArt">Box art</option>
				<option value="Snap">Screenshot</option>
			</select>
			{#if typeError}<div class="invalid-feedback">{typeError}</div>{/if}
		</div>
	</ConfirmModal>
{/if}

<ConfirmModal bind:open={viewOpen} title={viewing?.fileName ?? 'Image'} size="lg">
	{#if viewing}
		<img
			class="img-fluid d-block mx-auto"
			src={viewing.url}
			width={viewing.width}
			height={viewing.height}
			alt="{game.displayTitle} {typeLabels[viewing.type].toLowerCase()}, full size"
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
