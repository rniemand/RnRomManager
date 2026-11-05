<script lang="ts">
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type { Page, ReviewListItem } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { formatDateTime, formatSize } from '#lib/format.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	const pageSize = 50;

	let result = $state<Page<ReviewListItem> | null>(null);
	let error = $state<unknown>(null);
	let pageNumber = $state(1);
	let reload = $state(0);

	const pages = $derived(result ? Math.max(1, Math.ceil(result.total / pageSize)) : 1);

	$effect(() => {
		if (!auth.isAdmin) return;
		const url = `/api/review?status=Pending&page=${pageNumber}&pageSize=${pageSize}`;
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		api.get<Page<ReviewListItem>>(url, { signal: controller.signal }).then(
			(p) => (result = p),
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});
</script>

<svelte:head><title>Review queue · RnRomManager</title></svelte:head>

<AdminOnly>
	<h1 class="h3 mb-3"><Icon name="review" /> Review queue</h1>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !result}
		<Loading />
	{:else if result.items.length === 0}
		<EmptyState message="Nothing to review." />
	{:else}
		<div class="table-responsive">
			<table class="table table-hover align-middle">
				<thead>
					<tr>
						<th scope="col">File</th>
						<th scope="col">System</th>
						<th scope="col" class="text-end">Size</th>
						<th scope="col">Header title</th>
						<th scope="col">Reason</th>
						<th scope="col">Added</th>
					</tr>
				</thead>
				<tbody>
					{#each result.items as item (item.id)}
						<tr>
							<td class="text-break">
								<a href={resolve('/review/[id]', { id: String(item.id) })}>{item.fileName}</a>
							</td>
							<td>{item.system}</td>
							<td class="text-end text-nowrap">{formatSize(item.sizeBytes)}</td>
							<td>{item.headerTitle ?? '—'}</td>
							<td>{item.reason}</td>
							<td class="text-nowrap">{formatDateTime(item.createdAt)}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
		{#if pages > 1}
			<nav class="d-flex justify-content-center align-items-center gap-3" aria-label="Pages">
				<button
					type="button"
					class="btn btn-outline-secondary"
					disabled={pageNumber <= 1}
					onclick={() => pageNumber--}>Previous</button
				>
				<span>Page {pageNumber} of {pages}</span>
				<button
					type="button"
					class="btn btn-outline-secondary"
					disabled={pageNumber >= pages}
					onclick={() => pageNumber++}>Next</button
				>
			</nav>
		{/if}
	{/if}
</AdminOnly>
