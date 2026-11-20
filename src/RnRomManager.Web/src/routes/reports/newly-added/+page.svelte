<script lang="ts">
	import { resolve } from '$app/paths';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { SystemInfo } from '#lib/api/types.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatDateTime } from '#lib/format.js';

	/** Release editing B14: the 10 newest games per system. The system is URL state (`?system=`). */
	interface NewlyAdded {
		systems: {
			system: string;
			systemName: string;
			games: { id: string; displayTitle: string; addedAt: string; releaseCount: number }[];
		}[];
	}

	const system = $derived(page.url.searchParams.get('system') ?? '');

	let result = $state<NewlyAdded | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	let systems = $state<SystemInfo[]>([]);

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	function choose(next: string): void {
		const query = next ? `?system=${encodeURIComponent(next)}` : '';
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(`${resolve('/reports/newly-added')}${query}`, { replace: true, reset: false });
	}

	$effect(() => {
		const url = `/api/reports/newly-added${system ? `?system=${encodeURIComponent(system)}` : ''}`;
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		api.get<NewlyAdded>(url, { signal: controller.signal }).then(
			(r) => (result = r),
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});
</script>

<svelte:head><title>Newly Added · RnRomManager</title></svelte:head>

<div class="row g-2 align-items-end mb-3">
	<div class="col-12 col-sm-6 col-md-4">
		<label class="form-label" for="newly-added-system">System</label>
		<select
			id="newly-added-system"
			class="form-select"
			value={system}
			onchange={(e) => choose(e.currentTarget.value)}
		>
			<option value="">All systems</option>
			{#each systems as s (s.id)}
				<option value={s.id}>{s.displayName}</option>
			{/each}
		</select>
	</div>
</div>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !result}
	<Loading />
{:else if result.systems.length === 0}
	<div class="card"><EmptyState message="No games yet." /></div>
{:else}
	{#each result.systems as group (group.system)}
		<div class="card mb-3">
			<div class="card-header d-flex align-items-center gap-2">
				<SystemBadge system={group.system} />
				<span>{group.systemName}</span>
			</div>
			<div class="table-responsive">
				<table class="table table-sm align-middle mb-0">
					<thead>
						<tr>
							<th scope="col">Game</th>
							<th scope="col">Added</th>
							<th scope="col" class="text-end">Releases</th>
						</tr>
					</thead>
					<tbody>
						{#each group.games as game (game.id)}
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
	{/each}
{/if}
