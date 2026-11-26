<script lang="ts">
	import { fromAction } from 'svelte/attachments';
	import { untrack } from 'svelte';
	import type { SvelteMap, SvelteSet } from 'svelte/reactivity';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type {
		AdminAction,
		BundleEntry,
		GameDetail,
		ReleaseFile,
		ReleaseNoteState,
		RomDetail
	} from '#lib/types/index.js';
	import type { AddResult } from '#lib/enums/index.js';
	import Button from '#lib/components/forms/Button.svelte';
	import CopyButton from '#lib/components/forms/CopyButton.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import ReleaseDetails from '#lib/components/games/ReleaseDetails.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import {
		canPlay,
		opensInNewTab,
		playLinkEvents,
		playUrl,
		setMedia,
		usesPlayDisc
	} from '#lib/emulator.js';
	import { renderMarkdown } from '#lib/markdown.js';
	import { discNumber } from '#lib/releases.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { bundle, maxBundleEntries } from '#lib/stores/bundle.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { systemColour } from '#lib/systemColours.js';
	import { tooltip } from '#lib/tooltip.js';

	interface Props {
		game: GameDetail;
		/** The route's game id, for the note URL. */
		gameId: string;
		/** The page's tab: `releases`, `updates` or `dlc` while this panel shows. */
		tab: string;
		/** B10 (DLC B4): the Releases, Updates or DLC cards. */
		roms: RomDetail[];
		releaseCount: number;
		/** R1–R4: open cards by file name; the page keeps them across reloads and clears them for a new game. */
		openReleases: SvelteSet<string>;
		/** B20: open file lists, by file name like openReleases. */
		openFiles: SvelteSet<string>;
		/** B7: each open card's rendered note, by file name, loaded on first expand. */
		notes: SvelteMap<string, ReleaseNoteState>;
		/** File names whose note was just saved: their ids are stale until the page's refetch lands. */
		pendingNotes: SvelteSet<string>;
		ondownload: (url: string, rom: RomDetail) => unknown;
		onnote: (rom: RomDetail, mode?: 'edit') => void;
		onheader: (rom: RomDetail) => void;
		/** `discSet` is the set's playlist name for All discs, else null. */
		onplay: (rom: RomDetail, discSet: string | null) => void;
		onedit: (rom: RomDetail) => void;
		onaction: (action: AdminAction) => void;
		onrefresh: () => Promise<void>;
	}

	let {
		game,
		gameId,
		tab,
		roms,
		releaseCount,
		openReleases,
		openFiles,
		notes,
		pendingNotes,
		ondownload,
		onnote,
		onheader,
		onplay,
		onedit,
		onaction,
		onrefresh
	}: Props = $props();

	async function loadNote(rom: RomDetail): Promise<void> {
		const fileName = rom.fileName;
		notes.set(fileName, { loading: true });
		try {
			const text = await api.getText(
				`/api/games/${encodeURIComponent(gameId)}/roms/${rom.id}/note`
			);
			notes.set(fileName, { text });
		} catch (e) {
			if (isApiError(e) && e.status === 404) {
				// Deleted meanwhile, as the modal treats it: the block goes and the game is refetched.
				notes.delete(fileName);
				rom.hasNote = false;
				await onrefresh();
			} else notes.set(fileName, { error: e });
		}
	}

	$effect(() => {
		for (const rom of roms)
			if (
				rom.hasNote &&
				openReleases.has(rom.fileName) &&
				!notes.has(rom.fileName) &&
				!pendingNotes.has(rom.fileName)
			)
				untrack(() => void loadNote(rom));
	});

	const allReleasesOpen = $derived.by(() => roms.every((r) => openReleases.has(r.fileName)));

	function toggleRelease(fileName: string): void {
		if (openReleases.has(fileName)) openReleases.delete(fileName);
		else openReleases.add(fileName);
	}

	/** R2: empty header space toggles; its buttons and links don't, nor does ending a text selection (copying the name). */
	function onReleaseHeaderClick(event: MouseEvent, fileName: string): void {
		if ((event.target as Element).closest('button, a')) return;
		if (getSelection()?.toString()) return;
		toggleRelease(fileName);
	}

	function toggleAllReleases(): void {
		if (allReleasesOpen) openReleases.clear();
		else roms.forEach((r) => openReleases.add(r.fileName));
	}

	/** Bundle Creator B1/B3: a cart entry from the rendered metadata; `file` names a set file, else the main file. */
	function bundleEntry(g: GameDetail, rom: RomDetail, file?: ReleaseFile): BundleEntry {
		return {
			gameId: g.id,
			releaseSha1: rom.sha1,
			member: file ? { sha1: file.sha1, fileName: file.fileName } : null,
			gameTitle: g.displayTitle,
			fileName: file?.fileName ?? rom.fileName
		};
	}

	const bundleMessages: Record<AddResult, [string, 'success' | 'info' | 'warning' | 'danger']> = {
		added: ['Added {file} to the bundle.', 'success'],
		duplicate: ['{file} is already in the bundle.', 'info'],
		full: [`The bundle holds at most ${maxBundleEntries} files.`, 'warning'],
		signedOut: ['Sign in to use the bundle.', 'warning'],
		invalid: ["{file} can't be added to the bundle.", 'danger']
	};

	function addToBundle(entry: BundleEntry): void {
		const [text, kind] = bundleMessages[bundle.add(entry)];
		toasts.show(text.replace('{file}', entry.fileName), kind);
	}
</script>

<!-- Bundle Creator B1: adds one file to the working cart; shows a check once it's there. -->
{#snippet bundleButton(entry: BundleEntry)}
	{@const inCart = bundle.has(entry)}
	<Button
		icon={inCart ? 'cart-check' : 'cart-add'}
		class="btn btn-sm {inCart ? 'btn-primary' : 'btn-outline-primary'}"
		aria-label={inCart
			? `${entry.fileName} is in the bundle`
			: `Add ${entry.fileName} to the bundle`}
		title={inCart ? 'In the bundle' : 'Add to bundle'}
		onclick={() => addToBundle(entry)}
	/>
{/snippet}

<h2 class="h5 visually-hidden">
	{tab === 'updates' ? 'Updates' : tab === 'dlc' ? 'DLC' : 'Releases'}
</h2>
{#if tab === 'releases' && !releaseCount}
	<p class="text-body-secondary">No releases other than updates and DLC.</p>
{/if}
{#if roms.length >= 2}
	<Button variant="secondary" outline size="sm" class="mb-2" onclick={toggleAllReleases}
		>{allReleasesOpen ? 'Collapse all' : 'Expand all'}</Button
	>
{/if}
<!-- G2: one card per release, so nothing scrolls sideways on a phone. -->
<div class="d-flex flex-column gap-3 mb-4">
	{#each roms as rom (rom.id)}
		{@const open = openReleases.has(rom.fileName)}
		<!-- R1: a stripe in the system's colour and a tinted header mark where each release starts. -->
		<article
			class="card"
			style="border-left: 6px solid var(--bs-{systemColour(game.system).name})"
			aria-labelledby="release-{rom.id}-name"
		>
			<!-- The chevron button is the keyboard path; a click on empty header space is a mouse shortcut. -->
			<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
			<div
				class="card-header bg-body-tertiary release-header"
				onclick={(e) => onReleaseHeaderClick(e, rom.fileName)}
			>
				<div class="d-flex flex-wrap align-items-center gap-2">
					<!-- B6: chevron and name on the left; min-width 0 lets a long name wrap. -->
					<div class="d-flex align-items-center gap-2 min-w-0">
						<Button
							variant="link"
							size="sm"
							class="p-0 text-body chevron-toggle"
							icon="chevron"
							aria-expanded={open}
							aria-controls="release-{rom.id}-details"
							aria-label="{open ? 'Hide' : 'Show'} details for {rom.fileName}"
							onclick={() => toggleRelease(rom.fileName)}
						/>
						<h3 class="h6 mb-0 text-break" id="release-{rom.id}-name">{rom.fileName}</h3>
					</div>
					<!-- B6: badges, then buttons, on the right. -->
					<div class="ms-auto d-flex flex-wrap gap-2 align-items-center justify-content-end">
						{#if rom.isPrimary}<span class="badge text-bg-primary">Primary</span>{/if}
						{#if rom.version}<span class="badge text-bg-info">Version {rom.version}</span>{/if}
						{#if discNumber(rom) !== null}<span class="badge text-bg-info"
								>Disc {discNumber(rom)}</span
							>{/if}
						{#if rom.externalFileName}
							<!-- G7: stored next to the ZIP under this name. -->
							<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
							<span class="badge text-bg-dark" tabindex="0" use:tooltip={rom.externalFileName}
								>External</span
							>
						{/if}
						{#each rom.regions as region (region)}
							<span class="badge text-bg-secondary">{region}</span>
						{/each}
						<!-- R4: a filled button when the release has a note; Site users only view notes (U3). -->
						{#if auth.isAdmin || rom.hasNote}<Button
								icon={rom.hasNote ? 'note-filled' : 'note'}
								class="btn btn-sm {rom.hasNote ? 'btn-warning' : 'btn-outline-secondary'}"
								aria-label="{rom.hasNote ? 'Note for' : 'Add note for'} {rom.fileName}"
								{@attach fromAction(tooltip, () => (rom.hasNote ? 'View note' : 'Add note'))}
								onclick={() => onnote(rom)}
							/>{/if}
						{#if canPlay(game, rom) && game.browserCore && opensInNewTab(game.browserCore)}
							<!-- PSP B3: the isolated player runs in its own tab; a static page outside the SPA router. -->
							<!-- eslint-disable svelte/no-navigation-without-resolve -->
							<Button
								variant="success"
								outline
								size="sm"
								href={playUrl(game.id, rom, game.browserCore, usesPlayDisc(game.browserCore, rom))}
								target="_blank"
								rel="noopener"
								aria-label="Play {rom.fileName} (opens in a new tab)"
								{...playLinkEvents(game.id, rom.id)}
								><!-- eslint-enable svelte/no-navigation-without-resolve -->
								<Icon name="play" /></Button
							>
						{:else if canPlay(game, rom)}
							<Button
								variant="success"
								outline
								size="sm"
								icon="play"
								aria-label="Play {rom.fileName}"
								onclick={() => onplay(rom, null)}
							/>
							{#if rom.discSet}
								{@const discSet = rom.discSet}
								{@const media = setMedia(game.browserCore)}
								<!-- Multi-disc M7 (Amiga F8: disks): every disc of the set in one session, from Disc 1's card. -->
								<Button
									variant="success"
									outline
									size="sm"
									icon="play"
									aria-label="Play all {media} of {discSet.name}"
									{@attach fromAction(tooltip, () => `Play all ${media} in one session`)}
									onclick={() => onplay(rom, discSet.name)}>All {media}</Button
								>
							{/if}
						{/if}
						<Button
							variant="primary"
							outline
							size="sm"
							icon="download"
							aria-label="Download {rom.fileName}"
							onclick={() => ondownload(rom.downloadUrl, rom)}
						/>
						{@render bundleButton(bundleEntry(game, rom))}
					</div>
				</div>
			</div>
			<div class="card-body small" id="release-{rom.id}-details" hidden={!open}>
				{#if rom.hasNote}
					{@const note = notes.get(rom.fileName)}
					<!-- B7: the note comes first; a non-interactive block (its Markdown links work) beside an Edit/View button. -->
					<div class="border rounded p-2 mb-3" data-testid="release-note">
						<div class="d-flex align-items-center gap-2 mb-2">
							<h4 class="h6 mb-0">Release note</h4>
							<Button
								variant="secondary"
								outline
								size="sm"
								class="ms-auto"
								icon={auth.isAdmin ? 'edit' : 'note-filled'}
								aria-label="{auth.isAdmin ? 'Edit' : 'View'} note for {rom.fileName}"
								onclick={() => onnote(rom, auth.isAdmin ? 'edit' : undefined)}
								>{auth.isAdmin ? 'Edit note' : 'View note'}</Button
							>
						</div>
						{#if note && 'error' in note}
							<ErrorAlert error={note.error} onretry={() => loadNote(rom)} />
						{:else if note && 'text' in note}
							<div class="note-markdown">
								<!-- eslint-disable-next-line svelte/no-at-html-tags -- sanitised by DOMPurify in renderMarkdown -->
								{@html renderMarkdown(note.text)}
							</div>
						{:else}
							<Loading label="Loading note…" />
						{/if}
					</div>
				{/if}
				<ReleaseDetails {rom} />
				{#if rom.header || auth.isAdmin}
					<div class="d-flex flex-wrap gap-2 mt-2">
						{#if rom.header}
							<Button
								variant="secondary"
								outline
								size="sm"
								icon="info"
								onclick={() => onheader(rom)}>Header details</Button
							>
						{/if}
						{#if auth.isAdmin}
							<!-- B9: Header details stays left; Move and Delete release sit right. -->
							<div class="ms-auto d-flex flex-wrap gap-2">
								<Button
									variant="secondary"
									outline
									size="sm"
									icon="edit"
									aria-label="Edit release {rom.fileName}"
									onclick={() => onedit(rom)}>Edit release</Button
								>
								{#if !rom.isPrimary}
									<Button
										variant="secondary"
										outline
										size="sm"
										icon="star"
										aria-label="Make primary {rom.fileName}"
										onclick={() => onaction({ kind: 'primary', rom })}>Make primary</Button
									>
								{/if}
								<Button
									variant="secondary"
									outline
									size="sm"
									icon="move"
									aria-label="Move release {rom.fileName}"
									onclick={() => onaction({ kind: 'move', rom })}>Move release</Button
								>
								<Button
									variant="danger"
									outline
									size="sm"
									icon="delete"
									aria-label="Delete release {rom.fileName}"
									onclick={() => onaction({ kind: 'deleteRom', rom })}>Delete release</Button
								>
							</div>
						{/if}
					</div>
				{/if}
				{#if rom.files?.length}
					{@const filesOpen = openFiles.has(rom.fileName)}
					<!-- B20: the main file and the set's other files (cue, tracks); N counts all of them. -->
					<Button
						variant="secondary"
						outline
						size="sm"
						class="mt-2"
						icon="chevron"
						aria-expanded={filesOpen}
						aria-controls="release-{rom.id}-files"
						onclick={() =>
							filesOpen ? openFiles.delete(rom.fileName) : openFiles.add(rom.fileName)}
						>{rom.files.length + 1} files</Button
					>
					<div class="card mt-2" id="release-{rom.id}-files" hidden={!filesOpen}>
						<div class="table-responsive">
							<table class="table table-sm table-striped table-hover align-middle mb-0">
								<caption class="visually-hidden">Files of {rom.fileName}</caption>
								<thead class="table-dark">
									<tr>
										<th scope="col">File</th>
										<th scope="col" class="text-end">Size</th>
										<th scope="col">SHA1</th>
										<th scope="col"><span class="visually-hidden">Actions</span></th>
									</tr>
								</thead>
								<tbody>
									{#each [{ fileName: rom.fileName, sizeBytes: rom.sizeBytes, sha1: rom.sha1, downloadUrl: rom.downloadUrl }, ...rom.files] as file, index (file.fileName)}
										<tr>
											<td class="text-truncate file-name" title={file.fileName}>{file.fileName}</td>
											<td class="text-end text-nowrap"><FileSize bytes={file.sizeBytes} /></td>
											<td class="text-nowrap">
												<span class="font-monospace">{file.sha1}</span>
												<CopyButton value={file.sha1} label="SHA1 of {file.fileName}" />
											</td>
											<td class="text-end">
												<Button
													variant="primary"
													outline
													size="sm"
													icon="download"
													aria-label="Download {file.fileName}"
													onclick={() => ondownload(file.downloadUrl, rom)}
												/>
												<!-- Row 0 is the main file; the rest are separately downloadable set files (B1). -->
												{@render bundleButton(
													bundleEntry(game, rom, index === 0 ? undefined : rom.files[index - 1])
												)}
											</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					</div>
				{/if}
			</div>
		</article>
	{/each}
</div>

<style>
	.file-name {
		max-width: 32rem;
	}
</style>
