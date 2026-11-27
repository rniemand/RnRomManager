<script lang="ts">
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import CopyButton from '#lib/components/forms/CopyButton.svelte';
	import Select from '#lib/components/forms/Select.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import MergeDuplicatesModal from '#lib/components/modals/MergeDuplicatesModal.svelte';
	import { useAbortableGet } from '#lib/loadPage.svelte.js';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { Page, SystemInfo } from '#lib/types/index.js';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import FilterDropdown from '#lib/components/forms/FilterDropdown.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatCount, formatDateTime } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/**
	 * RP9 / D5: the Duplicates report body. `games` groups by IGDB id or title (Match by filter); `hash` groups by a
	 * shared file hash (D2). The filters and the page are component state only.
	 */
	interface Props {
		mode: 'games' | 'hash';
	}

	let { mode }: Props = $props();

	interface DuplicateGroup {
		system: string;
		systemName: string;
		/** For an IGDB group (D1) the smallest display title of its games; for a hash group the smallest file name. */
		baseTitle: string;
		gamesKey: string;
		/** D1: the shared IGDB id; null for a title or hash group. */
		igdbId: number | null;
		/** D3: set for a hash group only. */
		hashKind?: 'sha1' | 'md5' | null;
		hash?: string | null;
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

	let titleMatch = $state<'igdb' | 'title'>('igdb');
	const match = $derived(mode === 'hash' ? 'hash' : titleMatch);
	let system = $state('');
	let pageNumber = $state(1);
	let systems = $state<SystemInfo[]>([]);
	/** The chosen target per group (by its key); the first game until changed. */
	let targets = $state<Record<string, string>>({});
	let merging = $state<DuplicateGroup | null>(null);
	let mergeOpen = $state(false);
	let busy = $state(false);

	/** D5: two hash groups can share games, so the hash is part of the key. */
	const groupId = (group: DuplicateGroup) =>
		group.hash ? `${group.gamesKey}-${group.hash}` : group.gamesKey;
	const target = (group: DuplicateGroup) => targets[groupId(group)] ?? group.games[0].id;
	const mergeTarget = $derived(merging?.games.find((g) => g.id === target(merging!)));
	const hashLabel = (group: DuplicateGroup) => (group.hashKind === 'md5' ? 'MD5' : 'SHA1');

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	const report = useAbortableGet<Page<DuplicateGroup>>(() => {
		let url = `/api/reports/duplicates?match=${match}&page=${pageNumber}&pageSize=${pageSize}`;
		if (system) url += `&system=${encodeURIComponent(system)}`;
		return url;
	});
	const result = $derived(report.result);
	const error = $derived(report.error);
	// A new page or a retry drops the picked merge targets.
	$effect(() => {
		if (!report.result) targets = {};
	});

	const emptyMessage = $derived(
		mode === 'hash'
			? 'No games share a file hash.'
			: titleMatch === 'igdb'
				? 'No games share an IGDB ID.'
				: 'No duplicate games found.'
	);

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
			report.reload();
		}
	}

	function dismissBody(group: DuplicateGroup): object {
		if (group.hash)
			return {
				system: group.system,
				gamesKey: group.gamesKey,
				hash: `${group.hashKind}:${group.hash}`
			};
		return group.igdbId === null
			? { system: group.system, baseTitle: group.baseTitle, gamesKey: group.gamesKey }
			: { system: group.system, gamesKey: group.gamesKey, igdbId: group.igdbId };
	}

	async function dismiss(group: DuplicateGroup): Promise<void> {
		try {
			await api.post('/api/reports/duplicates/dismiss', dismissBody(group));
			toasts.show(`Dismissed ${group.baseTitle}.`, 'success');
			report.reload();
		} catch (e) {
			toasts.show(isApiError(e) ? e.title : 'Dismiss failed.', 'danger');
		}
	}
</script>

<div class="row g-2 align-items-end mb-3">
	{#if mode === 'games'}
		<div class="col-auto">
			<div class="input-group input-group-sm w-auto">
				<label class="input-group-text" for="duplicates-match">Match by</label>
				<Select
					class="w-auto"
					id="duplicates-match"
					value={titleMatch}
					onchange={(e) => {
						titleMatch = e.currentTarget.value === 'title' ? 'title' : 'igdb';
						pageNumber = 1;
					}}
					><option value="igdb">IGDB ID</option>
					<option value="title">Title</option></Select
				>
			</div>
		</div>
	{/if}
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
{:else if result.items.length === 0}
	<div class="card">
		<EmptyState message={emptyMessage} />
	</div>
{:else}
	<p class="small text-body-secondary mb-2">
		{formatCount(result.total)}
		{result.total === 1 ? 'group' : 'groups'}
	</p>
	{#each result.items as group (groupId(group) + group.system + group.baseTitle)}
		<section class="card mb-3" aria-labelledby="group-{groupId(group)}">
			<div class="card-header d-flex flex-wrap align-items-center gap-2">
				<SystemBadge system={group.system} name={group.systemName} />
				<h2 class="h6 mb-0" id="group-{groupId(group)}">{group.baseTitle}</h2>
				{#if group.igdbId !== null}<span class="badge text-bg-secondary">IGDB {group.igdbId}</span
					>{/if}
				{#if group.hash}
					<!-- D2: the hash shortened; the full value in the title and the copy button. -->
					<span class="badge text-bg-light border font-monospace" title={group.hash}
						>{hashLabel(group)} {group.hash.slice(0, 12)}…</span
					>
					<CopyButton value={group.hash} label={hashLabel(group)} />
				{/if}
			</div>
			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr>
							{#if auth.isAdmin}<th scope="col"><span class="visually-hidden">Target</span></th
								>{/if}
							<th scope="col">Game</th>
							<th scope="col" class="text-end">Releases</th>
							<!-- G6: the ZIP alone; an external game's ROMs sit next to it. -->
							<th scope="col" class="text-end">ZIP size</th>
							<th scope="col">Added</th>
						</tr>
					</thead>
					<tbody>
						{#each group.games as game, i (game.id)}
							<tr>
								{#if auth.isAdmin}
									<td>
										<Checkbox
											type="radio"
											name="target-{groupId(group)}"
											aria-label="Merge into {game.displayTitle}"
											group={target(group)}
											value={game.id}
											onchange={() => (targets[groupId(group)] = game.id)}
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
			{#if auth.isAdmin}
				<div class="card-footer d-flex gap-2 justify-content-end">
					<Button variant="secondary" outline size="sm" onclick={() => dismiss(group)}
						>Dismiss</Button
					>
					<Button variant="danger" size="sm" onclick={() => askMerge(group)}
						>Merge into selected</Button
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

<MergeDuplicatesModal
	bind:open={mergeOpen}
	count={merging && mergeTarget ? merging.games.length - 1 : null}
	targetTitle={mergeTarget?.displayTitle ?? null}
	{busy}
	onconfirm={merge}
/>
