<script lang="ts">
	import { replaceUrl } from '#lib/urlState.js';
	import { useAbortableGet } from '#lib/loadPage.svelte.js';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { SystemInfo } from '#lib/types/index.js';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { scrollActiveTab } from '#lib/scrollActiveTab.js';

	/**
	 * Release editing B14: the 10 newest games per system, one tab per system (Report Polish L4–L5). The tab is URL state
	 * (`?system=`); a missing or unknown id shows the first tab. The report loads once; switching tabs doesn't refetch.
	 */
	interface NewlyAdded {
		systems: {
			system: string;
			systemName: string;
			games: { id: string; displayTitle: string; addedAt: string; releaseCount: number }[];
		}[];
	}

	let systems = $state<SystemInfo[]>([]);

	const report = useAbortableGet<NewlyAdded>(() => '/api/reports/newly-added');
	const result = $derived(report.result);
	const error = $derived(report.error);
	const requested = $derived(page.url.searchParams.get('system') ?? '');
	const selected = $derived(
		result?.systems.find((g) => g.system === requested) ?? result?.systems[0] ?? null
	);
	const shortName = (id: string) => systems.find((s) => s.id === id)?.shortName ?? id.toUpperCase();

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the tab labels
		);
	});

	function choose(next: string): void {
		void replaceUrl(`${resolve('/reports/newly-added')}?system=${encodeURIComponent(next)}`);
	}
</script>

<svelte:head><title>Newly Added · RnRomManager</title></svelte:head>

{#if error}
	<ErrorAlert {error} onretry={() => report.reload()} />
{:else if !result}
	<Loading />
{:else if !selected}
	<div class="card"><EmptyState message="No games yet." /></div>
{:else}
	<ul class="nav nav-tabs mb-3" use:scrollActiveTab role="tablist">
		{#each result.systems as group (group.system)}
			<li class="nav-item" role="presentation">
				<button
					type="button"
					class="nav-link"
					class:active={group.system === selected.system}
					id="newly-added-{group.system}"
					role="tab"
					aria-selected={group.system === selected.system}
					aria-controls="newly-added-panel"
					title={group.systemName}
					onclick={() => choose(group.system)}
					><SystemBadge system={group.system} name={shortName(group.system)} /></button
				>
			</li>
		{/each}
	</ul>
	<div
		class="card"
		id="newly-added-panel"
		role="tabpanel"
		aria-labelledby="newly-added-{selected.system}"
	>
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col">Game</th>
						<th scope="col">Added</th>
						<th scope="col" class="text-end">Releases</th>
					</tr>
				</thead>
				<tbody>
					{#each selected.games as game (game.id)}
						<tr>
							<td><a href={resolve('/games/[id]', { id: game.id })}>{game.displayTitle}</a></td>
							<td class="text-nowrap">{formatDateTime(game.addedAt)}</td>
							<td class="text-end">{game.releaseCount}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
{/if}
