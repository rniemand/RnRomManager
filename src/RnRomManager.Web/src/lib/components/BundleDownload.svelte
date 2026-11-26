<script lang="ts">
	import { onDestroy } from 'svelte';
	import { resolve } from '$app/paths';
	import { BundleDownloadRun, runItemLabel } from '../bundleDownload';
	import type { RunView } from '#lib/types/index.js';
	import type { PauseReason } from '#lib/enums/index.js';
	import { formatCount } from '../format';
	import { bundle } from '../stores/bundle.svelte';
	import Icon from '#lib/icons/Icon.svelte';

	interface Props {
		/** The runner; tests pass one with a fake server. */
		run?: BundleDownloadRun;
	}

	let { run = new BundleDownloadRun() }: Props = $props();

	let view = $state<RunView>({
		status: 'idle',
		pause: null,
		stopRequested: false,
		canRetry: false,
		items: []
	});
	/** The account the current run belongs to; a different account (or none) ends it (B7). */
	let owner: number | null = null;

	// svelte-ignore state_referenced_locally
	const unsubscribe = run.subscribe((v) => (view = v));
	// svelte-ignore state_referenced_locally
	run.onfailure = () => void bundle.refresh();

	$effect(() => {
		const userId = bundle.userId;
		if (owner !== null && userId !== owner) {
			run.end();
			owner = null;
		}
	});

	onDestroy(() => {
		run.end();
		run.onfailure = null;
		unsubscribe();
	});

	const busy = $derived(view.status === 'running' || view.status === 'paused');
	const sent = $derived(view.items.filter((i) => i.state === 'sent').length);
	const failed = $derived(
		view.items.filter((i) => i.state === 'failed' || i.state === 'expired').length
	);
	const current = $derived(
		view.status === 'running'
			? view.items.find((i) => ['issuing', 'pending', 'sending'].includes(i.state))
			: undefined
	);
	const progress = $derived(view.items.length ? Math.round((sent / view.items.length) * 100) : 0);

	/** B6: manual links for files of the run the server hasn't sent yet, with the cart's current links. */
	const manual = $derived.by(() => {
		if (view.status === 'idle' || view.status === 'running') return [];
		const links = new Map(bundle.items.map((i) => [i.key, i.downloadUrl]));
		return view.items
			.filter((i) => i.state !== 'sent' && links.get(i.key))
			.map((i) => ({ key: i.key, fileName: i.entry.fileName, href: links.get(i.key)! }));
	});

	const pauseText: Record<PauseReason, string> = {
		blocked:
			"The browser didn't start the last download. Allow this site to download multiple files automatically (in Chrome: the download icon in the address bar, or Site settings → Automatic downloads), then retry.",
		network: 'The server could not be reached. Check your connection, then retry.',
		signIn: 'Your session has ended. Sign in again, then start the queue again.',
		busy: 'Another download for your account is still running, perhaps in another tab. Retry when it has finished.',
		full: 'The server is sending too many downloads right now. Retry in a moment.',
		unknown:
			'The server lost track of the last file, so it may or may not have been sent. Send it again, or skip it.'
	};

	function startRun(): void {
		owner = bundle.userId;
		if (owner === null) return;
		run.start(bundle.entries);
	}

	const signInHref = $derived(
		`${resolve('/login')}?returnUrl=${encodeURIComponent(resolve('/bundle-creator'))}`
	);
</script>

<section class="card" aria-labelledby="bundle-download-heading">
	<div class="card-body">
		<div class="d-flex flex-wrap align-items-center gap-2 mb-2">
			<h2 id="bundle-download-heading" class="h5 mb-0 me-auto">Download</h2>
			{#if view.status === 'running'}
				<button
					type="button"
					class="btn btn-sm btn-outline-secondary"
					disabled={view.stopRequested}
					onclick={() => run.stopAfterCurrent()}
				>
					Stop after current file
				</button>
			{:else if view.status === 'paused'}
				<button
					type="button"
					class="btn btn-sm btn-outline-secondary"
					onclick={() => run.stopAfterCurrent()}
				>
					Stop
				</button>
			{/if}
			{#if view.canRetry && view.pause !== 'signIn'}
				<button type="button" class="btn btn-sm btn-outline-primary" onclick={() => run.retry()}>
					<Icon name="retry" />
					{view.pause === 'unknown' ? 'Send it again' : 'Retry'}
				</button>
			{/if}
			{#if view.pause === 'unknown'}
				<button
					type="button"
					class="btn btn-sm btn-outline-secondary"
					onclick={() => run.skipUnknown()}
				>
					Skip it
				</button>
			{/if}
			<button
				type="button"
				class="btn btn-sm btn-primary"
				disabled={busy || !bundle.count}
				onclick={startRun}
			>
				<Icon name="download" /> Download queue
			</button>
		</div>

		<p class="text-body-secondary small mb-2">
			Files go to your browser's downloads one at a time, in cart order. Your browser may ask once
			to allow this site to download multiple files. "Sent" means the server finished sending a
			file; your browser decides where it's saved and how files with the same name are handled.
		</p>

		{#if view.pause}
			<div class="alert alert-warning d-flex flex-wrap align-items-center gap-2" role="alert">
				<span class="me-auto">{pauseText[view.pause]}</span>
				{#if view.pause === 'signIn'}
					<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- resolved paths plus a query -->
					<a class="btn btn-sm btn-warning" href={signInHref}>Sign in</a>
				{/if}
			</div>
		{/if}

		{#if view.status === 'ended'}
			<div class="alert alert-secondary" role="status">
				The download stopped. Files the browser had already started may still finish.
			</div>
		{/if}

		{#if view.items.length}
			<div
				class="progress mb-2"
				role="progressbar"
				aria-label="Files sent"
				aria-valuenow={sent}
				aria-valuemin={0}
				aria-valuemax={view.items.length}
			>
				<div class="progress-bar" style="width: {progress}%"></div>
			</div>
			<p class="small mb-2" role="status" aria-live="polite">
				{formatCount(sent)} of {formatCount(view.items.length)} sent{#if failed}, {formatCount(
						failed
					)} failed{/if}.
				{#if current}Now: {current.entry.fileName}.{/if}
				{#if view.status === 'finished'}Done.{:else if view.status === 'stopped'}Stopped.{/if}
				{#if view.stopRequested}Stopping after this file.{/if}
			</p>

			<div class="table-responsive">
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<caption class="visually-hidden">Download progress</caption>
					<thead class="table-dark">
						<tr>
							<th scope="col" class="text-end">#</th>
							<th scope="col">File</th>
							<th scope="col">Status</th>
						</tr>
					</thead>
					<tbody>
						{#each view.items as item, index (item.key)}
							<tr>
								<td class="text-end text-nowrap">{index + 1}</td>
								<td class="text-truncate" style="max-width: 32rem" title={item.entry.fileName}
									>{item.entry.fileName}</td
								>
								<td
									class="text-nowrap"
									class:text-danger-emphasis={item.state === 'failed' || item.state === 'expired'}
									class:text-success-emphasis={item.state === 'sent'}
									class:text-warning-emphasis={item.state === 'unknown'}
								>
									{runItemLabel(item)}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}

		{#if manual.length}
			<details class="mt-3">
				<summary>Download the remaining files manually ({formatCount(manual.length)})</summary>
				<ul class="list-unstyled small mt-2 mb-0">
					{#each manual as link (link.key)}
						<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -->
						<li><a href={link.href} download>{link.fileName}</a></li>
					{/each}
				</ul>
			</details>
		{/if}
	</div>
</section>
