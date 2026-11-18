<script lang="ts">
	import { onMount, type Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type { ActivityAuditItem, ActivityReport, SystemInfo } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	/** B20: the extra columns of an audit section, after Time and User. */
	type Column = 'game' | 'release' | 'action' | 'outcome';
	const columnLabels: Record<Column, string> = {
		game: 'Game',
		release: 'Release',
		action: 'Action',
		outcome: 'Outcome'
	};

	let report = $state<ActivityReport | null>(null);
	let reportError = $state<unknown>(null);
	let systems = $state<SystemInfo[]>([]);

	async function loadReport(): Promise<void> {
		reportError = null;
		report = null;
		try {
			report = await api.get<ActivityReport>('/api/reports/activity');
		} catch (e) {
			reportError = e;
		}
	}

	onMount(() => {
		if (!auth.isAdmin) return;
		void loadReport();
		// Names for the system badges only; without them SystemBadge falls back to the id.
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {}
		);
	});

	const systemName = (id: string) => systems.find((s) => s.id === id)?.displayName;
	/** B21: a gone game's name is the file name of its audit `path` (the server builds no fallback). */
	const fileName = (path: string | null) => path?.split('/').at(-1) || null;
</script>

<svelte:head><title>Activity · Reports · RnRomManager</title></svelte:head>

{#snippet section(key: string, title: string, body: Snippet)}
	<section class="card mb-3" aria-labelledby="activity-{key}">
		<div class="card-header">
			<h2 class="h5 mb-0" id="activity-{key}">{title}</h2>
		</div>
		{@render body()}
	</section>
{/snippet}

{#snippet reportState(empty: boolean, message: string, table: Snippet)}
	{#if reportError}
		<div class="card-body"><ErrorAlert error={reportError} onretry={loadReport} /></div>
	{:else if !report}
		<div class="card-body"><Loading /></div>
	{:else if empty}
		<EmptyState {message} />
	{:else}
		<div class="table-responsive">{@render table()}</div>
	{/if}
{/snippet}

{#snippet gameCell(item: { gameId: string | null; gameTitle: string | null; path: string | null })}
	{#if item.gameId && item.gameTitle}
		<a href={resolve('/games/[id]', { id: item.gameId })}>{item.gameTitle}</a>
	{:else}
		{fileName(item.path) ?? '—'}
	{/if}
{/snippet}

{#snippet auditTable(items: ActivityAuditItem[], columns: Column[])}
	<table class="table table-sm table-striped table-hover align-middle mb-0">
		<thead class="table-dark">
			<tr>
				<th scope="col">Time</th>
				<th scope="col">User</th>
				{#each columns as column (column)}<th scope="col">{columnLabels[column]}</th>{/each}
			</tr>
		</thead>
		<tbody>
			{#each items as item (item.id)}
				<tr>
					<td class="text-nowrap">{formatDateTime(item.occurredAt)}</td>
					<td
						>{#if item.user}{item.user}{:else}<em>System</em>{/if}</td
					>
					{#each columns as column (column)}
						<td class:text-break={column !== 'action' && column !== 'outcome'}>
							{#if column === 'game'}
								{@render gameCell(item)}
							{:else if column === 'release'}
								{item.details ?? '—'}
							{:else}
								{item.action}
							{/if}
						</td>
					{/each}
				</tr>
			{/each}
		</tbody>
	</table>
{/snippet}

{#snippet auditSection(
	key: string,
	title: string,
	items: ActivityAuditItem[] | undefined,
	columns: Column[],
	message: string
)}
	{#snippet body()}
		{#snippet table()}{@render auditTable(items ?? [], columns)}{/snippet}
		{@render reportState(!items?.length, message, table)}
	{/snippet}
	{@render section(key, title, body)}
{/snippet}

{#snippet addedBody()}
	{#snippet table()}
		<table class="table table-sm table-striped table-hover align-middle mb-0">
			<thead class="table-dark">
				<tr>
					<th scope="col">Time</th>
					<th scope="col">Game</th>
					<th scope="col">Release</th>
					<th scope="col">System</th>
					<th scope="col" class="text-end">Size</th>
				</tr>
			</thead>
			<tbody>
				{#each report?.addedRoms ?? [] as rom (rom.romId)}
					<tr>
						<td class="text-nowrap">{formatDateTime(rom.addedAt)}</td>
						<td class="text-break">
							<a href={resolve('/games/[id]', { id: rom.gameId })}>{rom.gameTitle}</a>
						</td>
						<td class="text-break">{rom.fileName}</td>
						<td><SystemBadge system={rom.system} name={systemName(rom.system)} /></td>
						<td class="text-end text-nowrap"><FileSize bytes={rom.sizeBytes} /></td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/snippet}
	{@render reportState(!report?.addedRoms.length, 'No ROMs added yet.', table)}
{/snippet}

{#snippet signInsBody()}
	{#if report?.lastSignIn}
		<p class="card-body mb-0 border-bottom" data-testid="last-sign-in">
			Last signed in: <strong>{report.lastSignIn.username}</strong>
			at {formatDateTime(report.lastSignIn.at)}
		</p>
	{/if}
	{#snippet table()}{@render auditTable(report?.signIns ?? [], ['outcome'])}{/snippet}
	{@render reportState(!report?.signIns.length, 'No sign-ins in the last 30 days.', table)}
{/snippet}

<AdminOnly>
	<p class="small text-body-secondary">
		Sign-ins, plays, saves, edits and deletions come from the audit log, which keeps the last 30
		days.
	</p>

	{@render section('added', 'Added ROMs', addedBody)}

	{@render section('sign-ins', 'Sign-ins', signInsBody)}

	{@render auditSection(
		'plays',
		'Plays',
		report?.plays,
		['game', 'release'],
		'No plays in the last 30 days.'
	)}
	{@render auditSection(
		'saves',
		'Saves',
		report?.saves,
		['game', 'action'],
		'No save changes in the last 30 days.'
	)}
	{@render auditSection(
		'edits',
		'Edits',
		report?.edits,
		['game', 'action'],
		'No edits in the last 30 days.'
	)}
	{@render auditSection(
		'deletions',
		'Deletions',
		report?.deletions,
		['game', 'action'],
		'No deletions in the last 30 days.'
	)}
</AdminOnly>
