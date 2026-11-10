<script lang="ts">
	import { onDestroy, untrack } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { GameFacets, GameListItem, Page, SystemInfo } from '#lib/api/types.js';
	import CollectionPickerModal from '#lib/components/CollectionPickerModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import GameResults from '#lib/components/GameResults.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import {
		defaultDir,
		facetsApiUrl,
		gamesApiUrl,
		hasFilters,
		letters,
		libraryPageSize,
		normaliseSearch,
		optionLabel,
		type FacetCounts,
		parseFilters,
		toSearch,
		type LibraryFilters,
		type LibrarySort
	} from '#lib/libraryQuery.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	type View = 'grid' | 'list';
	const viewKey = 'libraryView';

	function readView(): View {
		try {
			return localStorage.getItem(viewKey) === 'list' ? 'list' : 'grid';
		} catch {
			return 'grid';
		}
	}

	function setView(value: View): void {
		view = value;
		try {
			localStorage.setItem(viewKey, value);
		} catch {
			// remembering the view is a convenience
		}
	}

	let view = $state<View>(readView());
	let systems = $state<SystemInfo[]>([]);
	let result = $state<Page<GameListItem> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	let search = $state('');
	let searchTimer: ReturnType<typeof setTimeout> | undefined;
	// A search typed just before leaving the page must not navigate back to it.
	onDestroy(() => clearTimeout(searchTimer));

	// B26: bulk "Add to collection…"; the selection is per page and filter state.
	let selecting = $state(false);
	const selected = new SvelteSet<string>();
	let picking = $state(false);

	$effect(() => {
		void page.url.search;
		untrack(() => selected.clear());
	});

	function toggleSelecting(): void {
		selecting = !selecting;
		selected.clear();
	}

	const filters = $derived(parseFilters(page.url.searchParams));
	const regions = $derived(
		filters.system
			? (systems.find((s) => s.id === filters.system)?.regions ?? [])
			: [...new Set(systems.flatMap((s) => s.regions))].sort()
	);
	const pages = $derived(result ? Math.max(1, Math.ceil(result.total / libraryPageSize)) : 1);
	const dir = $derived(filters.dir ?? defaultDir(filters.sort));

	function go(next: LibraryFilters): void {
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/${toSearch(next)}`, { reset: false });
	}

	/** Any filter change goes back to page 1. */
	function update(changes: Partial<LibraryFilters>): void {
		go({ ...filters, page: 1, ...changes });
	}

	function onSearchInput(): void {
		clearTimeout(searchTimer);
		searchTimer = setTimeout(() => {
			const q = normaliseSearch(search);
			if (q !== filters.q) update({ q });
		}, 300);
	}

	// URL → search box (back/forward, Clear filters), without clobbering a 1-char draft.
	$effect(() => {
		const q = filters.q;
		untrack(() => {
			if (normaliseSearch(search) !== q) search = q;
		});
	});

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter options; the games call shows errors
		);
	});

	// F3: a string, so a page or sort change (same URL) doesn't rerun the facets effect.
	const facetsUrl = $derived(facetsApiUrl(filters));
	let facets = $state<{ systems: FacetCounts; regions: FacetCounts; letters: FacetCounts }>({
		systems: null,
		regions: null,
		letters: null
	});

	$effect(() => {
		const url = facetsUrl;
		const controller = new AbortController();
		facets = { systems: null, regions: null, letters: null };
		api.get<GameFacets>(url, { signal: controller.signal }).then(
			(f) =>
				(facets = {
					systems: Object.fromEntries(f.systems.map((s) => [s.id, s.count])),
					regions: Object.fromEntries(f.regions.map((r) => [r.region, r.count])),
					letters: Object.fromEntries(f.letters.map((l) => [l.letter, l.count]))
				}),
			() => {} // counts are a convenience: options just show none; the games call reports errors
		);
		return () => controller.abort();
	});

	$effect(() => {
		const url = gamesApiUrl(filters);
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		api.get<Page<GameListItem>>(url, { signal: controller.signal }).then(
			(page) => (result = page),
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});
</script>

<svelte:head><title>Library · RnRomManager</title></svelte:head>

<h1 class="h3 mb-3"><Icon name="library" /> Library</h1>

<form class="row g-2 align-items-end mb-3" role="search" onsubmit={(e) => e.preventDefault()}>
	<div class="col-12 col-md-4 col-lg-2">
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
			onchange={(e) => update({ system: e.currentTarget.value || null, region: null })}
		>
			<option value="">All systems</option>
			{#each systems as system (system.id)}
				<option value={system.id}
					>{optionLabel(system.displayName, facets.systems, system.id)}</option
				>
			{/each}
		</select>
	</div>
	<div class="col-6 col-md-4 col-lg-2">
		<label class="form-label" for="region">Region</label>
		<select
			id="region"
			class="form-select"
			value={filters.region ?? ''}
			onchange={(e) => update({ region: e.currentTarget.value || null })}
		>
			<option value="">Any region</option>
			{#each regions as region (region)}
				<option value={region}>{optionLabel(region, facets.regions, region)}</option>
			{/each}
			{#if filters.region && !regions.includes(filters.region)}
				<option value={filters.region}
					>{optionLabel(filters.region, facets.regions, filters.region)}</option
				>
			{/if}
		</select>
	</div>
	<div class="col-6 col-md-4 col-lg-2">
		<label class="form-label" for="letter">Letter</label>
		<select
			id="letter"
			class="form-select"
			value={filters.letter ?? ''}
			onchange={(e) => update({ letter: e.currentTarget.value || null })}
		>
			<option value="">All</option>
			{#each letters as letter (letter)}
				<option value={letter}>{optionLabel(letter, facets.letters, letter)}</option>
			{/each}
		</select>
	</div>
	<div class="col-6 col-md-4 col-lg-2">
		<label class="form-label" for="sort">Sort by</label>
		<div class="input-group">
			<select
				id="sort"
				class="form-select"
				value={filters.sort}
				onchange={(e) => update({ sort: e.currentTarget.value as LibrarySort, dir: null })}
			>
				<option value="title">Title</option>
				<option value="year">Year</option>
				<option value="added">Date added</option>
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
	<div class="col-12 col-md-4 col-lg-2 d-flex gap-2 justify-content-md-end">
		<button
			type="button"
			class="btn btn-outline-secondary"
			class:active={filters.favourite}
			aria-pressed={filters.favourite}
			aria-label="Favourites"
			onclick={() => update({ favourite: !filters.favourite })}
			><Icon name={filters.favourite ? 'favourite-fill' : 'favourite'} /></button
		>
		<div class="btn-group" role="group" aria-label="View">
			<button
				type="button"
				class="btn btn-outline-secondary"
				class:active={view === 'grid'}
				aria-pressed={view === 'grid'}
				aria-label="Grid view"
				onclick={() => setView('grid')}><Icon name="grid" /></button
			>
			<button
				type="button"
				class="btn btn-outline-secondary"
				class:active={view === 'list'}
				aria-pressed={view === 'list'}
				aria-label="List view"
				onclick={() => setView('list')}><Icon name="list" /></button
			>
		</div>
	</div>
</form>

<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
	<button
		type="button"
		class="btn btn-sm btn-outline-secondary"
		class:active={selecting}
		aria-pressed={selecting}
		onclick={toggleSelecting}><Icon name="select" /> Select</button
	>
	{#if selecting}
		<button
			type="button"
			class="btn btn-sm btn-outline-secondary"
			disabled={!result?.items.length}
			onclick={() => result?.items.forEach((g) => selected.add(g.id))}
			>Select all on this page</button
		>
		<button
			type="button"
			class="btn btn-sm btn-primary"
			disabled={selected.size === 0}
			onclick={() => (picking = true)}><Icon name="collections" /> Add to collection…</button
		>
		<span class="small text-body-secondary" aria-live="polite">{selected.size} selected</span>
	{/if}
</div>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !result}
	<Loading label="Loading games…" />
{:else if result.items.length === 0}
	{#if hasFilters(filters) || filters.page > 1}
		<EmptyState message="No games match these filters.">
			{#snippet action()}
				<button
					type="button"
					class="btn btn-outline-primary"
					onclick={() => go(parseFilters(new URLSearchParams()))}
				>
					Clear filters
				</button>
			{/snippet}
		</EmptyState>
	{:else if auth.isAdmin}
		<EmptyState message="The library is empty.">
			{#snippet action()}
				<a class="btn btn-primary" href={resolve('/ingestion')}
					><Icon name="ingest" /> Go to Ingestion</a
				>
			{/snippet}
		</EmptyState>
	{:else}
		<EmptyState message="The library is empty." />
	{/if}
{:else}
	<p class="text-body-secondary small" aria-live="polite">
		{result.total}
		{result.total === 1 ? 'game' : 'games'}
	</p>
	<GameResults bind:games={result.items} {view} selected={selecting ? selected : undefined} />

	{#if pages > 1}
		<nav class="d-flex justify-content-center align-items-center gap-3 mt-4" aria-label="Pages">
			<button
				type="button"
				class="btn btn-outline-secondary"
				disabled={filters.page <= 1}
				onclick={() => go({ ...filters, page: filters.page - 1 })}>Previous</button
			>
			<span>Page {filters.page} of {pages}</span>
			<button
				type="button"
				class="btn btn-outline-secondary"
				disabled={filters.page >= pages}
				onclick={() => go({ ...filters, page: filters.page + 1 })}>Next</button
			>
		</nav>
	{/if}
{/if}

<CollectionPickerModal
	bind:open={picking}
	gameIds={[...selected]}
	onadded={() => selected.clear()}
/>
