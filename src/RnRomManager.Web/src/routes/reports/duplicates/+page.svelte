<script lang="ts">
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { Page, SystemInfo } from '#lib/api/types.js';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/** RP9. The System filter and page are component state only. */
	interface DuplicateGroup {
		system: string;
		systemName: string;
		baseTitle: string;
		gamesKey: string;
		/** Suggested target first. */
		games: {
			id: string;
			displayTitle: string;
			romCount: number;
			zipSizeBytes: number;
			createdAt: string;
		}[];
	}

	const pageSize = 25;

	let system = $state('');
	let pageNumber = $state(1);
	let systems = $state<SystemInfo[]>([]);
	let result = $state<Page<DuplicateGroup> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	/** The chosen target per group (by games key); the first game until changed. */
	let targets = $state<Record<string, string>>({});
	let merging = $state<DuplicateGroup | null>(null);
	let mergeOpen = $state(false);
	let busy = $state(false);

	const target = (group: DuplicateGroup) => targets[group.gamesKey] ?? group.games[0].id;
	const mergeTarget = $derived(merging?.games.find((g) => g.id === target(merging!)));

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	$effect(() => {
		let url = `/api/reports/duplicates?page=${pageNumber}&pageSize=${pageSize}`;
		if (system) url += `&system=${encodeURIComponent(system)}`;
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		targets = {};
		api.get<Page<DuplicateGroup>>(url, { signal: controller.signal }).then(
			(p) => (result = p),
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});

	function askMerge(group: DuplicateGroup): void {
		merging = group;
		mergeOpen = true;
	}

	async function merge(): Promise<void> {
		const group = merging;
		if (!group) return;
		const targetGameId = target(group);
		busy = true;
		try {
			await api.post('/api/reports/duplicates/merge', {
				targetGameId,
				sourceGameIds: group.games.filter((g) => g.id !== targetGameId).map((g) => g.id)
			});
			toasts.show(`Merged into ${mergeTarget?.displayTitle ?? 'the target'}.`, 'success');
		} catch (e) {
			// A partial merge still changed the library, so the message says how far it got.
			const done =
				isApiError(e) && e.merged.length ? ` ${e.merged.length} merged before it stopped.` : '';
			if (isApiError(e, 'JobRunning') && !done) toasts.jobRunning(e);
			else toasts.show(`${isApiError(e) ? e.title : 'The merge failed.'}${done}`, 'danger');
		} finally {
			busy = false;
			mergeOpen = false;
			reload++;
		}
	}

	async function dismiss(group: DuplicateGroup): Promise<void> {
		try {
			await api.post('/api/reports/duplicates/dismiss', {
				system: group.system,
				baseTitle: group.baseTitle,
				gamesKey: group.gamesKey
			});
			toasts.show(`Dismissed ${group.baseTitle}.`, 'success');
			reload++;
		} catch (e) {
			toasts.show(isApiError(e) ? e.title : 'Dismiss failed.', 'danger');
		}
	}
</script>

<svelte:head><title>Duplicates · RnRomManager</title></svelte:head>

<div class="row g-2 align-items-end mb-3">
	<div class="col-12 col-sm-6 col-md-4">
		<label class="form-label" for="duplicates-system">System</label>
		<select
			id="duplicates-system"
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
{:else if result.items.length === 0}
	<div class="card"><EmptyState message="No duplicate games found." /></div>
{:else}
	{#each result.items as group (group.gamesKey + group.system + group.baseTitle)}
		<section class="card mb-3" aria-labelledby="group-{group.gamesKey}">
			<div class="card-header d-flex flex-wrap align-items-center gap-2">
				<SystemBadge system={group.system} name={group.systemName} />
				<h2 class="h6 mb-0" id="group-{group.gamesKey}">{group.baseTitle}</h2>
			</div>
			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr>
							{#if auth.canEditLibrary}<th scope="col"
									><span class="visually-hidden">Target</span></th
								>{/if}
							<th scope="col">Game</th>
							<th scope="col" class="text-end">Releases</th>
							<th scope="col" class="text-end">Size</th>
							<th scope="col">Added</th>
						</tr>
					</thead>
					<tbody>
						{#each group.games as game, i (game.id)}
							<tr>
								{#if auth.canEditLibrary}
									<td>
										<input
											class="form-check-input"
											type="radio"
											name="target-{group.gamesKey}"
											aria-label="Merge into {game.displayTitle}"
											checked={target(group) === game.id}
											onchange={() => (targets[group.gamesKey] = game.id)}
										/>
									</td>
								{/if}
								<td>
									<a href={resolve('/games/[id]', { id: game.id })}>{game.displayTitle}</a>
									{#if i === 0}<span class="badge text-bg-secondary ms-1">Suggested target</span
										>{/if}
								</td>
								<td class="text-end">{game.romCount}</td>
								<td class="text-end text-nowrap"><FileSize bytes={game.zipSizeBytes} /></td>
								<td class="text-nowrap">{formatDateTime(game.createdAt)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if auth.canEditLibrary}
				<div class="card-footer d-flex gap-2 justify-content-end">
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary"
						onclick={() => dismiss(group)}>Dismiss</button
					>
					<button type="button" class="btn btn-sm btn-danger" onclick={() => askMerge(group)}
						>Merge into selected</button
					>
				</div>
			{/if}
		</section>
	{/each}
	<Pagination
		page={pageNumber}
		pages={pageCount(result.total, pageSize)}
		onpage={(p) => (pageNumber = p)}
	/>
{/if}

<ConfirmModal
	bind:open={mergeOpen}
	title="Merge duplicates"
	confirmLabel="Merge"
	confirmClass="btn-danger"
	{busy}
	onconfirm={merge}
>
	{#if merging && mergeTarget}
		<p class="mb-0">
			Merge {merging.games.length - 1}
			{merging.games.length === 2 ? 'game' : 'games'} into
			<strong>{mergeTarget.displayTitle}</strong>? Their releases move into it and they are deleted.
		</p>
	{/if}
</ConfirmModal>
