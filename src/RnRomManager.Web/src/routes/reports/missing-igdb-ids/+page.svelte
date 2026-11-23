<script lang="ts">
	import { resolve } from '$app/paths';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { MissingIgdbItem, Page } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';

	/**
	 * Missing IGDB IDs R3: games not linked to IGDB (or marked Not on IGDB). The filter and page are URL state under the
	 * API's names (`?status=&page=`), defaults left out.
	 */
	type Status = 'missing' | 'not-on-igdb';
	interface View {
		status: Status;
		page: number;
	}

	const pageSize = 50;
	const statusOptions: { value: Status; label: string }[] = [
		{ value: 'missing', label: 'Not linked' },
		{ value: 'not-on-igdb', label: 'Not on IGDB' }
	];

	const params = $derived(page.url.searchParams);
	const view = $derived.by<View>(() => {
		const p = params.get('page');
		return {
			status: params.get('status') === 'not-on-igdb' ? 'not-on-igdb' : 'missing',
			page: p !== null && /^\d+$/.test(p) && Number(p) >= 1 ? Number(p) : 1
		};
	});

	/** Writes the state to the URL, leaving out defaults. */
	function show(next: View): void {
		const query = [
			next.status !== 'missing' && `status=${next.status}`,
			next.page !== 1 && `page=${next.page}`
		]
			.filter(Boolean)
			.join('&');
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(`${resolve('/reports/missing-igdb-ids')}${query ? `?${query}` : ''}`, {
			replace: true,
			reset: false
		});
	}

	let result = $state<Page<MissingIgdbItem> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);

	$effect(() => {
		const url = `/api/reports/missing-igdb-ids?status=${view.status}&page=${view.page}&pageSize=${pageSize}`;
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		api.get<Page<MissingIgdbItem>>(url, { signal: controller.signal }).then(
			(p) => (result = p),
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});
</script>

<svelte:head><title>Missing IGDB IDs · RnRomManager</title></svelte:head>

<AdminOnly>
	<form
		class="d-flex flex-wrap align-items-center gap-2 mb-3"
		role="search"
		onsubmit={(e) => e.preventDefault()}
	>
		<div class="input-group input-group-sm w-auto">
			<label class="input-group-text" for="missing-igdb-status">Show</label>
			<select
				id="missing-igdb-status"
				class="form-select w-auto"
				value={view.status}
				onchange={(e) => show({ ...view, status: e.currentTarget.value as Status, page: 1 })}
			>
				{#each statusOptions as option (option.value)}
					<option value={option.value}>{option.label}</option>
				{/each}
			</select>
		</div>
	</form>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !result}
		<Loading />
	{:else}
		{#if result.total > 0}
			<p class="small text-body-secondary mb-2">
				{formatCount(result.total)}
				{result.total === 1 ? 'game' : 'games'}
			</p>
		{/if}
		<div class="card">
			<div class="table-responsive">
				{#if result.total === 0}
					<EmptyState
						message={view.status === 'missing'
							? 'Every game is linked to IGDB.'
							: 'No games are marked as not on IGDB.'}
					/>
				{:else}
					<table class="table table-sm table-striped table-hover align-middle mb-0">
						<thead class="table-dark">
							<tr>
								<th scope="col">Game</th>
								<th scope="col">System</th>
								<th scope="col">Year</th>
							</tr>
						</thead>
						<tbody>
							{#each result.items as item (item.id)}
								<tr>
									<td><a href={resolve('/games/[id]', { id: item.id })}>{item.displayTitle}</a></td>
									<td><SystemBadge system={item.system} name={item.systemName} /></td>
									<td>{item.releaseYear ?? '—'}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				{/if}
			</div>
		</div>
		<Pagination
			page={view.page}
			pages={pageCount(result.total, pageSize)}
			onpage={(p) => show({ ...view, page: p })}
		/>
	{/if}
</AdminOnly>
