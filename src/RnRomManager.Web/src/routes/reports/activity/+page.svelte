<script lang="ts">
	import { onMount, type Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type {
		ActivityAuditItem,
		ActivityReport,
		BackupStatus,
		Job,
		Page,
		SystemInfo
	} from '#lib/api/types.js';
	import AccordionItem from '#lib/components/AccordionItem.svelte';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatDateTime, formatDuration } from '#lib/format.js';
	import { jobLabel, statusClass } from '#lib/jobs.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	/** B20: the extra columns of an audit section, after Time and User. */
	type Column = 'game' | 'release' | 'action' | 'outcome';
	const columnLabels: Record<Column, string> = {
		game: 'Game',
		release: 'Release',
		action: 'Action',
		outcome: 'Outcome'
	};

	/** B20: one API call behind some sections; each shows its own Loading or ErrorAlert, so one failure spares the rest. */
	class Source<T> {
		data = $state<T | null>(null);
		error = $state<unknown>(null);
		readonly url: string;
		constructor(url: string) {
			this.url = url;
		}
		load = async (): Promise<void> => {
			this.error = null;
			this.data = null;
			try {
				this.data = await api.get<T>(this.url);
			} catch (e) {
				this.error = e;
			}
		};
	}

	const reportSource = new Source<ActivityReport>('/api/reports/activity');
	const jobsSource = new Source<Page<Job>>('/api/jobs?pageSize=10');
	const backupSource = new Source<BackupStatus>('/api/admin/backups');
	const report = $derived(reportSource.data);
	let systems = $state<SystemInfo[]>([]);

	onMount(() => {
		if (!auth.isAdmin) return;
		for (const source of [reportSource, jobsSource, backupSource]) void source.load();
		// Names for the system badges only; without them SystemBadge falls back to the id.
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {}
		);
	});

	/** One section open at a time; Added ROMs starts open. Closed sections stay mounted. */
	let openSection = $state<string | null>('added');

	const systemName = (id: string) => systems.find((s) => s.id === id)?.displayName;
	/** B21: a gone game's name is the file name of its audit `path` (the server builds no fallback). */
	const fileName = (path: string | null) => path?.split('/').at(-1) || null;
</script>

<svelte:head><title>Activity · Reports · RnRomManager</title></svelte:head>

{#snippet section(key: string, title: string, body: Snippet)}
	<AccordionItem
		id="activity-{key}-panel"
		headingId="activity-{key}"
		flush
		bind:open={() => openSection === key, (v) => (openSection = v ? key : null)}
	>
		{#snippet header()}{title}{/snippet}
		{@render body()}
	</AccordionItem>
{/snippet}

{#snippet sourceState(
	source: { data: unknown; error: unknown; load: () => Promise<void> },
	empty: boolean,
	message: string,
	table: Snippet
)}
	{#if source.error}
		<div class="card-body"><ErrorAlert error={source.error} onretry={source.load} /></div>
	{:else if !source.data}
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
		{@render sourceState(reportSource, !items?.length, message, table)}
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
	{@render sourceState(reportSource, !report?.addedRoms.length, 'No ROMs added yet.', table)}
{/snippet}

{#snippet signInsBody()}
	{#if report?.lastSignIn}
		<p class="card-body mb-0 border-bottom" data-testid="last-sign-in">
			Last signed in: <strong>{report.lastSignIn.username}</strong>
			at {formatDateTime(report.lastSignIn.at)}
		</p>
	{/if}
	{#snippet table()}{@render auditTable(report?.signIns ?? [], ['outcome'])}{/snippet}
	{@render sourceState(
		reportSource,
		!report?.signIns.length,
		'No sign-ins in the last 30 days.',
		table
	)}
{/snippet}

{#snippet jobsBody()}
	{#snippet table()}
		<table class="table table-sm table-striped table-hover align-middle mb-0">
			<thead class="table-dark">
				<tr>
					<th scope="col">Type</th>
					<th scope="col">Status</th>
					<th scope="col">Started</th>
					<th scope="col">Duration</th>
				</tr>
			</thead>
			<tbody>
				{#each jobsSource.data?.items ?? [] as job (job.id)}
					<tr>
						<td>{jobLabel(job.type)}</td>
						<td><span class="badge {statusClass(job.status)}">{job.status}</span></td>
						<td class="text-nowrap">
							<a href={resolve('/jobs/[id]', { id: job.id })}>{formatDateTime(job.startedAt)}</a>
						</td>
						<td class="text-nowrap">
							{job.finishedAt
								? formatDuration(Date.parse(job.finishedAt) - Date.parse(job.startedAt))
								: 'running'}
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/snippet}
	{@render sourceState(jobsSource, !jobsSource.data?.items.length, 'No jobs have run yet.', table)}
{/snippet}

{#snippet backupBody()}
	{#snippet table()}
		{@const status = backupSource.data!}
		<table class="table table-sm table-striped table-hover align-middle mb-0">
			<tbody>
				{#if status.lastResult}
					<tr>
						<th scope="row">Last run</th>
						<td class="text-nowrap">{formatDateTime(status.lastResult.at)}</td>
					</tr>
					<tr>
						<th scope="row">Result</th>
						<td>
							{#if status.lastResult.ok}
								<span class="badge text-bg-success">OK</span>
								{status.lastResult.fileName ?? ''}
							{:else}
								<span class="badge text-bg-danger">Failed</span>
								<span class="text-break">{status.lastResult.error ?? ''}</span>
							{/if}
						</td>
					</tr>
				{:else}
					<!-- After a restart the last result is gone; the newest file still says when a backup was made. -->
					<tr>
						<th scope="row">Newest file</th>
						<td class="text-break">{status.backups[0].fileName}</td>
					</tr>
					<tr>
						<th scope="row">Created</th>
						<td class="text-nowrap">
							{formatDateTime(status.backups[0].createdAt)}
							<span class="text-body-secondary small">(since restart: no backup run)</span>
						</td>
					</tr>
				{/if}
			</tbody>
		</table>
	{/snippet}
	{@render sourceState(
		backupSource,
		!backupSource.data?.lastResult && !backupSource.data?.backups.length,
		'No backups yet.',
		table
	)}
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

	{@render section('jobs', 'Recent jobs', jobsBody)}
	{@render section('backup', 'Last backup', backupBody)}
</AdminOnly>
