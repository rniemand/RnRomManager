<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import IconButton from '#lib/components/forms/IconButton.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import DeleteImageModal from '#lib/components/modals/DeleteImageModal.svelte';
	import SuggestImagesModal from '#lib/components/modals/SuggestImagesModal.svelte';
	import UploadImageModal from '#lib/components/modals/UploadImageModal.svelte';
	import ViewImageModal from '#lib/components/modals/ViewImageModal.svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import { coverSearchQuery, googleImagesUrl, screenshotSearchQuery } from '#lib/googleSearch.js';
	import { imageTypeLabels, splitImages } from '#lib/imageResolutions.js';
	import { formatCount } from '#lib/format.js';
	import type { GameDetail, ImageInfo } from '#lib/types/index.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import FileSize from '#lib/components/common/FileSize.svelte';

	interface Props {
		game: GameDetail;
		/** After an upload, delete or cover change: the fresh detail (image ids change on every write). */
		onchanged: (detail: GameDetail) => void;
	}

	let { game, onchanged }: Props = $props();

	let viewing = $state<ImageInfo | null>(null);
	let viewOpen = $state(false);
	let deleting = $state<ImageInfo | null>(null);
	let deleteOpen = $state(false);
	let busy = $state(false);

	let uploadOpen = $state(false);
	let suggestOpen = $state(false);
	/** IM4: the type the next opened modal starts on, from the section that opened it. */
	let modalType = $state<'BoxArt' | 'Snap'>('BoxArt');

	type Section = 'covers' | 'screenshots';
	const sections = {
		covers: { title: 'Covers', type: 'BoxArt', empty: 'No covers yet.', query: coverSearchQuery },
		screenshots: {
			title: 'Screenshots',
			type: 'Snap',
			empty: 'No screenshots yet.',
			query: screenshotSearchQuery
		}
	} as const;
	const split = $derived(splitImages(game.images));

	function openModal(which: 'upload' | 'suggest', type: 'BoxArt' | 'Snap'): void {
		modalType = type;
		if (which === 'upload') uploadOpen = true;
		else suggestOpen = true;
	}

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

<!-- IM6: one image tile; Download for every user, then the Admin's Use as cover and Delete. -->
{#snippet tile(image: ImageInfo)}
	<div class="col">
		<!-- B10: the tile lifts on hover or keyboard focus. -->
		<!-- P6: the cover's card stands out. -->
		<div
			class="card h-100 image-tile"
			class:border-primary={image.isCover}
			class:border-2={image.isCover}
		>
			<!-- B10: the size badge sits over the thumbnail, outside its button. -->
			<div class="position-relative">
				<Button
					class="btn p-0 border-0 w-100"
					aria-label="View {image.fileName} full size"
					onclick={() => view(image)}
					><img
						class="card-img-top object-fit-contain bg-body-tertiary thumb"
						src={image.url}
						alt="{game.displayTitle} {imageTypeLabels[image.type].toLowerCase()}"
						loading="lazy"
					/></Button
				>
				<span class="badge text-bg-light border position-absolute top-0 end-0 m-1"
					><FileSize bytes={image.sizeBytes} /></span
				>
			</div>
			<div class="card-body p-2 small" class:bg-primary-subtle={image.isCover}>
				<div class="d-flex flex-wrap gap-1 mb-1">
					<span class="badge text-bg-secondary">{imageTypeLabels[image.type]}</span>
					{#if image.isCover}<span class="badge text-bg-primary">Cover</span>{/if}
					{#if image.isDefault}<span class="badge text-bg-light border">Placeholder</span>{/if}
				</div>
				<div class="text-body-secondary text-nowrap">{image.width}×{image.height}</div>
				<div class="d-flex gap-2 mt-2">
					<IconButton
						icon="download"
						label="Download {image.fileName}"
						tooltip="Download"
						href="/api/games/{encodeURIComponent(game.id)}/images/{image.id}?download=true"
						download
					/>
					{#if auth.isAdmin}
						{#if !image.isCover}
							<IconButton
								icon="cover"
								tone="primary"
								label="Use as cover: {image.fileName}"
								tooltip="Use as cover"
								disabled={busy}
								onclick={() => setCover(image)}
							/>
						{/if}
						{#if !image.isDefault}
							<IconButton
								icon="delete"
								tone="danger"
								label="Delete {image.fileName}"
								tooltip="Delete"
								disabled={busy}
								onclick={() => {
									deleting = image;
									deleteOpen = true;
								}}
							/>
						{/if}
					{/if}
				</div>
			</div>
		</div>
	</div>
{/snippet}

<!-- IM1: Covers then Screenshots, each with its own Admin toolbar on the heading row. -->
{#snippet imageSection(kind: Section, images: ImageInfo[])}
	{@const s = sections[kind]}
	<div class="d-flex flex-wrap align-items-center gap-2 mb-2">
		<h3 class="h6 mb-0">{s.title} ({formatCount(images.length)})</h3>
		{#if auth.isAdmin}
			<!-- IGDB suggestions S11: before Google Images, and the start of the right-aligned group. -->
			<Button
				icon="suggest-images"
				class="btn btn-outline-secondary btn-sm ms-auto"
				aria-label="Suggest Images ({kind})"
				onclick={() => openModal('suggest', s.type)}>Suggest Images</Button
			>
			<!-- B8: an external search, not an app page. -->
			<!-- eslint-disable svelte/no-navigation-without-resolve -->
			<Button
				icon="google"
				class="btn btn-outline-secondary btn-sm"
				href={googleImagesUrl(s.query(game))}
				target="_blank"
				rel="noopener noreferrer"
				aria-label="Google Images ({kind})">Google Images</Button
			>
			<!-- eslint-enable svelte/no-navigation-without-resolve -->
			<Button
				icon="upload"
				class="btn btn-outline-primary btn-sm"
				aria-label="Upload Image ({kind})"
				onclick={() => openModal('upload', s.type)}>Upload Image</Button
			>
		{/if}
	</div>
	{#if images.length}
		<div class="row row-cols-2 row-cols-sm-3 row-cols-lg-5 g-3 mb-3">
			{#each images as image (image.id)}{@render tile(image)}{/each}
		</div>
	{:else}
		<EmptyState message={s.empty} />
	{/if}
{/snippet}

<section class="mb-4" aria-labelledby="images-heading">
	<!-- I1: the tab names the panel; the heading stays for the outline only. -->
	<h2 class="visually-hidden" id="images-heading">Images</h2>
	{@render imageSection('covers', split.covers)}
	<hr />
	{@render imageSection('screenshots', split.screenshots)}
</section>

{#if auth.isAdmin}
	<UploadImageModal
		bind:open={uploadOpen}
		gameId={game.id}
		initialType={modalType}
		{onchanged}
		onfailed={failed}
	/>
	<SuggestImagesModal
		bind:open={suggestOpen}
		gameId={game.id}
		initialType={modalType}
		{onchanged}
		onfailed={failed}
	/>
{/if}

<ViewImageModal bind:open={viewOpen} image={viewing} gameTitle={game.displayTitle} />

<DeleteImageModal
	bind:open={deleteOpen}
	image={deleting}
	gameTitle={game.displayTitle}
	{busy}
	onconfirm={confirmDelete}
/>

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
	.thumb {
		height: 8rem;
	}
</style>
