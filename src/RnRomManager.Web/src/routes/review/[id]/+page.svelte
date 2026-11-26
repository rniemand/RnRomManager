<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import DismissReviewModal from '#lib/components/modals/DismissReviewModal.svelte';
	import NavTabs from '#lib/components/layout/NavTabs.svelte';
	import { onMount, untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type {
		DatSearchResult,
		GameListItem,
		ReviewDetail,
		ManualErrors,
		ManualForm
	} from '#lib/types/index.js';
	import type { AttachFlag } from '#lib/enums/index.js';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import JobStepProgress from '#lib/components/common/JobStepProgress.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import ReviewDeleteModal from '#lib/components/modals/ReviewDeleteModal.svelte';
	import ReviewAttachTab from '#lib/components/review/ReviewAttachTab.svelte';
	import ReviewManualTab from '#lib/components/review/ReviewManualTab.svelte';
	import ReviewHeader from '#lib/components/review/ReviewHeader.svelte';
	import ReviewItemInfo from '#lib/components/review/ReviewItemInfo.svelte';
	import ReviewMatchedTab from '#lib/components/review/ReviewMatchedTab.svelte';
	import ReviewSuggestionsTab from '#lib/components/review/ReviewSuggestionsTab.svelte';
	import { formatCount } from '#lib/format.js';
	import { navCounts } from '#lib/stores/navCounts.svelte.js';
	import {
		discError,
		emptyManualForm,
		titleFromFileName,
		toManualRequest,
		validateManual
	} from '#lib/reviewForm.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { reviewJobs } from '#lib/stores/reviewJobs.svelte.js';
	import { deleteReviewItem, nextReviewItem, toastDeleteFailed } from '#lib/review.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

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
	/** B21: bumped on every item change, so a DAT search for the previous item is ignored. */
	let searchSession = 0;

	let attachTarget = $state<GameListItem | null>(null);
	let attachTargetError = $state<string | undefined>();
	let attachFlag = $state<AttachFlag>('Release');
	let attachDisc = $state<number | null>(null);
	let attachDiscError = $state<string | undefined>();
	// B16: Disc only on a disc system; DLC only on a titledb (Switch) system.
	const attachFlags = $derived<readonly AttachFlag[]>([
		'Release',
		'Hack',
		'Translated',
		...(item?.fileSets ? (['Disc'] as const) : []),
		...(item?.addOns ? (['DLC'] as const) : [])
	]);

	let manual = $state<ManualForm>(emptyManualForm());
	let manualErrors = $state<ManualErrors>({});

	let confirmDismiss = $state(false);
	let confirmDelete = $state(false);

	type Tab = 'suggestions' | 'matched' | 'attach' | 'manual';
	/** U5: each tab's icon, before its label. */
	const tabIcons: Record<Tab, string> = {
		suggestions: 'review-suggestions',
		matched: 'review-matched',
		attach: 'review-attach',
		manual: 'edit'
	};
	const tabLabels: Record<Tab, string> = {
		suggestions: 'Suggestions',
		matched: 'Matched DAT entry',
		attach: 'Attach',
		manual: 'Manual'
	};
	// AmbiguousGroup items can only be attached to a candidate; the other actions need NoDatMatch.
	const tabs = $derived<Tab[]>(
		item?.reason === 'AmbiguousGroup'
			? ['matched']
			: item?.hasDat === false
				? ['attach', 'manual'] // MS-DOS N6: nothing to suggest or search
				: ['suggestions', 'attach', 'manual']
	);
	let chosenTab = $state<Tab | null>(null);
	// Default: Suggestions when there are some, else Manual (nothing close matched).
	const tab = $derived<Tab>(
		chosenTab && tabs.includes(chosenTab)
			? chosenTab
			: item?.reason === 'AmbiguousGroup'
				? 'matched'
				: item?.hasDat !== false && item?.suggestions?.length
					? 'suggestions'
					: 'manual'
	);

	const id = $derived(page.params.id ?? '');
	const base = $derived(`/api/review/${encodeURIComponent(id)}`);

	/** MS-DOS N7: the item id whose Manual title was prefilled, so a reload of the same item keeps the Admin's edits. */
	let prefilledFor: number | null = null;

	$effect(() => {
		if (!auth.isAdmin) return;
		const url = base;
		void reload;
		const controller = new AbortController();
		error = null;
		notFound = false;
		item = null;
		api.get<ReviewDetail>(url, { signal: controller.signal }).then(
			(i) => {
				item = i;
				if (i.hasDat === false && prefilledFor !== i.id) {
					manual.title = titleFromFileName(i.originalFileName);
					prefilledFor = i.id;
				}
			},
			(e) => {
				if (controller.signal.aborted) return;
				if (isApiError(e) && e.status === 404) notFound = true;
				else error = e;
			}
		);
		return () => controller.abort();
	});

	/** B21: moving to another item in place starts every per-item value as on a fresh load. */
	$effect(() => {
		void id;
		untrack(() => {
			updateChoice = null;
			chosenTab = null;
			searchText = '';
			searchResults = null;
			searching = false;
			searchSession++;
			attachTarget = null;
			attachTargetError = undefined;
			attachFlag = 'Release';
			attachDisc = null;
			attachDiscError = undefined;
			manual = emptyManualForm();
			manualErrors = {};
			actionError = null;
			confirmDismiss = false;
			confirmDelete = false;
		});
	});

	/** B11/B12: the next queue item for Use and Next: `loading`, its id, `none` or `failed`. */
	let next = $state<number | 'loading' | 'none' | 'failed'>('loading');

	$effect(() => {
		if (!auth.isAdmin) return;
		const current = Number(id);
		const controller = new AbortController();
		next = 'loading';
		nextReviewItem(current, controller.signal).then(
			(found) => {
				if (!controller.signal.aborted) next = found ?? 'none';
			},
			() => {
				if (!controller.signal.aborted) next = 'failed';
			}
		);
		return () => controller.abort();
	});

	/** B7: starts a resolution job; the store reports how it ended. B10: `andNext` marks a Use and Next job. */
	async function resolveWith(body: object, done: string, andNext = false): Promise<void> {
		if (disabled || !item) return;
		busy = true;
		actionError = null;
		try {
			const { jobId } = await api.post<{ jobId: string }>(`${base}/resolve`, body);
			reviewJobs.start({
				jobId,
				reviewId: item.id,
				fileName: item.originalFileName,
				done,
				...(andNext ? { next: true as const } : {})
			});
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
			manualErrors.disc = first('disc');
			attachDiscError = first('disc');
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

	function useDat(datGameName: string, andNext = false): Promise<void> {
		const update = titleDb && asUpdate;
		return resolveWith(
			titleDb
				? { action: 'Dat', datSystem: item!.system, datGameName, update }
				: { action: 'Dat', datSystem: item!.system, datGameName },
			update
				? `Added update of ${datGameName}.`
				: titleDb
					? `Added release of ${datGameName}.`
					: `Added as an unverified release of ${datGameName}.`,
			andNext
		);
	}

	async function search(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		const q = searchText.trim();
		if (!q || !item) return;
		const session = searchSession;
		searching = true;
		try {
			const params = new URLSearchParams({ system: item.system, q, limit: '20' });
			const results = await api.get<DatSearchResult[]>(`/api/dat/search?${params}`);
			if (session === searchSession) searchResults = results;
		} catch (e) {
			if (session === searchSession) handle(e);
		} finally {
			if (session === searchSession) searching = false;
		}
	}

	function attach(event: SubmitEvent): void {
		event.preventDefault();
		attachTargetError = attachTarget ? undefined : 'Choose the game to attach to.';
		attachDiscError = attachFlag === 'Disc' ? discError(attachDisc, true) : undefined;
		if (!attachTarget || attachDiscError) return;
		void resolveWith(
			attachFlag === 'Disc'
				? { action: 'Attach', targetGameId: attachTarget.id, flag: 'Disc', disc: attachDisc }
				: { action: 'Attach', targetGameId: attachTarget.id, flag: attachFlag },
			attachFlag === 'Disc'
				? `Attached to ${attachTarget.displayTitle} as disc ${attachDisc}.`
				: attachFlag === 'DLC'
					? `Attached to ${attachTarget.displayTitle} as DLC.`
					: `Attached to ${attachTarget.displayTitle}.`
		);
	}

	function submitManual(event: SubmitEvent | null, andNext = false): void {
		event?.preventDefault();
		manualErrors = validateManual(manual);
		if (Object.keys(manualErrors).length) return;
		void resolveWith(toManualRequest(manual), `Added as ${manual.title.trim()}.`, andNext);
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
	<p>
		<Button variant="primary" outline size="sm" icon="back" href={resolve('/review')}
			>Review queue</Button
		>
	</p>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
	{:else if notFound}
		<EmptyState icon="not-found" message="This review item doesn't exist any more.">
			{#snippet action()}
				<Button variant="primary" href={resolve('/review')}>Back to the queue</Button>
			{/snippet}
		</EmptyState>
	{:else if !item}
		<Loading />
	{:else}
		<ReviewHeader
			{item}
			{disabled}
			ondismiss={() => (confirmDismiss = true)}
			ondelete={askDelete}
		/>
		{#if navCounts.review !== null}
			<!-- U3: the pending count; the layout refreshes it on every navigation and job end. -->
			<p class="small text-body-secondary mb-3">
				{navCounts.review === 0
					? 'No items left'
					: `${formatCount(navCounts.review)} ${navCounts.review === 1 ? 'item' : 'items'} to go`}
			</p>
		{/if}

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
				<Button class="btn-close" aria-label="Dismiss" onclick={() => (actionError = null)} />
			</div>
		{/if}

		<div class="row g-4">
			<div class="col-12 col-lg-5">
				<ReviewItemInfo {item} />
			</div>

			<div class="col-12 col-lg-7">
				{#if !item.resolvable}
					<!-- B15: a disc track file can't be resolved or dismissed, only deleted. -->
					<p class="text-body-secondary">
						Disc track files must match the Redump DAT to be imported.
					</p>
				{:else}
					<NavTabs
						tabs={tabs.map((t) => ({
							id: t,
							label: tabLabels[t],
							icon: tabIcons[t],
							tabId: `tab-${t}`,
							controls: `panel-${t}`
						}))}
						active={tab}
						onselect={(t) => (chosenTab = t)}
						class="mb-3"
					/>

					<div id="panel-{tab}" role="tabpanel" aria-labelledby="tab-{tab}">
						{#if tab === 'matched'}
							<ReviewMatchedTab
								{item}
								{disabled}
								onattach={(game) =>
									resolveWith(
										{ action: 'Attach', targetGameId: game.id, flag: 'None' },
										`Attached to ${game.displayTitle}.`
									)}
							/>
						{:else if tab === 'suggestions'}
							<ReviewSuggestionsTab
								{item}
								{titleDb}
								{asUpdate}
								{disabled}
								{next}
								bind:searchText
								{searchResults}
								{searching}
								onupdatechange={(checked) => (updateChoice = checked)}
								onuse={useDat}
								onsearch={search}
							/>
						{:else if tab === 'attach'}
							<ReviewAttachTab
								{item}
								{attachFlags}
								bind:attachTarget
								{attachTargetError}
								bind:attachFlag
								bind:attachDisc
								{attachDiscError}
								{disabled}
								onsubmit={attach}
							/>
						{:else}
							<ReviewManualTab
								{item}
								bind:manual
								{manualErrors}
								{disabled}
								{next}
								onsubmit={submitManual}
							/>
						{/if}
					</div>
				{/if}
			</div>
		</div>

		<DismissReviewModal
			bind:open={confirmDismiss}
			archiveEntry={!!item.archiveEntry}
			{busy}
			onconfirm={dismiss}
		/>

		<ReviewDeleteModal bind:open={confirmDelete} id={item.id} ondeleted={deleted} />
	{/if}
</AdminOnly>
