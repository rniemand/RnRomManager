<script lang="ts">
	import { api } from '../api/client';
	import type { GameListItem, Page, SystemInfo } from '../api/types';
	import { pageCount, readPageSize, writePageSize } from '../pageSize';
	import EmptyState from './EmptyState.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import GameResults from './GameResults.svelte';
	import Icon from './Icon.svelte';
	import Loading from './Loading.svelte';
	import Pagination from './Pagination.svelte';
	import SystemEmulatorsModal from './SystemEmulatorsModal.svelte';

	/** Systems tab B5: the selected system's games, paged by the URL's `gamePage`, and its Emulators button. */
	interface Props {
		system: SystemInfo;
		gamePage: number;
		onpage: (gamePage: number) => void;
	}

	let { system, gamePage, onpage }: Props = $props();

	const sizeKey = 'systemGames';
	let pageSize = $state(readPageSize(sizeKey));
	let result = $state<Page<GameListItem> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	let emulatorsOpen = $state(false);
	let emulatorsButton = $state<HTMLButtonElement>();

	function setPageSize(size: number): void {
		pageSize = size;
		writePageSize(sizeKey, size);
		onpage(1);
	}

	$effect(() => {
		const search = new URLSearchParams({
			system: system.id,
			page: String(gamePage),
			pageSize: String(pageSize)
		});
		void reload;
		// A response for an earlier system or page never replaces the current one.
		let stale = false;
		error = null;
		result = null;
		api.get<Page<GameListItem>>(`/api/games?${search}`).then(
			(page) => {
				if (!stale) result = page;
			},
			(e) => {
				if (!stale) error = e;
			}
		);
		return () => (stale = true);
	});

	// B8: Bootstrap doesn't restore focus for a modal opened with show(), so return it to the button.
	let wasOpen = false;
	$effect(() => {
		if (wasOpen && !emulatorsOpen) emulatorsButton?.focus();
		wasOpen = emulatorsOpen;
	});
</script>

<section aria-labelledby="system-games-heading">
	<h2 class="h5 text-truncate" id="system-games-heading" title={system.displayName}>
		{system.displayName}
		{#if result}
			<span class="small text-body-secondary fw-normal"
				>{`· ${result.total} ${result.total === 1 ? 'game' : 'games'}`}</span
			>
		{/if}
	</h2>

	<div class="mb-3">
		<button
			bind:this={emulatorsButton}
			type="button"
			class="btn btn-outline-primary"
			onclick={() => (emulatorsOpen = true)}><Icon name="emulators" /> Emulators</button
		>
	</div>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !result}
		<Loading label="Loading games…" />
	{:else if result.total === 0}
		<EmptyState message="No games for {system.displayName} yet." />
	{:else if result.items.length === 0}
		<EmptyState message="There are no games on this page." />
	{:else}
		<GameResults bind:games={result.items} view="grid" />
	{/if}

	{#if result && result.total > 0}
		<Pagination
			page={gamePage}
			pages={pageCount(result.total, pageSize)}
			{onpage}
			{pageSize}
			onpagesize={setPageSize}
			label="Game pages"
		/>
	{/if}
</section>

<SystemEmulatorsModal bind:open={emulatorsOpen} {system} />
