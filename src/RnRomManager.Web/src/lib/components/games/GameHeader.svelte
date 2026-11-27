<script lang="ts">
	import { fromAction } from 'svelte/attachments';
	import { untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import type { AdminAction, CollectionRef, GameDetail, RomDetail } from '#lib/types/index.js';
	import Button from '#lib/components/forms/Button.svelte';
	import IconButton from '#lib/components/forms/IconButton.svelte';
	import RatingControl from '#lib/components/forms/RatingControl.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import GameTitle from '#lib/components/games/GameTitle.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import { formatCount, formatDateTime } from '#lib/format.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { hotkeys } from '#lib/stores/hotkeys.svelte.js';
	import { igdbScore } from '#lib/rating.js';
	import { tooltip } from '#lib/tooltip.js';

	interface Props {
		/** Bound: the rating control updates it in place. */
		game: GameDetail;
		/** The route's game id, for the ZIP download URL. */
		gameId: string;
		memberOf: CollectionRef[];
		/** B10: the header download's releases, primary first. */
		downloadRoms: RomDetail[];
		fromMissingMetadata: boolean;
		fromMissingIgdb: boolean;
		/** Bound: P5's Show more state, kept by the page across reloads of the same game. */
		descriptionExpanded: boolean;
		ondownload: (url: string, rom?: RomDetail) => unknown;
		onsaved: (game: GameDetail) => void;
		onstale: () => void;
		onaction: (action: AdminAction) => void;
		oninfojson: () => void;
		oncollections: () => void;
	}

	let {
		game = $bindable(),
		gameId,
		memberOf,
		downloadRoms,
		fromMissingMetadata,
		fromMissingIgdb,
		descriptionExpanded = $bindable(),
		ondownload,
		onsaved,
		onstale,
		onaction,
		oninfojson,
		oncollections
	}: Props = $props();

	/** P5: the description is clamped to two lines; the toggle shows only when the text is taller than that. */
	let descriptionEl = $state<HTMLParagraphElement>();
	let descriptionOverflows = $state(false);
	function measureDescription(): void {
		if (descriptionEl && !descriptionExpanded)
			descriptionOverflows = descriptionEl.scrollHeight > descriptionEl.clientHeight;
	}
	$effect(() => {
		void game.description;
		if (descriptionEl && !descriptionExpanded) untrack(measureDescription);
	});
	// P3: the cover (any type); screenshots are in the Images gallery.
	const cover = $derived(game.images.find((i) => i.isCover));
	// G5: a release pointer means the game's ROMs are stored next to its ZIP.
	const external = $derived(game.roms.some((r) => r.externalFileName !== null));
</script>

<svelte:window onresize={measureDescription} />

<!-- P2: each value links to its Collections group (resolved by name there); none is a plain dash. -->
{#snippet metadataLinks(tab: 'developers' | 'publishers' | 'genres', names: string[])}
	{#each names as name, i (i)}{i > 0 ? ', ' : ''}<a
			href="{resolve('/collections')}?{new URLSearchParams({ tab, name })}">{name}</a
		>{:else}—{/each}
{/snippet}

<div class="row g-4 mb-4">
	<div class="col-12 col-md-4 col-lg-3">
		{#if cover}
			<img
				class="img-fluid rounded border"
				src={cover.url}
				width={cover.width}
				height={cover.height}
				alt="{game.displayTitle} cover"
			/>
		{/if}
	</div>
	<div class="col-12 col-md-8 col-lg-9">
		<div class="d-flex flex-wrap align-items-center gap-3 mb-2">
			{#if auth.isAdmin}
				<GameTitle {game} {onsaved} {onstale} />
			{:else}
				<h1 class="h3 mb-0">{game.displayTitle}</h1>
			{/if}
			<RatingControl
				gameId={game.id}
				title={game.displayTitle}
				bind:rating={game.rating}
				bind:favourite={game.favourite}
				readonly={!auth.isAdmin}
				{onsaved}
			/>
			{#if game.igdbRating}
				{@const votes = game.igdbRating.votes}
				<!-- G1: the IGDB rating (rounded half away from zero), shown to every user; focusable for its tooltip. -->
				<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
				<span
					class="badge text-bg-light border"
					tabindex="0"
					use:tooltip={`IGDB rating (users and critics), refreshed ${formatDateTime(game.igdbRating.syncedAt)}`}
					>IGDB {igdbScore(game.igdbRating.score)}/100 · {formatCount(votes)}
					{votes === 1 ? 'vote' : 'votes'}</span
				>
			{/if}
		</div>
		{#if memberOf.length}
			<ul class="list-inline mb-2" aria-label="In collections">
				{#each memberOf as collection (collection.id)}
					<li class="list-inline-item">
						<a
							class="badge text-bg-secondary text-decoration-none"
							href={resolve('/collections/[id]', { id: collection.id })}
							><Icon name="collections" /> {collection.name}</a
						>
					</li>
				{/each}
			</ul>
		{/if}
		<!-- G1: key/value table (T3). -->
		<div class="card mb-3">
			<table class="table table-sm table-striped table-hover mb-0">
				<caption class="visually-hidden">Game information</caption>
				<tbody>
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">System</th>
						<td class="text-break">
							<div class="d-flex align-items-center gap-2">
								<SystemBadge system={game.system} name={game.systemName} class="text-wrap" />
								{#if auth.isAdmin}
									<!-- Quick edit: opens the same Edit Metadata modal as the Manage menu. -->
									<IconButton
										class="ms-auto quick-edit"
										icon="edit"
										label="Quick edit"
										tooltip="Edit Metadata"
										onclick={() => onaction({ kind: 'edit' })}
									/>
									{#if game.igdbConfigured}
										<!-- GP1, G1: Edit Metadata opened on Lookup Info, searching at once, as Find on IGDB does. -->
										<IconButton
											icon="search"
											label="Lookup Info for {game.displayTitle}"
											tooltip="Lookup Info"
											onclick={() => onaction({ kind: 'edit', lookup: true })}
										/>
									{/if}
								{/if}
							</div>
						</td>
					</tr>
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">Year</th>
						<td class="text-break">
							{#if game.releaseYear !== null && game.igdbId !== null && game.igdbId > 0}
								<!-- P2: Timeline only shows IGDB-linked games, so only those link there. -->
								<a
									href="{resolve('/timeline')}?{new URLSearchParams({
										from: String(game.releaseYear),
										to: String(game.releaseYear)
									})}">{game.releaseYear}</a
								>
							{:else}
								{game.releaseYear ?? '—'}
							{/if}
						</td>
					</tr>
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">Developers</th>
						<td class="text-break">{@render metadataLinks('developers', game.developers)}</td>
					</tr>
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">Publishers</th>
						<td class="text-break">{@render metadataLinks('publishers', game.publishers)}</td>
					</tr>
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">Genres</th>
						<td class="text-break">{@render metadataLinks('genres', game.genres)}</td>
					</tr>
				</tbody>
			</table>
		</div>
		{#if auth.isAdmin && game.igdbConfigured && game.igdbId === null}
			<!-- IGDB lookup N1: unlinked games only; gone once a save returns the linked detail. -->
			<div class="alert alert-info d-flex flex-wrap align-items-center gap-2 mb-3" role="status">
				This game isn't linked to IGDB.
				<Button
					variant="primary"
					outline
					size="sm"
					class="ms-auto"
					icon="search"
					onclick={() => onaction({ kind: 'edit', lookup: true })}>Find on IGDB</Button
				>
			</div>
		{/if}
		{#if game.description}
			<p
				id="game-description"
				class="game-description"
				class:clamped={!descriptionExpanded}
				class:mb-1={descriptionOverflows}
				bind:this={descriptionEl}
			>
				{game.description}
			</p>
			{#if descriptionOverflows}
				<Button
					class="btn btn-link btn-sm p-0 mb-3"
					aria-expanded={descriptionExpanded}
					aria-controls="game-description"
					onclick={() => (descriptionExpanded = !descriptionExpanded)}
					>{descriptionExpanded ? 'Show less' : 'Show more'}</Button
				>
			{/if}
		{/if}
		<div class="d-flex flex-wrap gap-2">
			{#if external}
				<!-- G5: an external system's ZIP holds no ROMs; they download per release. -->
				<Button
					variant="primary"
					size="sm"
					icon="download"
					{@attach fromAction(tooltip, () => 'ROM files are downloaded per release')}
					onclick={() => ondownload(`/api/games/${encodeURIComponent(gameId)}/download`)}
					>Download ZIP</Button
				>
			{:else}
				<Button
					variant="primary"
					size="sm"
					icon="download"
					onclick={() => ondownload(`/api/games/${encodeURIComponent(gameId)}/download`)}
					>Download ZIP</Button
				>
			{/if}
			{#if downloadRoms.length === 1}
				<Button
					variant="primary"
					outline
					size="sm"
					icon="download"
					onclick={() => ondownload(downloadRoms[0].downloadUrl, downloadRoms[0])}
					>Download ROM</Button
				>
			{:else if downloadRoms.length > 1}
				<div class="dropdown">
					<Button
						variant="primary"
						outline
						size="sm"
						class="dropdown-toggle"
						icon="download"
						data-bs-toggle="dropdown"
						aria-expanded="false">Download ROM</Button
					>
					<ul class="dropdown-menu">
						{#each downloadRoms as rom (rom.id)}
							<li>
								<Button class="dropdown-item" onclick={() => ondownload(rom.downloadUrl, rom)}
									>{rom.fileName}<span class="badge text-bg-light border ms-2"
										><FileSize bytes={rom.sizeBytes} /></span
									></Button
								>
							</li>
						{/each}
					</ul>
				</div>
			{/if}
			<!-- G3: filled with a count while the game is in a collection. -->
			{#if memberOf.length}
				<Button variant="primary" size="sm" icon="collections" onclick={oncollections}
					>Collections<span class="badge text-bg-light ms-1"
						>{formatCount(memberOf.length)}<span class="visually-hidden">&#32;collections</span
						></span
					></Button
				>
			{:else}
				<Button variant="secondary" outline size="sm" icon="collections" onclick={oncollections}
					>Collections</Button
				>
			{/if}
			{#if fromMissingMetadata && auth.isAdmin}
				<Button
					variant="primary"
					outline
					size="sm"
					icon="report-missing-metadata"
					onclick={() => void hotkeys.run('reports.missingMetadata.next')}
					>Next missing metadata</Button
				>
			{/if}
			{#if fromMissingIgdb && auth.isAdmin}
				<Button
					variant="primary"
					outline
					size="sm"
					icon="search"
					onclick={() => void hotkeys.run('reports.missingIgdbId.next')}
					>Next missing IGDB ID</Button
				>
			{/if}
			<!-- B3: Manage, last in the row: View info.json for everyone, then the Admin actions. -->
			<div class="dropdown">
				<Button
					variant="secondary"
					outline
					size="sm"
					class="dropdown-toggle"
					icon="admin"
					data-bs-toggle="dropdown"
					aria-expanded="false">Manage</Button
				>
				<ul class="dropdown-menu dropdown-menu-end">
					<li>
						<Button icon="code" class="dropdown-item" onclick={oninfojson}>View info.json</Button>
					</li>
					{#if auth.isAdmin}
						<li><hr class="dropdown-divider" /></li>
						<li>
							<Button icon="edit" class="dropdown-item" onclick={() => onaction({ kind: 'edit' })}
								>Edit Metadata</Button
							>
						</li>
						<li>
							<Button icon="retry" class="dropdown-item" onclick={() => onaction({ kind: 'retry' })}
								>Retry media</Button
							>
						</li>
						<li>
							<Button icon="merge" class="dropdown-item" onclick={() => onaction({ kind: 'merge' })}
								>Merge into…</Button
							>
						</li>
						<li><hr class="dropdown-divider" /></li>
						<li>
							<!-- text-danger-emphasis: plain text-danger fails AA on the dark dropdown. -->
							<Button
								icon="delete"
								class="dropdown-item text-danger-emphasis"
								onclick={() => onaction({ kind: 'deleteGame' })}>Delete game</Button
							>
						</li>
					{/if}
				</ul>
			</div>
		</div>
	</div>
</div>

<style>
	.game-description {
		white-space: pre-line;
	}
	/* P5: two lines until Show more. */
	.game-description.clamped {
		display: -webkit-box;
		-webkit-box-orient: vertical;
		-webkit-line-clamp: 2;
		line-clamp: 2;
		overflow: hidden;
	}
</style>
