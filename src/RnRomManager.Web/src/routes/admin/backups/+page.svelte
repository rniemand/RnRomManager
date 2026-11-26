<script lang="ts">
	import { onMount } from 'svelte';
	import { api, resetClient } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { BackupFile, BackupRestoreResult, BackupStatus } from '#lib/types/index.js';
	import AccordionItem from '#lib/components/layout/AccordionItem.svelte';
	import ConfirmModal from '#lib/components/modals/ConfirmModal.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatCount, formatDateTime } from '#lib/format.js';
	import { navigate } from '#lib/navigation.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { navCounts } from '#lib/stores/navCounts.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	const restoreDocs =
		'https://github.com/rniemand/RnRomManager/blob/main/docs/deployment.md#backup-and-restore';

	let status = $state<BackupStatus | null>(null);
	let error = $state<unknown>(null);
	let form = $state({ enabled: true, time: '03:30', retention: 30 });
	let errors = $state<{ time?: string; retention?: string; enabled?: string }>({});
	let saveError = $state<unknown>(null);
	let saving = $state(false);
	let backingUp = $state(false);

	function apply(next: BackupStatus): void {
		status = next;
		form = { enabled: next.enabled, time: next.time, retention: next.retention };
	}

	async function load(): Promise<void> {
		error = null;
		try {
			apply(await api.get<BackupStatus>('/api/admin/backups'));
		} catch (e) {
			error = e;
		}
	}

	onMount(() => {
		if (auth.isAdmin) void load();
	});

	const timePattern = /^([01][0-9]|2[0-3]):[0-5][0-9]$/;

	function validate(): boolean {
		const next: typeof errors = {};
		if (!timePattern.test(form.time)) next.time = 'Use HH:mm, 00:00–23:59.';
		const retention = Number(form.retention);
		if (!Number.isInteger(retention) || retention < 1 || retention > 365)
			next.retention = 'Keep 1–365 backups.';
		errors = next;
		return Object.keys(next).length === 0;
	}

	async function save(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		saveError = null;
		if (!validate()) return;
		saving = true;
		try {
			apply(
				await api.put<BackupStatus>('/api/admin/backups/settings', {
					enabled: form.enabled,
					time: form.time,
					retention: Number(form.retention)
				})
			);
			toasts.show('Backup settings saved.', 'success');
		} catch (e) {
			if (isApiError(e, 'ValidationFailed')) {
				const first = (field: string) => e.errors[field]?.[0];
				errors = { time: first('time'), retention: first('retention'), enabled: first('enabled') };
			} else {
				saveError = e;
			}
		} finally {
			saving = false;
		}
	}

	/** B26: the backup being restored, and the modal's state. */
	let restoring = $state<BackupFile | null>(null);
	let restoreOpen = $state(false);
	let restoreBusy = $state(false);
	let restoreError = $state<{ title: string; detail?: string } | null>(null);

	function askRestore(backup: BackupFile): void {
		restoring = backup;
		restoreError = null;
		restoreOpen = true;
	}

	/** B27.8: every session ended, so forget this one (as logout does) and the cached antiforgery token, then sign in. */
	async function signedOut(message: string, kind: 'success' | 'warning'): Promise<void> {
		resetClient();
		auth.set(null);
		navCounts.reset();
		toasts.show(message, kind);
		await navigate('/login');
	}

	async function restore(): Promise<void> {
		if (!restoring) return;
		restoreBusy = true;
		restoreError = null;
		try {
			const result = await api.post<BackupRestoreResult>(
				`/api/admin/backups/${encodeURIComponent(restoring.fileName)}/restore`
			);
			restoreOpen = false;
			await signedOut(
				`Restored ${result.restored}; safety backup ${result.safetyBackup}. Sign in again, then run Integrity → Run quick.`,
				'success'
			);
		} catch (e) {
			// No ProblemDetails came back (network error, or a proxy's 502/504): the restore may still be running.
			const lost =
				isApiError(e, 'NetworkError') ||
				(isApiError(e, 'HttpError') && (e.status === 502 || e.status === 504));
			if (lost || !isApiError(e)) {
				restoreOpen = false;
				await signedOut(
					'The restore may still be running. Sign in again in a minute, then check Backups.',
					'warning'
				);
			} else {
				restoreError = { title: e.title, detail: e.detail };
			}
		} finally {
			restoreBusy = false;
		}
	}

	async function backUpNow(): Promise<void> {
		backingUp = true;
		try {
			const file = await api.post<BackupFile>('/api/admin/backups');
			toasts.show(`Backup ${file.fileName} written.`, 'success');
			await load();
		} catch (e) {
			if (isApiError(e, 'BackupRunning')) toasts.show('A backup is already running.', 'warning');
			else toasts.show(isApiError(e) ? e.title : 'The backup failed.', 'danger');
			await load();
		} finally {
			backingUp = false;
		}
	}
</script>

<svelte:head><title>Backups · Admin · RnRomManager</title></svelte:head>

<p class="text-body-secondary">
	Database backups are written to the server's <code>Paths:Backups</code> folder (by default
	<code>/app/backups</code>). They hold password hashes and keys: keep that folder private.
	<!-- External link: resolve() is for app routes. -->
	<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -->
	<a href={restoreDocs} target="_blank" rel="noopener noreferrer">How to restore a backup</a>.
</p>

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !status}
	<Loading />
{:else}
	{@const backupCount = status.backups.length}
	<!-- Release editing B34: one accordion item each; only the list opens on load. -->
	<AccordionItem id="backups-schedule-body" headingId="backup-schedule">
		{#snippet header()}Schedule{/snippet}
		<form novalidate onsubmit={save} aria-labelledby="backup-schedule">
			{#if saveError}<ErrorAlert error={saveError} ondismiss={() => (saveError = null)} />{/if}
			<div class="form-check form-switch mb-3">
				<input
					id="backup-enabled"
					class="form-check-input"
					type="checkbox"
					role="switch"
					bind:checked={form.enabled}
				/>
				<label class="form-check-label" for="backup-enabled">Daily backups enabled</label>
			</div>
			<div class="row g-3 mb-3">
				<div class="col-12 col-sm-7">
					<label class="form-label" for="backup-time"
						>Daily at (server time, {status.timeZone})</label
					>
					<input
						id="backup-time"
						class="form-control"
						class:is-invalid={errors.time}
						type="time"
						bind:value={form.time}
					/>
					{#if errors.time}<div class="invalid-feedback">{errors.time}</div>{/if}
				</div>
				<div class="col-12 col-sm-5">
					<label class="form-label" for="backup-retention">Backups to keep</label>
					<input
						id="backup-retention"
						class="form-control"
						class:is-invalid={errors.retention}
						type="number"
						min="1"
						max="365"
						step="1"
						bind:value={form.retention}
					/>
					{#if errors.retention}<div class="invalid-feedback">{errors.retention}</div>{/if}
				</div>
			</div>
			<div>
				<button type="submit" class="btn btn-primary" disabled={saving}>
					{#if saving}<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>{/if}
					Save
				</button>
			</div>
		</form>
	</AccordionItem>
	<AccordionItem id="backups-status-body" headingId="backups-status">
		{#snippet header()}Status{/snippet}
		<dl class="row mb-3">
			<dt class="col-sm-4">Next run</dt>
			<dd class="col-sm-8" data-testid="next-run">
				{status.nextRunAt ? formatDateTime(status.nextRunAt) : 'Disabled'}
			</dd>
			<dt class="col-sm-4">Last result</dt>
			<dd class="col-sm-8" data-testid="last-result">
				{#if !status.lastResult}
					—
				{:else if status.lastResult.ok}
					<Icon name="success" class="text-success" />
					{formatDateTime(status.lastResult.at)} · {status.lastResult.fileName}
				{:else}
					<Icon name="error" class="text-danger" />
					{formatDateTime(status.lastResult.at)} · {status.lastResult.error}
				{/if}
			</dd>
		</dl>
		<div>
			<button
				type="button"
				class="btn btn-outline-primary"
				disabled={backingUp || status.running}
				onclick={backUpNow}
			>
				{#if backingUp || status.running}
					<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>
					Backing up…
				{:else}
					<Icon name="download" /> Back up now
				{/if}
			</button>
		</div>
	</AccordionItem>
	<AccordionItem id="backups-list-body" headingId="backups-list" open flush>
		{#snippet header()}Backups ({formatCount(backupCount)}){/snippet}
		{#if status.backups.length === 0}
			<EmptyState message="No backups yet." />
		{:else}
			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<caption class="visually-hidden">Backups, newest first</caption>
					<thead class="table-dark">
						<tr class="small text-uppercase text-body-secondary">
							<th scope="col">File</th>
							<th scope="col">Created</th>
							<th scope="col" class="text-end">Size</th>
							<th scope="col"><span class="visually-hidden">Actions</span></th>
						</tr>
					</thead>
					<tbody>
						{#each status.backups as backup (backup.fileName)}
							<tr>
								<td class="text-break font-monospace small">{backup.fileName}</td>
								<td class="text-nowrap">{formatDateTime(backup.createdAt)}</td>
								<td class="text-end"><FileSize bytes={backup.sizeBytes} /></td>
								<td class="text-end">
									<button
										type="button"
										class="btn btn-sm btn-outline-danger"
										aria-label="Restore {backup.fileName}"
										onclick={() => askRestore(backup)}><Icon name="restore" /> Restore</button
									>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</AccordionItem>
{/if}

<ConfirmModal
	bind:open={restoreOpen}
	title="Restore backup"
	confirmLabel="Restore"
	confirmClass="btn-danger"
	busy={restoreBusy}
	onconfirm={restore}
>
	{#if restoring}
		<p>
			Restore <strong class="font-monospace">{restoring.fileName}</strong> from {formatDateTime(
				restoring.createdAt
			)}?
		</p>
		<ul>
			<li>Every table in the database is replaced by the backup's.</li>
			<li>A safety backup of the current database is taken first.</li>
			<li>Everyone, including you, is signed out.</li>
			<li>Run Integrity → Run quick afterwards: the library ZIPs may be newer than the backup.</li>
		</ul>
		{#if restoreError}
			<div class="alert alert-danger mb-0" role="alert">
				<strong>{restoreError.title}</strong>{#if restoreError.detail}<div>
						{restoreError.detail}
					</div>{/if}
			</div>
		{/if}
	{/if}
</ConfirmModal>
