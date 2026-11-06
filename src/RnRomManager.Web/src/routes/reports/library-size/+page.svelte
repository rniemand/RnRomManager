<script lang="ts">
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import { formatSize } from '#lib/format.js';
	import { systemColour } from '#lib/systemColours.js';
	import { squarify } from '#lib/treemap.js';

	/** RP3: the library (or one system, `?system=`) as a treemap of on-disk ZIP sizes, with the table as its text equivalent. */
	interface LibrarySize {
		totalBytes: number;
		gameCount: number;
		systems: { system: string; systemName: string; bytes: number; gameCount: number }[];
	}
	interface SystemSize {
		system: string;
		systemName: string;
		bytes: number;
		games: { id: string; displayTitle: string; bytes: number; romCount: number }[];
	}
	interface Tile {
		key: string;
		label: string;
		value: number;
		system: string;
		href: string | null;
	}

	const width = 1000;
	const height = 500;
	/** At the system level only the largest games are drawn; the rest share one rectangle. */
	const maxGames = 300;
	const base = resolve('/reports/library-size');

	const system = $derived(page.url.searchParams.get('system'));
	let library = $state<LibrarySize | null>(null);
	let detail = $state<SystemSize | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);

	$effect(() => {
		const id = system;
		void reload;
		const controller = new AbortController();
		error = null;
		library = null;
		detail = null;
		const done = (e: unknown) => {
			if (!controller.signal.aborted) error = e;
		};
		if (id)
			api
				.get<SystemSize>(`/api/reports/library-size/${encodeURIComponent(id)}`, {
					signal: controller.signal
				})
				.then((d) => (detail = d), done);
		else
			api
				.get<LibrarySize>('/api/reports/library-size', { signal: controller.signal })
				.then((l) => (library = l), done);
		return () => controller.abort();
	});

	const systemHref = (id: string) => `${base}?system=${encodeURIComponent(id)}`;
	const gameHref = (id: string) => resolve('/games/[id]', { id });
	const percent = (bytes: number, total: number) =>
		total > 0 ? `${((bytes / total) * 100).toFixed(1)}%` : '—';

	const tiles = $derived.by((): Tile[] => {
		if (library)
			return library.systems.map((s) => ({
				key: s.system,
				label: s.systemName,
				value: s.bytes,
				system: s.system,
				href: systemHref(s.system)
			}));
		if (!detail) return [];
		const d = detail;
		const shown: Tile[] = d.games.slice(0, maxGames).map((g) => ({
			key: g.id,
			label: g.displayTitle,
			value: g.bytes,
			system: d.system,
			href: gameHref(g.id)
		}));
		const others = d.games.slice(maxGames);
		if (others.length)
			shown.push({
				key: 'others',
				label: `${others.length} others`,
				value: others.reduce((s, g) => s + g.bytes, 0),
				system: d.system,
				href: null
			});
		return shown;
	});
	const rects = $derived(squarify(tiles, width, height));

	const total = $derived(library?.totalBytes ?? detail?.bytes ?? 0);
	const count = $derived(library?.gameCount ?? detail?.games.length ?? 0);
	const summary = $derived(
		`Treemap of ${detail ? `${detail.systemName} games` : 'systems'} by size: ` +
			tiles
				.slice(0, 5)
				.map((t) => `${t.label} ${formatSize(t.value)}`)
				.join(', ') +
			(tiles.length > 5 ? `, and ${tiles.length - 5} more` : '')
	);

	/** A label only fits a tile of at least 60×20; long names are cut to the width. */
	function label(text: string, w: number): string {
		const max = Math.floor((w - 8) / 7);
		return text.length > max ? `${text.slice(0, Math.max(1, max - 1))}…` : text;
	}
</script>

<svelte:head><title>Library Size · RnRomManager</title></svelte:head>

<!-- eslint-disable svelte/no-navigation-without-resolve -- resolved paths plus a ?system= query -->
<nav aria-label="breadcrumb">
	<ol class="breadcrumb">
		{#if system}
			<li class="breadcrumb-item"><a href={base}>Library</a></li>
			<li class="breadcrumb-item active" aria-current="page">{detail?.systemName ?? system}</li>
		{:else}
			<li class="breadcrumb-item active" aria-current="page">Library</li>
		{/if}
	</ol>
</nav>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !library && !detail}
	<Loading />
{:else if count === 0}
	<div class="card">
		<EmptyState message={system ? 'This system has no games.' : 'The library is empty.'} />
	</div>
{:else}
	<p><FileSize bytes={total} /> in {count} {count === 1 ? 'game' : 'games'}</p>

	<svg
		class="d-block w-100 h-auto mb-3 border rounded"
		viewBox="0 0 {width} {height}"
		role="img"
		aria-label={summary}
	>
		{#each rects as r (r.item.key)}
			{@const colour = systemColour(r.item.system)}
			{#snippet tile()}
				<rect
					x={r.x}
					y={r.y}
					width={r.w}
					height={r.h}
					fill="var(--bs-{colour.name})"
					stroke="var(--bs-body-bg)"
					stroke-width="1"
				/>
				{#if r.w >= 60 && r.h >= 20}
					<text x={r.x + 4} y={r.y + 15} fill={colour.foreground} font-size="12"
						>{label(r.item.label, r.w)}</text
					>
				{/if}
				<title>{r.item.label} — {formatSize(r.item.value)}</title>
			{/snippet}
			<!-- The table below is the keyboard and screen-reader route; the tiles are pointer shortcuts. -->
			{#if r.item.href}
				<a href={r.item.href} tabindex="-1" aria-hidden="true">{@render tile()}</a>
			{:else}
				<g aria-hidden="true">{@render tile()}</g>
			{/if}
		{/each}
	</svg>

	<div class="card">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col">{detail ? 'Game' : 'System'}</th>
						<th scope="col" class="text-end">{detail ? 'Releases' : 'Games'}</th>
						<th scope="col" class="text-end">Size</th>
						<th scope="col" class="text-end">% of {detail ? 'system' : 'library'}</th>
					</tr>
				</thead>
				<tbody>
					{#if library}
						{#each library.systems as s (s.system)}
							<tr>
								<td><a href={systemHref(s.system)}>{s.systemName}</a></td>
								<td class="text-end">{s.gameCount}</td>
								<td class="text-end text-nowrap"><FileSize bytes={s.bytes} /></td>
								<td class="text-end text-nowrap">{percent(s.bytes, total)}</td>
							</tr>
						{/each}
					{:else if detail}
						{#each detail.games as g (g.id)}
							<tr>
								<td><a href={gameHref(g.id)}>{g.displayTitle}</a></td>
								<td class="text-end">{g.romCount}</td>
								<td class="text-end text-nowrap"><FileSize bytes={g.bytes} /></td>
								<td class="text-end text-nowrap">{percent(g.bytes, total)}</td>
							</tr>
						{/each}
					{/if}
				</tbody>
			</table>
		</div>
	</div>
{/if}
<!-- eslint-enable svelte/no-navigation-without-resolve -->
