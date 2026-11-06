<script lang="ts">
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type { Page, ReviewDetail, ReviewListItem } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import ReviewDeleteModal from '#lib/components/ReviewDeleteModal.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { pageCount, readPageSize, writePageSize } from '#lib/pageSize.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	let pageSize = $state(readPageSize('review'));
	let result = $state<Page<ReviewListItem> | null>(null);
	let error = $state<unknown>(null);
	let pageNumber = $state(1);
	let reload = $state(0);

	let deleteId = $state<number | null>(null);
	let deleteOpen = $state(false);

	const pages = $derived(result ? pageCount(result.total, pageSize) : 1);

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

	function setPageSize(size: number): void {
		writePageSize('review', size);
		pageSize = size;
		pageNumber = 1;
	}

	function askDelete(item: ReviewListItem): void {
		deleteId = item.id;
		deleteOpen = true;
	}

	function deleted(item: ReviewDetail): void {
		toasts.show(`Deleted ${item.originalFileName}.`, 'success');
		// D5: the page may now be empty; step back instead of showing an empty page.
		if (result?.items.length === 1 && pageNumber > 1) pageNumber--;
		else reload++;
	}
</script>

<svelte:head><title>Review queue · RnRomManager</title></svelte:head>

<AdminOnly>
	<h1 class="h3 mb-3"><Icon name="review" /> Review queue</h1>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !result}
		<Loading />
	{:else}
		<div class="card">
			<div class="table-responsive">
				{#if result.items.length === 0}
					<EmptyState message="Nothing to review." />
				{:else}
					<table class="table table-sm table-hover align-middle mb-0">
						<thead>
							<tr class="small text-uppercase text-body-secondary">
								<th scope="col">File</th>
								<th scope="col">System</th>
								<th scope="col" class="text-end">Size</th>
								<th scope="col">Header title</th>
								<th scope="col">Reason</th>
								<th scope="col">Added</th>
								<th scope="col" class="text-end"><span class="visually-hidden">Actions</span></th>
							</tr>
						</thead>
						<tbody>
							{#each result.items as item (item.id)}
								<tr>
									<td class="text-truncate" style="max-width: 32rem" title={item.fileName}>
										<a href={resolve('/review/[id]', { id: String(item.id) })}>{item.fileName}</a>
									</td>
									<td><SystemBadge system={item.system} /></td>
									<td class="text-end"><FileSize bytes={item.sizeBytes} /></td>
									<td>{item.headerTitle ?? '—'}</td>
									<td>{item.reason}</td>
									<td class="text-nowrap">{formatDateTime(item.createdAt)}</td>
									<td class="text-end">
										<button
											type="button"
											class="btn btn-sm btn-outline-danger"
											aria-label="Delete {item.fileName}"
											onclick={() => askDelete(item)}
										>
											<Icon name="delete" />
										</button>
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				{/if}
			</div>
		</div>
		<Pagination
			page={pageNumber}
			{pages}
			onpage={(p) => (pageNumber = p)}
			{pageSize}
			onpagesize={setPageSize}
		/>
	{/if}

	<ReviewDeleteModal bind:open={deleteOpen} id={deleteId} ondeleted={deleted} />
</AdminOnly>
