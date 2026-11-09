<script lang="ts">
	import { onDestroy, untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import type { CollectionListItem, Page } from '#lib/api/types.js';
	import { collectionsApi, collectionsSearch } from '#lib/collections.js';
	import CollectionModal from '#lib/components/CollectionModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import { pageCount, readPageSize, writePageSize } from '#lib/pageSize.js';

	/** B22: the caller's collections as cards, filtered by `q` and paged; both live in the URL. */
	const sizeKey = 'collections';

	let result = $state<Page<CollectionListItem> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	let pageSize = $state(readPageSize(sizeKey));
	let filter = $state('');
	let creating = $state(false);
	let filterTimer: ReturnType<typeof setTimeout> | undefined;
	onDestroy(() => clearTimeout(filterTimer));

	const q = $derived(page.url.searchParams.get('q')?.trim() ?? '');
	const pageNumber = $derived(
		Math.max(1, Math.floor(Number(page.url.searchParams.get('page'))) || 1)
	);

	function go(next: { q: string; page: number }): void {
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/collections${collectionsSearch(next)}`, { replace: true, reset: false });
	}

	function onFilterInput(): void {
		clearTimeout(filterTimer);
		filterTimer = setTimeout(() => {
			if (filter.trim() !== q) go({ q: filter.trim(), page: 1 });
		}, 300);
	}

	function setPageSize(size: number): void {
		pageSize = size;
		writePageSize(sizeKey, size);
		go({ q, page: 1 });
	}

	// URL → filter box (back/forward).
	$effect(() => {
		const current = q;
		untrack(() => {
			if (filter.trim() !== current) filter = current;
		});
	});

	$effect(() => {
		const params = { q, page: pageNumber, pageSize };
		void reload;
		let stale = false;
		error = null;
		result = null;
		collectionsApi.list(params).then(
			(list) => {
				if (!stale) result = list;
			},
			(e) => {
				if (!stale) error = e;
			}
		);
		return () => (stale = true);
	});
</script>

<svelte:head><title>Collections · RnRomManager</title></svelte:head>

<div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-3">
	<h1 class="h3 mb-0"><Icon name="collections" /> Collections</h1>
	<button type="button" class="btn btn-primary" onclick={() => (creating = true)}>
		<Icon name="add" /> New collection
	</button>
</div>

<form class="row g-2 mb-3" role="search" onsubmit={(e) => e.preventDefault()}>
	<div class="col-12 col-md-6 col-lg-4">
		<label class="visually-hidden" for="collection-filter">Filter collections</label>
		<input
			id="collection-filter"
			type="search"
			class="form-control"
			placeholder="Filter by name"
			maxlength="255"
			bind:value={filter}
			oninput={onFilterInput}
		/>
	</div>
</form>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !result}
	<Loading label="Loading collections…" />
{:else if result.items.length === 0}
	{#if q}
		<EmptyState message="No collections match “{q}”." />
	{:else}
		<EmptyState message="You have no collections yet." icon="collections">
			{#snippet action()}
				<button type="button" class="btn btn-primary" onclick={() => (creating = true)}>
					<Icon name="add" /> New collection
				</button>
			{/snippet}
		</EmptyState>
	{/if}
{:else}
	<div class="row row-cols-2 row-cols-md-3 row-cols-lg-4 row-cols-xl-6 g-3">
		{#each result.items as collection (collection.id)}
			<div class="col">
				<div class="card h-100">
					<div class="card-img-top ratio ratio-1x1 bg-body-secondary">
						{#if collection.coverUrl}
							<img
								class="object-fit-cover"
								src={collection.coverUrl}
								alt="{collection.name} cover"
								loading="lazy"
							/>
						{:else}
							<div
								class="d-flex align-items-center justify-content-center"
								data-testid="placeholder"
							>
								<Icon name="collections" class="fs-1" />
							</div>
						{/if}
					</div>
					<div class="card-body p-2">
						<a
							class="d-block small fw-semibold text-truncate text-decoration-none stretched-link"
							href={resolve('/collections/[id]', { id: collection.id })}
							title={collection.name}>{collection.name}</a
						>
						<div class="small text-body-secondary">
							{collection.gameCount}
							{collection.gameCount === 1 ? 'game' : 'games'}
						</div>
					</div>
				</div>
			</div>
		{/each}
	</div>
{/if}

{#if result}
	<Pagination
		page={pageNumber}
		pages={pageCount(result.total, pageSize)}
		onpage={(p) => go({ q, page: p })}
		{pageSize}
		onpagesize={setPageSize}
		label="Collection pages"
	/>
{/if}

<CollectionModal bind:open={creating} collection={null} />
