<script lang="ts">
	import { untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { GameListItem, Page, SystemInfo } from '#lib/api/types.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import {
		defaultDir,
		gamesApiUrl,
		hasFilters,
		letters,
		libraryPageSize,
		normaliseSearch,
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

	const filters = $derived(parseFilters(page.url.searchParams));
	const regions = $derived(
		filters.system
			? (systems.find((s) => s.id === filters.system)?.regions ?? [])
			: [...new Set(systems.flatMap((s) => s.regions))].sort()
	);
	const systemNames = $derived(new Map(systems.map((s) => [s.id, s.displayName])));
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
			onchange={(e) => update({ system: e.currentTarget.value || null, region: null })}
		>
			<option value="">All systems</option>
			{#each systems as system (system.id)}
				<option value={system.id}>{system.displayName}</option>
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
				<option value={region}>{region}</option>
			{/each}
			{#if filters.region && !regions.includes(filters.region)}
				<option value={filters.region}>{filters.region}</option>
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
				<option value={letter}>{letter}</option>
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
	<div class="col-12 col-md-4 col-lg-1 d-flex justify-content-md-end">
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
				<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- route arrives in S44 -->
				<a class="btn btn-primary" href="/ingestion"><Icon name="ingest" /> Go to Ingestion</a>
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
	{#if view === 'grid'}
		<div class="row row-cols-2 row-cols-md-3 row-cols-lg-4 row-cols-xl-6 g-3">
			{#each result.items as game (game.id)}
				<div class="col">
					<a class="card h-100 text-decoration-none" href={resolve('/games/[id]', { id: game.id })}>
						{#if game.boxArtUrl}
							<img
								class="card-img-top"
								src={game.boxArtUrl}
								alt="{game.displayTitle} box art"
								loading="lazy"
							/>
						{:else}
							<div class="card-img-top ratio ratio-1x1 bg-body-secondary">
								<div class="d-flex align-items-center justify-content-center">
									<Icon name="system-{game.system}" class="fs-1" />
								</div>
							</div>
						{/if}
						<div class="card-body p-2">
							<div class="small fw-semibold">{game.displayTitle}</div>
							<div class="small text-body-secondary">
								{[game.releaseYear, game.regions.join(', ')].filter(Boolean).join(' · ')}
							</div>
						</div>
					</a>
				</div>
			{/each}
		</div>
	{:else}
		<div class="table-responsive">
			<table class="table table-hover align-middle">
				<thead>
					<tr>
						<th scope="col">Title</th>
						<th scope="col">System</th>
						<th scope="col">Year</th>
						<th scope="col">Regions</th>
						<th scope="col" class="text-end">Releases</th>
					</tr>
				</thead>
				<tbody>
					{#each result.items as game (game.id)}
						<tr>
							<td><a href={resolve('/games/[id]', { id: game.id })}>{game.displayTitle}</a></td>
							<td>{systemNames.get(game.system) ?? game.system}</td>
							<td>{game.releaseYear ?? ''}</td>
							<td>{game.regions.join(', ')}</td>
							<td class="text-end">{game.romCount}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}

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
