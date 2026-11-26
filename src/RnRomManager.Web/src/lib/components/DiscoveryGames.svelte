<script lang="ts">
	import { resolve } from '$app/paths';
	import { ApiError } from '../api/errors';
	import type { GameListItem, Page, DiscoveryState } from '#lib/types/index.js';
	import { discoveryApi, discoverySearch, discoveryText } from '../discovery';
	import { pageCount, readPageSize, writePageSize } from '../pageSize';
	import EmptyState from './EmptyState.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import GameResults from './GameResults.svelte';
	import Loading from './Loading.svelte';
	import Pagination from './Pagination.svelte';

	/**
	 * Discovery B2: the selected group's games across systems, as the Library's grid (system badges, game links), paged
	 * by the URL's `gamePage`. A group no game has any more (404) gets its own empty state.
	 */
	interface Props {
		/** The tab's state; `key` is the selected group. */
		view: DiscoveryState & { key: string };
		/** The group's name when the group list has it (it may be on another page). */
		name: string | null;
		onpage: (gamePage: number) => void;
	}

	let { view, name, onpage }: Props = $props();

	const sizeKey = 'discoveryGames';
	let pageSize = $state(readPageSize(sizeKey));
	let result = $state<Page<GameListItem> | null>(null);
	let gone = $state(false);
	let error = $state<unknown>(null);
	let reload = $state(0);

	const noun = $derived(discoveryText[view.kind].singular);
	const heading = $derived(name ?? `Selected ${noun}`);

	function setPageSize(size: number): void {
		pageSize = size;
		writePageSize(sizeKey, size);
		onpage(1);
	}

	$effect(() => {
		const { kind, key } = view;
		const params = { page: view.gamePage, pageSize };
		void reload;
		// A response for an earlier group or page never replaces the current one.
		let stale = false;
		error = null;
		gone = false;
		result = null;
		discoveryApi.games(kind, key, params).then(
			(page) => {
				if (!stale) result = page;
			},
			(e) => {
				if (stale) return;
				if (e instanceof ApiError && e.status === 404) gone = true;
				else error = e;
			}
		);
		return () => (stale = true);
	});
</script>

<section aria-labelledby="discovery-games-heading">
	<h2 class="h5 text-truncate" id="discovery-games-heading" title={heading}>
		{heading}
		{#if result}
			<span class="small text-body-secondary fw-normal"
				>{`· ${result.total} ${result.total === 1 ? 'game' : 'games'}`}</span
			>
		{/if}
	</h2>

	{#if gone}
		<EmptyState message="No games have this {noun} any more." icon="collections">
			{#snippet action()}
				<a
					class="btn btn-outline-primary"
					href="{resolve('/collections')}{discoverySearch({ ...view, key: null, gamePage: 1 })}"
					>Back to all {discoveryText[view.kind].plural}</a
				>
			{/snippet}
		</EmptyState>
	{:else if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !result}
		<Loading label="Loading games…" />
	{:else if result.items.length === 0}
		<EmptyState message="There are no games on this page." />
	{:else}
		<GameResults bind:games={result.items} view="grid" narrow />
	{/if}

	{#if result}
		<Pagination
			page={view.gamePage}
			pages={pageCount(result.total, pageSize)}
			{onpage}
			{pageSize}
			onpagesize={setPageSize}
			label="Game pages"
		/>
	{/if}
</section>
