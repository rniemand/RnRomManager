<script lang="ts">
	import { isApiError } from '../api/errors';
	import type { BundleQueue, BundleQueueSummary } from '../api/types';
	import { bundleQueuesApi, queueErrorMessage, queueNameError, sameQueueName } from '../bundles';
	import { formatCount } from '../format';
	import { bundle } from '../stores/bundle.svelte';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import EmptyState from './EmptyState.svelte';
	import Icon from './Icon.svelte';

	/**
	 * B2/B4: the signed-in user's saved queues. Save snapshots the working cart (a clashing name asks before saving over
	 * that queue at its latest revision), Load replaces the cart (asking first when the cart has other files), Delete
	 * asks first and leaves the cart alone. Answers that arrive after an account change are ignored.
	 */

	type Pending =
		| { kind: 'overwrite'; queue: BundleQueueSummary }
		| { kind: 'load'; queue: BundleQueue }
		| { kind: 'delete'; queue: BundleQueueSummary };

	let queues = $state<BundleQueueSummary[] | null>(null);
	let listError = $state<string | null>(null);
	let message = $state<string | null>(null);
	let name = $state('');
	let nameTouched = $state(false);
	let busy = $state(false);
	let pending = $state<Pending | null>(null);
	let confirmOpen = $state(false);

	const nameProblem = $derived(nameTouched ? queueNameError(name) : null);

	/** The account the component works for; a change drops every answer still in flight. */
	let owner = bundle.userId;
	const current = (forOwner: number | null) => forOwner === bundle.userId && forOwner === owner;

	$effect(() => {
		const user = bundle.userId;
		owner = user;
		queues = null;
		message = null;
		pending = null;
		confirmOpen = false;
		if (user !== null) void reload();
	});

	async function reload(): Promise<void> {
		const forOwner = owner;
		listError = null;
		try {
			const list = await bundleQueuesApi.list();
			if (current(forOwner)) queues = list;
		} catch (error) {
			if (current(forOwner)) listError = queueErrorMessage(error);
		}
	}

	function ask(next: Pending): void {
		pending = next;
		confirmOpen = true;
	}

	/** Runs a queue call for the current account; failures show their B4 message and reload the list when stale. */
	async function run(work: () => Promise<void>): Promise<void> {
		const forOwner = owner;
		busy = true;
		message = null;
		try {
			await work();
		} catch (error) {
			if (!current(forOwner)) return;
			message = queueErrorMessage(error);
			if (isApiError(error, 'BundleQueueChanged') || (isApiError(error) && error.status === 404))
				await reload();
		} finally {
			if (current(forOwner)) busy = false;
		}
	}

	async function save(): Promise<void> {
		nameTouched = true;
		if (queueNameError(name) || !bundle.count) return;
		const existing = queues?.find((q) => sameQueueName(q.name, name));
		if (existing) return ask({ kind: 'overwrite', queue: existing });
		const forOwner = owner;
		await run(async () => {
			try {
				const created = await bundleQueuesApi.create(name, bundle.entries);
				if (!current(forOwner)) return;
				toasts.show(`Saved "${created.name}".`, 'success');
				name = '';
				nameTouched = false;
				await reload();
			} catch (error) {
				// The list was stale: offer to save over the queue that has this name.
				if (!isApiError(error, 'BundleQueueNameTaken') || !current(forOwner)) throw error;
				await reload();
				const clash = queues?.find((q) => sameQueueName(q.name, name));
				if (clash) ask({ kind: 'overwrite', queue: clash });
				else throw error;
			}
		});
	}

	async function overwrite(queue: BundleQueueSummary): Promise<void> {
		const forOwner = owner;
		await run(async () => {
			const saved = await bundleQueuesApi.replace(queue.id, name, bundle.entries, queue.revision);
			if (!current(forOwner)) return;
			toasts.show(`Saved over "${saved.name}".`, 'success');
			name = '';
			nameTouched = false;
			await reload();
		});
	}

	async function load(summary: BundleQueueSummary): Promise<void> {
		const forOwner = owner;
		await run(async () => {
			const queue = await bundleQueuesApi.get(summary.id);
			if (!current(forOwner)) return;
			if (bundle.count && !bundle.sameAs(queue.entries)) ask({ kind: 'load', queue });
			else apply(queue);
		});
	}

	function apply(queue: BundleQueue): void {
		bundle.replace(queue.entries);
		void bundle.refresh();
		toasts.show(`Loaded "${queue.name}" into the cart.`, 'success');
	}

	async function remove(queue: BundleQueueSummary): Promise<void> {
		const forOwner = owner;
		await run(async () => {
			await bundleQueuesApi.remove(queue.id, queue.revision);
			if (!current(forOwner)) return;
			toasts.show(`Deleted "${queue.name}".`, 'success');
			await reload();
		});
	}

	function confirm(): void {
		const next = pending;
		confirmOpen = false;
		pending = null;
		if (!next) return;
		if (next.kind === 'overwrite') void overwrite(next.queue);
		else if (next.kind === 'load') apply(next.queue);
		else void remove(next.queue);
	}

	const confirmText = $derived.by(() => {
		if (!pending) return { title: '', label: 'Confirm', danger: false, body: '' };
		const queueName = pending.queue.name;
		switch (pending.kind) {
			case 'overwrite':
				return {
					title: 'Save over this queue?',
					label: 'Save over',
					danger: true,
					body: `"${queueName}" already exists. Saving replaces its files with the ${formatCount(bundle.count)} in the cart.`
				};
			case 'load':
				return {
					title: 'Replace the cart?',
					label: 'Replace cart',
					danger: true,
					body: `Loading "${queueName}" replaces the ${formatCount(bundle.count)} files in the cart with its ${formatCount(pending.queue.entries.length)}.`
				};
			case 'delete':
				return {
					title: 'Delete this queue?',
					label: 'Delete',
					danger: true,
					body: `"${queueName}" is deleted from your account. The working cart is not changed.`
				};
		}
	});
</script>

<section aria-labelledby="saved-queues-title" class="mb-4">
	<h2 class="h5" id="saved-queues-title">Saved queues</h2>

	<form
		class="row g-2 align-items-start mb-3"
		onsubmit={(e) => {
			e.preventDefault();
			void save();
		}}
	>
		<div class="col-sm">
			<label class="visually-hidden" for="queue-name">Queue name</label>
			<input
				id="queue-name"
				class="form-control form-control-sm"
				class:is-invalid={nameProblem}
				placeholder="Queue name"
				maxlength="200"
				bind:value={name}
				aria-describedby={nameProblem ? 'queue-name-error' : undefined}
				oninput={() => (nameTouched = true)}
			/>
			{#if nameProblem}<div class="invalid-feedback" id="queue-name-error">{nameProblem}</div>{/if}
		</div>
		<div class="col-sm-auto">
			<button type="submit" class="btn btn-sm btn-primary" disabled={busy || !bundle.count}>
				<Icon name="upload" /> Save cart as queue
			</button>
		</div>
	</form>

	{#if message}
		<div class="alert alert-warning py-2" role="alert">{message}</div>
	{/if}

	{#if listError}
		<div class="alert alert-danger py-2" role="alert">
			{listError}
			<button type="button" class="btn btn-sm btn-link" onclick={() => void reload()}>Retry</button>
		</div>
	{:else if queues === null}
		<p class="text-body-secondary small">Loading saved queues…</p>
	{:else if !queues.length}
		<div class="card"><EmptyState message="No saved queues yet." icon="list" /></div>
	{:else}
		<div class="card">
			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<caption class="visually-hidden">Saved queues</caption>
					<thead class="table-dark">
						<tr>
							<th scope="col">Name</th>
							<th scope="col" class="text-end">Files</th>
							<th scope="col"><span class="visually-hidden">Actions</span></th>
						</tr>
					</thead>
					<tbody>
						{#each queues as queue (queue.id)}
							<tr>
								<td class="text-truncate" style="max-width: 32rem" title={queue.name}
									>{queue.name}</td
								>
								<td class="text-end text-nowrap">{formatCount(queue.itemCount)}</td>
								<td class="text-end text-nowrap">
									<button
										type="button"
										class="btn btn-sm btn-outline-primary"
										aria-label="Load {queue.name} into the cart"
										disabled={busy}
										onclick={() => void load(queue)}
									>
										<Icon name="download" /> Load
									</button>
									<button
										type="button"
										class="btn btn-sm btn-outline-danger"
										aria-label="Delete {queue.name}"
										disabled={busy}
										onclick={() => ask({ kind: 'delete', queue })}
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
</section>

<ConfirmModal
	bind:open={confirmOpen}
	title={confirmText.title}
	confirmLabel={confirmText.label}
	confirmClass={confirmText.danger ? 'btn-danger' : 'btn-primary'}
	onconfirm={confirm}
>
	<p class="mb-0">{confirmText.body}</p>
</ConfirmModal>
