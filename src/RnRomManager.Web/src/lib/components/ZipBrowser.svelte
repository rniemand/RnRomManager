<script lang="ts">
	import { untrack } from 'svelte';
	import type { SvelteSet } from 'svelte/reactivity';
	import { api } from '../api/client';
	import type { GameDetail, ZipEntries } from '../api/types';
	import { formatDateTime, formatSize } from '../format';
	import { ancestors, buildTree, type ZipFolder } from '../zipTree';
	import EmptyState from './EmptyState.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';
	import Loading from './Loading.svelte';

	/**
	 * Z1/Z4: browses the game's ZIP. Loads lazily the first time it's `active`; a replaced `game` object marks the
	 * listing stale (refetched at once while active, else on the next show). Selection and expansion live in the page,
	 * so they survive the page remounting this on a reload; the page resets them for a new game id.
	 */
	interface Props {
		game: GameDetail;
		active: boolean;
		/** Bindable: the selected folder path (`''` = root). */
		selected: string;
		/** The expanded folder paths. */
		expanded: SvelteSet<string>;
	}

	let { game, active, selected = $bindable(), expanded }: Props = $props();

	let data = $state<ZipEntries | null>(null);
	let error = $state<unknown>(null);
	let loadedFor: GameDetail | null = null;
	let controller: AbortController | null = null;

	const tree = $derived(data ? buildTree(data.entries) : null);
	const folder = $derived(tree?.folders.get(selected) ?? tree?.root);

	$effect(() => {
		const current = game;
		if (active && current !== loadedFor) untrack(() => load(current));
	});
	$effect(() => () => controller?.abort());

	function load(current: GameDetail): void {
		// Z1: a different game than the last load expands every folder; a refetch of the same game keeps the toggles.
		const newGame = loadedFor?.id !== current.id;
		loadedFor = current;
		controller?.abort();
		const signal = (controller = new AbortController()).signal;
		error = null;
		data = null;
		api
			.get<ZipEntries>(`/api/games/${encodeURIComponent(current.id)}/zip-entries`, { signal })
			.then(
				(listing) => {
					data = listing;
					const folders = buildTree(listing.entries).folders;
					// After a refetch: keep what still exists, else fall back to the root.
					if (!folders.has(selected)) selected = '';
					for (const path of [...expanded]) if (!folders.has(path)) expanded.delete(path);
					if (newGame) for (const path of folders.keys()) expanded.add(path);
				},
				(e) => {
					if (!signal.aborted) error = e;
				}
			);
	}

	function toggle(path: string): void {
		if (expanded.has(path)) expanded.delete(path);
		else expanded.add(path);
	}

	/** Z1: selecting never changes expansion; only the chevron does. */
	function select(path: string): void {
		selected = path;
	}

	const label = (f: ZipFolder) => (f.path ? f.name : (data?.zipName ?? ''));
	const downloadUrl = (path: string) =>
		`/api/games/${encodeURIComponent(game.id)}/zip-entries/download?path=${encodeURIComponent(path)}`;
</script>

{#snippet node(f: ZipFolder)}
	{@const open = expanded.has(f.path)}
	<li>
		<div
			class="d-flex align-items-center gap-1"
			class:bg-primary-subtle={selected === f.path}
			class:rounded={selected === f.path}
		>
			<button
				type="button"
				class="btn btn-sm btn-link text-body chevron-toggle"
				aria-expanded={open}
				aria-label="{open ? 'Collapse' : 'Expand'} {label(f)}"
				onclick={() => toggle(f.path)}><Icon name="chevron" /></button
			>
			<button
				type="button"
				class="btn btn-sm btn-link text-start text-break text-decoration-none"
				class:fw-semibold={selected === f.path}
				class:text-primary-emphasis={selected === f.path}
				aria-current={selected === f.path ? 'true' : undefined}
				onclick={() => select(f.path)}><Icon name="folder" /> {label(f)}</button
			>
		</div>
		{#if open && f.folders.length}
			<ul class="list-unstyled ps-3 mb-0">
				{#each f.folders as child (child.path)}{@render node(child)}{/each}
			</ul>
		{/if}
	</li>
{/snippet}

{#if error}
	<ErrorAlert {error} onretry={() => load(game)} />
{:else if !tree || !folder || !data}
	<Loading label="Loading ZIP contents…" />
{:else}
	<div class="row g-3">
		<div class="col-12 col-md-4">
			<ul class="list-unstyled mb-0" aria-label="Folders">{@render node(tree.root)}</ul>
		</div>
		<div class="col-12 col-md-8">
			<nav aria-label="Folder path">
				<ol class="breadcrumb mb-2">
					{#each ancestors(folder.path) as path (path)}
						<li class="breadcrumb-item">
							<button
								type="button"
								class="btn btn-link btn-sm p-0 text-break"
								aria-current={path === folder.path ? 'location' : undefined}
								onclick={() => select(path)}
								>{path ? path.slice(path.lastIndexOf('/') + 1) : data.zipName}</button
							>
						</li>
					{/each}
				</ol>
			</nav>
			{#if folder.folders.length + folder.files.length === 0}
				<EmptyState message="This folder is empty." />
			{:else}
				<div class="table-responsive">
					<table class="table table-sm table-striped table-hover align-middle">
						<caption class="visually-hidden">Contents of {label(folder)}</caption>
						<thead class="table-dark">
							<tr>
								<th scope="col">Name</th>
								<th scope="col" class="text-end">Size</th>
								<th scope="col" class="text-end">Compressed</th>
								<th scope="col">Modified</th>
								<th scope="col"><span class="visually-hidden">Download</span></th>
							</tr>
						</thead>
						<tbody>
							{#each folder.folders as child (child.path)}
								<tr>
									<td class="text-truncate name-cell" title={child.name}>
										<button
											type="button"
											class="btn btn-link btn-sm p-0 text-start text-decoration-none"
											onclick={() => select(child.path)}><Icon name="folder" /> {child.name}</button
										>
									</td>
									<td class="text-end text-nowrap">—</td>
									<td class="text-end text-nowrap">—</td>
									<td class="text-nowrap">—</td>
									<td></td>
								</tr>
							{/each}
							{#each folder.files as file (file.entry.path)}
								<tr>
									<td class="text-truncate name-cell" title={file.name}>{file.name}</td>
									<!-- Z2: sizes as the Library card's size badge. -->
									<td class="text-end text-nowrap"
										><span class="badge text-bg-light border"
											><FileSize bytes={file.entry.sizeBytes} /></span
										></td
									>
									<td class="text-end text-nowrap"
										><span class="badge text-bg-light border"
											><FileSize bytes={file.entry.compressedSizeBytes} /></span
										></td
									>
									<td class="text-nowrap">{formatDateTime(file.entry.modifiedUtc)}</td>
									<td class="text-end">
										<!-- An API download, not an app route. -->
										<!-- eslint-disable svelte/no-navigation-without-resolve -->
										<a
											class="btn btn-sm btn-outline-primary"
											href={downloadUrl(file.entry.path)}
											download
											aria-label="Download {file.name}"><Icon name="download" /></a
										>
										<!-- eslint-enable svelte/no-navigation-without-resolve -->
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
			<p class="small text-body-secondary mb-0">
				{tree.fileCount}
				{tree.fileCount === 1 ? 'file' : 'files'} · {formatSize(tree.totalSize)} ({formatSize(
					tree.totalCompressed
				)} compressed)
			</p>
		</div>
	</div>
{/if}

<style>
	.name-cell {
		max-width: 32rem;
	}
</style>
