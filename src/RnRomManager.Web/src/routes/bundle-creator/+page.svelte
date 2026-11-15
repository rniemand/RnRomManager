<script lang="ts">
	import { untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import BundleDownload from '#lib/components/BundleDownload.svelte';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import SavedBundleQueues from '#lib/components/SavedBundleQueues.svelte';
	import { formatCount } from '#lib/format.js';
	import { bundle, maxBundleEntries } from '#lib/stores/bundle.svelte.js';

	let clearOpen = $state(false);

	const items = $derived(bundle.items);
	const unavailable = $derived(items.filter((i) => i.status === 'unavailable').length);

	// B5: availability is refreshed when the page opens and on demand. After a reload the account is known only once
	// sign-in has loaded, so this waits for it (and refreshes again for another account); it doesn't follow the cart.
	$effect(() => {
		if (bundle.userId !== null) untrack(() => void bundle.refresh());
	});

	function clearCart(): void {
		bundle.clear();
		clearOpen = false;
	}
</script>

<svelte:head><title>Bundle Creator · RnRomManager</title></svelte:head>

<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
	<h1 class="h3 mb-0 me-auto">Bundle Creator</h1>
	<button
		type="button"
		class="btn btn-sm btn-outline-secondary"
		disabled={!items.length || bundle.refreshing}
		onclick={() => void bundle.refresh()}
	>
		<Icon name="retry" /> Refresh
	</button>
	<button
		type="button"
		class="btn btn-sm btn-outline-danger"
		disabled={!items.length}
		onclick={() => (clearOpen = true)}
	>
		<Icon name="delete" /> Clear
	</button>
</div>

<p class="text-body-secondary small">
	Add files from a game's page. The cart is kept in this browser for your account, in the order you
	add files, up to {formatCount(maxBundleEntries)} files.
</p>

{#if bundle.refreshError}
	<div class="alert alert-warning" role="alert">
		Couldn't check which files are still available. The cart is unchanged.
	</div>
{/if}
{#if unavailable}
	<div class="alert alert-secondary" role="status">
		{formatCount(unavailable)}
		{unavailable === 1 ? 'file is' : 'files are'} no longer in the library. Remove them or keep them for
		later.
	</div>
{/if}

{#if !items.length}
	<div class="card">
		<EmptyState message="The bundle is empty." icon="cart">
			{#snippet action()}
				<a class="btn btn-sm btn-primary" href={resolve('/')}>Browse the library</a>
			{/snippet}
		</EmptyState>
	</div>
{:else}
	<div class="card">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<caption class="visually-hidden">Files in the bundle</caption>
				<thead class="table-dark">
					<tr>
						<th scope="col" class="text-end">#</th>
						<th scope="col">File</th>
						<th scope="col">Game</th>
						<th scope="col">Status</th>
						<th scope="col"><span class="visually-hidden">Actions</span></th>
					</tr>
				</thead>
				<tbody>
					{#each items as item, index (item.key)}
						<tr>
							<td class="text-end text-nowrap">{index + 1}</td>
							<td class="text-truncate" style="max-width: 32rem" title={item.fileName}
								>{item.fileName}</td
							>
							<td class="text-truncate" style="max-width: 20rem" title={item.gameTitle}>
								{#if item.status === 'unavailable'}
									{item.gameTitle}
								{:else}
									<a href={resolve('/games/[id]', { id: item.entry.gameId })}>{item.gameTitle}</a>
								{/if}
							</td>
							<td class="text-nowrap">
								{#if item.status === 'unavailable'}
									<span class="badge text-bg-secondary">Unavailable</span>
								{:else if item.status === 'available'}
									<span class="badge text-bg-success">Available</span>
								{:else}
									<span class="text-body-secondary small">Checking…</span>
								{/if}
							</td>
							<td class="text-end">
								<button
									type="button"
									class="btn btn-sm btn-outline-danger"
									aria-label="Remove {item.fileName} from the bundle"
									onclick={() => bundle.remove(item.key)}
								>
									<Icon name="delete" />
								</button>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
{/if}

<div class="mt-4"><BundleDownload /></div>

<div class="mt-4"><SavedBundleQueues /></div>

<ConfirmModal
	bind:open={clearOpen}
	title="Clear the bundle?"
	confirmLabel="Clear"
	confirmClass="btn-danger"
	onconfirm={clearCart}
>
	<p class="mb-0">
		This removes all {formatCount(items.length)} files from the working cart. Saved queues are not affected.
	</p>
</ConfirmModal>
