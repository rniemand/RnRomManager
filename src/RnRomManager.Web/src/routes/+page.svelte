<script lang="ts">
	import SearchInput from '#lib/components/forms/SearchInput.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import ViewToggle from '#lib/components/common/ViewToggle.svelte';
	import { debounced } from '#lib/debounce.js';
	import { replaceUrl } from '#lib/urlState.js';
	import PageTitle from '#lib/components/layout/PageTitle.svelte';
	import { onDestroy, untrack } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type {
		GameFacets,
		GameListItem,
		Page,
		SystemInfo,
		FilterOption,
		FacetCounts,
		LibraryFilters
	} from '#lib/types/index.js';
	import CollectionPickerModal from '#lib/components/modals/CollectionPickerModal.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import FilterDropdown from '#lib/components/forms/FilterDropdown.svelte';
	import { systemFilterOptions } from '#lib/systemFilter.js';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import GameResults from '#lib/components/games/GameResults.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import {
		defaultDir,
		facetsApiUrl,
		gamesApiUrl,
		hasFilters,
		letters,
		libraryPageSize,
		normaliseSearch,
		parseFilters,
		toSearch
	} from '#lib/libraryQuery.js';
	import type { LibrarySort, LibraryView } from '#lib/enums/index.js';
	import {
		libraryFilterParams,
		readLibraryView,
		readSavedLibraryFilter,
		saveLibraryFilter,
		saveLibraryView
	} from '#lib/libraryFilter.js';
	import { formatCount } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	function setView(value: LibraryView): void {
		view = value;
		saveLibraryView(value);
	}

	let view = $state<LibraryView>(readLibraryView());
	let systems = $state<SystemInfo[]>([]);
	let result = $state<Page<GameListItem> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	let search = $state('');
	// A search typed just before leaving the page must not navigate back to it.
	onDestroy(() => onSearchInput.cancel());

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
	// Only systems with ROMs; a selected one from the URL stays listed so it can be seen and changed.
	const systemOptions = $derived(systemFilterOptions(systems, filters.system));
	// A region from the URL that the list doesn't have stays listed, so it can be seen and changed.
	const regionOptions = $derived<FilterOption[]>(
		[
			...regions,
			...(filters.region && !regions.includes(filters.region) ? [filters.region] : [])
		].map((r) => ({ value: r, label: r }))
	);
	const letterOptions: FilterOption[] = letters.map((l) => ({ value: l, label: l }));
	const sortOptions: FilterOption[] = [
		{ value: 'title', label: 'Title' },
		{ value: 'year', label: 'Year' },
		{ value: 'added', label: 'Date added' },
		{ value: 'rating', label: 'Rating' }
	];

	/** L6: ` (1,234)` from the facet counts; '' while they load or after a failed call. */
	function countSuffix(counts: FacetCounts, key: string): string {
		return counts ? ` (${formatCount(counts[key] ?? 0)})` : '';
	}

	const chips = $derived.by(() => {
		const list: { field: string; text: string; clear: Partial<LibraryFilters> }[] = [];
		if (filters.system) {
			const name = systems.find((s) => s.id === filters.system)?.displayName ?? filters.system;
			list.push({
				field: 'System',
				text: `System: ${name}${countSuffix(facets.systems, filters.system)}`,
				clear: { system: null, region: null }
			});
		}
		if (filters.region)
			list.push({
				field: 'Region',
				text: `Region: ${filters.region}${countSuffix(facets.regions, filters.region)}`,
				clear: { region: null }
			});
		if (filters.letter)
			list.push({
				field: 'Letter',
				text: `Letter: ${filters.letter}${countSuffix(facets.letters, filters.letter)}`,
				clear: { letter: null }
			});
		if (filters.q) list.push({ field: 'Search', text: `Search: "${filters.q}"`, clear: { q: '' } });
		if (filters.favourite)
			list.push({ field: 'Favourites', text: 'Favourites', clear: { favourite: false } });
		return list;
	});
	/** B2: Clear all is enabled by a search draft, any filter or a later page; sort and direction don't count. */
	const canClear = $derived(search.trim() !== '' || hasFilters(filters) || filters.page > 1);

	/** B2: clears the search box (and its pending debounce), every filter and the page; keeps sort and direction. The
	 * saved filter is then re-saved by the normal save path, holding just sort/dir. */
	function resetFilters(): void {
		onSearchInput.cancel();
		search = '';
		go({ ...parseFilters(new URLSearchParams()), sort: filters.sort, dir: filters.dir });
	}

	const pages = $derived(result ? pageCount(result.total, libraryPageSize) : 1);
	const dir = $derived(filters.dir ?? defaultDir(filters.sort));

	function go(next: LibraryFilters): void {
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/${toSearch(next)}`, { reset: false });
	}

	/** Any filter change goes back to page 1. */
	function update(changes: Partial<LibraryFilters>): void {
		go({ ...filters, page: 1, ...changes });
	}

	// L7: on mount, a URL without filter params restores the saved filter (parsed, so bad values fall back);
	// after that every URL state is saved, so an explicit URL wins and a later bare `/` clears it.
	let restoring =
		!libraryFilterParams.some((p) => page.url.searchParams.has(p)) && !!readSavedLibraryFilter();
	$effect(() => {
		const search = toSearch(filters);
		untrack(() => {
			if (restoring) {
				restoring = false;
				const saved = toSearch(parseFilters(new URLSearchParams(readSavedLibraryFilter())));
				saveLibraryFilter(saved);
				void replaceUrl(`/${saved}`);
			} else saveLibraryFilter(search);
		});
	});

	const onSearchInput = debounced(() => {
		const q = normaliseSearch(search);
		if (q !== filters.q) update({ q });
	}, 300);

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

<PageTitle icon="library" text="Library" />

<form
	class="d-flex flex-wrap align-items-center gap-2 mb-2"
	role="search"
	onsubmit={(e) => e.preventDefault()}
>
	<label class="visually-hidden" for="q">Search</label>
	<SearchInput
		size="sm"
		class="flex-grow-1 search-input"
		id="q"
		placeholder="At least 2 characters"
		maxlength={255}
		bind:value={search}
		oninput={onSearchInput}
	/>
	<FilterDropdown
		label="System"
		options={systemOptions}
		value={filters.system}
		counts={facets.systems}
		all={{ label: 'All systems', sum: true }}
		filterPlaceholder="Filter systems…"
		onselect={(system) => update({ system, region: null })}
	/>
	<FilterDropdown
		label="Region"
		options={regionOptions}
		value={filters.region}
		counts={facets.regions}
		all={{ label: 'All regions', sum: false }}
		onselect={(region) => update({ region })}
	/>
	<FilterDropdown
		label="Letter"
		options={letterOptions}
		value={filters.letter}
		counts={facets.letters}
		all={{ label: 'All letters', sum: true }}
		onselect={(letter) => update({ letter })}
	/>
	<FilterDropdown
		label="Sort"
		options={sortOptions}
		value={filters.sort}
		active={filters.sort !== 'title'}
		onselect={(sort) => update({ sort: (sort ?? 'title') as LibrarySort, dir: null })}
	/>
	<Button
		variant="secondary"
		outline
		size="sm"
		icon={dir === 'asc' ? 'sort-asc' : 'sort-desc'}
		aria-label={dir === 'asc'
			? 'Ascending, switch to descending'
			: 'Descending, switch to ascending'}
		onclick={() => update({ dir: dir === 'asc' ? 'desc' : 'asc' })}
	/>
	<Button
		variant="secondary"
		outline
		size="sm"
		class={filters.favourite ? 'active' : undefined}
		icon={filters.favourite ? 'favourite-fill' : 'favourite'}
		aria-pressed={filters.favourite}
		aria-label="Favourites"
		onclick={() => update({ favourite: !filters.favourite })}
	/>
	<!-- B1/B2: Clear all, then Select, then the View toggle last. -->
	<Button
		variant="secondary"
		outline
		size="sm"
		icon="clear"
		disabled={!canClear}
		onclick={resetFilters}>Clear all</Button
	>
	<Button
		variant="secondary"
		outline
		size="sm"
		class={selecting ? 'active' : undefined}
		icon="select"
		aria-pressed={selecting}
		onclick={toggleSelecting}>Select</Button
	>
	<ViewToggle {view} onchange={setView} size="sm" />
</form>

{#if chips.length > 0}
	<div class="d-flex flex-wrap align-items-center gap-2 mb-2" data-testid="active-filters">
		{#each chips as chip (chip.field)}
			<span class="badge rounded-pill text-bg-secondary d-inline-flex align-items-center gap-1">
				{chip.text}
				<Button
					class="btn-close btn-close-white chip-close"
					aria-label="Remove {chip.field} filter"
					onclick={() => update(chip.clear)}
				/>
			</span>
		{/each}
	</div>
{/if}

{#if selecting}
	<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
		<Button
			variant="secondary"
			outline
			size="sm"
			disabled={!result?.items.length}
			onclick={() => result?.items.forEach((g) => selected.add(g.id))}
			>Select all on this page</Button
		>
		<Button
			variant="primary"
			size="sm"
			icon="collections"
			disabled={selected.size === 0}
			onclick={() => (picking = true)}>Add to collection…</Button
		>
		<span class="small text-body-secondary" aria-live="polite">{selected.size} selected</span>
	</div>
{/if}

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !result}
	<Loading label="Loading games…" />
{:else if result.items.length === 0}
	{#if hasFilters(filters) || filters.page > 1}
		<EmptyState message="No games match these filters.">
			{#snippet action()}
				<Button variant="primary" outline onclick={resetFilters}>Clear filters</Button>
			{/snippet}
		</EmptyState>
	{:else if auth.isAdmin}
		<EmptyState message="The library is empty.">
			{#snippet action()}
				<Button variant="primary" icon="ingest" href={resolve('/ingestion')}>Go to Ingestion</Button
				>
			{/snippet}
		</EmptyState>
	{:else}
		<EmptyState message="The library is empty." />
	{/if}
{:else}
	<p class="text-body-secondary small" aria-live="polite">
		{formatCount(result.total)}
		{result.total === 1 ? 'game' : 'games'}
	</p>
	<GameResults bind:games={result.items} {view} selected={selecting ? selected : undefined} />

	{#if pages > 1}
		<nav class="d-flex justify-content-center align-items-center gap-3 mt-4" aria-label="Pages">
			<Button
				variant="secondary"
				outline
				disabled={filters.page <= 1}
				onclick={() => go({ ...filters, page: filters.page - 1 })}>Previous</Button
			>
			<span>Page {filters.page} of {pages}</span>
			<Button
				variant="secondary"
				outline
				disabled={filters.page >= pages}
				onclick={() => go({ ...filters, page: filters.page + 1 })}>Next</Button
			>
		</nav>
	{/if}
{/if}

<CollectionPickerModal
	bind:open={picking}
	gameIds={[...selected]}
	onadded={() => selected.clear()}
/>

<style>
	/* Classes on kit components' elements, which this page doesn't render itself. */
	:global(.search-input) {
		min-width: 12rem;
		width: auto;
	}

	/* Release editing B36: below md the search takes the toolbar's first row; the controls wrap after it. */
	@media (max-width: 767.98px) {
		:global(.search-input) {
			flex-basis: 100%;
		}
	}

	:global(.chip-close) {
		font-size: 0.6em;
	}
</style>
