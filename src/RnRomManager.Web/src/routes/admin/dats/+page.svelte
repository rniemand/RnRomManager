<script lang="ts">
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { DatRefreshSettings, DatVersion } from '#lib/api/types.js';
	import AccordionItem from '#lib/components/AccordionItem.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { percent } from '#lib/jobs.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	/** One accordion panel open at a time; Automatic refresh starts open. */
	let openPanel = $state<'schedule' | 'files' | null>('schedule');
	let dats = $state<DatVersion[] | null>(null);
	let error = $state<unknown>(null);
	let busy = $state(false);

	const job = $derived(jobs.current);

	/** B28: the daily automatic refresh. */
	let schedule = $state<DatRefreshSettings | null>(null);
	let scheduleError = $state<unknown>(null);
	let form = $state({ enabled: true, time: '04:00' });
	let timeError = $state<string | null>(null);
	let saving = $state(false);

	function applySchedule(next: DatRefreshSettings): void {
		schedule = next;
		form = { enabled: next.enabled, time: next.time };
	}

	async function loadSchedule(): Promise<void> {
		scheduleError = null;
		try {
			applySchedule(await api.get<DatRefreshSettings>('/api/admin/dats/settings'));
		} catch (e) {
			scheduleError = e;
		}
	}

	async function saveSchedule(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		timeError = /^([01][0-9]|2[0-3]):[0-5][0-9]$/.test(form.time)
			? null
			: 'Use HH:mm, 00:00–23:59.';
		if (timeError) return;
		saving = true;
		try {
			applySchedule(await api.put<DatRefreshSettings>('/api/admin/dats/settings', form));
			toasts.show('DAT refresh schedule saved.', 'success');
		} catch (e) {
			if (isApiError(e, 'ValidationFailed') && e.errors.time) timeError = e.errors.time[0];
			else toasts.show(isApiError(e) ? e.title : 'The schedule could not be saved.', 'danger');
		} finally {
			saving = false;
		}
	}

	async function load(): Promise<void> {
		error = null;
		try {
			dats = await api.get<DatVersion[]>('/api/admin/dats');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (!auth.isAdmin) return;
		void load();
		void loadSchedule();
		const stopWatching = jobs.watch();
		const stopEnded = jobs.onEnded((ended) => {
			if (ended.type === 'DatRefresh') void load();
		});
		return () => {
			stopWatching();
			stopEnded();
		};
	});

	async function refresh(): Promise<void> {
		busy = true;
		try {
			await api.post('/api/jobs/dat-refresh');
			await jobs.refresh();
		} catch (e) {
			if (!toasts.jobRunning(e)) toasts.show(isApiError(e) ? e.title : 'Refresh failed.', 'danger');
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>DATs · Admin · RnRomManager</title></svelte:head>

<div class="mb-3" aria-live="polite">
	{#if job}
		<div class="card card-body">
			<strong class="mb-2">{job.type} running</strong>
			<div
				class="progress mb-1"
				role="progressbar"
				aria-label="Job progress"
				aria-valuenow={percent(job.current, job.total)}
				aria-valuemin="0"
				aria-valuemax="100"
			>
				<div class="progress-bar" style="width: {percent(job.current, job.total)}%"></div>
			</div>
			<div class="small text-body-secondary text-break">
				{job.current} / {job.total}{job.currentItem ? ` · ${job.currentItem}` : ''}
			</div>
		</div>
	{:else}
		<button type="button" class="btn btn-primary" disabled={busy} onclick={refresh}>
			<Icon name="retry" /> Refresh DATs
		</button>
	{/if}
</div>

<AccordionItem
	id="dat-schedule-panel"
	headingId="dat-schedule"
	bind:open={() => openPanel === 'schedule', (v) => (openPanel = v ? 'schedule' : null)}
>
	{#snippet header()}Automatic refresh{/snippet}
	<form novalidate onsubmit={saveSchedule} aria-labelledby="dat-schedule">
		{#if scheduleError}
			<ErrorAlert error={scheduleError} onretry={loadSchedule} />
		{:else if !schedule}
			<Loading />
		{:else}
			<div class="form-check form-switch mb-3">
				<input
					id="dat-enabled"
					class="form-check-input"
					type="checkbox"
					role="switch"
					bind:checked={form.enabled}
				/>
				<label class="form-check-label" for="dat-enabled">Enabled</label>
			</div>
			<div class="mb-3 dat-time">
				<label class="form-label" for="dat-time">Time (server time, {schedule.timeZone})</label>
				<input
					id="dat-time"
					class="form-control"
					class:is-invalid={timeError}
					type="time"
					bind:value={form.time}
				/>
				{#if timeError}<div class="invalid-feedback">{timeError}</div>{/if}
			</div>
			<p class="small text-body-secondary" data-testid="dat-next-run">
				Next run: {schedule.nextRunAt ? formatDateTime(schedule.nextRunAt) : 'Disabled'}
			</p>
			<div>
				<button type="submit" class="btn btn-primary" disabled={saving}>
					{#if saving}<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>{/if}
					Save
				</button>
			</div>
		{/if}
	</form>
</AccordionItem>

<AccordionItem
	id="dat-files-panel"
	headingId="dat-files"
	bind:open={() => openPanel === 'files', (v) => (openPanel = v ? 'files' : null)}
>
	{#snippet header()}DAT files{/snippet}
	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if !dats}
		<Loading />
	{:else if dats.length === 0}
		<EmptyState message="No DATs downloaded yet. Refresh to download them." />
	{:else}
		<div class="card">
			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr class="small text-uppercase text-body-secondary">
							<th scope="col">DAT</th>
							<th scope="col">Version</th>
							<th scope="col">Downloaded</th>
							<th scope="col"><span class="visually-hidden">Actions</span></th>
						</tr>
					</thead>
					<tbody>
						{#each dats as dat (dat.datName)}
							<tr>
								<td>{dat.datName}</td>
								<td>{dat.version}</td>
								<td class="text-nowrap">{formatDateTime(dat.downloadedAt)}</td>
								<td class="text-end">
									<!-- eslint-disable svelte/no-navigation-without-resolve -- an API download, not a page -->
									<a
										class="btn btn-sm btn-outline-secondary"
										href="/api/admin/dats/{encodeURIComponent(dat.datName)}/download"
										download
										aria-label="Download {dat.datName}"><Icon name="download" /></a
									>
									<!-- eslint-enable svelte/no-navigation-without-resolve -->
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</div>
	{/if}
</AccordionItem>

<style>
	.dat-time {
		max-width: 16rem;
	}
</style>
