<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { GameListItem, Page, SystemInfo, TimelineYears } from '#lib/api/types.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FilterDropdown from '#lib/components/FilterDropdown.svelte';
	import GameResults from '#lib/components/GameResults.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import PageTitle from '#lib/components/PageTitle.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import { formatCount } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';
	import {
		pan,
		rangeLabel,
		timelineDisabled,
		timelineSearch,
		timelineState,
		timelineSystem,
		zoomIn,
		zoomOut,
		type YearRange
	} from '#lib/timeline.js';

	/**
	 * Timeline T1–T6: IGDB-linked games with a release year, counted per year in a zoomable strip, and the shown years'
	 * games below. Systems load first, so an unknown `?system=` is told apart from a known one; the counts load per
	 * system, the games per system, range and page.
	 */
	const pageSize = 50;
	let systems = $state<SystemInfo[] | null>(null);
	let systemsError = $state<unknown>(null);
	let timeline = $state<TimelineYears | null>(null);
	let timelineError = $state<unknown>(null);
	let reload = $state(0);
	let games = $state<Page<GameListItem> | null>(null);
	let gamesError = $state<unknown>(null);
	let gamesReload = $state(0);

	const system = $derived(
		systems ? timelineSystem(page.url, (id) => systems!.some((s) => s.id === id)) : null
	);
	const counts = $derived(new Map(timeline?.years.map((y) => [y.year, y.count]) ?? []));
	const range = $derived<YearRange | null>(
		timeline?.years.length
			? { min: timeline.years[0].year, max: timeline.years[timeline.years.length - 1].year }
			: null
	);
	const view = $derived(range ? timelineState(page.url, system, range) : null);
	const disabled = $derived(view && range ? timelineDisabled(view, range) : null);
	const ticks = $derived(
		view ? Array.from({ length: view.to - view.from + 1 }, (_, i) => view.from + i) : []
	);

	$effect(() => {
		void reload;
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

	$effect(() => {
		if (!systems) return;
		const current = system;
		void reload;
		let stale = false;
		timelineError = null;
		timeline = null;
		const query = current ? `?${new URLSearchParams({ system: current })}` : '';
		api.get<TimelineYears>(`/api/timeline${query}`).then(
			(t) => {
				if (!stale) timeline = t;
			},
			(e) => {
				if (!stale) timelineError = e;
			}
		);
		return () => (stale = true);
	});

	$effect(() => {
		if (!view) return;
		const { system, from, to, page: pageNumber } = view;
		void gamesReload;
		let stale = false;
		gamesError = null;
		games = null;
		const params = new URLSearchParams({
			...(system ? { system } : {}),
			from: String(from),
			to: String(to),
			...(pageNumber > 1 ? { page: String(pageNumber) } : {}),
			pageSize: String(pageSize)
		});
		api.get<Page<GameListItem>>(`/api/timeline/games?${params}`).then(
			(p) => {
				if (!stale) games = p;
			},
			(e) => {
				if (!stale) gamesError = e;
			}
		);
		return () => (stale = true);
	});

	/** T3: every change is a new history entry, as the Library's filters are. */
	function go(search: string): void {
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/timeline${search}`, { reset: false });
	}

	/** A new range goes back to page 1. */
	function show(years: { from: number; to: number }): void {
		if (view && range) go(timelineSearch({ ...view, ...years, page: 1 }, range));
	}

	/** A new system shows its whole range from page 1. */
	function selectSystem(value: string | null): void {
		go(value ? `?${new URLSearchParams({ system: value })}` : '');
	}

	const gamesLabel = (n: number) => `${formatCount(n)} ${n === 1 ? 'game' : 'games'}`;
</script>

<svelte:head><title>Timeline · RnRomManager</title></svelte:head>

<PageTitle icon="timeline" text="Timeline" />

{#if systemsError}
	<ErrorAlert error={systemsError} onretry={() => reload++} />
{:else if timelineError}
	<ErrorAlert error={timelineError} onretry={() => reload++} />
{:else if !systems || !timeline}
	<Loading />
{:else}
	<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
		<div>
			<FilterDropdown
				label="System"
				options={systemFilterOptions(systems, system)}
				value={system}
				all={{ label: 'All systems', sum: false }}
				filterPlaceholder="Filter systems…"
				onselect={selectSystem}
			/>
		</div>
		{#if view && range && disabled}
			<div class="btn-group btn-group-sm">
				<button
					type="button"
					class="btn btn-outline-secondary"
					aria-label="Zoom in"
					disabled={disabled.zoomIn}
					onclick={() => show(zoomIn(view))}><Icon name="zoom-in" /></button
				>
				<button
					type="button"
					class="btn btn-outline-secondary"
					aria-label="Zoom out"
					disabled={disabled.zoomOut}
					onclick={() => show(zoomOut(view, range))}><Icon name="zoom-out" /></button
				>
				<button
					type="button"
					class="btn btn-outline-secondary"
					aria-label="Earlier"
					disabled={disabled.earlier}
					onclick={() => show(pan(view, range, -1))}><Icon name="earlier" /></button
				>
				<button
					type="button"
					class="btn btn-outline-secondary"
					aria-label="Later"
					disabled={disabled.later}
					onclick={() => show(pan(view, range, 1))}><Icon name="later" /></button
				>
				<button
					type="button"
					class="btn btn-outline-secondary"
					disabled={disabled.showAll}
					onclick={() => show({ from: range.min, to: range.max })}>Show all</button
				>
			</div>
			<span class="small text-body-secondary" aria-live="polite">{rangeLabel(view)}</span>
		{/if}
	</div>

	{#if !view}
		<EmptyState message="No games linked to IGDB have a release year yet." />
	{:else}
		<div class="d-flex gap-1 overflow-x-auto pb-2 mb-3" role="group" aria-label="Release years">
			{#each ticks as year (year)}
				{@const count = counts.get(year) ?? 0}
				{@const pressed = view.from === year && view.to === year}
				<button
					type="button"
					class="btn btn-sm text-nowrap {pressed ? 'btn-primary' : 'btn-outline-secondary'}"
					aria-label="{year}: {gamesLabel(count)}"
					aria-pressed={pressed}
					onclick={() => show({ from: year, to: year })}
				>
					<span class="d-block">{year}</span>
					<span class="d-block small" class:text-body-secondary={count === 0}
						>{formatCount(count)}</span
					>
				</button>
			{/each}
		</div>

		{#if gamesError}
			<ErrorAlert error={gamesError} onretry={() => gamesReload++} />
		{:else if !games}
			<Loading />
		{:else if games.total === 0}
			<EmptyState message="No games in this period." />
		{:else}
			<p class="small text-body-secondary mb-2">{gamesLabel(games.total)}</p>
			<GameResults
				bind:games={games.items}
				view="list"
				action={timeline.igdbConfigured ? viewOnIgdb : undefined}
			/>
			<Pagination
				page={view.page}
				pages={pageCount(games.total, pageSize)}
				onpage={(p) => range && go(timelineSearch({ ...view!, page: p }, range))}
			/>
		{/if}
	{/if}
{/if}

<!-- T6: after the Play button in the actions cell; only when IGDB is configured. -->
{#snippet viewOnIgdb(game: GameListItem)}
	<!-- A server redirect to IGDB: resolve() is for app routes. -->
	<!-- eslint-disable svelte/no-navigation-without-resolve -->
	<a
		class="btn btn-sm btn-outline-secondary"
		href="/api/igdb/games/{game.igdbId}/page"
		target="_blank"
		rel="noopener noreferrer"
		title="View on IGDB"
		aria-label="View {game.displayTitle} on IGDB"><Icon name="external" /></a
	>
	<!-- eslint-enable svelte/no-navigation-without-resolve -->
{/snippet}
