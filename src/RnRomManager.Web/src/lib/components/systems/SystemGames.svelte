<script lang="ts">
	import { api } from '#lib/api/client.js';
	import type { GameListItem, Page, SystemInfo } from '#lib/types/index.js';
	import { pageCount, readPageSize, writePageSize } from '#lib/pageSize.js';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import GameResults from '#lib/components/games/GameResults.svelte';
	import { auth } from '#lib/stores/auth.svelte.js';
	import GroupLogo from '#lib/components/discovery/GroupLogo.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import SystemEmulatorsModal from '#lib/components/modals/SystemEmulatorsModal.svelte';

	/**
	 * Systems tab B5: the selected system's games, paged by the URL's `gamePage`, its logo (B18) and Emulators button,
	 * and for Admins a Change logo button (B19).
	 */
	interface Props {
		system: SystemInfo;
		gamePage: number;
		onpage: (gamePage: number) => void;
		/** B19: open the logo modal for this system. */
		onedit?: (system: SystemInfo) => void;
	}

	let { system, gamePage, onpage, onedit }: Props = $props();

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
	<h2
		class="h5 text-truncate d-flex align-items-center gap-2"
		id="system-games-heading"
		title={system.displayName}
	>
		<GroupLogo
			kind="systems"
			item={{ key: system.id, name: system.displayName, logoUrl: system.logoUrl }}
		/>
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
		{#if auth.isAdmin && onedit}
			<button type="button" class="btn btn-outline-secondary ms-2" onclick={() => onedit(system)}
				><Icon name="edit" /> Change logo</button
			>
		{/if}
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
		<GameResults bind:games={result.items} view="grid" narrow />
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
