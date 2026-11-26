<script lang="ts">
	import DataTable from '#lib/components/common/DataTable.svelte';
	import { useAbortableGet } from '#lib/loadPage.svelte.js';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type { Page, SystemInfo } from '#lib/types/index.js';
	import FilterDropdown from '#lib/components/forms/FilterDropdown.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';
	import { formatCount } from '#lib/format.js';

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

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	const report = useAbortableGet<Page<MissingImage>>(() => {
		let url = `/api/reports/missing-images?page=${pageNumber}&pageSize=${pageSize}`;
		if (system) url += `&system=${encodeURIComponent(system)}`;
		return url;
	});
	const result = $derived(report.result);
	const error = $derived(report.error);
</script>

<svelte:head><title>Missing Images · RnRomManager</title></svelte:head>

<div class="row g-2 align-items-end mb-3">
	<div class="col-12 col-sm-6 col-md-4">
		<FilterDropdown
			label="System"
			options={systemFilterOptions(systems, system || null)}
			value={system || null}
			all={{ label: 'All systems', sum: false }}
			filterPlaceholder="Filter systems…"
			onselect={(v) => {
				system = v ?? '';
				pageNumber = 1;
			}}
		/>
	</div>
</div>

{#if error}
	<ErrorAlert {error} onretry={() => report.reload()} />
{:else if !result}
	<Loading />
{:else}
	{#if result.total > 0}
		<p class="small text-body-secondary mb-2">
			{formatCount(result.total)}
			{result.total === 1 ? 'game' : 'games'}
		</p>
	{/if}
	<DataTable
		items={result.items}
		key={(item) => item.id}
		empty="Every game has a cover image."
		emptyInCard
	>
		{#snippet head()}
			<tr>
				<th scope="col">Game</th>
				<th scope="col">System</th>
				<th scope="col">Reason</th>
			</tr>
		{/snippet}
		{#snippet row(item)}
			<tr>
				<td>
					<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query -->
					<a href="{resolve('/games/[id]', { id: item.id })}?tab=images">{item.displayTitle}</a>
				</td>
				<td><SystemBadge system={item.system} name={item.systemName} /></td>
				<td>{reasons[item.reason]}</td>
			</tr>
		{/snippet}
	</DataTable>
	<Pagination
		page={pageNumber}
		pages={pageCount(result.total, pageSize)}
		onpage={(p) => (pageNumber = p)}
	/>
{/if}
