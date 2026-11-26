<script lang="ts">
	import { useAbortableGet } from '#lib/loadPage.svelte.js';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { formatCount, formatSize } from '#lib/format.js';
	import { systemColour } from '#lib/systemColours.js';
	import { squarify } from '#lib/treemap.js';

	/** RP3: the library (or one system, `?system=`) as a treemap of on-disk ZIP sizes, with the table as its text equivalent. */
	interface LibrarySize {
		totalBytes: number;
		gameCount: number;
		updateCount: number;
		systems: {
			system: string;
			systemName: string;
			bytes: number;
			gameCount: number;
			updateCount: number;
		}[];
	}
	interface SystemSize {
		system: string;
		systemName: string;
		bytes: number;
		updateCount: number;
		games: {
			id: string;
			displayTitle: string;
			bytes: number;
			romCount: number;
			updateCount: number;
		}[];
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

	const report = useAbortableGet<LibrarySize | SystemSize>(() =>
		system ? `/api/reports/library-size/${encodeURIComponent(system)}` : '/api/reports/library-size'
	);
	const error = $derived(report.error);
	const library = $derived(system ? null : (report.result as LibrarySize | null));
	const detail = $derived(system ? (report.result as SystemSize | null) : null);

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

	/** B22: a click anywhere in a row follows its first-cell link (which stays the keyboard and screen-reader route). */
	function followRow(e: MouseEvent): void {
		if ((e.target as Element).closest('a')) return;
		(e.currentTarget as HTMLElement).querySelector('a')?.click();
	}

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
	<ErrorAlert {error} onretry={() => report.reload()} />
{:else if !library && !detail}
	<Loading />
{:else if count === 0}
	<div class="card">
		<EmptyState message={system ? 'This system has no games.' : 'The library is empty.'} />
	</div>
{:else}
	<p><FileSize bytes={total} /> in {formatCount(count)} {count === 1 ? 'game' : 'games'}</p>

	<svg
		class="d-block w-100 h-auto mb-3 border rounded"
		viewBox="0 0 {width} {height}"
		role="group"
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
			<table class="table table-sm table-striped table-hover align-middle mb-0 clickable-rows">
				<thead class="table-dark">
					<tr>
						<th scope="col">{detail ? 'Game' : 'System'}</th>
						<th scope="col" class="text-end">{detail ? 'Releases' : 'Games'}</th>
						<th scope="col" class="text-end">Updates</th>
						<th scope="col" class="text-end">Size</th>
						<th scope="col" class="text-end">% of {detail ? 'system' : 'library'}</th>
					</tr>
				</thead>
				<tbody>
					{#if library}
						{#each library.systems as s (s.system)}
							<!-- B22: a pointer shortcut; the link in the first cell is the keyboard route. -->
							<tr onclick={followRow}>
								<!-- B19: the Library link comes after the system link, so followRow still opens the system. -->
								<td>
									<div class="d-flex align-items-center gap-2">
										<a href={systemHref(s.system)}>{s.systemName}</a>
										<a
											class="btn btn-sm btn-outline-secondary ms-auto"
											href="{resolve('/')}?system={encodeURIComponent(s.system)}"
											aria-label="Open {s.systemName} in Library"><Icon name="library" /></a
										>
									</div>
								</td>
								<td class="text-end">{formatCount(s.gameCount)}</td>
								<td class="text-end">{formatCount(s.updateCount)}</td>
								<td class="text-end text-nowrap"><FileSize bytes={s.bytes} /></td>
								<td class="text-end text-nowrap">{percent(s.bytes, total)}</td>
							</tr>
						{/each}
					{:else if detail}
						{#each detail.games as g (g.id)}
							<!-- B22: a pointer shortcut; the link in the first cell is the keyboard route. -->
							<tr onclick={followRow}>
								<td><a href={gameHref(g.id)}>{g.displayTitle}</a></td>
								<td class="text-end">{formatCount(g.romCount)}</td>
								<td class="text-end">{formatCount(g.updateCount)}</td>
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
