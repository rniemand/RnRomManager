<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { DatSearchResult, GameListItem, ReviewDetail } from '#lib/api/types.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import CopyButton from '#lib/components/CopyButton.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import GameTypeahead from '#lib/components/GameTypeahead.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import JobStepProgress from '#lib/components/JobStepProgress.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import ReviewDeleteModal from '#lib/components/ReviewDeleteModal.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatDateTime, formatKey, formatValue } from '#lib/format.js';
	import {
		emptyManualForm,
		manualFileStem,
		toManualRequest,
		validateManual,
		type ManualErrors,
		type ManualForm
	} from '#lib/reviewForm.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { reviewJobs } from '#lib/stores/reviewJobs.svelte.js';
	import { googleSearchUrl, reviewSearchQuery } from '#lib/googleSearch.js';
	import { deleteReviewItem, toastDeleteFailed } from '#lib/review.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';

	let item = $state<ReviewDetail | null>(null);
	let notFound = $state(false);
	let error = $state<unknown>(null);
	let reload = $state(0);

	let busy = $state(false);
	let actionError = $state<string | null>(null);
	// B7: any action would get JobRunning while a resolution runs, on every item's page.
	const resolving = $derived(reviewJobs.running);
	const disabled = $derived(busy || resolving !== null);

	onMount(() => {
		// The jobs store polls only while watched; it feeds the step bar.
		const stopWatching = jobs.watch();
		const stopHandling = reviewJobs.onFailure(handle);
		return () => {
			stopWatching();
			stopHandling();
		};
	});

	let searchText = $state('');
	let searchResults = $state<DatSearchResult[] | null>(null);
	let searching = $state(false);

	let attachTarget = $state<GameListItem | null>(null);
	let attachTargetError = $state<string | undefined>();
	let attachFlag = $state<'Hack' | 'Translated'>('Hack');

	let manual = $state<ManualForm>(emptyManualForm());
	let manualErrors = $state<ManualErrors>({});

	let confirmDismiss = $state(false);
	let confirmDelete = $state(false);

	type Tab = 'suggestions' | 'matched' | 'attach' | 'manual';
	const tabLabels: Record<Tab, string> = {
		suggestions: 'Suggestions',
		matched: 'Matched DAT entry',
		attach: 'Attach',
		manual: 'Manual'
	};
	// AmbiguousGroup items can only be attached to a candidate; the other actions need NoDatMatch.
	const tabs = $derived<Tab[]>(
		item?.reason === 'AmbiguousGroup' ? ['matched'] : ['suggestions', 'attach', 'manual']
	);
	let chosenTab = $state<Tab | null>(null);
	// Default: Suggestions when there are some, else Manual (nothing close matched).
	const tab = $derived<Tab>(
		chosenTab && tabs.includes(chosenTab)
			? chosenTab
			: item?.reason === 'AmbiguousGroup'
				? 'matched'
				: item?.suggestions?.length
					? 'suggestions'
					: 'manual'
	);

	const id = $derived(page.params.id ?? '');
	const base = $derived(`/api/review/${encodeURIComponent(id)}`);

	$effect(() => {
		if (!auth.isAdmin) return;
		const url = base;
		void reload;
		const controller = new AbortController();
		error = null;
		notFound = false;
		item = null;
		api.get<ReviewDetail>(url, { signal: controller.signal }).then(
			(i) => (item = i),
			(e) => {
				if (controller.signal.aborted) return;
				if (isApiError(e) && e.status === 404) notFound = true;
				else error = e;
			}
		);
		return () => controller.abort();
	});

	/** B7: starts a resolution job; the store reports how it ended. */
	async function resolveWith(body: object, done: string): Promise<void> {
		if (disabled || !item) return;
		busy = true;
		actionError = null;
		try {
			const { jobId } = await api.post<{ jobId: string }>(`${base}/resolve`, body);
			reviewJobs.start({ jobId, reviewId: item.id, fileName: item.originalFileName, done });
			void jobs.refresh();
		} catch (e) {
			handle(e);
		} finally {
			busy = false;
		}
	}

	function handle(e: unknown): void {
		if (toasts.jobRunning(e)) return;
		if (isApiError(e, 'SourceMissing')) {
			// §7.5: the row is deleted.
			toasts.show('The source file is gone, so the item was removed.', 'warning');
			void goto(resolve('/review'));
		} else if (isApiError(e, 'AmbiguousGroup')) {
			actionError = 'Several games already have this title. Attach it to one of them instead.';
		} else if (isApiError(e) && e.status === 404) {
			notFound = true;
		} else if (isApiError(e, 'ValidationFailed')) {
			const first = (field: string) => e.errors[field]?.[0];
			manualErrors = {
				title: first('title'),
				revision: first('revision'),
				regions: first('regions'),
				languages: first('languages'),
				flags: first('flags')
			};
			attachTargetError = first('targetGameId');
			actionError = Object.values(e.errors).flat()[0] ?? e.title;
		} else {
			actionError = isApiError(e) ? e.title : 'Something went wrong.';
		}
	}

	// G8: Switch is identified by title id through titledb, so a pick is as verified as an automatic match.
	const titleDb = $derived(item?.system === 'switch');

	// B8: one "update" checkbox for every titledb Use button; pre-ticked by an [UPD] marker in the file name.
	let updateChoice = $state<boolean | null>(null);
	const asUpdate = $derived(updateChoice ?? /\[upd\]/i.test(item?.originalFileName ?? ''));

	function useDat(datGameName: string): Promise<void> {
		const update = titleDb && asUpdate;
		return resolveWith(
			titleDb
				? { action: 'Dat', datSystem: item!.system, datGameName, update }
				: { action: 'Dat', datSystem: item!.system, datGameName },
			update
				? `Added update of ${datGameName}.`
				: titleDb
					? `Added release of ${datGameName}.`
					: `Added as an unverified release of ${datGameName}.`
		);
	}

	async function search(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		const q = searchText.trim();
		if (!q || !item) return;
		searching = true;
		try {
			const params = new URLSearchParams({ system: item.system, q, limit: '20' });
			searchResults = await api.get<DatSearchResult[]>(`/api/dat/search?${params}`);
		} catch (e) {
			handle(e);
		} finally {
			searching = false;
		}
	}

	function attach(event: SubmitEvent): void {
		event.preventDefault();
		if (!attachTarget) {
			attachTargetError = 'Choose the game to attach to.';
			return;
		}
		void resolveWith(
			{ action: 'Attach', targetGameId: attachTarget.id, flag: attachFlag },
			`Attached to ${attachTarget.displayTitle}.`
		);
	}

	function submitManual(event: SubmitEvent): void {
		event.preventDefault();
		manualErrors = validateManual(manual);
		if (Object.keys(manualErrors).length) return;
		void resolveWith(toManualRequest(manual), `Added as ${manual.title.trim()}.`);
	}

	async function dismiss(): Promise<void> {
		busy = true;
		try {
			await api.post(`${base}/dismiss`);
			confirmDismiss = false;
			toasts.show('Dismissed.', 'success');
			await goto(resolve('/review'));
		} catch (e) {
			confirmDismiss = false;
			handle(e);
		} finally {
			busy = false;
		}
	}

	const percent = (score: number) => `${Math.round(score * 100)}%`;

	async function askDelete(event: MouseEvent): Promise<void> {
		// RV1: Shift+click skips the modal, except for an archive (deleting it deletes every file in it).
		if (!event.shiftKey || !item || item.archivePath) {
			confirmDelete = true;
			return;
		}
		busy = true;
		try {
			await deleteReviewItem(item.id);
		} catch (e) {
			toastDeleteFailed(e);
			return;
		} finally {
			busy = false;
		}
		await deleted();
	}

	async function deleted(): Promise<void> {
		toasts.show(`Deleted ${item?.originalFileName ?? 'the file'}.`, 'success');
		await goto(resolve('/review'));
	}
</script>

<svelte:head><title>Review · RnRomManager</title></svelte:head>

<AdminOnly>
	<p><a href={resolve('/review')}>← Review queue</a></p>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if notFound}
		<EmptyState icon="not-found" message="This review item doesn't exist any more.">
			{#snippet action()}
				<a class="btn btn-primary" href={resolve('/review')}>Back to the queue</a>
			{/snippet}
		</EmptyState>
	{:else if !item}
		<Loading />
	{:else}
		<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
			<h1 class="h4 mb-0 text-truncate" style="max-width: 32rem" title={item.originalFileName}>
				{item.originalFileName}
			</h1>
			<SystemBadge system={item.system} />
			<span class="badge text-bg-secondary">{item.reason}</span>
			{#if item.status !== 'Pending'}<span class="badge text-bg-light border">{item.status}</span
				>{/if}
			<div class="ms-auto d-flex gap-2">
				<!-- External link: resolve() is for app routes. -->
				<!-- eslint-disable svelte/no-navigation-without-resolve -->
				<a
					class="btn btn-outline-secondary"
					href={googleSearchUrl(reviewSearchQuery(item, item.systemName))}
					target="_blank"
					rel="noopener noreferrer"><Icon name="google" /> Google it</a
				>
				<!-- eslint-enable svelte/no-navigation-without-resolve -->
				{#if item.resolvable}
					<button
						type="button"
						class="btn btn-outline-secondary"
						{disabled}
						onclick={() => (confirmDismiss = true)}>Dismiss</button
					>
				{/if}
				<button
					type="button"
					class="btn btn-outline-danger"
					{disabled}
					use:tooltip={'Delete (Shift+click to skip confirmation)'}
					onclick={askDelete}><Icon name="delete" /> Delete</button
				>
			</div>
		</div>

		{#if resolving}
			<div class="alert alert-info" role="status">
				<div class="d-flex align-items-center gap-2">
					<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>
					Resolving {resolving.fileName}…
				</div>
				{#if jobs.current?.id === resolving.jobId}<JobStepProgress step={jobs.current.step} />{/if}
			</div>
		{/if}

		{#if actionError}
			<div class="alert alert-danger alert-dismissible" role="alert">
				{actionError}
				<button
					type="button"
					class="btn-close"
					aria-label="Dismiss"
					onclick={() => (actionError = null)}
				></button>
			</div>
		{/if}

		<div class="row g-4">
			<div class="col-12 col-lg-5">
				<div class="card mb-4">
					<div class="card-header"><h2 class="h6 mb-0">File</h2></div>
					<div class="card-body">
						<dl class="row small mb-0">
							<dt class="col-4">Size</dt>
							<dd class="col-8"><FileSize bytes={item.sizeBytes} /></dd>
							{#each [['CRC32', item.crc32], ['MD5', item.md5], ['SHA1', item.sha1]] as [label, value] (label)}
								<dt class="col-4">{label}</dt>
								<dd class="col-8 font-monospace text-break">
									{value}
									<CopyButton {value} {label} />
								</dd>
							{/each}
							<dt class="col-4">Source</dt>
							<dd class="col-8 text-break">
								{item.archiveEntry ? `${item.archivePath} › ${item.archiveEntry}` : item.sourcePath}
							</dd>
							<dt class="col-4">Added</dt>
							<dd class="col-8">{formatDateTime(item.createdAt)}</dd>
						</dl>
					</div>
				</div>
				<div class="card mb-4">
					<div class="card-header"><h2 class="h6 mb-0">Header</h2></div>
					{#if item.header}
						<div class="table-responsive">
							<table class="table table-sm table-striped table-hover align-middle mb-0">
								<thead class="table-dark">
									<tr class="small text-uppercase text-body-secondary">
										<th scope="col">Field</th>
										<th scope="col">Value</th>
									</tr>
								</thead>
								<tbody>
									{#each Object.entries(item.header) as [key, value] (key)}
										<tr>
											<th scope="row" class="fw-normal text-body-secondary">{formatKey(key)}</th>
											<td>{formatValue(value)}</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					{:else}
						<div class="card-body">
							<p class="text-body-secondary mb-0">The header couldn't be read.</p>
						</div>
					{/if}
				</div>
			</div>

			<div class="col-12 col-lg-7">
				{#if !item.resolvable}
					<!-- B15: a disc track file can't be resolved or dismissed, only deleted. -->
					<p class="text-body-secondary">
						Disc track files must match the Redump DAT to be imported.
					</p>
				{:else}
					<ul class="nav nav-tabs mb-3" role="tablist">
						{#each tabs as t (t)}
							<li class="nav-item" role="presentation">
								<button
									type="button"
									class="nav-link"
									class:active={tab === t}
									id="tab-{t}"
									role="tab"
									aria-selected={tab === t}
									aria-controls="panel-{t}"
									onclick={() => (chosenTab = t)}>{tabLabels[t]}</button
								>
							</li>
						{/each}
					</ul>

					<div id="panel-{tab}" role="tabpanel" aria-labelledby="tab-{tab}">
						{#if tab === 'matched'}
							{#if item.datIdentification}
								<p>
									{item.datIdentification.romName}
									<span class="text-body-secondary small">
										({item.datIdentification.datName}
										{item.datIdentification.datVersion})
									</span>
								</p>
							{/if}
							<p>Several games share its title. Attach it to one of them:</p>
							<ul class="list-group">
								{#each item.candidateGames as game (game.id)}
									<li class="list-group-item d-flex align-items-center gap-2">
										<a class="me-auto" href={resolve('/games/[id]', { id: game.id })}
											>{game.displayTitle}</a
										>
										<button
											type="button"
											class="btn btn-sm btn-primary"
											{disabled}
											onclick={() =>
												resolveWith(
													{ action: 'Attach', targetGameId: game.id, flag: 'None' },
													`Attached to ${game.displayTitle}.`
												)}>Attach</button
										>
									</li>
								{:else}
									<li class="list-group-item text-body-secondary">
										None of the candidate games exist any more. Run ingestion again.
									</li>
								{/each}
							</ul>
						{:else if tab === 'suggestions'}
							<p class="small text-body-secondary">
								{#if titleDb}
									Choose a titledb result to identify this release by title ID.
								{:else}
									Picking a DAT entry adds the file as an unverified release, because its hashes
									differ.
								{/if}
							</p>
							{#if titleDb}
								<div class="form-check mb-3">
									<input
										id="as-update"
										class="form-check-input"
										type="checkbox"
										checked={asUpdate}
										onchange={(e) => (updateChoice = e.currentTarget.checked)}
									/>
									<label class="form-check-label" for="as-update">This file is an update</label>
								</div>
							{/if}
							{#if item.suggestions?.length}
								<div class="card mb-3">
									<div class="table-responsive">
										<table class="table table-sm table-striped table-hover align-middle mb-0">
											<thead class="table-dark">
												<tr class="small text-uppercase text-body-secondary">
													<th scope="col">DAT entry</th>
													<th scope="col" class="text-end">Score</th>
													<th scope="col" class="text-end"
														><span class="visually-hidden">Actions</span></th
													>
												</tr>
											</thead>
											<tbody>
												{#each item.suggestions as suggestion (suggestion.romName)}
													<tr>
														<td class="text-break">{suggestion.romName}</td>
														<td class="text-end text-nowrap">{percent(suggestion.score)}</td>
														<td class="text-end">
															<button
																type="button"
																class="btn btn-sm btn-primary"
																{disabled}
																onclick={() => useDat(suggestion.datGameName)}>Use</button
															>
														</td>
													</tr>
												{/each}
											</tbody>
										</table>
									</div>
								</div>
							{:else}
								<p class="text-body-secondary">No close DAT matches.</p>
							{/if}

							<form class="row g-2 align-items-end" role="search" onsubmit={search}>
								<div class="col">
									<label class="form-label" for="dat-search">Search the {item.system} DAT</label>
									<input
										id="dat-search"
										type="search"
										class="form-control"
										bind:value={searchText}
									/>
								</div>
								<div class="col-auto">
									<button type="submit" class="btn btn-outline-secondary" disabled={searching}>
										Search
									</button>
								</div>
							</form>
							{#if searchResults}
								{#if searchResults.length}
									<ul class="list-group mt-2">
										{#each searchResults as result (result.romName)}
											<li class="list-group-item d-flex align-items-center gap-2">
												<span class="me-auto text-break">{result.romName}</span>
												{#if !(titleDb && result.sizeBytes === 0)}
													<!-- G8: titledb results carry no size (T3). -->
													<span class="small text-body-secondary text-nowrap"
														><FileSize bytes={result.sizeBytes} /></span
													>
												{/if}
												<button
													type="button"
													class="btn btn-sm btn-primary"
													{disabled}
													onclick={() => useDat(result.gameName)}>Use</button
												>
											</li>
										{/each}
									</ul>
								{:else}
									<p class="text-body-secondary mt-2">No DAT entries match.</p>
								{/if}
							{/if}
						{:else if tab === 'attach'}
							<p class="small text-body-secondary">Attach to a game as a hack or translation.</p>
							<form onsubmit={attach} novalidate>
								<div class="mb-2">
									<GameTypeahead
										id="attach-target"
										label="Game"
										system={item.system}
										bind:selected={attachTarget}
										invalid={attachTargetError}
									/>
								</div>
								<fieldset class="mb-2">
									<legend class="form-label fs-6">Flag</legend>
									{#each ['Hack', 'Translated'] as const as flag (flag)}
										<div class="form-check form-check-inline">
											<input
												id="flag-{flag}"
												class="form-check-input"
												type="radio"
												value={flag}
												bind:group={attachFlag}
											/>
											<label class="form-check-label" for="flag-{flag}">{flag}</label>
										</div>
									{/each}
								</fieldset>
								<button type="submit" class="btn btn-primary" {disabled}>Attach</button>
							</form>
						{:else}
							<form onsubmit={submitManual} novalidate>
								<div class="mb-2">
									<label class="form-label" for="manual-title">Title</label>
									<input
										id="manual-title"
										class="form-control"
										class:is-invalid={manualErrors.title}
										maxlength="255"
										bind:value={manual.title}
									/>
									{#if manualErrors.title}<div class="invalid-feedback">
											{manualErrors.title}
										</div>{/if}
								</div>
								<div class="row g-2 mb-2">
									{#each [['regions', 'Regions', 'e.g. USA, Europe'], ['languages', 'Languages', 'e.g. En, Fr'], ['flags', 'Flags', 'e.g. Proto, Unl']] as const as [field, label, hint] (field)}
										<div class="col-12 col-md-4">
											<label class="form-label" for="manual-{field}">{label}</label>
											<input
												id="manual-{field}"
												class="form-control"
												class:is-invalid={manualErrors[field]}
												aria-describedby="manual-{field}-help"
												bind:value={manual[field]}
											/>
											{#if manualErrors[field]}<div class="invalid-feedback">
													{manualErrors[field]}
												</div>{/if}
											<div id="manual-{field}-help" class="form-text">Comma-separated, {hint}.</div>
										</div>
									{/each}
								</div>
								<div class="mb-2">
									<label class="form-label" for="manual-revision">Revision</label>
									<input
										id="manual-revision"
										class="form-control"
										class:is-invalid={manualErrors.revision}
										placeholder="e.g. Rev 1"
										maxlength="32"
										bind:value={manual.revision}
									/>
									{#if manualErrors.revision}<div class="invalid-feedback">
											{manualErrors.revision}
										</div>{/if}
								</div>
								{#if manual.title.trim()}
									<p class="form-text">File name: {manualFileStem(manual)}</p>
								{/if}
								<button type="submit" class="btn btn-primary" {disabled}>Add manually</button>
							</form>
						{/if}
					</div>
				{/if}
			</div>
		</div>

		<ConfirmModal
			bind:open={confirmDismiss}
			title="Dismiss"
			confirmLabel="Dismiss"
			confirmClass="btn-danger"
			{busy}
			onconfirm={dismiss}
		>
			{#if item.archiveEntry}
				The archive itself is left untouched.
			{:else}
				The file moves to the system's <code>_failed</code> folder.
			{/if}
		</ConfirmModal>

		<ReviewDeleteModal bind:open={confirmDelete} id={item.id} ondeleted={deleted} />
	{/if}
</AdminOnly>
