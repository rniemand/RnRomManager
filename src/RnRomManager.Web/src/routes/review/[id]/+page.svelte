<script lang="ts">
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
	import GameTypeahead from '#lib/components/GameTypeahead.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { formatKey, formatSize, formatValue } from '#lib/format.js';
	import {
		emptyManualForm,
		manualFileStem,
		toManualRequest,
		validateManual,
		type ManualErrors,
		type ManualForm
	} from '#lib/reviewForm.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	let item = $state<ReviewDetail | null>(null);
	let notFound = $state(false);
	let error = $state<unknown>(null);
	let reload = $state(0);

	let busy = $state(false);
	let actionError = $state<string | null>(null);

	let searchText = $state('');
	let searchResults = $state<DatSearchResult[] | null>(null);
	let searching = $state(false);

	let attachTarget = $state<GameListItem | null>(null);
	let attachTargetError = $state<string | undefined>();
	let attachFlag = $state<'Hack' | 'Translated'>('Hack');

	let manual = $state<ManualForm>(emptyManualForm());
	let manualErrors = $state<ManualErrors>({});

	let confirmDismiss = $state(false);

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

	/** Sends a resolution; on success goes back to the queue. */
	async function resolveWith(body: object, done: string): Promise<void> {
		if (busy) return;
		busy = true;
		actionError = null;
		try {
			const result = await api.post<{ gameId: string }>(`${base}/resolve`, body);
			toasts.show(done, 'success');
			await goto(
				result?.gameId ? resolve('/games/[id]', { id: result.gameId }) : resolve('/review')
			);
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

	function useDat(datGameName: string): Promise<void> {
		return resolveWith(
			{ action: 'Dat', datSystem: item!.system, datGameName },
			`Added as an unverified release of ${datGameName}.`
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
		<h1 class="h4 text-break">{item.originalFileName}</h1>
		<p class="text-body-secondary">
			{item.system} · {formatSize(item.sizeBytes)} · {item.reason}{item.status !== 'Pending'
				? ` · ${item.status}`
				: ''}
		</p>

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

		<div class="row g-4 mb-4">
			<div class="col-12 col-lg-6">
				<h2 class="h5">File</h2>
				<dl class="row small mb-0">
					<dt class="col-4">Source</dt>
					<dd class="col-8 text-break">
						{item.archiveEntry ? `${item.archivePath} › ${item.archiveEntry}` : item.sourcePath}
					</dd>
					{#each [['CRC32', item.crc32], ['MD5', item.md5], ['SHA1', item.sha1]] as [label, value] (label)}
						<dt class="col-4">{label}</dt>
						<dd class="col-8 font-monospace text-break">
							{value}
							<CopyButton {value} {label} />
						</dd>
					{/each}
				</dl>
			</div>
			<div class="col-12 col-lg-6">
				<h2 class="h5">Header</h2>
				{#if item.header}
					<table class="table table-sm small">
						<tbody>
							{#each Object.entries(item.header) as [key, value] (key)}
								<tr>
									<th scope="row" class="fw-normal text-body-secondary">{formatKey(key)}</th>
									<td>{formatValue(value)}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				{:else}
					<p class="text-body-secondary">The header couldn't be read.</p>
				{/if}
			</div>
		</div>

		{#if item.reason === 'AmbiguousGroup'}
			<section class="mb-4" aria-labelledby="ambiguous-heading">
				<h2 class="h5" id="ambiguous-heading">Matched DAT entry</h2>
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
								disabled={busy}
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
			</section>
		{:else}
			<section class="mb-4" aria-labelledby="suggestions-heading">
				<h2 class="h5" id="suggestions-heading">Suggestions</h2>
				<p class="small text-body-secondary">
					Picking a DAT entry adds the file as an unverified release, because its hashes differ.
				</p>
				{#if item.suggestions?.length}
					<ul class="list-group mb-3">
						{#each item.suggestions as suggestion (suggestion.romName)}
							<li class="list-group-item d-flex align-items-center gap-2">
								<span class="me-auto text-break">{suggestion.romName}</span>
								<span class="badge text-bg-secondary">{percent(suggestion.score)}</span>
								<button
									type="button"
									class="btn btn-sm btn-primary"
									disabled={busy}
									onclick={() => useDat(suggestion.datGameName)}>Use</button
								>
							</li>
						{/each}
					</ul>
				{:else}
					<p class="text-body-secondary">No close DAT matches.</p>
				{/if}

				<form class="row g-2 align-items-end" role="search" onsubmit={search}>
					<div class="col">
						<label class="form-label" for="dat-search">Search the {item.system} DAT</label>
						<input id="dat-search" type="search" class="form-control" bind:value={searchText} />
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
									<span class="small text-body-secondary text-nowrap"
										>{formatSize(result.sizeBytes)}</span
									>
									<button
										type="button"
										class="btn btn-sm btn-primary"
										disabled={busy}
										onclick={() => useDat(result.gameName)}>Use</button
									>
								</li>
							{/each}
						</ul>
					{:else}
						<p class="text-body-secondary mt-2">No DAT entries match.</p>
					{/if}
				{/if}
			</section>

			<section class="mb-4" aria-labelledby="attach-heading">
				<h2 class="h5" id="attach-heading">Attach to a game as a hack or translation</h2>
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
					<button type="submit" class="btn btn-primary" disabled={busy}>Attach</button>
				</form>
			</section>

			<section class="mb-4" aria-labelledby="manual-heading">
				<h2 class="h5" id="manual-heading">Manual entry</h2>
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
						{#if manualErrors.title}<div class="invalid-feedback">{manualErrors.title}</div>{/if}
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
					<button type="submit" class="btn btn-primary" disabled={busy}>Add manually</button>
				</form>
			</section>
		{/if}

		<button
			type="button"
			class="btn btn-outline-danger"
			disabled={busy}
			onclick={() => (confirmDismiss = true)}>Dismiss</button
		>

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
	{/if}
</AdminOnly>
