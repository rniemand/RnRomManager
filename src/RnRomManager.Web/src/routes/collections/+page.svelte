<script lang="ts">
	import { scrollActiveTab } from '#lib/scrollActiveTab.js';
	import PageTitle from '#lib/components/PageTitle.svelte';
	import { onDestroy, untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type {
		CollectionListItem,
		Page,
		SystemInfo,
		DiscoveryState,
		SystemsState
	} from '#lib/types/index.js';
	import { collectionsApi, collectionsSearch } from '#lib/collections.js';
	import {
		collectionsTab,
		collectionsTabs,
		discoveryApi,
		discoveryName,
		discoverySearch,
		discoveryState,
		discoveryText,
		nameAsFind,
		listSearch,
		readCollectionsTab,
		restoredSearch,
		saveCollectionsTab,
		systemsSearch,
		systemsState,
		tabSearch
	} from '#lib/discovery.js';
	import CollectionModal from '#lib/components/CollectionModal.svelte';
	import DiscoveryGames from '#lib/components/DiscoveryGames.svelte';
	import DiscoveryGroups from '#lib/components/DiscoveryGroups.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemGames from '#lib/components/SystemGames.svelte';
	import SystemList from '#lib/components/SystemList.svelte';
	import LogoModal from '#lib/components/LogoModal.svelte';
	import { pageCount, readPageSize, writePageSize } from '#lib/pageSize.js';

	/**
	 * B22: the caller's collections as cards, filtered by `q` and paged; both live in the URL. Discovery B1: link tabs
	 * switch to the Publishers, Developers and Genres indexes and the Systems tab (`?tab=`); a bare URL is User collections.
	 */
	const sizeKey = 'collections';
	const tab = $derived(collectionsTab(page.url));

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

	const discovery = $derived(discoveryState(page.url));
	/** Group names seen in the group list, by key, for the games heading (the API's games page has none). */
	const groupNames = $state<Record<string, string>>({});

	/**
	 * Paging within a discovery tab replaces the history entry; selecting a group is a link (a new entry). G8: the user's
	 * own search, sort and Clear pass `remember`, so the tab keeps them in this browser.
	 */
	function goDiscovery(next: DiscoveryState, remember = false): void {
		const search = discoverySearch(next);
		if (remember) saveCollectionsTab(next.kind, listSearch(search));
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/collections${search}`, { replace: true, reset: false });
	}

	/**
	 * G8: a tab URL without its own list params or selection gets the tab's remembered search and sort; a malformed
	 * saved value is forgotten.
	 */
	$effect(() => {
		const current = tab;
		if (current === 'collections') return;
		const search = restoredSearch(current, page.url, readCollectionsTab(current));
		if (search === undefined) return;
		if (!search) {
			saveCollectionsTab(current, '');
			return;
		}
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/collections${search}`, { replace: true, reset: false });
	});

	/**
	 * P3: a game page's metadata link (`?tab=<kind>&name=<value>`) opens the group: the name's key replaces it, or `find`
	 * when it can't be resolved. A `key` already in the URL wins, and `name` is dropped.
	 */
	$effect(() => {
		const state = discovery;
		if (!state || !page.url.searchParams.has('name')) return;
		const name = discoveryName(page.url);
		if (!name) {
			goDiscovery(state);
			return;
		}
		let stale = false;
		discoveryApi.key(state.kind, name).then(
			({ key }) => {
				if (!stale) goDiscovery({ ...state, key, gamePage: 1 });
			},
			() => {
				if (!stale) goDiscovery({ ...state, find: nameAsFind(name) });
			}
		);
		return () => (stale = true);
	});

	/** Systems tab B2/B3: the selection from the URL, and the system list, loaded on entering the tab. */
	const systemsView = $derived(tab === 'systems' ? systemsState(page.url) : null);
	let systems = $state<SystemInfo[] | null>(null);
	let systemsError = $state<unknown>(null);
	let systemsReload = $state(0);
	const selectedSystem = $derived(
		systemsView?.system ? systems?.find((s) => s.id === systemsView.system) : undefined
	);

	/** B19: the system whose logo is being changed; a save reloads the list (new `v=` URLs). */
	let editingSystem = $state<SystemInfo | null>(null);
	let systemLogoOpen = $state(false);
	function editSystemLogo(system: SystemInfo): void {
		editingSystem = system;
		systemLogoOpen = true;
	}

	function goSystems(next: SystemsState, remember = false): void {
		const search = systemsSearch(next);
		if (remember) saveCollectionsTab('systems', listSearch(search));
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/collections${search}`, { replace: true, reset: false });
	}

	$effect(() => {
		if (tab !== 'systems') return;
		void systemsReload;
		let stale = false;
		systemsError = null;
		systems = null;
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => {
				if (!stale) systems = list;
			},
			(e) => {
				if (!stale) systemsError = e;
			}
		);
		return () => (stale = true);
	});

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
		if (tab !== 'collections') return;
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

<!-- B2: ms-auto keeps New collection at the right when the title is hidden. -->
<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
	<PageTitle icon="collections" text="Collections" class="mb-0" />
	{#if tab === 'collections'}
		<button type="button" class="btn btn-primary ms-auto" onclick={() => (creating = true)}>
			<Icon name="add" /> New collection
		</button>
	{/if}
</div>

<nav class="mb-3" aria-label="Collections views">
	<ul class="nav nav-tabs" use:scrollActiveTab>
		{#each collectionsTabs as item (item.tab)}
			<li class="nav-item">
				<a
					class="nav-link"
					class:active={tab === item.tab}
					aria-current={tab === item.tab ? 'page' : undefined}
					href="{resolve('/collections')}{tabSearch(item.tab)}">{item.label}</a
				>
			</li>
		{/each}
	</ul>
</nav>

{#if tab === 'collections'}
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
{:else if discovery}
	<div class="row g-4">
		<div class="col-12 col-lg-4">
			<DiscoveryGroups
				view={discovery}
				onpage={(groupPage) => goDiscovery({ ...discovery, groupPage })}
				onselected={(group) => (groupNames[group.key] = group.name)}
				onfind={(find) =>
					goDiscovery({ ...discovery, groupPage: 1, find: find || undefined }, true)}
				onsort={(sort, dir) => goDiscovery({ ...discovery, groupPage: 1, sort, dir }, true)}
				onclear={() =>
					goDiscovery(
						{
							...discovery,
							groupPage: 1,
							find: undefined,
							sort: undefined,
							dir: undefined
						},
						true
					)}
			/>
		</div>
		<div class="col-12 col-lg-8">
			{#if discovery.key}
				{#key `${discovery.kind}:${discovery.key}`}
					<DiscoveryGames
						view={{ ...discovery, key: discovery.key }}
						name={groupNames[discovery.key] ?? null}
						onpage={(gamePage) => goDiscovery({ ...discovery, gamePage })}
					/>
				{/key}
			{:else}
				<EmptyState
					message="Choose a {discoveryText[discovery.kind]
						.singular} to see its games across every system."
					icon="collections"
				/>
			{/if}
		</div>
	</div>
{:else if systemsView}
	<div class="row g-4">
		<div class="col-12 col-lg-4">
			{#if systemsError}
				<ErrorAlert error={systemsError} onretry={() => systemsReload++} />
			{:else if !systems}
				<Loading label="Loading systems…" />
			{:else if systems.length === 0}
				<EmptyState message="No systems are supported." />
			{:else}
				<SystemList
					{systems}
					selected={systemsView.system}
					onedit={editSystemLogo}
					find={systemsView.find}
					onfind={(find) => goSystems({ ...systemsView, find: find || undefined }, true)}
					sort={systemsView.sort}
					dir={systemsView.dir}
					onsort={(sort, dir) => goSystems({ ...systemsView, sort, dir }, true)}
					onclear={() =>
						goSystems({ ...systemsView, find: undefined, sort: undefined, dir: undefined }, true)}
				/>
			{/if}
		</div>
		<div class="col-12 col-lg-8">
			{#if !systemsView.system}
				{#if !systemsError}
					<EmptyState
						message="Choose a system to see its games and emulators."
						icon="collections"
					/>
				{/if}
			{:else if systemsError}
				<!-- B6: nothing on the right until the list loads. -->
			{:else if !systems}
				<Loading label="Loading systems…" />
			{:else if selectedSystem}
				{#key selectedSystem.id}
					<SystemGames
						system={selectedSystem}
						gamePage={systemsView.gamePage}
						onpage={(gamePage) => goSystems({ ...systemsView, gamePage })}
						onedit={editSystemLogo}
					/>
				{/key}
			{:else}
				<EmptyState message="This system isn't supported.">
					{#snippet action()}
						<a
							class="btn btn-outline-primary"
							href="{resolve('/collections')}{tabSearch('systems')}">Back to all systems</a
						>
					{/snippet}
				</EmptyState>
			{/if}
		</div>
	</div>
{/if}

<CollectionModal bind:open={creating} collection={null} />

<LogoModal
	bind:open={systemLogoOpen}
	kind="systems"
	item={editingSystem
		? { key: editingSystem.id, name: editingSystem.displayName, logoUrl: editingSystem.logoUrl }
		: null}
	onchanged={() => systemsReload++}
/>
