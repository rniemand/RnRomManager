<script lang="ts">
	import ViewToggle from '#lib/components/common/ViewToggle.svelte';
	import DeleteCollectionModal from '#lib/components/modals/DeleteCollectionModal.svelte';
	import { debounced } from '#lib/debounce.js';
	import { onDestroy, untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type {
		CollectionDetail,
		GameListItem,
		Page,
		SystemInfo,
		LibraryFilters
	} from '#lib/types/index.js';
	import { collectionsApi } from '#lib/collections.js';
	import CollectionModal from '#lib/components/modals/CollectionModal.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import GameResults from '#lib/components/games/GameResults.svelte';
	import GameTypeahead from '#lib/components/forms/GameTypeahead.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import {
		defaultDir,
		gamesApiUrl,
		libraryPageSize,
		normaliseSearch,
		parseFilters,
		toSearch
	} from '#lib/libraryQuery.js';
	import type { LibrarySort, LibraryView } from '#lib/enums/index.js';
	import { readLibraryView, saveLibraryView } from '#lib/libraryFilter.js';
	import { pageCount } from '#lib/pageSize.js';
	import { renderMarkdown } from '#lib/markdown.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/** B24: one collection — its header, then its games with the Library's view, filters, sort and paging. */
	function setView(value: LibraryView): void {
		view = value;
		saveLibraryView(value);
	}

	let view = $state<LibraryView>(readLibraryView());
	let collection = $state<CollectionDetail | null>(null);
	let notFound = $state(false);
	let error = $state<unknown>(null);
	let reloadDetail = $state(0);
	let systems = $state<SystemInfo[]>([]);
	let result = $state<Page<GameListItem> | null>(null);
	let gamesError = $state<unknown>(null);
	let reloadGames = $state(0);
	let search = $state('');
	onDestroy(() => onSearchInput.cancel());
	let editing = $state(false);
	let deleting = $state(false);
	let deleteBusy = $state(false);
	let picked = $state<GameListItem | null>(null);
	let pickerKey = $state(0);

	const id = $derived(page.params.id ?? '');
	const scope = $derived({ collection: id });
	const filters = $derived(parseFilters(page.url.searchParams, scope));
	const dir = $derived(filters.dir ?? defaultDir(filters.sort));
	const pages = $derived(result ? pageCount(result.total, libraryPageSize) : 1);

	function go(next: LibraryFilters): void {
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/collections/${encodeURIComponent(id)}${toSearch(next, scope)}`, { reset: false });
	}

	/** Any filter change goes back to page 1. */
	function update(changes: Partial<LibraryFilters>): void {
		go({ ...filters, page: 1, ...changes });
	}

	const onSearchInput = debounced(() => {
		const q = normaliseSearch(search);
		if (q !== filters.q) update({ q });
	}, 300);

	// URL → search box (back/forward), without clobbering a 1-char draft.
	$effect(() => {
		const q = filters.q;
		untrack(() => {
			if (normaliseSearch(search) !== q) search = q;
		});
	});

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the system filter
		);
	});

	$effect(() => {
		const collectionId = id;
		void reloadDetail;
		let stale = false;
		error = null;
		notFound = false;
		collectionsApi.get(collectionId).then(
			(c) => {
				if (!stale) collection = c;
			},
			(e) => {
				if (stale) return;
				if (isApiError(e) && e.status === 404) notFound = true;
				else error = e;
			}
		);
		return () => (stale = true);
	});

	$effect(() => {
		const url = gamesApiUrl(filters, scope);
		void reloadGames;
		const controller = new AbortController();
		gamesError = null;
		result = null;
		api.get<Page<GameListItem>>(url, { signal: controller.signal }).then(
			(list) => {
				// Removing the last game of the last page leaves an empty page with no pager: go to the new last page.
				if (list?.items.length === 0 && list.total > 0 && filters.page > 1)
					go({ ...filters, page: pageCount(list.total, libraryPageSize) });
				else result = list;
			},
			(e) => {
				// A 404 here means the collection is gone; the detail call shows that.
				if (!controller.signal.aborted && !(isApiError(e) && e.status === 404)) gamesError = e;
			}
		);
		return () => controller.abort();
	});

	/** Detail (count, updatedAt) and the list both change after an add or remove. */
	function refresh(): void {
		reloadDetail++;
		reloadGames++;
	}

	async function add(game: GameListItem): Promise<void> {
		try {
			const { added } = await collectionsApi.addGames(id, [game.id]);
			toasts.show(
				added === 1 ? `Added ${game.displayTitle}` : 'Already in this collection',
				added === 1 ? 'success' : 'info'
			);
			refresh();
		} catch (e) {
			toasts.show(isApiError(e) ? e.title : 'The game could not be added.', 'danger');
		} finally {
			pickerKey++; // a fresh, empty picker
		}
	}

	$effect(() => {
		const game = picked;
		if (game) untrack(() => void add(game));
	});

	async function remove(game: GameListItem): Promise<void> {
		try {
			await collectionsApi.removeGame(id, game.id);
			toasts.show(`Removed ${game.displayTitle}`, 'success');
			refresh();
		} catch (e) {
			toasts.show(isApiError(e) ? e.title : 'The game could not be removed.', 'danger');
		}
	}

	async function confirmDelete(): Promise<void> {
		if (!collection || deleteBusy) return;
		deleteBusy = true;
		try {
			await collectionsApi.delete(collection.id);
			toasts.show(`Deleted ${collection.name}.`, 'success');
			deleting = false;
			await goto(resolve('/collections'));
		} catch (e) {
			deleting = false;
			error = e;
		} finally {
			deleteBusy = false;
		}
	}
</script>

<svelte:head><title>{collection?.name ?? 'Collection'} · RnRomManager</title></svelte:head>

<div class="mb-3">
	<a class="btn btn-sm btn-outline-primary" href={resolve('/collections')}
		><Icon name="back" /> Back to Collections</a
	>
</div>

{#if error}
	<ErrorAlert {error} onretry={() => reloadDetail++} />
{:else if notFound}
	<EmptyState icon="not-found" message="This collection doesn't exist.">
		{#snippet action()}
			<a class="btn btn-primary" href={resolve('/collections')}>Go to Collections</a>
		{/snippet}
	</EmptyState>
{:else if !collection}
	<Loading label="Loading collection…" />
{:else}
	<div class="d-flex flex-wrap gap-3 mb-4">
		<div class="cover ratio ratio-1x1 bg-body-secondary rounded flex-shrink-0">
			{#if collection.coverUrl}
				<img
					class="object-fit-cover rounded"
					src={collection.coverUrl}
					alt="{collection.name} cover"
				/>
			{:else}
				<div class="d-flex align-items-center justify-content-center">
					<Icon name="collections" class="fs-1" />
				</div>
			{/if}
		</div>
		<div class="flex-grow-1 min-w-0">
			<div class="d-flex flex-wrap align-items-start justify-content-between gap-2">
				<h1 class="h3 mb-1 text-break">{collection.name}</h1>
				<div class="d-flex gap-2">
					<button type="button" class="btn btn-outline-secondary" onclick={() => (editing = true)}>
						<Icon name="edit" /> Edit
					</button>
					<button type="button" class="btn btn-outline-danger" onclick={() => (deleting = true)}>
						<Icon name="delete" /> Delete
					</button>
				</div>
			</div>
			<p class="text-body-secondary small mb-2">
				{collection.gameCount}
				{collection.gameCount === 1 ? 'game' : 'games'}
			</p>
			{#if collection.notes}
				<div class="collection-notes" data-testid="collection-notes">
					<!-- eslint-disable-next-line svelte/no-at-html-tags -- sanitised by DOMPurify in renderMarkdown -->
					{@html renderMarkdown(collection.notes)}
				</div>
			{/if}
		</div>
	</div>

	<div class="row mb-3">
		<div class="col-12 col-md-6 col-lg-4">
			{#key pickerKey}
				<GameTypeahead
					id="add-game"
					label="Add games"
					excludeCollection={id}
					overlay
					bind:selected={picked}
				/>
			{/key}
		</div>
	</div>

	<form class="row g-2 align-items-end mb-3" role="search" onsubmit={(e) => e.preventDefault()}>
		<div class="col-12 col-md-4 col-lg-3">
			<label class="form-label" for="q">Search</label>
			<input
				id="q"
				type="search"
				class="form-control"
				placeholder="At least 2 characters"
				maxlength="255"
				bind:value={search}
				oninput={onSearchInput}
			/>
		</div>
		<div class="col-6 col-md-4 col-lg-2">
			<label class="form-label" for="system">System</label>
			<select
				id="system"
				class="form-select"
				value={filters.system ?? ''}
				onchange={(e) => update({ system: e.currentTarget.value || null })}
			>
				<option value="">All systems</option>
				{#each systems as system (system.id)}
					<option value={system.id}>{system.displayName}</option>
				{/each}
			</select>
		</div>
		<div class="col-6 col-md-4 col-lg-3">
			<label class="form-label" for="sort">Sort by</label>
			<div class="input-group">
				<select
					id="sort"
					class="form-select"
					value={filters.sort}
					onchange={(e) => update({ sort: e.currentTarget.value as LibrarySort, dir: null })}
				>
					<option value="added">Added to collection</option>
					<option value="title">Title</option>
					<option value="year">Year</option>
					<option value="rating">Rating</option>
				</select>
				<button
					type="button"
					class="btn btn-outline-secondary"
					aria-label={dir === 'asc'
						? 'Ascending, switch to descending'
						: 'Descending, switch to ascending'}
					onclick={() => update({ dir: dir === 'asc' ? 'desc' : 'asc' })}
				>
					<Icon name={dir === 'asc' ? 'sort-asc' : 'sort-desc'} />
				</button>
			</div>
		</div>
		<div class="col-12 col-md-12 col-lg-4 d-flex gap-2 justify-content-lg-end">
			<button
				type="button"
				class="btn btn-outline-secondary"
				class:active={filters.favourite}
				aria-pressed={filters.favourite}
				aria-label="Favourites"
				onclick={() => update({ favourite: !filters.favourite })}
				><Icon name={filters.favourite ? 'favourite-fill' : 'favourite'} /></button
			>
			<ViewToggle {view} onchange={setView} />
		</div>
	</form>

	{#if gamesError}
		<ErrorAlert error={gamesError} onretry={() => reloadGames++} />
	{:else if !result}
		<Loading label="Loading games…" />
	{:else if result.items.length === 0}
		<EmptyState
			message={filters.q || filters.system || filters.favourite || filters.page > 1
				? 'No games in this collection match these filters.'
				: 'This collection has no games yet. Add some with the box above.'}
		/>
	{:else}
		<p class="text-body-secondary small" aria-live="polite">
			{result.total}
			{result.total === 1 ? 'game' : 'games'}
		</p>
		<GameResults bind:games={result.items} {view}>
			{#snippet action(game)}
				<button
					type="button"
					class="btn btn-sm btn-outline-danger"
					aria-label="Remove {game.displayTitle} from collection"
					title="Remove from collection"
					onclick={() => remove(game)}><Icon name="delete" /></button
				>
			{/snippet}
		</GameResults>
		<Pagination page={filters.page} {pages} onpage={(p) => go({ ...filters, page: p })} />
	{/if}

	<CollectionModal bind:open={editing} {collection} onchanged={(c) => (collection = c)} />
	<DeleteCollectionModal
		bind:open={deleting}
		name={collection.name}
		busy={deleteBusy}
		onconfirm={confirmDelete}
	/>
{/if}

<style>
	.cover {
		width: 8rem;
	}
	.collection-notes :global(:last-child) {
		margin-bottom: 0;
	}
	.collection-notes :global(img) {
		max-width: 100%;
	}
</style>
