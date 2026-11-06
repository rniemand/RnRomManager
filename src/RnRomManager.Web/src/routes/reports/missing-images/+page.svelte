<script lang="ts">
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type { Page, SystemInfo } from '#lib/api/types.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { pageCount } from '#lib/pageSize.js';

	/** RP4/RP5. The System filter and page are component state only. */
	interface MissingImage {
		id: string;
		displayTitle: string;
		system: string;
		systemName: string;
		reason: 'NoCover' | 'Placeholder';
	}

	const pageSize = 50;
	const reasons = { NoCover: 'No cover', Placeholder: 'Placeholder art' } as const;

	let system = $state('');
	let pageNumber = $state(1);
	let systems = $state<SystemInfo[]>([]);
	let result = $state<Page<MissingImage> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	$effect(() => {
		let url = `/api/reports/missing-images?page=${pageNumber}&pageSize=${pageSize}`;
		if (system) url += `&system=${encodeURIComponent(system)}`;
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		api.get<Page<MissingImage>>(url, { signal: controller.signal }).then(
			(p) => (result = p),
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});
</script>

<svelte:head><title>Missing Images · RnRomManager</title></svelte:head>

<div class="row g-2 align-items-end mb-3">
	<div class="col-12 col-sm-6 col-md-4">
		<label class="form-label" for="missing-system">System</label>
		<select
			id="missing-system"
			class="form-select"
			value={system}
			onchange={(e) => {
				system = e.currentTarget.value;
				pageNumber = 1;
			}}
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
{:else}
	<div class="card">
		<div class="table-responsive">
			{#if result.items.length === 0}
				<EmptyState message="Every game has a cover image." />
			{:else}
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr>
							<th scope="col">Game</th>
							<th scope="col">System</th>
							<th scope="col">Reason</th>
						</tr>
					</thead>
					<tbody>
						{#each result.items as item (item.id)}
							<tr>
								<td>
									<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query -->
									<a href="{resolve('/games/[id]', { id: item.id })}?tab=images"
										>{item.displayTitle}</a
									>
								</td>
								<td><SystemBadge system={item.system} name={item.systemName} /></td>
								<td>{reasons[item.reason]}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			{/if}
		</div>
	</div>
	<Pagination
		page={pageNumber}
		pages={pageCount(result.total, pageSize)}
		onpage={(p) => (pageNumber = p)}
	/>
{/if}
