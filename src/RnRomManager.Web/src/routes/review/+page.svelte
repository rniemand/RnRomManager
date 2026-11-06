<script lang="ts">
	import { onDestroy, untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { Page, ReviewDetail, ReviewListItem, SystemInfo } from '#lib/api/types.js';
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
	import { deleteReviewItem, toastDeleteFailed } from '#lib/review.js';
	import {
		parseReviewFilter,
		readSavedReviewFilter,
		reviewApiUrl,
		reviewFilterParams,
		reviewFilterSearch,
		saveReviewFilter,
		type ReviewFilter
	} from '#lib/reviewFilter.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';

	function go(search: string): void {
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
		void goto(`/review${search}`, { replace: true, reset: false });
	}

	const filter = $derived(parseReviewFilter(page.url.searchParams));
	const filtered = $derived(!!(filter.q || filter.system || filter.reason));

	/** Any filter change goes back to page 1. */
	function update(changes: Partial<ReviewFilter>): void {
		go(reviewFilterSearch({ ...filter, page: 1, ...changes }));
	}

	// First run: a URL without these params restores the saved filter; after that every URL state is saved.
	let restoring =
		!reviewFilterParams.some((p) => page.url.searchParams.has(p)) && !!readSavedReviewFilter();
	$effect(() => {
		const search = reviewFilterSearch(filter);
		untrack(() => {
			if (restoring) {
				restoring = false;
				go(readSavedReviewFilter());
			} else saveReviewFilter(search);
		});
	});

	let search = $state('');
	let searchTimer: ReturnType<typeof setTimeout> | undefined;
	onDestroy(() => clearTimeout(searchTimer));

	function onSearchInput(): void {
		clearTimeout(searchTimer);
		searchTimer = setTimeout(() => {
			const q = search.trim();
			if (q !== filter.q) update({ q });
		}, 300);
	}

	// URL → search box (restore, Clear).
	$effect(() => {
		const q = filter.q;
		untrack(() => {
			if (search.trim() !== q) search = q;
		});
	});

	function clear(): void {
		clearTimeout(searchTimer);
		search = '';
		go('');
	}

	let systems = $state<SystemInfo[]>([]);
	$effect(() => {
		if (!auth.isAdmin) return;
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the System select; the list call shows errors
		);
	});

	let pageSize = $state(readPageSize('review'));
	let result = $state<Page<ReviewListItem> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);

	let deleteId = $state<number | null>(null);
	let deleteOpen = $state(false);

	const pages = $derived(result ? pageCount(result.total, pageSize) : 1);

	$effect(() => {
		if (!auth.isAdmin) return;
		const url = reviewApiUrl(filter, pageSize);
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
		update({});
	}

	async function askDelete(event: MouseEvent, item: ReviewListItem): Promise<void> {
		// RV1: Shift+click skips the modal, except for an archive (deleting it deletes every file in it).
		if (!event.shiftKey || item.isArchive) {
			deleteId = item.id;
			deleteOpen = true;
			return;
		}
		try {
			await deleteReviewItem(item.id);
		} catch (e) {
			toastDeleteFailed(e);
			return;
		}
		removed(item.fileName);
	}

	function deleted(item: ReviewDetail): void {
		removed(item.originalFileName);
	}

	function removed(fileName: string): void {
		toasts.show(`Deleted ${fileName}.`, 'success');
		// D5: the page may now be empty; step back instead of showing an empty page.
		if (result?.items.length === 1 && filter.page > 1)
			go(reviewFilterSearch({ ...filter, page: filter.page - 1 }));
		else reload++;
	}
</script>

<svelte:head><title>Review queue · RnRomManager</title></svelte:head>

<AdminOnly>
	<h1 class="h3 mb-3"><Icon name="review" /> Review queue</h1>

	<form class="row g-2 align-items-end mb-3" role="search" onsubmit={(e) => e.preventDefault()}>
		<div class="col-12 col-md-5">
			<label class="form-label" for="review-q">Search</label>
			<input
				id="review-q"
				type="search"
				class="form-control"
				placeholder="Search file name or title"
				maxlength="100"
				bind:value={search}
				oninput={onSearchInput}
			/>
		</div>
		<div class="col-6 col-md-3">
			<label class="form-label" for="review-system">System</label>
			<select
				id="review-system"
				class="form-select"
				value={filter.system}
				onchange={(e) => update({ system: e.currentTarget.value })}
			>
				<option value="">All systems</option>
				{#each systems as s (s.id)}
					<option value={s.id}>{s.displayName}</option>
				{/each}
			</select>
		</div>
		<div class="col-6 col-md-2">
			<label class="form-label" for="review-reason">Reason</label>
			<select
				id="review-reason"
				class="form-select"
				value={filter.reason}
				onchange={(e) => update({ reason: e.currentTarget.value })}
			>
				<option value="">All reasons</option>
				<option value="NoDatMatch">No DAT match</option>
				<option value="AmbiguousGroup">Ambiguous group</option>
			</select>
		</div>
		<div class="col-12 col-md-2">
			<button
				type="button"
				class="btn btn-outline-secondary w-100"
				disabled={!filtered}
				onclick={clear}>Clear</button
			>
		</div>
	</form>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if !result}
		<Loading />
	{:else}
		<div class="card">
			<div class="table-responsive">
				{#if result.items.length === 0}
					<EmptyState
						message={filtered ? 'No review items match this filter.' : 'Nothing to review.'}
					/>
				{:else}
					<table class="table table-sm table-striped table-hover align-middle mb-0">
						<thead class="table-dark">
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
											use:tooltip={'Delete (Shift+click to skip confirmation)'}
											onclick={(e) => askDelete(e, item)}
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
			page={filter.page}
			{pages}
			onpage={(p) => go(reviewFilterSearch({ ...filter, page: p }))}
			{pageSize}
			onpagesize={setPageSize}
		/>
	{/if}

	<ReviewDeleteModal bind:open={deleteOpen} id={deleteId} ondeleted={deleted} />
</AdminOnly>
