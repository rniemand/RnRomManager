<script lang="ts">
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { api } from '#lib/api/client.js';
	import type { RomStorageRow } from '#lib/types/index.js';
	import BaseModal from '#lib/components/modals/BaseModal.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';

	/**
	 * ROM Zip Config B15–B16: each system's default and configured ROM storage; configurable systems switch through a
	 * confirmation that starts the storage job. Buttons are disabled while any job runs. ZIP Compression Z11–Z12: in-ZIP
	 * systems change their compression level, or repack again, through a confirmation that starts the repack job.
	 * ROM Zip Config and Jobs Restyle R1–R11: storage as icons with tooltips, levels as coloured badges, a Folder column,
	 * system badges linking to the Library, one column per action and grouped headers. L1–L3: the legend shows the same
	 * badges and a speed icon per level.
	 */
	type Level = RomStorageRow['zipCompression'];

	let rows = $state<RomStorageRow[] | null>(null);
	let error = $state<unknown>(null);
	let actionError = $state<unknown>(null);
	let target = $state<RomStorageRow | null>(null);
	let confirmOpen = $state(false);
	let busy = $state(false);
	let zipTarget = $state<RomStorageRow | null>(null);
	let zipLevel = $state<Level>('Med');
	let zipOpen = $state(false);
	let repackOpen = $state(false);

	const levels: { value: Level; label: string; badge: string }[] = [
		{ value: 'Store', label: 'Store', badge: 'text-bg-secondary' },
		{ value: 'Med', label: 'Medium', badge: 'text-bg-primary' },
		{ value: 'High', label: 'High', badge: 'text-bg-success' }
	];
	const level = (value: Level) => levels.find((l) => l.value === value) ?? levels[1];

	/** R6: the storage icon, its accessible label and its tooltip. */
	const storage = (embed: boolean) =>
		embed
			? {
					icon: 'storage-in-zip',
					label: 'Stored in ZIP',
					tip: "Stored in ZIP: ROMs are inside each game's ZIP"
				}
			: {
					icon: 'storage-beside-zip',
					label: 'Beside ZIP',
					tip: "Beside ZIP: ROMs are separate files next to each game's ZIP"
				};
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

	async function run(
		action: () => Promise<{ jobId: string } | undefined>,
		onStarted: (result: { jobId: string }) => Promise<void> = started
	): Promise<void> {
		busy = true;
		actionError = null;
		try {
			const result = await action();
			if (result === undefined) await load();
			else await onStarted(result);
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

	function askZip(row: RomStorageRow, repack: boolean): void {
		zipTarget = row;
		zipLevel = row.zipCompression;
		actionError = null;
		if (repack) repackOpen = true;
		else zipOpen = true;
	}

	/** Z11: the repack job started; its own job page is linked (the storage toast links the Jobs list). */
	function repackStarted(row: RomStorageRow) {
		return async ({ jobId }: { jobId: string }) => {
			await jobs.refresh();
			toasts.show(`Repacking ${row.displayName} ZIPs`, 'success', {
				href: resolve('/jobs/[id]', { id: jobId }),
				text: 'View job'
			});
		};
	}

	function confirmZip(): void {
		const row = zipTarget!;
		const level = zipLevel;
		zipOpen = false;
		void run(
			() =>
				api.put<{ jobId: string } | undefined>(
					`/api/admin/zip-compression/${encodeURIComponent(row.id)}`,
					{ level }
				),
			repackStarted(row)
		);
	}

	function confirmRepack(): void {
		const row = zipTarget!;
		repackOpen = false;
		void run(
			() =>
				api.post<{ jobId: string } | undefined>(
					`/api/admin/zip-compression/${encodeURIComponent(row.id)}/repack`
				),
			repackStarted(row)
		);
	}
</script>

<svelte:head><title>ROM Zip Config · Admin · RnRomManager</title></svelte:head>

<!-- eslint-disable svelte/no-navigation-without-resolve -- resolved paths plus a ?system= query -->

{#if error}
	<ErrorAlert {error} onretry={load} />
{:else if !rows}
	<Loading />
{:else}
	<p class="text-body-secondary">
		Where each system keeps its ROMs and how its game ZIPs are compressed. Changing either rewrites
		every game of that system in a long-running job.
	</p>
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
						<th scope="colgroup" colspan="3"><span class="visually-hidden">System</span></th>
						<th scope="colgroup" colspan="3" class="text-center">ROM storage</th>
						<th scope="colgroup" colspan="2" class="text-center">ZIP compression</th>
					</tr>
					<tr>
						<th scope="col">System</th>
						<th scope="col">Folder</th>
						<th scope="col" class="text-end">Games</th>
						<th scope="col" class="text-center">Default</th>
						<th scope="col" class="text-center">Configured</th>
						<th scope="col" class="text-end">Change</th>
						<th scope="col" class="text-center">Level</th>
						<th scope="col" class="text-end">Repack</th>
					</tr>
				</thead>
				<tbody>
					{#each rows as row (row.id)}
						<tr>
							<td>
								<a
									class="text-decoration-none"
									href="{resolve('/')}?system={encodeURIComponent(row.id)}"
									use:tooltip={`Show ${row.displayName} in the Library`}
									><SystemBadge system={row.id} name={row.displayName} /></a
								>
							</td>
							<td>
								<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
								<code
									tabindex="0"
									use:tooltip={`Put ROMs in ingest/${row.id}; stored in roms/${row.id}`}
									>{row.id}</code
								>
							</td>
							<td class="text-end">{formatCount(row.gameCount)}</td>
							<td class="text-center">{@render storageIcon(row.defaultEmbedRoms)}</td>
							<td class="text-center">
								{@render storageIcon(row.embedRoms)}
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
							<td class="text-center text-nowrap">
								{#if row.compressionConfigurable}
									{@render levelBadge(row.zipCompression)}
									<button
										type="button"
										class="btn btn-sm btn-outline-primary ms-2"
										aria-label="Edit ZIP compression of {row.displayName}"
										use:tooltip={'Edit compression level'}
										disabled={blocked}
										onclick={() => askZip(row, false)}><Icon name="edit" /></button
									>
								{:else}
									<span class="text-body-secondary" aria-hidden="true">—</span><span
										class="visually-hidden">Not applicable: ROMs are beside the ZIP</span
									>
								{/if}
							</td>
							<td class="text-end">
								{#if row.compressionConfigurable}
									<button
										type="button"
										class="btn btn-sm btn-outline-primary"
										disabled={blocked}
										onclick={() => askZip(row, true)}
										>Repack again&#32;<span class="visually-hidden">{row.displayName} ZIPs</span
										></button
									>
								{/if}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
	<h2 class="h6 mt-4">ZIP compression levels</h2>
	<div class="card">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col">Level</th>
						<th scope="col">Typical size</th>
						<th scope="col">Speed</th>
						<th scope="col">Use for</th>
					</tr>
				</thead>
				<tbody>
					<tr>
						<th scope="row">{@render levelBadge('Store')}</th>
						<td>~100% of the ROM size (no compression)</td>
						<td><Icon name="speed-fast" class="me-1" />Fastest to read and write, almost no CPU</td>
						<td
							>Large ROMs that don't compress well (DS), or when fewer files on disk is the goal</td
						>
					</tr>
					<tr>
						<th scope="row">
							{@render levelBadge('Med')}<span class="badge rounded-pill text-bg-light border ms-1"
								>Default</span
							>
						</th>
						<td>About 40–90% of the ROM size, depending on the ROM</td>
						<td><Icon name="speed-medium" class="me-1" />Fast</td>
						<td>Most systems; today's behaviour</td>
					</tr>
					<tr>
						<th scope="row">{@render levelBadge('High')}</th>
						<td>A few percent smaller than Medium</td>
						<td
							><Icon name="speed-slow" class="me-1" />Writes several times slower, reads like Medium</td
						>
						<td>Small cartridge ROMs where space matters most</td>
					</tr>
				</tbody>
			</table>
		</div>
	</div>
	<p class="small text-body-secondary mt-2">These numbers are typical estimates, not guarantees.</p>
{/if}

<BaseModal
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
</BaseModal>

{#snippet levelBadge(value: Level)}
	<span class="badge {level(value).badge}">{level(value).label}</span>
{/snippet}

{#snippet storageIcon(embed: boolean)}
	{@const s = storage(embed)}
	<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
	<span class="d-inline-block fs-5" tabindex="0" use:tooltip={s.tip}
		><Icon name={s.icon} label={s.label} /></span
	>
{/snippet}

{#snippet repackWarning(name: string)}
	<p>
		Every game ZIP of {name} is rewritten, one at a time. On a large library this takes a long time and
		uses a lot of disk and CPU. No other job runs and no game can be edited until it ends.
	</p>
	<p>If you cancel, some ZIPs keep the old level. They still play; use Repack again to finish.</p>
	<p class="mb-0">
		Each game needs free space about the size of its uncompressed ROMs while it's rewritten.
	</p>
{/snippet}

<BaseModal
	bind:open={zipOpen}
	title={zipTarget ? `ZIP compression: ${zipTarget.displayName}` : ''}
	confirmLabel="Change and repack"
	confirmClass="btn-warning"
	confirmDisabled={zipLevel === zipTarget?.zipCompression}
	onconfirm={confirmZip}
>
	{#if zipTarget}
		<div class="mb-3">
			<label for="zip-compression-level" class="form-label">Compression level</label>
			<select id="zip-compression-level" class="form-select" bind:value={zipLevel}>
				{#each levels as level (level.value)}
					<option value={level.value}>{level.label}</option>
				{/each}
			</select>
		</div>
		{@render repackWarning(zipTarget.displayName)}
	{/if}
</BaseModal>

<BaseModal
	bind:open={repackOpen}
	title={zipTarget ? `Repack ${zipTarget.displayName} ZIPs` : ''}
	confirmLabel="Repack again"
	confirmClass="btn-warning"
	onconfirm={confirmRepack}
>
	{#if zipTarget}
		{@render repackWarning(zipTarget.displayName)}
	{/if}
</BaseModal>
