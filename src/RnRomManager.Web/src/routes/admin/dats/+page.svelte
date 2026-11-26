<script lang="ts">
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import FormField from '#lib/components/forms/FormField.svelte';
	import Switch from '#lib/components/forms/Switch.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import { onMount } from 'svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { DatRefreshSettings, DatVersion } from '#lib/types/index.js';
	import AccordionItem from '#lib/components/layout/AccordionItem.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatDateTime } from '#lib/format.js';
	import { jobLabel, percent } from '#lib/jobs.js';
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
			<strong class="mb-2">{jobLabel(job.type)} running</strong>
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
		<Button variant="primary" icon="retry" disabled={busy} onclick={refresh}>Refresh DATs</Button>
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
			<Switch id="dat-enabled" label="Enabled" wrapperClass="mb-3" bind:checked={form.enabled} />
			<div class="mb-3 dat-time">
				<FormField label="Time (server time, {schedule.timeZone})" for="dat-time">
					<TextInput
						id="dat-time"
						class={timeError ? 'is-invalid' : undefined}
						type="time"
						bind:value={form.time}
					/>
				</FormField>
				{#if timeError}<div class="invalid-feedback">{timeError}</div>{/if}
			</div>
			<p class="small text-body-secondary" data-testid="dat-next-run">
				Next run: {schedule.nextRunAt ? formatDateTime(schedule.nextRunAt) : 'Disabled'}
			</p>
			<div>
				<Button type="submit" variant="primary" busy={saving}>Save</Button>
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
		<DataTable items={dats} key={(dat) => dat.datName}>
			{#snippet head()}
				<tr class="small text-uppercase text-body-secondary">
					<th scope="col">DAT</th>
					<th scope="col">Version</th>
					<th scope="col">Downloaded</th>
					<th scope="col"><span class="visually-hidden">Actions</span></th>
				</tr>
			{/snippet}
			{#snippet row(dat)}
				<tr>
					<td>{dat.datName}</td>
					<td>{dat.version}</td>
					<td class="text-nowrap">{formatDateTime(dat.downloadedAt)}</td>
					<td class="text-end">
						<Button
							variant="secondary"
							outline
							size="sm"
							icon="download"
							href="/api/admin/dats/{encodeURIComponent(dat.datName)}/download"
							download
							aria-label="Download {dat.datName}"
						/>
					</td>
				</tr>
			{/snippet}
		</DataTable>
	{/if}
</AccordionItem>

<style>
	.dat-time {
		max-width: 16rem;
	}
</style>
