<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type { RomStorageRow } from '#lib/api/types.js';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/**
	 * ROM Zip Config B15–B16: each system's default and configured ROM storage; configurable systems switch through a
	 * confirmation that starts the storage job. Buttons are disabled while any job runs.
	 */
	let rows = $state<RomStorageRow[] | null>(null);
	let error = $state<unknown>(null);
	let actionError = $state<unknown>(null);
	let target = $state<RomStorageRow | null>(null);
	let confirmOpen = $state(false);
	let busy = $state(false);

	const storage = (embed: boolean) => (embed ? 'Stored in ZIP' : 'Beside ZIP');
	const pending = $derived(rows?.filter((r) => r.configurable && r.pendingGames > 0) ?? []);
	const blocked = $derived(busy || jobs.current !== null);

	async function load(): Promise<void> {
		error = null;
		try {
			rows = await api.get<RomStorageRow[]>('/api/admin/rom-storage');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		void load();
		const stopWatching = jobs.watch();
		const stopEnded = jobs.onEnded(() => void load());
		return () => {
			stopWatching();
			stopEnded();
		};
	});

	function ask(row: RomStorageRow): void {
		target = row;
		actionError = null;
		confirmOpen = true;
	}

	async function started(): Promise<void> {
		await jobs.refresh();
		toasts.show('The storage job has started.', 'success', {
			href: resolve('/jobs'),
			text: 'View jobs'
		});
	}

	async function run(action: () => Promise<unknown>): Promise<void> {
		busy = true;
		actionError = null;
		try {
			const result = await action();
			if (result === undefined) await load();
			else await started();
		} catch (e) {
			if (!toasts.jobRunning(e)) actionError = e;
		} finally {
			busy = false;
		}
	}

	function confirm(): void {
		const row = target!;
		confirmOpen = false;
		void run(() =>
			api.put<{ jobId: string } | undefined>(
				`/api/admin/rom-storage/${encodeURIComponent(row.id)}`,
				{ embedRoms: !row.embedRoms }
			)
		);
	}

	const resume = () =>
		run(() => api.post<{ jobId: string } | undefined>('/api/jobs/storage-migration'));
</script>

<svelte:head><title>ROM Zip Config · Admin · RnRomManager</title></svelte:head>

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !rows}
	<Loading />
{:else}
	{#if actionError}
		<ErrorAlert error={actionError} />
	{/if}
	{#if pending.length > 0}
		<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
			<button type="button" class="btn btn-outline-warning" disabled={blocked} onclick={resume}
				>Resume moves</button
			>
			<span class="small text-body-secondary"
				>Some games still wait to be moved to their system's configured storage.</span
			>
		</div>
	{/if}
	<div class="card">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col">System</th>
						<th scope="col">Default Behaviour</th>
						<th scope="col">Configured Behaviour</th>
						<th scope="col"><span class="visually-hidden">Change</span></th>
					</tr>
				</thead>
				<tbody>
					{#each rows as row (row.id)}
						<tr>
							<td><SystemBadge system={row.id} name={row.displayName} /></td>
							<td>{storage(row.defaultEmbedRoms)}</td>
							<td>
								{storage(row.embedRoms)}
								{#if row.pendingGames > 0}
									<span class="badge text-bg-warning ms-1"
										>{row.pendingGames} game{row.pendingGames === 1 ? '' : 's'} still to move</span
									>
								{/if}
							</td>
							<td class="text-end">
								{#if row.configurable}
									<button
										type="button"
										class="btn btn-sm btn-outline-primary"
										disabled={blocked}
										onclick={() => ask(row)}
										>{row.embedRoms ? 'Store beside ZIP' : 'Store in ZIP'}</button
									>
								{/if}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
{/if}

<ConfirmModal
	bind:open={confirmOpen}
	title={target
		? `${target.embedRoms ? 'Store beside ZIP' : 'Store in ZIP'}: ${target.displayName}`
		: ''}
	confirmLabel="Start moving"
	confirmClass="btn-warning"
	onconfirm={confirm}
>
	{#if target}
		<p>
			Every game of {target.displayName} is rewritten, one at a time. This takes a long time (hours for
			a large library) and is heavy on disk and CPU. Free space of about one game's ROMs plus its ZIP
			is needed.
		</p>
		<p>
			Until it ends, no other job (ingest, integrity, scheduled checks) can run and no game of any
			system can be edited. Games it doesn't reach (cancelled or failed) stay read-only until they
			are moved. You can cancel it from the Jobs page and resume it later; games already moved stay
			moved.
		</p>
		{#if target.embedRoms}
			<p class="mb-0">Each ROM becomes a separate file next to its ZIP.</p>
		{:else}
			<p class="mb-0">
				Saving a game state or battery save for this system then rewrites the whole ZIP, including
				its ROM (up to 512 MiB), so saves get slower.
			</p>
		{/if}
	{/if}
</ConfirmModal>
